using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ESDERomCleaner;

// Preferencias de la descarga masiva; se recuerdan entre sesiones.
// Qué hacer cuando ya hay en destino un medio de la ROM en esa carpeta.
internal enum BulkOverwrite { No, Always, IfSizeDiffers }

internal sealed record BulkMediaSettings(string[] Folders, bool NormalizedVideo, BulkOverwrite Overwrite, bool Backup, bool SearchByName, string[] Systems)
{
    public static BulkMediaSettings Default(bool normalizedVideo) => new(
        ["covers", "backcovers", "3dboxes", "physicalmedia", "screenshots", "titlescreens", "marquees", "miximages", "videos"],
        normalizedVideo, BulkOverwrite.No, true, false, []);

    public static BulkMediaSettings Load(bool defaultNormalizedVideo)
    {
        var loaded = AppSettings.Get<BulkMediaSettings?>("BulkMedia", null);
        return loaded is null ? Default(defaultNormalizedVideo) : loaded with { Folders = loaded.Folders ?? [], Systems = loaded.Systems ?? [] };
    }

    public void Save() => AppSettings.Set("BulkMedia", this);
}

internal sealed record BulkMediaOptions(
    string Root, IReadOnlyList<RomRow> Roms, IReadOnlyList<SsSystem> SsSystems, IReadOnlyList<string> LocalSystems,
    IReadOnlyList<EsDeMediaFolder> Folders, bool NormalizedVideo, BulkOverwrite Overwrite, bool Backup, bool SearchByName, int Threads, bool Metadata, bool GamelistBackup, bool Languages)
{
    // Juego elegido a mano para una ROM (desde «Aproximaciones por nombre» del formulario de una ROM): se usa tal cual, sin buscar en ScreenScraper.
    public Func<RomRow, SsGame?>? ForcedGame { get; init; }
}

internal sealed record BulkIssue(string System, string Rom, string Result, string Detail);

// Espacio por tipo de medio: lo que ocupaban los archivos sustituidos (Before) y lo que ocupan los nuevos (After).
internal sealed class BulkFolderStats
{
    public int Files, Replaced;
    public long BytesBefore, BytesAfter;
}

internal sealed class BulkSummary
{
    public readonly Dictionary<string, BulkFolderStats> ByFolder = new(StringComparer.OrdinalIgnoreCase);
    public int RomsFinished;   // ROMs cuyo proceso ya ha terminado (RomsDone cuenta también las que acaban de empezar)
    public int RomsTotal, RomsDone, AlreadyComplete, NotFound, FoundByName, WithErrors, SkippedNotFile, SkippedSystems;
    public int FilesSaved, FilesReplaced, FilesIdentical, FoldersUnavailable, MetadataUpdated, MetadataFailed, LanguagesStored, MetadataFetched;
    public long BytesDownloaded;
    public string StoppedReason = "";
    // ROMs (sistema: nombre) cuyos metadatos llegaron de ScreenScraper sin categoría.
    public readonly System.Collections.Concurrent.ConcurrentBag<string> NoGenre = new();
}

internal sealed record BulkStatus(string Text, BulkSummary Summary);

internal sealed record BulkPlan(int Systems, int UnresolvedSystems, int Roms, int RomsWithWork, int RomsComplete, int FolderTargets);

// Índice de los medios que ya existen en destino: se lee cada carpeta una sola vez en lugar de listarla por cada ROM.
internal sealed class ExistingMediaIndex
{
    private readonly Dictionary<string, HashSet<string>> directories = new(StringComparer.OrdinalIgnoreCase);

    // Varios hilos consultan y actualizan el índice a la vez.
    public bool Has(string targetBase) { lock (directories) return Stems(Path.GetDirectoryName(targetBase)!).Contains(Path.GetFileName(targetBase)); }

    public void Add(string targetBase) { lock (directories) Stems(Path.GetDirectoryName(targetBase)!).Add(Path.GetFileName(targetBase)); }

    private HashSet<string> Stems(string directory)
    {
        if (directories.TryGetValue(directory, out var stems)) return stems;
        stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(directory))
                foreach (string file in Directory.EnumerateFiles(directory))
                    if (EsDeMediaInstaller.IsMediaFile(file)) stems.Add(Path.GetFileNameWithoutExtension(file));
        }
        catch { }
        directories[directory] = stems;
        return stems;
    }
}

// Recorre las ROMs de los sistemas elegidos y, para cada una, consulta ScreenScraper y guarda en ES-DE los medios pedidos.
// Se procesan varias ROMs a la vez, hasta los hilos que admita la cuenta de ScreenScraper, y es reanudable: sin «sobrescribir», lo que ya existe
// en destino no se vuelve a pedir, ni siquiera la consulta de la ROM.
internal sealed class BulkMediaRunner
{
    private readonly BulkMediaOptions o;
    private readonly ScreenScraperClient client;
    private readonly TraceLog trace;
    private readonly IProgress<BulkStatus>? status;
    private readonly IProgress<BulkIssue>? issues;
    private readonly ExistingMediaIndex index = new();
    // Idiomas de las ROMs: solo se rellena si se pidió la opción «Idiomas» (para las reglas de curado).
    private readonly RomLanguageIndex? languageIndex;
    private int consecutiveErrors;
    // Metadatos pendientes de escribir, por gamelist.xml y ruta relativa; se escriben una vez por archivo, cuando se han
    // procesado todas sus ROMs (o al terminar si se cancela).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<string, GamelistMetadata>> pendingMetadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int[]> remainingByGamelist = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> systemByGamelist = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? stopSource;

    public BulkSummary Summary { get; } = new();
    public bool CollectionChanged { get; private set; }
    public bool MetadataChanged { get; private set; }

    public BulkMediaRunner(BulkMediaOptions options, ScreenScraperCredentials credentials, TraceLog trace, IProgress<BulkStatus>? status, IProgress<BulkIssue>? issues)
    {
        o = options;
        client = new ScreenScraperClient(credentials);
        languageIndex = options.Languages ? RomLanguageIndex.Load() : null;
        this.trace = trace;
        this.status = status;
        this.issues = issues;
    }

    private static string RomFolderTarget(BulkMediaOptions o, RomRow row, EsDeMediaFolder folder) => EsDeMediaInstaller.TargetBase(o.Root, row, folder.Folder);

    private static IEnumerable<RomRow> RomsOf(BulkMediaOptions o, string system) =>
        o.Roms.Where(r => r.Status == RomStatus.Ok && r.Path.Length > 0 && r.System.Equals(system, StringComparison.OrdinalIgnoreCase))
              .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase);

    // «Tiene metadatos»: el juego ya trae descripción o categoría en su gamelist.xml (una entrada recién creada solo trae ruta y nombre).
    private static bool HasMetadata(RomRow row) =>
        !string.IsNullOrWhiteSpace(row.Description) || (!string.IsNullOrWhiteSpace(row.Genre) && row.Genre != "Sin categoría");

    // Metadatos que hay que pedir para esta ROM: siempre si se sobrescribe; con «no sobrescribir», solo los juegos que todavía no tienen.
    private static bool NeedsMetadata(BulkMediaOptions o, RomRow row) => o.Metadata && (o.Overwrite != BulkOverwrite.No || !HasMetadata(row));

    // Cuenta, sin tocar nada, cuánto trabajo habría: ROMs de los sistemas elegidos y cuántas ya lo tienen todo en destino.
    public static BulkPlan Plan(BulkMediaOptions o)
    {
        var index = new ExistingMediaIndex();
        var languageIndex = o.Languages ? RomLanguageIndex.Load() : null;
        int roms = 0, withWork = 0, targets = 0, unresolved = 0;
        foreach (string system in o.LocalSystems)
        {
            var rows = RomsOf(o, system).ToList();
            if (SsSystemResolver.Find(o.SsSystems, system) is null) { unresolved++; continue; }
            foreach (var row in rows)
            {
                roms++;
                int pending = 0;
                foreach (var folder in o.Folders)
                {
                    try { if (o.Overwrite != BulkOverwrite.No || !index.Has(RomFolderTarget(o, row, folder))) pending++; }
                    catch { pending++; }
                }
                bool needLanguages = languageIndex is not null && (o.Overwrite != BulkOverwrite.No || !languageIndex.Has(RomKey.Of(row)));
                if (pending > 0 || NeedsMetadata(o, row) || needLanguages) { withWork++; targets += pending; }
            }
        }
        return new BulkPlan(o.LocalSystems.Count, unresolved, roms, withWork, roms - withWork, targets);
    }

    // Registro de depuración (lo muestra la ventana «Registro» de la descarga masiva): qué se decide y por qué, ROM a ROM y carpeta a carpeta.
    public Action<string>? Debug { get; set; }

    private void Dbg(string text) => Debug?.Invoke($"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,2}] {text}");

    private void Report(string text)
    {
        status?.Report(new BulkStatus(text, Summary));
    }

    private void Issue(RomRow row, string result, string detail) => issues?.Report(new BulkIssue(row.System, row.RomName, result, detail));

    // Errores tras los que no tiene sentido seguir pidiendo: cuota agotada, bloqueo, credenciales o servidor cerrado.
    private static bool IsFatal(ScreenScraperException ex) => ex.StatusCode is 401 or 403 or 423 or 426 or 429 or 430 or 431;

    // Detiene toda la descarga (cuota agotada, bloqueo, demasiados errores…) y deja constancia del motivo.
    private void Stop(string reason, RomRow? row, string result)
    {
        lock (Summary) { if (Summary.StoppedReason.Length == 0) Summary.StoppedReason = reason; }
        if (row is not null) Issue(row, result, reason);
        try { stopSource?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async Task RunAsync(CancellationToken userToken)
    {
        var systems = o.LocalSystems.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        Summary.RomsTotal = systems.Sum(s => RomsOf(o, s).Count());
        int threads = Math.Max(1, o.Threads);
        Dbg($"=== INICIO === sistemas={systems.Count}; ROMs={Summary.RomsTotal}; hilos={threads}");
        Dbg($"Carpetas pedidas: {string.Join(", ", o.Folders.Select(f => f.Folder))}; metadatos={o.Metadata}; idiomas={o.Languages}");
        Dbg("Sobrescribir archivos de medios: " + (o.Overwrite == BulkOverwrite.No ? "NO (solo se descarga lo que falta en destino)" : o.Overwrite == BulkOverwrite.Always ? "SIEMPRE" : "solo si el tamaño cambia")
            + $"; copia de seguridad={o.Backup}; buscar por nombre={o.SearchByName}; vídeo normalizado={o.NormalizedVideo}");
        if (o.Metadata) Dbg(o.Overwrite == BulkOverwrite.No
            ? "METADATOS: solo se piden y escriben para los juegos que aún no tienen (sin descripción ni categoría); los que ya tienen se respetan."
            : "METADATOS: se piden y reescriben en el gamelist.xml de todos los juegos (nombre, descripción, género…).");
        trace.Add("bulk_media_started", $"systems={string.Join(",", systems)}; folders={string.Join(",", o.Folders.Select(f => f.Folder))}; normalized={o.NormalizedVideo}; overwrite={o.Overwrite}; backup={o.Backup}; by_name={o.SearchByName}; threads={threads}; roms={Summary.RomsTotal}");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(userToken);
        stopSource = stop;
        var work = new List<(RomRow Row, SsSystem Ss)>();
        foreach (string system in systems)
        {
            var rows = RomsOf(o, system).ToList();
            var ss = SsSystemResolver.Find(o.SsSystems, system);
            if (ss is null)
            {
                Summary.SkippedSystems++;
                Dbg($"Sistema «{system}»: no reconocido en ScreenScraper, se omiten {rows.Count} ROMs");
                Summary.RomsDone += rows.Count;
                issues?.Report(new BulkIssue(system, "(todo el sistema)", "Sistema no reconocido", $"No se pudo deducir el sistema de ScreenScraper a partir de la carpeta «{system}»; se omiten {rows.Count} ROMs."));
                continue;
            }
            work.AddRange(rows.Select(r => (r, ss)));
        }

        if (o.Metadata)
            foreach (var group in work.Where(w => w.Row.Gamelist.Length > 0).GroupBy(w => w.Row.Gamelist, StringComparer.OrdinalIgnoreCase))
            {
                remainingByGamelist[group.Key] = [group.Count()];
                systemByGamelist[group.Key] = group.First().Row.System;
            }

        try
        {
            await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = stop.Token }, async (item, ct) =>
            {
                var (row, ss) = item;
                Interlocked.Increment(ref Summary.RomsDone);
                Report($"{row.System}: {row.RomName}" + (threads > 1 ? $"  (+ hasta {threads - 1} más en paralelo)" : ""));
                try
                {
                    await ProcessAsync(row, ss, ct);
                    Interlocked.Exchange(ref consecutiveErrors, 0);
                }
                catch (OperationCanceledException) { throw; }
                catch (ScreenScraperException ex) when (IsFatal(ex)) { Stop(ex.Message, row, "Descarga detenida"); }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref Summary.WithErrors);
                    Issue(row, "Error", ex.Message);
                    trace.Add("bulk_media_rom_error", $"system={row.System}; rom={row.RomName}; {Mask(ex.Message)}");
                    if (Interlocked.Increment(ref consecutiveErrors) >= 10 * threads)
                        Stop("Demasiados errores seguidos; se detiene para no gastar la cuota. Último: " + ex.Message, null, "");
                }
            });
        }
        catch (OperationCanceledException) { lock (Summary) { if (Summary.StoppedReason.Length == 0) Summary.StoppedReason = "Cancelado por el usuario."; } }
        finally
        {
            stopSource = null;
            languageIndex?.Save();
            // Lo que quede sin escribir (cancelación o parada) se guarda igualmente: son juegos ya consultados.
            foreach (string gamelist in pendingMetadata.Keys.ToList()) FlushGamelist(gamelist);
            trace.Add("bulk_media_finished", $"done={Summary.RomsDone}/{Summary.RomsTotal}; complete={Summary.AlreadyComplete}; saved={Summary.FilesSaved}; replaced={Summary.FilesReplaced}; identical={Summary.FilesIdentical}; metadata={Summary.MetadataUpdated}/{Summary.MetadataFailed}; languages={Summary.LanguagesStored}; " +
                $"not_found={Summary.NotFound}; by_name={Summary.FoundByName}; errors={Summary.WithErrors}; unavailable={Summary.FoldersUnavailable}; bytes={Summary.BytesDownloaded}" +
                $"; space={string.Join(",", Summary.ByFolder.Select(p => $"{p.Key}:{p.Value.BytesAfter - p.Value.BytesBefore}"))}" +
                (Summary.StoppedReason.Length > 0 ? $"; stopped={Summary.StoppedReason}" : ""));
            Dbg($"=== FIN === ROMs {Summary.RomsFinished}/{Summary.RomsTotal}; guardados={Summary.FilesSaved}; sustituidos={Summary.FilesReplaced}; idénticos={Summary.FilesIdentical}; ya completas={Summary.AlreadyComplete}; no encontradas={Summary.NotFound}; sin medio={Summary.FoldersUnavailable}" + (Summary.StoppedReason.Length > 0 ? $"; detenida: {Summary.StoppedReason}" : ""));
            Report(Summary.StoppedReason.Length > 0 ? Summary.StoppedReason : "Descarga terminada.");
        }
    }

    // Las credenciales no deben aparecer nunca en la traza ni en la lista de incidencias.
    private static string Mask(string text) => text.Length > 300 ? text[..300] : text;

    private async Task ProcessAsync(RomRow row, SsSystem ss, CancellationToken token)
    {
        try { await ProcessCoreAsync(row, ss, token); }
        finally
        {
            if (Interlocked.Increment(ref Summary.RomsFinished) % 50 == 0) languageIndex?.Save();
            // Al terminar la última ROM de un gamelist.xml se escriben sus metadatos de una sola vez.
            if (o.Metadata && row.Gamelist.Length > 0 && remainingByGamelist.TryGetValue(row.Gamelist, out var left) && Interlocked.Decrement(ref left[0]) == 0)
                await Task.Run(() => FlushGamelist(row.Gamelist));
        }
    }

    private void FlushGamelist(string gamelist)
    {
        if (!pendingMetadata.TryRemove(gamelist, out var entries)) return;
        string system = systemByGamelist.GetValueOrDefault(gamelist, "");
        var errors = new List<string>();
        int updated = 0;
        try { updated = GamelistMetadataWriter.Apply(gamelist, entries, o.GamelistBackup, errors); }
        catch (Exception ex) { errors.Add(ex.Message); }
        Dbg($"Gamelist de «{system}»: metadatos escritos en {updated} juego(s), {entries.Count - updated} fallidos (copia de seguridad: {o.GamelistBackup})");
        Interlocked.Add(ref Summary.MetadataUpdated, updated);
        Interlocked.Add(ref Summary.MetadataFailed, entries.Count - updated);
        if (updated > 0) MetadataChanged = true;
        foreach (string error in errors.Take(25)) issues?.Report(new BulkIssue(system, Path.GetFileName(Path.GetDirectoryName(gamelist) ?? ""), "Metadatos", Mask(error)));
        if (errors.Count > 25) issues?.Report(new BulkIssue(system, "", "Metadatos", $"…y {errors.Count - 25} errores más en este gamelist.xml"));
        trace.Add("bulk_metadata_written", $"system={system}; gamelist={gamelist}; updated={updated}; failed={entries.Count - updated}; backup={o.GamelistBackup}");
    }

    private async Task ProcessCoreAsync(RomRow row, SsSystem ss, CancellationToken token)
    {
        string id = $"{row.System}: {row.RomName}";
        if (!File.Exists(row.Path)) { Interlocked.Increment(ref Summary.SkippedNotFile); Dbg($"{id}: se omite (no es un archivo)"); return; }

        var pending = new List<EsDeMediaFolder>();
        foreach (var folder in o.Folders)
        {
            string target = RomFolderTarget(o, row, folder);
            bool exists = index.Has(target);
            bool wanted = o.Overwrite != BulkOverwrite.No || !exists;
            Dbg($"{id} · {folder.Folder}: {(exists ? "ya existe" : "no existe")} en destino ({Path.GetFileName(target)}) → {(wanted ? "se pide" : "se omite (no se sobrescribe)")}");
            if (wanted) pending.Add(folder);
        }
        bool needLanguages = languageIndex is not null && (o.Overwrite != BulkOverwrite.No || !languageIndex.Has(RomKey.Of(row)));
        bool needMetadata = NeedsMetadata(o, row);
        if (o.Metadata && !needMetadata) Dbg($"{id}: ya tiene metadatos, no se tocan (no se sobrescribe)");
        if (pending.Count == 0 && !needMetadata && !needLanguages) { Interlocked.Increment(ref Summary.AlreadyComplete); Dbg($"{id}: completa en destino, no se consulta a ScreenScraper"); return; }
        if (pending.Count == 0) Dbg($"{id}: sus medios ya están, pero se consulta igualmente porque se piden " + (needMetadata ? "metadatos" : "idiomas"));

        var (game, via, note) = await FindGameAsync(row, ss, token);
        Dbg($"{id}: consulta a ScreenScraper → " + (game is null ? $"NO encontrada ({note})" : $"juego {game.Id} «{game.Name}» (vía {via})"));
        if (game is null)
        {
            Interlocked.Increment(ref Summary.NotFound);
            Issue(row, "No encontrada", note);
            Dbg($"{id}: RESULTADO → NO se descarga nada: la ROM no se ha encontrado en ScreenScraper" + (pending.Count > 0 ? $" (se habían pedido: {string.Join(", ", pending.Select(f => f.Folder))})" : ""));
            trace.Add("bulk_media_rom", $"system={row.System}; rom={row.RomName}; not_found; {note}");
            return;
        }
        if (via == "nombre") { Interlocked.Increment(ref Summary.FoundByName); Issue(row, "Encontrada por nombre", note); }

        var regions = SsMediaSelector.RomRegions(game, row.RomName);
        if (languageIndex is not null)
        {
            // Se guarda también si ScreenScraper no da idiomas (lista vacía), para no volver a consultar esa ROM.
            languageIndex.Set(RomKey.Of(row), new RomLanguageInfo { Languages = game.RomLanguages.ToList(), Regions = game.RomRegions.ToList(), FetchedAt = DateTime.Now });
            Interlocked.Increment(ref Summary.LanguagesStored);
        }
        if (needMetadata)
        {
            if (row.Gamelist.Length == 0 || row.GamelistPath.Length == 0) Issue(row, "Metadatos", "La ROM no tiene entrada en ningún gamelist.xml");
            else
            {
                var fetched = GamelistMetadata.From(game, regions);
                if (fetched.Genre.Length == 0) Summary.NoGenre.Add($"{row.System}: {row.RomName}");
                pendingMetadata.GetOrAdd(row.Gamelist, _ => new(StringComparer.OrdinalIgnoreCase))[PathRules.Relative(row.GamelistPath)] = fetched;
                Dbg($"{id}: metadatos recibidos (nombre «{fetched.Name}», género «{fetched.Genre}», descripción de {fetched.Desc.Length} caracteres) → se escribirán en el gamelist.xml");
                Interlocked.Increment(ref Summary.MetadataFetched); // descargados; se escriben en el gamelist.xml de una sola vez al terminar cada sistema
            }
        }
        var requests = new List<MediaInstallRequest>();
        var temps = new List<string>();
        var errors = new List<string>();
        var missing = new List<string>();
        int savedHere = 0;
        try
        {
            foreach (var folder in pending)
            {
                string[] order = SsMediaSelector.TypeOrder(folder, o.NormalizedVideo, miximageV2Only: true);
                var (candidates, _) = SsMediaSelector.Candidates(game, regions, folder, order);
                var media = candidates.FirstOrDefault();
                if (media is null) { Interlocked.Increment(ref Summary.FoldersUnavailable); missing.Add(folder.Folder); Dbg($"{id} · {folder.Folder}: ScreenScraper no tiene este medio"); continue; }

                if (o.Overwrite == BulkOverwrite.IfSizeDiffers && SameSizeAsExisting(RomFolderTarget(o, row, folder), media)) { Interlocked.Increment(ref Summary.FilesIdentical); Dbg($"{id} · {folder.Folder}: mismo tamaño que el actual, se omite"); continue; }

                string source = SsMediaCache.PathFor(game.Id, media);
                if (!SsMediaCache.IsCached(game.Id, media))
                {
                    // Sin pasar por la caché de comparación: se descarga a un temporal y se borra tras instalarlo.
                    source = Path.Combine(SsMediaCache.Root, "_lote", Guid.NewGuid().ToString("N") + "." + media.Format.ToLowerInvariant());
                    string label = media.Label;
                    Dbg($"{id} · {folder.Folder}: descarga {label} ({(media.Size > 0 ? ScreenScraperDialog.FormatBytes(media.Size) : "tamaño desconocido")})");
                    var progress = new Progress<long>(received => status?.Report(new BulkStatus(
                        $"{row.System}: {row.RomName} · {folder.Folder}: {label} {ScreenScraperDialog.FormatBytes(received)}" + (media.Size > 0 ? $" de {ScreenScraperDialog.FormatBytes(media.Size)}" : ""), Summary)));
                    try
                    {
                        await client.DownloadMediaAsync(media, source, progress, token);
                        Interlocked.Add(ref Summary.BytesDownloaded, new FileInfo(source).Length);
                        temps.Add(source);
                    }
                    catch (ScreenScraperException ex) when (!IsFatal(ex))
                    {
                        Dbg($"{id} · {folder.Folder}: ERROR al descargar: {Mask(ex.Message)}");
                        errors.Add($"{folder.Folder}: {Mask(ex.Message)}");
                        continue;
                    }
                }
                requests.Add(new MediaInstallRequest(folder.Folder, media, source));
            }

            if (requests.Count > 0)
            {
                var results = new List<MediaInstallResult>();
                var installErrors = new List<string>();
                // Lo que ocupa lo que se va a sustituir, medido antes de que Install lo aparte.
                var sizeBefore = new Dictionary<string, long>();
                foreach (var request in requests)
                {
                    string targetBase = EsDeMediaInstaller.TargetBase(o.Root, row, request.Folder);
                    if (!index.Has(targetBase)) continue;
                    long total = 0;
                    foreach (string existing in EsDeMediaInstaller.Existing(targetBase)) try { total += new FileInfo(existing).Length; } catch { }
                    sizeBefore[request.Folder] = total;
                }
                try { await Task.Run(() => EsDeMediaInstaller.Install(o.Root, row, requests, results, installErrors, null, token, o.Backup), token); }
                finally
                {
                    foreach (var r in results)
                    {
                        Dbg($"{id} · {r.Folder}: guardado {Path.GetFileName(r.TargetPath)}" + (r.ReplacedFiles.Count > 0 ? $" (sustituye {r.ReplacedFiles.Count} archivo(s))" : " (nuevo)"));
                        index.Add(EsDeMediaInstaller.TargetBase(o.Root, row, r.Folder));
                        if (r.ReplacedFiles.Count > 0) Interlocked.Increment(ref Summary.FilesReplaced);
                        lock (Summary.ByFolder)
                        {
                            if (!Summary.ByFolder.TryGetValue(r.Folder, out var stats)) Summary.ByFolder[r.Folder] = stats = new BulkFolderStats();
                            stats.Files++;
                            if (r.ReplacedFiles.Count > 0) stats.Replaced++;
                            stats.BytesBefore += sizeBefore.GetValueOrDefault(r.Folder);
                            try { stats.BytesAfter += new FileInfo(r.TargetPath).Length; } catch { }
                        }
                    }
                    Interlocked.Add(ref Summary.FilesSaved, results.Count);
                    savedHere = results.Count;
                    if (results.Count > 0) CollectionChanged = true;
                    errors.AddRange(installErrors);
                    trace.Add("bulk_media_rom", $"system={row.System}; rom={row.RomName}; ss_game={game.Id}; via={via}; " +
                        $"saved={string.Join(",", results.Select(r => $"{r.Folder}:{r.Media.Label}" + (r.ReplacedFiles.Count > 0 ? "[sustituye]" : "")))}" +
                        (missing.Count > 0 ? $"; unavailable={string.Join(",", missing)}" : "") +
                        (errors.Count > 0 ? $"; errors={string.Join(" | ", errors)}" : ""));
                }
            }
            else if (missing.Count > 0)
                trace.Add("bulk_media_rom", $"system={row.System}; rom={row.RomName}; ss_game={game.Id}; via={via}; saved=; unavailable={string.Join(",", missing)}");
        }
        finally
        {
            foreach (string temp in temps) try { File.Delete(temp); } catch { }
        }

        // Resultado de la ROM: qué se ha descargado de lo pedido y qué no.
        Dbg($"{id}: RESULTADO → descargados y guardados {savedHere} · sin medio en ScreenScraper {missing.Count}" + (missing.Count > 0 ? $" ({string.Join(", ", missing)})" : "")
            + $" · errores {errors.Count} · omitidos por existir {o.Folders.Count - pending.Count}");

        // Medios pedidos que ScreenScraper no tiene para esta ROM.
        if (missing.Count > 0)
        {
            string names = string.Join(", ", missing.Select(f => EsDeMediaInstaller.Folders.FirstOrDefault(x => x.Folder == f)?.Description ?? f));
            Issue(row, "Medio no disponible", $"ScreenScraper no tiene: {names}");
        }

        if (errors.Count > 0)
        {
            Interlocked.Increment(ref Summary.WithErrors);
            Issue(row, "Errores al guardar", string.Join(" | ", errors));
        }
    }

    // «Solo si el tamaño es distinto»: si algún archivo de destino pesa lo mismo que el medio de ScreenScraper, se deja como está.
    private bool SameSizeAsExisting(string targetBase, SsMedia media)
    {
        if (media.Size <= 0 || !index.Has(targetBase)) return false;
        foreach (string existing in EsDeMediaInstaller.Existing(targetBase))
        {
            try { if (new FileInfo(existing).Length == media.Size) return true; }
            catch (IOException) { }
        }
        return false;
    }

    // 1) por hash (lo normal); 2) si se pidió y el hash no la reconoce, por el nombre del juego dentro del sistema.
    private async Task<(SsGame? Game, string Via, string Note)> FindGameAsync(RomRow row, SsSystem ss, CancellationToken token)
    {
        if (o.ForcedGame?.Invoke(row) is { } forced) return (forced, "elegido", "Juego elegido a mano");
        SsLookupResult? result = null;
        string hashNote = "";
        try
        {
            bool isZip = Path.GetExtension(row.Path).Equals(".zip", StringComparison.OrdinalIgnoreCase);
            bool packed = isZip || Path.GetExtension(row.Path).ToLowerInvariant() is ".7z" or ".rar";
            var hashes = await Task.Run(() => RomHasher.Compute(row.Path, packed, null, token), token);
            try { result = await WithRetry(() => client.LookupAsync(ss.Id, row.RomName, hashes, token), token); }
            catch (ScreenScraperException ex) when (ex.StatusCode == 404 && isZip)
            {
                var whole = await Task.Run(() => RomHasher.Compute(row.Path, false, null, token), token);
                result = await WithRetry(() => client.LookupAsync(ss.Id, row.RomName, whole, token), token);
            }
        }
        catch (ScreenScraperException ex) when (ex.StatusCode == 404) { hashNote = "No reconocida por hash"; }
        if (result is { Games.Count: > 0 }) return (result.Games[0], "hash", "");
        if (hashNote.Length == 0) hashNote = "Sin juego en ScreenScraper";
        if (!o.SearchByName) return (null, "", hashNote);

        string note = "";
        var tried = new List<string>();
        foreach (string query in NameQueries(row))
        {
            tried.Add(query);
            var found = await WithRetry(() => client.SearchGamesAsync(ss.Id, query, token), token);
            string key = NormalizeName(query);
            var matches = found.Where(g => g.Names.Any(n => NormalizeName(n) == key)).ToList();
            if (matches.Count == 1)
            {
                var full = await WithRetry(() => client.LookupByGameIdAsync(ss.Id, matches[0].Id, token), token);
                if (full.Games.Count > 0) return (full.Games[0], "nombre", $"Nombre exacto: «{query}»");
            }
            else if (matches.Count > 1) note = $"Varios juegos con el nombre exacto «{query}»";
        }
        return (null, "", $"{hashNote} · " + (note.Length > 0 ? note : $"sin coincidencia por nombre: {string.Join(" / ", tried.Select(t => "«" + t + "»"))}"));
    }

    private static async Task<T> WithRetry<T>(Func<Task<T>> action, CancellationToken token)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return await action(); }
            catch (ScreenScraperException ex) when (ex.StatusCode == 429 && attempt < 4) { await Task.Delay(TimeSpan.FromSeconds(20 * attempt), token); }
        }
    }

    // Nombres a probar: el del gamelist y, si difiere, el del archivo sin extensión ni etiquetas entre paréntesis o corchetes.
    private static IEnumerable<string> NameQueries(RomRow row)
    {
        var seen = new HashSet<string>();
        string cleaned = Regex.Replace(Path.GetFileNameWithoutExtension(row.RomName), @"\s*[\(\[][^\)\]]*[\)\]]", "").Replace('_', ' ').Trim();
        foreach (string candidate in new[] { row.GameName?.Trim() ?? "", cleaned })
            if (candidate.Length > 0 && seen.Add(NormalizeName(candidate))) yield return candidate;
    }

    // Sin tildes ni signos, en minúsculas y con el artículo final movido al principio («Story of Thor, The» = «The Story of Thor»).
    internal static string NormalizeName(string text)
    {
        string t = text.Trim();
        var m = Regex.Match(t, @"^(.*),\s*(the|a|an|le|la|les|el|los|las|der|die|das)$", RegexOptions.IgnoreCase);
        if (m.Success) t = m.Groups[2].Value + " " + m.Groups[1].Value;
        string d = t.Normalize(NormalizationForm.FormD).ToLowerInvariant();
        return new string(d.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)).ToArray());
    }
}
