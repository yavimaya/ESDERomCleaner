using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ESDERomCleaner;

internal sealed record DeletionMediaFile(string System, string Path, long Size);
internal sealed record PlannedRomDeletion(RomRow Row, IReadOnlyList<AccessoryFile> Accessories, IReadOnlyList<DeletionMediaFile> Media);
internal sealed record DeletionGamelist(string Path, string Sha256, IReadOnlyList<RomRow> Rows);
internal sealed record DeletionPlan(
    string Root,
    IReadOnlyList<PlannedRomDeletion> Items,
    IReadOnlyList<AccessoryFile> Accessories,
    IReadOnlyList<DeletionMediaFile> Media,
    IReadOnlyList<DeletionGamelist> Gamelists,
    long RomBytes,
    long AccessoryBytes,
    long MediaBytes);
internal sealed record DeletionResult(
    int RomCountPlanned,
    int DeletedRomCount,
    long DeletedRomBytes,
    int AccessoryCountPlanned,
    int DeletedAccessoryCount,
    long DeletedAccessoryBytes,
    int MediaCountPlanned,
    int DeletedMediaCount,
    long DeletedMediaBytes,
    int GameEntriesRemoved,
    IReadOnlyList<string> Backups,
    IReadOnlyList<string> Errors,
    DateTime StartedAt,
    DateTime EndedAt)
{
    public long TotalDeletedBytes => DeletedRomBytes + DeletedAccessoryBytes + DeletedMediaBytes;
}

internal sealed class DeletionService
{
    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = false,
        IgnoreWhitespace = false
    };

    public DeletionPlan BuildPlan(
        string root,
        IEnumerable<RomRow> selectedRows,
        IReadOnlyList<AccessoryFile> accessorySnapshot,
        IProgress<string>? progress,
        CancellationToken token)
    {
        root = Path.GetFullPath(root);
        string romRoot = Path.Combine(root, "Roms");
        string gamelistsRoot = Path.Combine(root, "ES-DE", "gamelists");
        string mediaRoot = Path.Combine(root, "ES-DE", "downloaded_media");
        if (!Directory.Exists(romRoot)) throw new DirectoryNotFoundException($"No se encuentra Roms:\r\n{romRoot}");
        if (!Directory.Exists(gamelistsRoot)) throw new DirectoryNotFoundException($"No se encuentra ES-DE\\gamelists:\r\n{gamelistsRoot}");

        var rows = selectedRows.Where(r => r.Status == RomStatus.Ok).DistinctBy(r => Path.GetFullPath(r.Path), StringComparer.OrdinalIgnoreCase).ToArray();
        if (rows.Length == 0) throw new InvalidOperationException("No hay ROMs válidas seleccionadas para eliminar.");

        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            string systemRoot = Path.GetFullPath(Path.Combine(romRoot, row.System));
            string romPath = Path.GetFullPath(row.Path);
            if (!PathRules.Inside(romPath, systemRoot) || (!File.Exists(romPath) && !Directory.Exists(romPath)))
                throw new InvalidOperationException($"La ruta de ROM no existe o sale de su carpeta de sistema:\r\n{row.Path}");
            if (HasReparsePointInPath(romPath, systemRoot))
                throw new InvalidOperationException($"No se eliminarán enlaces ni puntos de reanálisis:\r\n{romPath}");

            string expectedList = Path.GetFullPath(Path.Combine(gamelistsRoot, row.System, "gamelist.xml"));
            string actualList = Path.GetFullPath(row.Gamelist);
            if (!actualList.Equals(expectedList, StringComparison.OrdinalIgnoreCase) ||
                !PathRules.Inside(actualList, gamelistsRoot) ||
                !File.Exists(actualList))
                throw new InvalidOperationException($"No se encuentra un gamelist.xml válido dentro de ES-DE\\gamelists para:\r\n{romPath}");
            if (HasReparsePointInPath(actualList, gamelistsRoot))
                throw new InvalidOperationException($"El gamelist atraviesa un enlace o punto de reanálisis y no se tocará:\r\n{actualList}");
            if (string.IsNullOrWhiteSpace(row.GamelistPath))
                throw new InvalidOperationException($"La ROM no tiene una ruta XML asociada:\r\n{romPath}");
        }

        var gamelistPlans = new List<DeletionGamelist>();
        var groups = rows.GroupBy(r => Path.GetFullPath(r.Gamelist), StringComparer.OrdinalIgnoreCase).ToArray();
        int groupIndex = 0;
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
            groupIndex++;
            progress?.Report($"Validando gamelists: {groupIndex}/{groups.Length}");
            var (document, _) = LoadWrapped(group.Key);
            var gameLists = document.Root!.Elements().Where(e => e.Name.LocalName == "gameList").ToArray();
            var games = gameLists.SelectMany(list => list.Elements().Where(e => e.Name.LocalName == "game")).ToArray();

            foreach (var row in group)
            {
                string wanted = PathRules.Relative(row.GamelistPath);
                int matches = games.Count(game => PathRules.Relative(DirectChildValue(game, "path")) == wanted);
                if (matches != 1)
                    throw new InvalidOperationException($"No se ha podido identificar exactamente una entrada <game>:\r\nROM: {row.Path}\r\nPath gamelist: {row.GamelistPath}\r\nCoincidencias: {matches}");
            }

            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(group.Key)));
            gamelistPlans.Add(new DeletionGamelist(group.Key, hash, group.ToArray()));
        }

        var accessoriesByRom = new Dictionary<string, List<AccessoryFile>>(StringComparer.OrdinalIgnoreCase);
        var allAccessories = new Dictionary<string, AccessoryFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            string systemRoot = Path.Combine(romRoot, row.System);
            string relative = PathRules.Relative(Path.GetRelativePath(systemRoot, row.Path));
            string key = PathRules.AccessoryMatchKey(relative);
            var matches = accessorySnapshot
                .Where(file => file.System.Equals(row.System, StringComparison.OrdinalIgnoreCase) &&
                               file.MatchKey.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                               PathRules.Inside(Path.GetFullPath(file.Path), systemRoot) &&
                               File.Exists(file.Path) &&
                               !HasReparsePointInPath(file.Path, systemRoot))
                .GroupBy(file => Path.GetFullPath(file.Path), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            accessoriesByRom[Path.GetFullPath(row.Path)] = matches;
            foreach (var file in matches) allAccessories.TryAdd(Path.GetFullPath(file.Path), file);
        }

        var mediaByRom = rows.ToDictionary(
            row => Path.GetFullPath(row.Path),
            _ => new Dictionary<string, DeletionMediaFile>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var allMedia = new Dictionary<string, DeletionMediaFile>(StringComparer.OrdinalIgnoreCase);
        var systemGroups = rows.GroupBy(r => r.System, StringComparer.OrdinalIgnoreCase).ToArray();
        int systemIndex = 0;
        foreach (var systemGroup in systemGroups)
        {
            token.ThrowIfCancellationRequested();
            systemIndex++;
            string system = systemGroup.First().System;
            string systemMediaRoot = Path.Combine(mediaRoot, system);
            if (!Directory.Exists(systemMediaRoot) || HasReparsePointInPath(systemMediaRoot, mediaRoot)) continue;

            progress?.Report($"Buscando media: sistema {systemIndex}/{systemGroups.Length}");
            var targets = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in systemGroup)
            {
                string romKey = Path.GetFullPath(row.Path);
                string physicalRelative = PathRules.Relative(Path.GetRelativePath(Path.Combine(romRoot, row.System), row.Path));
                AddMediaTargetKeys(targets, romKey, physicalRelative);
                AddMediaTargetKeys(targets, romKey, row.GamelistPath);
            }

            int seen = 0;
            foreach (string filePath in EnumerateFilesWithoutReparse(systemMediaRoot, token))
            {
                token.ThrowIfCancellationRequested();
                seen++;
                if (seen % 250 == 0) progress?.Report($"Buscando media de {system}: {seen:N0} archivos revisados");
                string relative = PathRules.Relative(Path.GetRelativePath(systemMediaRoot, filePath));
                string[] parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
                string gameMediaKey = parts.Length >= 2 ? string.Join('/', parts.Skip(1)) : relative;
                string withoutExtension = WithoutLastExtension(gameMediaKey);

                var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (targets.TryGetValue(gameMediaKey, out var exact)) matches.UnionWith(exact);
                if (targets.TryGetValue(withoutExtension, out var noExt)) matches.UnionWith(noExt);
                if (matches.Count == 0) continue;

                var info = new FileInfo(filePath);
                var planned = new DeletionMediaFile(system, filePath, info.Length);
                string full = Path.GetFullPath(filePath);
                allMedia.TryAdd(full, planned);
                foreach (string romKey in matches) mediaByRom[romKey].TryAdd(full, planned);
            }
        }

        var items = rows.Select(row =>
        {
            string key = Path.GetFullPath(row.Path);
            return new PlannedRomDeletion(
                row,
                accessoriesByRom[key],
                mediaByRom[key].Values.ToArray());
        }).ToArray();

        progress?.Report("Preparación de eliminación terminada.");
        return new DeletionPlan(
            root,
            items,
            allAccessories.Values.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            allMedia.Values.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            gamelistPlans,
            rows.Sum(r => r.Size),
            allAccessories.Values.Sum(x => x.Size),
            allMedia.Values.Sum(x => x.Size));
    }

    public DeletionResult Execute(DeletionPlan plan, IProgress<string>? progress, bool createGamelistBackups)
    {
        var started = DateTime.Now;
        var errors = new List<string>();
        var backups = new List<string>();
        var deletedRoms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deletedAccessories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deletedMedia = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long deletedRomBytes = 0, deletedAccessoryBytes = 0, deletedMediaBytes = 0;
        int gameEntriesRemoved = 0;

        // Detectar cambios mientras el usuario veía el resumen; no empezar si ha cambiado el origen.
        foreach (var list in plan.Gamelists)
        {
            string listsRoot = Path.Combine(plan.Root, "ES-DE", "gamelists");
            if (HasReparsePointInPath(list.Path, listsRoot) || !File.Exists(list.Path))
                throw new InvalidOperationException($"El gamelist ya no existe o su ruta es insegura. No se ha borrado ningún archivo:\r\n{list.Path}");
            string currentHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(list.Path)));
            if (!currentHash.Equals(list.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException($"El gamelist cambió después de preparar el borrado. No se ha borrado ningún archivo:\r\n{list.Path}");
        }
        foreach (var item in plan.Items)
        {
            string path = Path.GetFullPath(item.Row.Path);
            string systemRoot = Path.Combine(plan.Root, "Roms", item.Row.System);
            if (!PathRules.Inside(path, systemRoot) || !File.Exists(path))
                throw new InvalidOperationException($"La ROM ya no existe, no es un archivo o su ruta salió de la carpeta del sistema. No se ha borrado ningún archivo:\r\n{path}");
            if (HasReparsePointInPath(path, systemRoot))
                throw new InvalidOperationException($"La ruta de ROM se volvió insegura. No se ha borrado ningún archivo:\r\n{path}");
            var currentInfo = new FileInfo(path);
            if (currentInfo.Length != item.Row.Size)
                throw new InvalidOperationException($"La ROM cambió de tamaño después de preparar el borrado. No se ha borrado ningún archivo:\r\n{path}");
            foreach (var accessory in item.Accessories)
            {
                string accessoryPath = Path.GetFullPath(accessory.Path);
                if (!PathRules.Inside(accessoryPath, systemRoot) || HasReparsePointInPath(accessoryPath, systemRoot))
                    throw new InvalidOperationException($"Un accesorio tiene una ruta insegura. No se ha borrado ningún archivo:\r\n{accessoryPath}");
            }
            foreach (var media in item.Media)
            {
                string mediaRootForSystem = Path.Combine(plan.Root, "ES-DE", "downloaded_media", media.System);
                string mediaPath = Path.GetFullPath(media.Path);
                if (!PathRules.Inside(mediaPath, mediaRootForSystem) || HasReparsePointInPath(mediaPath, mediaRootForSystem))
                    throw new InvalidOperationException($"Un archivo de media tiene una ruta insegura. No se ha borrado ningún archivo:\r\n{mediaPath}");
            }
        }

        if (createGamelistBackups)
        {
            progress?.Report("Creando copia de seguridad de cada gamelist...");
            foreach (var list in plan.Gamelists)
            {
                string backup = list.Path + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N")[..6];
                try
                {
                    File.Copy(list.Path, backup, false);
                    backups.Add(backup);
                    progress?.Report("Copia de seguridad creada: " + backup);
                }
                catch (Exception ex)
                {
                    errors.Add($"ERROR BACKUP: {list.Path} -> {ex.Message}");
                    throw new IOException($"No se pudieron respaldar todos los gamelists. No se ha eliminado ninguna ROM.\r\n{list.Path}\r\n{ex.Message}", ex);
                }
            }
        }
        else
        {
            progress?.Report("Copias de seguridad de gamelist.xml deshabilitadas en Opciones.");
        }

        foreach (var item in plan.Items)
        {
            string romPath = Path.GetFullPath(item.Row.Path);
            try
            {
                progress?.Report("Borrando ROM: " + romPath);
                if (Directory.Exists(romPath)) Directory.Delete(romPath, true);
                else File.Delete(romPath);
                deletedRoms.Add(romPath);
                deletedRomBytes += item.Row.Size;
                progress?.Report("ROM eliminada: " + romPath);
            }
            catch (Exception ex)
            {
                errors.Add($"ERROR ROM: {romPath} -> {ex.Message}");
                progress?.Report($"ERROR ROM: {romPath} -> {ex.Message}");
                continue;
            }

            foreach (var accessory in item.Accessories)
            {
                string path = Path.GetFullPath(accessory.Path);
                if (deletedAccessories.Contains(path) || !File.Exists(path)) continue;
                if (HasReparsePointInPath(path, Path.Combine(plan.Root, "Roms", item.Row.System))) { errors.Add($"ERROR ACCESORIO: ruta insegura: {path}"); continue; }
                try
                {
                    progress?.Report("Borrando accesorio: " + path);
                    long size = new FileInfo(path).Length;
                    File.Delete(path);
                    deletedAccessories.Add(path);
                    deletedAccessoryBytes += size;
                }
                catch (Exception ex)
                {
                    errors.Add($"ERROR ACCESORIO: {path} -> {ex.Message}");
                    progress?.Report($"ERROR ACCESORIO: {path} -> {ex.Message}");
                }
            }

            foreach (var media in item.Media)
            {
                string path = Path.GetFullPath(media.Path);
                if (deletedMedia.Contains(path) || !File.Exists(path)) continue;
                if (HasReparsePointInPath(path, Path.Combine(plan.Root, "ES-DE", "downloaded_media", media.System))) { errors.Add($"ERROR MEDIA: ruta insegura: {path}"); continue; }
                try
                {
                    progress?.Report("Borrando media: " + path);
                    long size = new FileInfo(path).Length;
                    File.Delete(path);
                    deletedMedia.Add(path);
                    deletedMediaBytes += size;
                }
                catch (Exception ex)
                {
                    errors.Add($"ERROR MEDIA: {path} -> {ex.Message}");
                    progress?.Report($"ERROR MEDIA: {path} -> {ex.Message}");
                }
            }
        }

        foreach (var list in plan.Gamelists)
        {
            var successful = list.Rows.Where(r => deletedRoms.Contains(Path.GetFullPath(r.Path))).ToArray();
            if (successful.Length == 0) continue;
            string temp = list.Path + ".tmp_" + Guid.NewGuid().ToString("N");
            try
            {
                var (document, declaration) = LoadWrapped(list.Path);
                var games = document.Root!.Elements()
                    .Where(e => e.Name.LocalName == "gameList")
                    .SelectMany(e => e.Elements().Where(game => game.Name.LocalName == "game"))
                    .ToArray();
                var nodes = new List<XElement>();
                foreach (var row in successful)
                {
                    string wanted = PathRules.Relative(row.GamelistPath);
                    var matches = games.Where(game => PathRules.Relative(DirectChildValue(game, "path")) == wanted).ToArray();
                    if (matches.Length != 1) throw new InvalidOperationException($"No se pudo volver a resolver la entrada {row.GamelistPath}; coincidencias: {matches.Length}.");
                    nodes.Add(matches[0]);
                }

                foreach (var node in nodes) node.Remove();
                int removedThisGamelist = nodes.Count;
                string updated = SerializeWrapped(document, declaration);
                _ = ParseWrapped(updated);
                File.WriteAllText(temp, updated, new UTF8Encoding(false));
                File.Move(temp, list.Path, true);
                gameEntriesRemoved += removedThisGamelist;
                progress?.Report("Gamelist actualizado: " + list.Path);
            }
            catch (Exception ex)
            {
                string error = $"ERROR GAMELIST: {list.Path} -> {ex.Message}";
                errors.Add(error);
                progress?.Report(error);
            }
            finally
            {
                if (File.Exists(temp)) try { File.Delete(temp); } catch { }
            }
        }

        return new DeletionResult(
            plan.Items.Count,
            deletedRoms.Count,
            deletedRomBytes,
            plan.Accessories.Count,
            deletedAccessories.Count,
            deletedAccessoryBytes,
            plan.Media.Count,
            deletedMedia.Count,
            deletedMediaBytes,
            gameEntriesRemoved,
            backups,
            errors,
            started,
            DateTime.Now);
    }

    private static void AddMediaTargetKeys(Dictionary<string, List<string>> map, string romKey, string rawPath)
    {
        string key = PathRules.Relative(rawPath);
        if (key.Length == 0) return;
        Add(key);
        Add(WithoutLastExtension(key));

        void Add(string value)
        {
            if (value.Length == 0) return;
            if (!map.TryGetValue(value, out var targets)) map[value] = targets = new();
            if (!targets.Contains(romKey, StringComparer.OrdinalIgnoreCase)) targets.Add(romKey);
        }
    }

    private static string WithoutLastExtension(string path)
    {
        int slash = path.LastIndexOf('/');
        int dot = path.LastIndexOf('.');
        return dot > slash ? path[..dot] : path;
    }

    private static string DirectChildValue(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";

    private static (XDocument Document, string Declaration) LoadWrapped(string path) =>
        ParseWrapped(File.ReadAllText(path), out var declaration) is var document ? (document, declaration) : throw new InvalidOperationException("No se pudo cargar el gamelist.");

    private static XDocument ParseWrapped(string xml) => ParseWrapped(xml, out _);

    private static XDocument ParseWrapped(string xml, out string declaration)
    {
        var match = Regex.Match(xml, @"^\s*<\?xml\b.*?\?>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        declaration = match.Success ? match.Value : "";
        string content = match.Success ? xml[match.Length..] : xml;
        using var input = new StringReader("<__esde_document>" + content + "</__esde_document>");
        using var reader = XmlReader.Create(input, XmlSettings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static string SerializeWrapped(XDocument document, string declaration)
    {
        if (document.Root?.Name.LocalName != "__esde_document") throw new InvalidOperationException("Estructura de gamelist inválida.");
        return declaration + string.Concat(document.Root.Nodes().Select(node => node.ToString(SaveOptions.DisableFormatting)));
    }

    internal static bool HasReparsePointInPath(string path, string boundary)
    {
        string current = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        string root = Path.GetFullPath(boundary).TrimEnd(Path.DirectorySeparatorChar);
        if (!current.Equals(root, StringComparison.OrdinalIgnoreCase) && !PathRules.Inside(current, root))
            return true;

        while (true)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
                }
                catch { return true; }
            }
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) return false;
            string? parent = Path.GetDirectoryName(current);
            if (parent is null) return true;
            current = parent;
        }
    }
    private static IEnumerable<string> EnumerateFilesWithoutReparse(string root, CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (string entry in entries)
            {
                token.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else yield return entry;
            }
        }
    }
}