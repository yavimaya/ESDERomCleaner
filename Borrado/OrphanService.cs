using System.Security.Cryptography;

namespace ESDERomCleaner;

internal sealed record OrphanFile(string System, string Path, long Size);

// Gamelist con entradas <game> sin ROM. Si al quitarlas no queda ninguna, el gamelist entero se elimina (WholeFile).
internal sealed record OrphanGamelist(string System, string Path, string Sha256, HashSet<string> OrphanKeys, int OrphanCount, int TotalGames, bool WholeFile);

// Resultado de buscar huérfanos: medios sin ROM, entradas de gamelist sin ROM y carpetas de medios de sistemas que ya no tienen ROMs.
internal sealed class OrphanScan
{
    public string Root { get; init; } = "";
    public List<OrphanFile> Media { get; } = new();
    public List<OrphanGamelist> Gamelists { get; } = new();
    public List<string> EmptySystemMediaFolders { get; } = new();
    public List<OrphanFile> EmptySystemMedia { get; } = new();
    public int EntryCount => Gamelists.Sum(g => g.OrphanCount);
    public int WholeGamelists => Gamelists.Count(g => g.WholeFile);
}

internal sealed record OrphanResult(int MediaDeleted, long MediaBytes, int EntriesRemoved, int GamelistsDeleted, int FoldersDeleted, int FolderFiles, long FolderBytes, List<string> Errors);

// Huérfano = lo que no tiene correspondencia con una ROM real de Roms\<sistema>. Reglas conservadoras: un medio solo es huérfano si ningún archivo
// ni carpeta del sistema comparte su ruta (sin extensión) y solo se miran las carpetas de medios que ES-DE lee; una entrada <game> solo es huérfana si
// ni su ruta ni su nombre normalizado coinciden con nada de Roms\<sistema> (el mismo criterio que usa el análisis).
internal static class OrphanService
{
    private static string Ext(string path)
    {
        int slash = path.LastIndexOf('/');
        int dot = path.LastIndexOf('.');
        return dot > slash ? path[..dot] : path;
    }

    // Sistemas que aparecen en alguna de las tres carpetas (Roms, gamelists, medios).
    public static List<string> SystemNames(string rootPath)
    {
        string root = Path.GetFullPath(rootPath);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in new[] { "Roms", @"ES-DE\gamelists", @"ES-DE\downloaded_media" })
        {
            string dir = Path.Combine(root, part.Replace('\\', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir)) continue;
            try { foreach (string sub in Directory.EnumerateDirectories(dir)) names.Add(Path.GetFileName(sub)); } catch { }
        }
        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<FileSystemInfo> Entries(string dir, CancellationToken token)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
        {
            token.ThrowIfCancellationRequested();
            yield return info;
        }
    }

    public static OrphanScan Scan(string rootPath, IReadOnlyCollection<string> systems, IProgress<string>? progress, CancellationToken token)
    {
        string root = Path.GetFullPath(rootPath);
        var scan = new OrphanScan { Root = root };
        string romsRoot = Path.Combine(root, "Roms");
        string listsRoot = Path.Combine(root, "ES-DE", "gamelists");
        string mediaRoot = Path.Combine(root, "ES-DE", "downloaded_media");
        int index = 0;
        foreach (string system in systems)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report($"Buscando huérfanos: {system} ({++index}/{systems.Count})");
            string romsDir = Path.Combine(romsRoot, system);
            string listPath = Path.Combine(listsRoot, system, "gamelist.xml");
            string mediaDir = Path.Combine(mediaRoot, system);

            // Lo que existe de verdad en Roms\<sistema>.
            var liveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // rutas relativas, con y sin extensión
            var physical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // rutas relativas tal cual
            var liveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // nombres normalizados
            bool hasRoms = false;
            if (Directory.Exists(romsDir) && !DeletionService.HasReparsePointInPath(romsDir, romsRoot))
            {
                foreach (var info in Entries(romsDir, token))
                {
                    string rel = PathRules.Relative(Path.GetRelativePath(romsDir, info.FullName));
                    if (rel.Length == 0) continue;
                    if (info is FileInfo) hasRoms = true;
                    physical.Add(rel);
                    liveKeys.Add(rel); liveKeys.Add(Ext(rel));
                    liveNames.Add(PathRules.Name(info.Name));
                }
            }

            // Entradas <game> sin ROM.
            if (File.Exists(listPath) && !DeletionService.HasReparsePointInPath(listPath, listsRoot))
            {
                try
                {
                    var (total, paths) = DeletionService.ReadGamePaths(listPath);
                    var orphanKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int orphanCount = 0;
                    var seenKeys = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    foreach (string raw in paths)
                    {
                        string key = PathRules.Relative(raw);
                        if (key.Length == 0) continue;
                        if (!seenKeys.TryGetValue(key, out bool orphan))
                            seenKeys[key] = orphan = !physical.Contains(key) && !liveNames.Contains(PathRules.Name(raw));
                        if (orphan) { orphanKeys.Add(key); orphanCount++; }
                    }
                    bool whole = total - orphanCount == 0 && (orphanCount > 0 || !hasRoms);
                    if (orphanCount > 0 || whole)
                        scan.Gamelists.Add(new OrphanGamelist(system, listPath, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(listPath))), orphanKeys, orphanCount, total, whole));
                }
                catch { /* un gamelist que no se puede leer no se toca */ }
            }

            // Medios.
            if (!Directory.Exists(mediaDir) || DeletionService.HasReparsePointInPath(mediaDir, mediaRoot)) continue;
            if (!hasRoms)
            {
                // Sistema sin ROMs: toda su carpeta de medios sobra.
                scan.EmptySystemMediaFolders.Add(mediaDir);
                foreach (var info in Entries(mediaDir, token))
                    if (info is FileInfo f) scan.EmptySystemMedia.Add(new OrphanFile(system, f.FullName, f.Length));
                continue;
            }
            foreach (string folder in EsDeStructure.MediaFolders)
            {
                string folderDir = Path.Combine(mediaDir, folder);
                if (!Directory.Exists(folderDir) || DeletionService.HasReparsePointInPath(folderDir, mediaDir)) continue;
                foreach (var info in Entries(folderDir, token))
                {
                    if (info is not FileInfo file) continue;
                    string key = PathRules.Relative(Path.GetRelativePath(folderDir, file.FullName));
                    if (key.Length == 0 || liveKeys.Contains(key) || liveKeys.Contains(Ext(key))) continue;
                    scan.Media.Add(new OrphanFile(system, file.FullName, file.Length));
                }
            }
        }
        return scan;
    }

    // Borra lo marcado. Los gamelists se vuelven a comprobar por hash: si cambiaron desde la búsqueda, no se tocan.
    public static OrphanResult Execute(OrphanScan scan, bool media, bool entries, bool emptyFolders, bool backupGamelists, IProgress<string>? progress)
    {
        var errors = new List<string>();
        string root = scan.Root;
        string listsRoot = Path.Combine(root, "ES-DE", "gamelists");
        string mediaRoot = Path.Combine(root, "ES-DE", "downloaded_media");
        int mediaDeleted = 0, entriesRemoved = 0, listsDeleted = 0, foldersDeleted = 0, folderFiles = 0;
        long mediaBytes = 0, folderBytes = 0;

        if (media)
        {
            var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in scan.Media)
            {
                try
                {
                    string systemMedia = Path.Combine(mediaRoot, file.System);
                    if (!PathRules.Inside(file.Path, systemMedia) || DeletionService.HasReparsePointInPath(file.Path, systemMedia)) { errors.Add("Ruta insegura: " + file.Path); continue; }
                    if (!File.Exists(file.Path)) continue;
                    File.SetAttributes(file.Path, FileAttributes.Normal);
                    File.Delete(file.Path);
                    mediaDeleted++; mediaBytes += file.Size;
                    if (Path.GetDirectoryName(file.Path) is { } parent) parents.Add(parent);
                    if (mediaDeleted % 200 == 0) progress?.Report($"Borrando medios huérfanos: {mediaDeleted:N0}");
                }
                catch (Exception ex) { errors.Add($"{file.Path}: {ex.Message}"); }
            }
            // Las carpetas que se quedan vacías se quitan (hasta la carpeta del sistema, que también si queda vacía).
            foreach (string parent in parents.OrderByDescending(p => p.Length))
            {
                string dir = parent;
                while (PathRules.Inside(dir, mediaRoot) && !dir.TrimEnd('\\').Equals(mediaRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    try { if (!Directory.Exists(dir)) { dir = Path.GetDirectoryName(dir)!; continue; } if (Directory.EnumerateFileSystemEntries(dir).Any()) break; Directory.Delete(dir); }
                    catch { break; }
                    dir = Path.GetDirectoryName(dir)!;
                }
            }
        }

        if (entries)
        {
            foreach (var list in scan.Gamelists)
            {
                try
                {
                    if (!File.Exists(list.Path) || !PathRules.Inside(list.Path, listsRoot) || DeletionService.HasReparsePointInPath(list.Path, listsRoot)) { errors.Add("Gamelist inseguro o inexistente: " + list.Path); continue; }
                    if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(list.Path))).Equals(list.Sha256, StringComparison.Ordinal)) { errors.Add("El gamelist cambió desde la búsqueda, no se toca: " + list.Path); continue; }
                    progress?.Report("Gamelist: " + list.Path);
                    if (backupGamelists)
                    {
                        string dir = Path.Combine(AppPaths.Data, "gamelist_backups");
                        Directory.CreateDirectory(dir);
                        File.Copy(list.Path, Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{list.System}_gamelist.xml"), overwrite: false);
                    }
                    if (list.WholeFile)
                    {
                        File.SetAttributes(list.Path, FileAttributes.Normal);
                        File.Delete(list.Path);
                        listsDeleted++; entriesRemoved += list.OrphanCount;
                        string folder = Path.GetDirectoryName(list.Path)!;
                        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
                    }
                    else
                    {
                        var (removed, remaining) = DeletionService.RemoveGamesByPath(list.Path, list.OrphanKeys);
                        entriesRemoved += removed;
                        // Si por el camino se quedó sin juegos, el gamelist sobra.
                        if (remaining == 0)
                        {
                            File.Delete(list.Path); listsDeleted++;
                            string folder = Path.GetDirectoryName(list.Path)!;
                            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
                        }
                    }
                }
                catch (Exception ex) { errors.Add($"{list.Path}: {ex.Message}"); }
            }
        }

        if (emptyFolders)
        {
            foreach (string folder in scan.EmptySystemMediaFolders)
            {
                try
                {
                    if (!PathRules.Inside(folder, mediaRoot) || DeletionService.HasReparsePointInPath(folder, mediaRoot)) { errors.Add("Ruta insegura: " + folder); continue; }
                    if (!Directory.Exists(folder)) continue;
                    progress?.Report("Borrando carpeta de medios: " + folder);
                    foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    {
                        try { long size = new FileInfo(file).Length; File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); folderFiles++; folderBytes += size; }
                        catch (Exception ex) { errors.Add($"{file}: {ex.Message}"); }
                    }
                    Directory.Delete(folder, recursive: true);
                    foldersDeleted++;
                }
                catch (Exception ex) { errors.Add($"{folder}: {ex.Message}"); }
            }
        }
        return new OrphanResult(mediaDeleted, mediaBytes, entriesRemoved, listsDeleted, foldersDeleted, folderFiles, folderBytes, errors);
    }
}
