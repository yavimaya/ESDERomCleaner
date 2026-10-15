using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using SharpCompress.Archives;

namespace ESDERomCleaner;

// Cómo se guardan las ROMs al añadirlas a la colección.
internal enum StorageFormat { Keep, Raw, Zip, SevenZip }
internal enum StorageLevel { Store, Fast, Normal, Max }
internal sealed record StorageOptions(StorageFormat Format, StorageLevel Level);

// Preferencias de almacenamiento (se recuerdan entre sesiones, en la carpeta de datos).
internal sealed class StorageSettings
{
    public StorageFormat Format { get; set; } = StorageFormat.Keep;
    public StorageLevel Level { get; set; } = StorageLevel.Normal;
    // Al añadir ROMs de arcade, omitir los clones cuyo juego padre ya esté (en el lote o en la colección).
    public bool SkipClones { get; set; }
    // Una duplicada cuyo contenido resulta distinto del existente se pasa sola a identificadas (reemplazará al existente al añadirla).
    public bool AddDifferentDuplicates { get; set; }

    public static StorageSettings Load()
    {
        return AppSettings.Get("Storage", new StorageSettings());
    }

    public void Save()
    {
        AppSettings.Set("Storage", this);
    }
}

// Extensiones que admite cada sistema de ES-DE: las de su systeminfo.txt si existe (puede estar personalizado) o las oficiales de es_systems.xml.
internal static class SystemFormats
{
    // Caché por sistema: se vuelve a leer solo si cambia la fecha de su systeminfo.txt (se consulta una vez por fila y por cambio de formato).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Stamp, HashSet<string>? Set)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static HashSet<string>? AllowedExtensions(string root, string folder)
    {
        string info = Path.Combine(root, "Roms", folder, "systeminfo.txt");
        long stamp = File.Exists(info) ? File.GetLastWriteTimeUtc(info).Ticks : 0;
        string key = root + "|" + folder;
        if (Cache.TryGetValue(key, out var cached) && cached.Stamp == stamp) return cached.Set;
        var set = ReadAllowed(info, folder);
        Cache[key] = (stamp, set);
        return set;
    }

    private static HashSet<string>? ReadAllowed(string info, string folder)
    {
        try
        {
            if (File.Exists(info))
            {
                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool inBlock = false;
                foreach (var raw in File.ReadLines(info))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("Supported file extensions", StringComparison.OrdinalIgnoreCase)) { inBlock = true; continue; }
                    if (!inBlock) continue;
                    if (line.Length == 0) { if (found.Count > 0) break; continue; }
                    foreach (var token in line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                        if (token.StartsWith('.')) found.Add(token.ToLowerInvariant());
                }
                if (found.Count > 0) return found;
            }
        }
        catch { /* si no se puede leer, se usa la lista oficial */ }
        return EsDeSystemExtensions.BySystem.TryGetValue(folder, out var official) ? new HashSet<string>(official, StringComparer.OrdinalIgnoreCase) : null;
    }

    // Crea el systeminfo.txt de una carpeta de sistema si falta (ES-DE lo genera al arrancar; aquí se deja listo para poder consultar las extensiones).
    public static void EnsureSystemInfo(string root, string folder)
    {
        try
        {
            string dir = Path.Combine(root, "Roms", folder);
            string info = Path.Combine(dir, "systeminfo.txt");
            if (File.Exists(info) || !EsDeSystemExtensions.BySystem.TryGetValue(folder, out var extensions)) return;
            Directory.CreateDirectory(dir);
            var text = new StringBuilder();
            // Mismo formato que el que genera ES-DE; se omiten los comandos de lanzamiento, que dependen de la plataforma y del emulador de cada usuario.
            EsDeSystemInfo.BySystem.TryGetValue(folder, out var info2);
            text.AppendLine("System name:").AppendLine(folder).AppendLine();
            if (info2.FullName is { Length: > 0 }) text.AppendLine("Full system name:").AppendLine(info2.FullName).AppendLine();
            text.AppendLine("Supported file extensions:");
            text.AppendLine(string.Join(" ", extensions.SelectMany(e => new[] { e, e.ToUpperInvariant() }).Distinct())).AppendLine();
            if (info2.Platform is { Length: > 0 }) text.AppendLine("Platform (for scraping):").AppendLine(info2.Platform).AppendLine();
            if (info2.Theme is { Length: > 0 }) text.AppendLine("Theme folder:").AppendLine(info2.Theme);
            File.WriteAllText(info, text.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    // Crea Roms\systems.txt si falta: la lista «carpeta: nombre completo» de todos los sistemas que ES-DE genera al crear la estructura (orden alfabético, un sistema por línea).
    public static void EnsureSystemsList(string root, IEnumerable<string> folders)
    {
        try
        {
            string file = Path.Combine(root, "Roms", "systems.txt");
            if (File.Exists(file)) return;
            var lines = folders.Where(f => EsDeSystemInfo.BySystem.TryGetValue(f, out var i) && i.FullName.Length > 0)
                .OrderBy(f => f, StringComparer.Ordinal).Select(f => f + ": " + EsDeSystemInfo.BySystem[f].FullName);
            File.WriteAllText(file, string.Join((char)10, lines) + (char)10, new UTF8Encoding(false));
        }
        catch { }
    }
}

internal static class RomStorage
{
    private static bool IsArchive(string path) => Path.GetExtension(path).ToLowerInvariant() is ".zip" or ".7z" or ".rar";

    // 7-Zip para crear archivos .7z (.NET no sabe escribirlos): primero el 7za.exe que lleva incorporado la aplicación y, si no está, el 7-Zip instalado.
    private static readonly Lazy<string?> SevenZip = new(FindSevenZip);
    public static string? SevenZipPath() => SevenZip.Value;

    private static string? FindSevenZip()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "7z", "7za.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe")
        ];
        foreach (var path in candidates) if (File.Exists(path)) return path;
        return null;
    }

    // Si hay que guardar sin comprimir pero el comprimido lleva varios archivos de ROM (o subcarpetas), extraerlo aplanado mezclaría archivos y podría
    // perder alguno con el mismo nombre: se guarda tal como llega.
    public static (StorageFormat Format, string Note) Plan(RomIdentity id, StorageOptions options, HashSet<string>? allowed)
    {
        var (format, note) = PlanCore(id, options, allowed);
        if (format == StorageFormat.Raw && id.Files.Any(f => IsArchive(f) && IsMultiFile(f)))
            return (StorageFormat.Keep, "tiene varios archivos: se guarda comprimido, como llega");
        return (format, note);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> MultiFileCache = new(StringComparer.OrdinalIgnoreCase);

    private static bool IsMultiFile(string archive) => MultiFileCache.GetOrAdd(archive, path =>
    {
        try
        {
            var names = new List<string>();
            if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(path);
                names.AddRange(zip.Entries.Where(e => e.Name.Length > 0).Select(e => e.FullName));
            }
            else
            {
                using var a = ArchiveFactory.OpenArchive(path);
                names.AddRange(a.Entries.Where(e => !e.IsDirectory).Select(e => e.Key ?? ""));
            }
            if (names.Any(n => n.Contains('/') || n.Contains('\\'))) return true;
            return names.Count(n => !GamelistGenerator.IsNotRom(n)) > 1;
        }
        catch { return false; }
    });

    // Formato con el que se guardará realmente la ROM: el pedido si el sistema lo admite y se puede hacer; si no, el más parecido que sí, con el motivo.
    private static (StorageFormat Format, string Note) PlanCore(RomIdentity id, StorageOptions options, HashSet<string>? allowed)
    {
        bool Admits(string ext) => allowed is null || allowed.Contains(ext);
        string source = Path.GetExtension(id.Path).ToLowerInvariant();
        bool sourceIsArchive = IsArchive(id.Path);

        StorageFormat wanted = options.Format;
        if (wanted == StorageFormat.SevenZip && SevenZipPath() is null)
        {
            if (Admits(".zip")) return (StorageFormat.Zip, "no se encuentra 7za.exe: se guarda en zip");
            wanted = StorageFormat.Raw;
        }
        if (wanted == StorageFormat.SevenZip && !Admits(".7z"))
            return Admits(".zip") ? (StorageFormat.Zip, "el sistema no admite .7z: se guarda en zip") : (StorageFormat.Raw, "el sistema no admite .7z ni .zip: se guarda sin comprimir");
        if (wanted == StorageFormat.Zip && !Admits(".zip"))
            return (StorageFormat.Raw, "el sistema no admite .zip: se guarda sin comprimir");
        if (wanted == StorageFormat.Keep)
            return (StorageFormat.Keep, sourceIsArchive && !Admits(source) ? $"el sistema no admite {source}" : "");
        return (wanted, "");
    }

    public static string Describe(StorageFormat format, StorageLevel level) => format switch
    {
        StorageFormat.Zip => $"zip ({LevelName(level)})",
        StorageFormat.SevenZip => $"7z ({LevelName(level)})",
        StorageFormat.Raw => "sin comprimir",
        _ => "como llega"
    };

    public static string LevelName(StorageLevel level) => level switch { StorageLevel.Store => "sin compresión", StorageLevel.Fast => "rápida", StorageLevel.Max => "máxima", _ => "normal" };

    // Guarda la ROM en la carpeta del sistema con el formato indicado. Si «move», borra los originales al terminar con éxito.
    // Devuelve los archivos que se han creado en la carpeta de destino (para saber después cuáles son las ROMs recién añadidas).
    // Sustitución de un archivo de la colección por la ROM nueva, sin copia de seguridad. Antes de guardar la nueva, el archivo existente se aparta con otro nombre
    // en su misma carpeta (HoldExisting); si el guardado sale bien se borra (ReleaseHeld) y si falla se devuelve a su sitio (RestoreHeld), así no se pierde nada
    // por un error a medias. Solo se toca un archivo dentro de la carpeta del sistema y sin enlaces simbólicos en el camino.
    public static string? HoldExisting(string root, string folder, string existing)
    {
        string systemDir = Path.GetFullPath(Path.Combine(root, "Roms", folder));
        if (!File.Exists(existing)) return null;
        if (!PathRules.Inside(existing, systemDir)) throw new InvalidOperationException("El archivo que se iba a sustituir no está en la carpeta del sistema.");
        if (DeletionService.HasReparsePointInPath(existing, systemDir)) throw new InvalidOperationException("El archivo que se iba a sustituir está tras un enlace simbólico o unión.");
        string held = existing + ".replacing_" + Guid.NewGuid().ToString("N")[..8];
        File.Move(existing, held);
        return held;
    }

    public static void ReleaseHeld(string held) { try { File.Delete(held); } catch { } }

    public static void RestoreHeld(string held, string original)
    {
        try { if (!File.Exists(original) && File.Exists(held)) File.Move(held, original); } catch { }
    }

    public static List<string> Store(RomIdentity id, string destinationDir, StorageFormat format, StorageLevel level, bool move, CancellationToken token)
    {
        Directory.CreateDirectory(destinationDir);
        // Cada acción anota los archivos que crea ella misma: así se puede llamar en paralelo sobre la misma carpeta sin mezclar resultados.
        var created = new List<string>();
        StoreCore(id, destinationDir, format, level, move, token, created);
        return created;
    }

    private static void StoreCore(RomIdentity id, string destinationDir, StorageFormat format, StorageLevel level, bool move, CancellationToken token, List<string> created)
    {
        switch (format)
        {
            case StorageFormat.Keep:
                foreach (var file in id.Files) CopyAtomic(file, Path.Combine(destinationDir, Path.GetFileName(file)), created);
                break;
            case StorageFormat.Raw:
                foreach (var file in id.Files)
                {
                    if (IsArchive(file)) ExtractAll(file, destinationDir, token, created);
                    else CopyAtomic(file, Path.Combine(destinationDir, Path.GetFileName(file)), created);
                }
                break;
            default:
                Pack(id, destinationDir, format, level, token, created);
                break;
        }
        if (move) foreach (var file in id.Files) try { File.Delete(file); } catch { }
    }

    private static void CopyAtomic(string source, string target, List<string> created)
    {
        if (File.Exists(target)) return;
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.Copy(source, temp); File.Move(temp, target); created.Add(target); }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    // Extrae todos los archivos de un zip/7z/rar a una carpeta, sin subcarpetas y sin sobrescribir.
    private static void ExtractAll(string archive, string directory, CancellationToken token, List<string>? created = null)
    {
        Directory.CreateDirectory(directory);
        long budget = ArchiveExpander.MaxExtractBytes;
        void Write(string name, Func<Stream> open)
        {
            token.ThrowIfCancellationRequested();
            string target = Path.Combine(directory, Path.GetFileName(name));
            if (Path.GetFileName(name).Length == 0 || ArchiveExpander.IsBlocked(name) || File.Exists(target)) return;
            string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var from = open())
                using (var to = File.Create(temp, 1 << 20)) ArchiveExpander.CopyLimited(from, to, ref budget);
                File.Move(temp, target);
                created?.Add(target);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }
        if (Path.GetExtension(archive).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var e in zip.Entries.Where(e => e.Name.Length > 0)) Write(e.Name, e.Open);
        }
        else
        {
            using var sevenZip = ArchiveFactory.OpenArchive(archive);
            foreach (var e in sevenZip.Entries.Where(e => !e.IsDirectory))
            {
                var entry = e;
                Write(entry.Key ?? "", () => entry.OpenEntryStream());
            }
        }
    }

    // Crea un zip o un 7z con los archivos de la ROM (si venía comprimida, se extrae antes a una carpeta temporal y se recomprime).
    private static void Pack(RomIdentity id, string destinationDir, StorageFormat format, StorageLevel level, CancellationToken token, List<string> created)
    {
        string primary = id.Files[0];
        string baseName = Path.GetFileNameWithoutExtension(primary);
        string target = Path.Combine(destinationDir, baseName + (format == StorageFormat.Zip ? ".zip" : ".7z"));
        if (File.Exists(target)) return;

        string stage = Path.Combine(AppPaths.Temp, "store", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Archivos a comprimir: los sueltos se comprimen desde su sitio (sin copiarlos antes); solo lo que viene comprimido se extrae a «stage».
            var entries = new List<(string Path, string Name)>();
            foreach (var file in id.Files)
            {
                if (IsArchive(file)) ExtractAll(file, stage, token);
                else entries.Add((file, Path.GetFileName(file)));
            }
            entries.AddRange(Directory.EnumerateFiles(stage).Select(f => (f, Path.GetFileName(f))));
            if (entries.Count == 0) throw new InvalidDataException("No hay archivos que comprimir.");

            if (format == StorageFormat.Zip)
            {
                var zipLevel = level switch { StorageLevel.Store => CompressionLevel.NoCompression, StorageLevel.Fast => CompressionLevel.Fastest, StorageLevel.Max => CompressionLevel.SmallestSize, _ => CompressionLevel.Optimal };
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                    foreach (var (path, name) in entries)
                    {
                        token.ThrowIfCancellationRequested();
                        zip.CreateEntryFromFile(path, name, zipLevel);
                    }
            }
            else
            {
                string exe = SevenZipPath() ?? throw new FileNotFoundException("No se encuentra 7-Zip (7z.exe).");
                int mx = level switch { StorageLevel.Store => 0, StorageLevel.Fast => 1, StorageLevel.Max => 9, _ => 5 };
                var start = new ProcessStartInfo(exe) { WorkingDirectory = stage, CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                // Varias ROMs se comprimen a la vez: cada 7-Zip usa pocos hilos para no saturar el equipo. Con rutas completas, 7-Zip guarda solo el nombre del archivo.
                foreach (var arg in new[] { "a", "-t7z", $"-mx={mx}", "-mmt=2", "-y", "-bso0", "-bsp0", temp }.Concat(entries.Select(e => e.Path))) start.ArgumentList.Add(arg);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("No se pudo ejecutar 7-Zip.");
                process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException($"7-Zip terminó con error {process.ExitCode}. {error}".Trim());
            }
            File.Move(temp, target);
            created.Add(target);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            try { Directory.Delete(stage, recursive: true); } catch { }
        }
    }
}
