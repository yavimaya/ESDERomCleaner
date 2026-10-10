using System.Text;

namespace ESDERomCleaner;

internal sealed class CollectionScanner
{
    public ScanResult Scan(string root, IProgress<ScanProgress> progress, CancellationToken token, IReadOnlyCollection<string>? onlySystems = null, bool writeCache = true)
    {
        root = Path.GetFullPath(root);
        var romRoot = Path.Combine(root, "Roms");
        var listRoot = Path.Combine(root, "ES-DE", "gamelists");
        if (!Directory.Exists(romRoot)) throw new DirectoryNotFoundException($"No se encuentra Roms:\r\n{romRoot}");
        if (!Directory.Exists(listRoot)) throw new DirectoryNotFoundException($"No se encuentra ES-DE\\gamelists:\r\n{listRoot}");
        var availableSystems = Directory.EnumerateDirectories(romRoot).Select(Path.GetFileName).Where(n => !string.IsNullOrWhiteSpace(n)).Cast<string>().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        var systems = onlySystems is null ? availableSystems : availableSystems.Where(system => onlySystems.Contains(system, StringComparer.OrdinalIgnoreCase)).ToArray();
        var rows = new List<RomRow>(); var accessories = new List<AccessoryFile>(); var systemFileCounts = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase); int gamelists = 0, systemCount = 0, fileCount = 0; var extensionCounts = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        for (int si = 0; si < systems.Length; si++)
        {
            token.ThrowIfCancellationRequested();
            var system = systems[si]; var sysPath = Path.Combine(romRoot, system);
            progress.Report(new ScanProgress($"Analizando {system}…", si, systems.Length));
            var listPath = Path.Combine(listRoot, system, "gamelist.xml");
            List<GameEntry> games = new();
            if (File.Exists(listPath))
            {
                gamelists++;
                try { games = GamelistParser.Parse(listPath, token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { progress.Report(new ScanProgress($"Error gamelist {system}: {e.Message}", si + 1, systems.Length)); }
            }

            var files = new List<string>();
            try { foreach (var f in Directory.EnumerateFiles(sysPath, "*", SearchOption.AllDirectories)) { token.ThrowIfCancellationRequested(); files.Add(f); } }
            catch (UnauthorizedAccessException e) { progress.Report(new ScanProgress($"Acceso parcial {system}: {e.Message}", si + 1, systems.Length)); }
            catch (IOException e) { progress.Report(new ScanProgress($"Lectura parcial {system}: {e.Message}", si + 1, systems.Length)); }

            systemFileCounts[system] = files.Count;
            fileCount += files.Count;
            if (games.Count == 0)
            {
                foreach (var f in files)
                {
                    token.ThrowIfCancellationRequested();
                    var p = Path.GetFullPath(f);
                    string relativePath = PathRules.Relative(Path.GetRelativePath(sysPath, p));
                    accessories.Add(new AccessoryFile(system, p, Length(p), relativePath, PathRules.AccessoryMatchKey(relativePath)));
                }
                continue;
            }

            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int rowStart = rows.Count;
            var directories = new List<string>(); var unmatched = new List<GameEntry>();
            foreach (var game in games)
            {
                token.ThrowIfCancellationRequested();
                var path = Path.GetFullPath(Path.Combine(sysPath, game.RelativeKey.Replace('/', Path.DirectorySeparatorChar)));
                if (!Inside(path, sysPath)) { unmatched.Add(game); continue; }
                bool file = File.Exists(path), dir = Directory.Exists(path);
                if (!file && !dir) { unmatched.Add(game); continue; }
                if (!matched.Add(path)) continue;
                rows.Add(MakeRow(system, path, sysPath, listPath, game, file ? Length(path) : DirectorySize(path, token)));
                if (dir) directories.Add(path);
            }

            if (unmatched.Count > 0)
            {
                var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    token.ThrowIfCancellationRequested(); var key = PathRules.Name(Path.GetFileName(f));
                    if (key.Length == 0) continue;
                    if (!byName.TryGetValue(key, out var list)) byName[key] = list = new();
                    list.Add(f);
                }
                foreach (var game in unmatched)
                {
                    token.ThrowIfCancellationRequested();
                    var matches = byName.TryGetValue(game.NameKey, out var list) ? list.Where(p => !matched.Contains(Path.GetFullPath(p))).ToArray() : Array.Empty<string>();
                    if (matches.Length == 1)
                    {
                        var p = Path.GetFullPath(matches[0]);
                        if (matched.Add(p)) rows.Add(MakeRow(system, p, sysPath, listPath, game, Length(p)));
                    }
                    else if (matches.Length > 1) rows.Add(new RomRow(system, "", game.Name, 0, game.Name, game.Genre, game.Description, RomStatus.Ambiguous, listPath, game.Path));
                }
            }
            foreach (var f in files)
            {
                token.ThrowIfCancellationRequested(); var p = Path.GetFullPath(f);
                if (matched.Contains(p) || directories.Any(d => PathRules.Inside(p, d))) continue;
                string relativePath = PathRules.Relative(Path.GetRelativePath(sysPath, p));
                accessories.Add(new AccessoryFile(system, p, Length(p), relativePath, PathRules.AccessoryMatchKey(relativePath)));
            }
            var okRows = rows.Skip(rowStart).Where(r => r.Status == RomStatus.Ok).ToArray();
            if (okRows.Length > 0)
            {
                systemCount++;
                foreach (var row in okRows)
                {
                    string ext = Path.GetExtension(row.Path).TrimStart('.').ToUpperInvariant();
                    if (ext.Length > 0) extensionCounts[ext] = extensionCounts.GetValueOrDefault(ext) + 1;
                }
            }
        }
        string cachePath = writeCache ? WriteCache(root, rows) : "Caché pendiente de actualizar";
        progress.Report(new ScanProgress("Escaneo completado.", systems.Length, systems.Length));
        return new ScanResult(rows, accessories, systemFileCounts, gamelists, systemCount, fileCount, extensionCounts, cachePath);
    }
    internal static long CalculateMediaBytes(string root, CancellationToken token)
    {
        string mediaRoot = Path.Combine(Path.GetFullPath(root), "ES-DE", "downloaded_media");
        return Directory.Exists(mediaRoot) ? DirectorySize(mediaRoot, token) : 0;
    }
    internal static string WriteCache(string root, List<RomRow> rows)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ESDERomCleaner");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "database.csv"), temp = Path.Combine(dir, "database.tmp." + Guid.NewGuid().ToString("N") + ".csv");
        static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        try
        {
            using (var writer = new StreamWriter(temp, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("System,RomPath,RomName,SizeBytes,LastWriteTimeUtc,GameName,Genre,Description,Status,GamelistPath,GamelistRomPath,PhysicalRelativePathKey");
                foreach (var row in rows)
                {
                    string lastWrite = !string.IsNullOrWhiteSpace(row.Path) && (File.Exists(row.Path) || Directory.Exists(row.Path)) ? File.GetLastWriteTimeUtc(row.Path).ToString("o") : "";
                    string relative = string.IsNullOrWhiteSpace(row.Path) ? "" : PathRules.Relative(Path.GetRelativePath(Path.Combine(root, "Roms", row.System), row.Path));
                    string state = row.Status == RomStatus.Ok ? "OK" : "AMBIGUO";
                    writer.WriteLine(string.Join(",", new[] {row.System,row.Path,row.RomName,row.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),lastWrite,row.GameName,row.Genre,row.Description,state,row.Gamelist,row.GamelistPath,relative}.Select(Csv)));
                }
            }
            File.Move(temp, path, true);
            return path;
        }
        catch (Exception e) { return "No se pudo guardar: " + e.Message; }
        finally { if (File.Exists(temp)) try { File.Delete(temp); } catch { } }
    }
    private static bool Inside(string path, string root) => path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static RomRow MakeRow(string system, string path, string root, string list, GameEntry game, long size) =>
        new(system, path, Path.GetFileName(path), size, game.Name, game.Genre, game.Description, RomStatus.Ok, list, game.Path);
    private static long Length(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }
    private static long DirectorySize(string path, CancellationToken token)
    {
        long n = 0;
        try { foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) { token.ThrowIfCancellationRequested(); n += Length(f); } }
        catch (UnauthorizedAccessException) { } catch (IOException) { }
        return n;
    }
}
