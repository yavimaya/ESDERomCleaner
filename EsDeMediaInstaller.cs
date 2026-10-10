namespace ESDERomCleaner;

internal sealed record EsDeMediaFolder(string Folder, string Description, string Kind, string[] SsTypes);

internal sealed record MediaInstallRequest(string Folder, SsMedia Media, string SourcePath);

internal sealed record MediaInstallResult(string Folder, SsMedia Media, string TargetPath, List<string> ReplacedFiles, List<string> BackupFiles);

// Guarda medios descargados en ES-DE/downloaded_media. ES-DE no lee rutas de medios del gamelist.xml:
// los localiza por el nombre del archivo de la ROM (sin extensión) y su subcarpeta relativa dentro de Roms/<sistema>.
internal static class EsDeMediaInstaller
{
    // Orden de los tipos = preferencia al proponer un candidato por defecto.
    public static readonly EsDeMediaFolder[] Folders =
    [
        new("covers", "Carátula frontal", "image", ["box-2D"]),
        new("backcovers", "Carátula trasera", "image", ["box-2D-back"]),
        new("3dboxes", "Caja 3D", "image", ["box-3D"]),
        new("physicalmedia", "Soporte físico (cartucho, disco)", "image", ["support-2D"]),
        new("screenshots", "Captura de pantalla", "image", ["ss"]),
        new("titlescreens", "Pantalla de título", "image", ["sstitle"]),
        new("marquees", "Logo / marquee", "image", ["wheel", "wheel-hd", "wheel-carbon", "wheel-steel", "marquee", "screenmarquee", "screenmarqueesmall"]),
        new("miximages", "Miximage", "image", ["mixrbv2", "mixrbv1"]),
        new("fanart", "Fan art", "image", ["fanart"]),
        new("videos", "Vídeo", "video", ["video-normalized", "video"]),
        new("manuals", "Manual", "pdf", ["manuel"])
    ];

    private static readonly string[] ImageFormats = ["png", "jpg", "jpeg"];
    private static readonly string[] VideoFormats = ["mp4", "m4v", "mkv", "avi", "mov", "webm", "wmv", "mpg", "mpeg", "ogv"];
    private static readonly string[] MediaExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".webm", ".wmv", ".mpg", ".mpeg", ".ogv", ".pdf"];

    public static string BackupRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ESDERomCleaner", "media_backups");

    // Solo formatos que ES-DE sabe mostrar en esa carpeta (por ejemplo, nada de SVG en carátulas).
    public static bool Accepts(EsDeMediaFolder folder, SsMedia media)
    {
        if (!media.IsGameMedia || !folder.SsTypes.Contains(media.Type, StringComparer.OrdinalIgnoreCase)) return false;
        string format = media.Format.ToLowerInvariant();
        return folder.Kind switch
        {
            "image" => ImageFormats.Contains(format),
            "video" => VideoFormats.Contains(format),
            "pdf" => format == "pdf",
            _ => false
        };
    }

    public static string SystemMediaRoot(string root, string system) => Path.Combine(Path.GetFullPath(root), "ES-DE", "downloaded_media", system);

    // Ruta sin extensión: downloaded_media/<sistema>/<carpeta>/<subcarpetas de la ROM>/<nombre de la ROM sin extensión>.
    public static string TargetBase(string root, RomRow row, string folder)
    {
        string romsRoot = Path.Combine(Path.GetFullPath(root), "Roms", row.System);
        string romPath = Path.GetFullPath(row.Path);
        if (!PathRules.Inside(romPath, romsRoot))
            throw new InvalidOperationException($"La ROM no está dentro de {romsRoot}; no se puede deducir dónde guardar sus medios.");
        string relativeDir = Path.GetDirectoryName(Path.GetRelativePath(romsRoot, romPath)) ?? "";
        string stem = Path.GetFileNameWithoutExtension(romPath);
        return Path.Combine(SystemMediaRoot(root, row.System), folder, relativeDir, stem);
    }

    public static bool IsMediaFile(string path) => MediaExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // Medios que ES-DE ya tiene para esa ROM en esa carpeta (mismo nombre, cualquier extensión de medio).
    public static List<string> Existing(string targetBase)
    {
        string? dir = Path.GetDirectoryName(targetBase);
        if (dir is null || !Directory.Exists(dir)) return new();
        string stem = Path.GetFileName(targetBase);
        try
        {
            return Directory.EnumerateFiles(dir)
                .Where(f => Path.GetFileNameWithoutExtension(f).Equals(stem, StringComparison.OrdinalIgnoreCase) &&
                            MediaExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new(); }
    }

    // Instala cada medio: copia a un temporal junto al destino, aparta los medios existentes a la copia de seguridad
    // y mueve el temporal a su sitio. Si algo falla en un medio, se restauran sus archivos apartados.
    public static void Install(string root, RomRow row, IReadOnlyList<MediaInstallRequest> requests, List<MediaInstallResult> results, List<string> errors, IProgress<string>? progress, CancellationToken token, bool keepBackup = true)
    {
        string mediaRoot = Path.Combine(Path.GetFullPath(root), "ES-DE", "downloaded_media");
        string systemMediaRoot = SystemMediaRoot(root, row.System);
        if (!Directory.Exists(mediaRoot)) throw new InvalidOperationException($"No existe la carpeta de medios de ES-DE: {mediaRoot}");
        if (DeletionService.HasReparsePointInPath(systemMediaRoot, mediaRoot))
            throw new InvalidOperationException($"La carpeta de medios del sistema es un enlace simbólico o unión; no se escribe en ella: {systemMediaRoot}");

        string backupDir = Path.Combine(BackupRoot, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..24]);
        foreach (var request in requests)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report($"Guardando {request.Folder}…");
            try { results.Add(InstallOne(root, row, request, systemMediaRoot, backupDir, keepBackup)); }
            catch (Exception ex) { errors.Add($"{request.Folder}: {ex.Message}"); }
        }
    }

    private static MediaInstallResult InstallOne(string root, RomRow row, MediaInstallRequest request, string systemMediaRoot, string backupDir, bool keepBackup)
    {
        string targetBase = TargetBase(root, row, request.Folder);
        string target = targetBase + "." + request.Media.Format.ToLowerInvariant();
        string targetDir = Path.GetDirectoryName(target)!;
        if (!PathRules.Inside(target, systemMediaRoot) || DeletionService.HasReparsePointInPath(targetDir, systemMediaRoot))
            throw new InvalidOperationException($"Ruta de destino insegura (fuera del sistema o con enlaces): {target}");
        if (!File.Exists(request.SourcePath)) throw new FileNotFoundException("El medio descargado ya no está en la caché.", request.SourcePath);

        Directory.CreateDirectory(targetDir);
        if (DeletionService.HasReparsePointInPath(targetDir, systemMediaRoot))
            throw new InvalidOperationException($"Ruta de destino insegura (enlace simbólico o unión): {targetDir}");

        string temp = Path.Combine(targetDir, $".{Path.GetFileName(targetBase)}.{Guid.NewGuid():N}.tmp");
        var moved = new List<(string Original, string Backup)>();
        var backups = new List<string>();
        try
        {
            File.Copy(request.SourcePath, temp, overwrite: false);
            if (new FileInfo(temp).Length != new FileInfo(request.SourcePath).Length)
                throw new IOException("La copia al destino quedó incompleta.");

            foreach (string existing in Existing(targetBase))
            {
                string relative = Path.GetRelativePath(systemMediaRoot, existing);
                // Con copia de seguridad el archivo se aparta a media_backups; sin ella, a un archivo oculto temporal junto al original
                // (para poder deshacer si algo falla) y no se crea ninguna carpeta de copias.
                string backup;
                if (keepBackup)
                {
                    backup = Path.Combine(backupDir, row.System, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                }
                else backup = Path.Combine(Path.GetDirectoryName(existing)!, "." + Path.GetFileName(existing) + "." + Guid.NewGuid().ToString("N") + ".old");
                File.Move(existing, backup, overwrite: false);
                moved.Add((existing, backup));
                backups.Add(backup);
            }
            File.Move(temp, target, overwrite: false);
            // Sin copia de seguridad, lo sustituido se elimina una vez el medio nuevo ya está en su sitio.
            if (!keepBackup)
            {
                foreach (var (_, backupFile) in moved) try { File.Delete(backupFile); } catch { }
                backups.Clear();
            }
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            foreach (var (original, backup) in moved)
                try { if (!File.Exists(original)) File.Move(backup, original); } catch { }
            throw;
        }
        return new MediaInstallResult(request.Folder, request.Media, target, moved.Select(m => m.Original).ToList(), backups);
    }
}
