using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ESDERomCleaner;

// Una de las carpetas que se borran al eliminar un sistema completo.
// En Roms\<sistema> la carpeta no se borra nunca (guarda systeminfo.txt): OnlyFiles son los archivos de su raíz que sí se borran y, si DeleteSubfolders,
// también todas sus subcarpetas. En los sistemas de arcade las subcarpetas (bios, cfg, CHDs…) se conservan.
internal sealed record SystemRemovalFolder(string Label, string Path, int Files, long Bytes, string Detail, IReadOnlyList<string>? OnlyFiles = null, bool DeleteSubfolders = false)
{
    public bool KeepsFolder => OnlyFiles is not null;

    // true si ya no queda nada de lo que se iba a borrar.
    public bool IsGone() => !KeepsFolder ? !Directory.Exists(Path)
        : OnlyFiles!.All(f => !File.Exists(f)) && (!DeleteSubfolders || !Directory.Exists(Path) || !Directory.EnumerateDirectories(Path).Any());
}

// Plan de borrado de un sistema: las tres carpetas que existen y sus recuentos. Si Error no está vacío, no se puede borrar nada.
internal sealed class SystemRemovalPlan
{
    public string Root { get; init; } = "";
    public string System { get; init; } = "";
    public List<SystemRemovalFolder> Folders { get; } = new();
    public string Error { get; set; } = "";
    public int TotalFiles => Folders.Sum(f => f.Files);
    public long TotalBytes => Folders.Sum(f => f.Bytes);
}

internal sealed record SystemRemovalResult(int DeletedFiles, long DeletedBytes, List<string> Errors, string? GamelistBackup);

// Borrado permanente de un sistema completo de la colección: Roms\<sistema>, ES-DE\gamelists\<sistema> y ES-DE\downloaded_media\<sistema>.
// Mismas garantías que DeletionService: solo carpetas dentro de la raíz, sin enlaces simbólicos ni uniones, y se vuelve a comprobar antes de borrar.
internal static class SystemRemovalService
{
    private static readonly string[] Parts = [@"Roms", @"ES-DE\gamelists", @"ES-DE\downloaded_media"];

    // Sistemas de arcade (los que ScreenScraper agrupa en «Arcade»): su carpeta lleva además bios, cfg, CHDs y otros archivos necesarios,
    // así que de ella solo se borran las ROMs de la raíz.
    private static readonly HashSet<string> ArcadeFolders = new(StringComparer.OrdinalIgnoreCase) { "arcade", "mame", "fba", "fbneo", "cps", "cps1", "cps2", "cps3" };

    private static string Combine(string root, string part, string system) => Path.Combine(root, part.Replace('\\', Path.DirectorySeparatorChar), system);

    public static SystemRemovalPlan BuildPlan(string rootPath, string system, CancellationToken token = default)
    {
        var plan = new SystemRemovalPlan { Root = rootPath, System = system };
        if (system.Length == 0 || system is "." or ".." || system.IndexOfAny(['\\', '/', ':']) >= 0 || system.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            plan.Error = "El nombre del sistema no es válido.";
            return plan;
        }
        string root = Path.GetFullPath(rootPath);
        foreach (string part in Parts)
        {
            token.ThrowIfCancellationRequested();
            string partRoot = Path.Combine(root, part.Replace('\\', Path.DirectorySeparatorChar));
            string folder = Combine(root, part, system);
            if (!Directory.Exists(folder)) continue;
            if (!PathRules.Inside(folder, partRoot) || DeletionService.HasReparsePointInPath(folder, partRoot))
            {
                plan.Error = $"La carpeta «{folder}» es un enlace o queda fuera de la colección. No se borra nada.";
                return plan;
            }
            int files = 0; long bytes = 0;
            bool isRoms = part == "Roms";
            bool arcade = isRoms && ArcadeFolders.Contains(system);
            var rootFiles = new List<string>();
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
                if (isRoms)
                {
                    // systeminfo.txt no se borra nunca. En arcade solo se borran las ROMs de la raíz (según las extensiones del sistema, si se conocen).
                    var allowed = arcade ? SystemFormats.AllowedExtensions(root, system) : null;
                    foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
                    {
                        token.ThrowIfCancellationRequested();
                        if ((file.Attributes & FileAttributes.ReparsePoint) != 0) { plan.Error = $"«{file.FullName}» es un enlace simbólico o una unión. No se borra nada."; return plan; }
                        if (file.Name.Equals("systeminfo.txt", StringComparison.OrdinalIgnoreCase)) continue;
                        if (allowed is not null && !allowed.Contains(file.Extension.ToLowerInvariant())) continue;
                        rootFiles.Add(file.FullName); files++; bytes += file.Length;
                    }
                    if (!arcade)
                        foreach (var sub in new DirectoryInfo(folder).EnumerateDirectories())
                        {
                            if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) { plan.Error = $"«{sub.FullName}» es un enlace simbólico o una unión. No se borra nada."; return plan; }
                            foreach (var info in sub.EnumerateFileSystemInfos("*", options))
                            {
                                token.ThrowIfCancellationRequested();
                                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { plan.Error = $"«{info.FullName}» es un enlace simbólico o una unión. No se borra nada."; return plan; }
                                if (info is FileInfo f) { files++; bytes += f.Length; }
                            }
                        }
                }
                else
                    foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
                    {
                        token.ThrowIfCancellationRequested();
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            plan.Error = $"«{info.FullName}» es un enlace simbólico o una unión. No se borra nada.";
                            return plan;
                        }
                        if (info is FileInfo file) { files++; bytes += file.Length; }
                    }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                plan.Error = $"No se pudo leer «{folder}»: {ex.Message}";
                return plan;
            }
            string detail = part switch
            {
                "Roms" => arcade ? $"{files:N0} ROMs de la raíz" : $"{files:N0} archivos",
                @"ES-DE\gamelists" => files == 1 ? "gamelist.xml" : $"{files:N0} archivos",
                _ => $"{files:N0} archivos de medios"
            };
            plan.Folders.Add(isRoms
                ? new SystemRemovalFolder(part, folder, files, bytes, detail, rootFiles, DeleteSubfolders: !arcade)
                : new SystemRemovalFolder(part, folder, files, bytes, detail));
        }
        if (plan.Folders.Count == 0) plan.Error = "El sistema no tiene ninguna carpeta en la colección.";
        return plan;
    }

    // Borra las carpetas del plan. Antes vuelve a comprobar las rutas; si cambia algo, no toca nada.
    public static SystemRemovalResult Execute(SystemRemovalPlan plan, bool backupGamelist, bool keepMedia = false, IProgress<string>? progress = null)
    {
        var errors = new List<string>();
        string root = Path.GetFullPath(plan.Root);
        // Con «conservar medios» la carpeta downloaded_media del sistema se queda donde está.
        var folders = plan.Folders.Where(f => !(keepMedia && f.Label == @"ES-DE\downloaded_media")).ToList();
        foreach (var folder in folders)
        {
            string partRoot = Path.Combine(root, folder.Label.Replace('\\', Path.DirectorySeparatorChar));
            if (!PathRules.Inside(folder.Path, partRoot) || DeletionService.HasReparsePointInPath(folder.Path, partRoot))
                return new SystemRemovalResult(0, 0, ["Ruta insegura, no se borra nada: " + folder.Path], null);
        }

        // Copia del gamelist fuera de la carpeta que se va a borrar (opcional, según la opción general de copias).
        string? backup = null;
        if (backupGamelist)
        {
            string gamelist = Path.Combine(Combine(root, @"ES-DE\gamelists", plan.System), "gamelist.xml");
            try
            {
                if (File.Exists(gamelist))
                {
                    string dir = Path.Combine(AppPaths.Data, "gamelist_backups");
                    Directory.CreateDirectory(dir);
                    backup = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{plan.System}_gamelist.xml");
                    File.Copy(gamelist, backup, overwrite: false);
                }
            }
            catch (Exception ex) { errors.Add($"No se pudo copiar el gamelist: {ex.Message}"); return new SystemRemovalResult(0, 0, errors, null); }
        }

        int deleted = 0; long deletedBytes = 0;
        foreach (var folder in folders)
        {
            progress?.Report($"Eliminando {folder.Path}…");
            try
            {
                if (folder.KeepsFolder)
                {
                    // Roms\<sistema>: la carpeta y su systeminfo.txt se quedan. Se borran las ROMs de la raíz y, salvo en arcade, las subcarpetas.
                    string systemRoot = Path.GetFullPath(folder.Path);
                    foreach (string file in folder.OnlyFiles!)
                    {
                        try
                        {
                            if (!File.Exists(file)) continue;
                            if (!PathRules.Inside(file, systemRoot) || DeletionService.HasReparsePointInPath(file, systemRoot)) { errors.Add("Ruta insegura: " + file); continue; }
                            long size = new FileInfo(file).Length;
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                            deleted++; deletedBytes += size;
                        }
                        catch (Exception ex) { errors.Add($"{file}: {ex.Message}"); }
                    }
                    if (folder.DeleteSubfolders)
                        foreach (string sub in Directory.EnumerateDirectories(systemRoot))
                        {
                            if (DeletionService.HasReparsePointInPath(sub, systemRoot)) { errors.Add("Ruta insegura: " + sub); continue; }
                            foreach (string file in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
                            {
                                try { long size = new FileInfo(file).Length; File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); deleted++; deletedBytes += size; }
                                catch (Exception ex) { errors.Add($"{file}: {ex.Message}"); }
                            }
                            try { Directory.Delete(sub, recursive: true); } catch (Exception ex) { errors.Add($"{sub}: {ex.Message}"); }
                        }
                    continue;
                }
                foreach (string file in Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        long size = new FileInfo(file).Length;
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                        deleted++; deletedBytes += size;
                    }
                    catch (Exception ex) { errors.Add($"{file}: {ex.Message}"); }
                }
                if (errors.Count == 0) Directory.Delete(folder.Path, recursive: true);
                else foreach (string dir in Directory.EnumerateDirectories(folder.Path, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                        try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
            }
            catch (Exception ex) { errors.Add($"{folder.Path}: {ex.Message}"); }
        }
        return new SystemRemovalResult(deleted, deletedBytes, errors, backup);
    }

    // Quita del almacén de idiomas, de RetroAchievements y de las exclusiones del curado lo que pertenece al sistema borrado.
    public static void CleanStores(string system)
    {
        string prefix = system + "|";
        try
        {
            string path = Path.Combine(AppPaths.Data, "rom_languages.json");
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject map)
            {
                foreach (string key in map.Select(p => p.Key).Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList()) map.Remove(key);
                WriteAtomic(path, map);
            }
        }
        catch { }
        try
        {
            string path = Path.Combine(AppPaths.Data, "retroachievements.json");
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject ra)
            {
                foreach (var prop in ra)
                {
                    if (prop.Key.Equals("Matches", StringComparison.OrdinalIgnoreCase) && prop.Value is JsonObject matches)
                        foreach (string key in matches.Select(p => p.Key).Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList()) matches.Remove(key);
                    if (prop.Key.Equals("Systems", StringComparison.OrdinalIgnoreCase) && prop.Value is JsonObject checkedSystems)
                        foreach (string key in checkedSystems.Select(p => p.Key).Where(k => k.Equals(system, StringComparison.OrdinalIgnoreCase)).ToList()) checkedSystems.Remove(key);
                }
                WriteAtomic(path, ra);
            }
        }
        catch { }
        try
        {
            string path = Path.Combine(AppPaths.Data, "curation.json");
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject curation)
            {
                foreach (var prop in curation)
                    if (prop.Key.Equals("Excluded", StringComparison.OrdinalIgnoreCase) && prop.Value is JsonArray excluded)
                        for (int i = excluded.Count - 1; i >= 0; i--)
                            if (excluded[i]?.GetValue<string>() is { } key && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) excluded.RemoveAt(i);
                WriteAtomic(path, curation);
            }
        }
        catch { }
    }

    private static void WriteAtomic(string path, JsonNode node)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, node.ToJsonString(), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) try { File.Delete(temp); } catch { } }
    }
}
