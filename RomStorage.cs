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

    private static string FilePath => Path.Combine(AppPaths.Data, "storage_options.json");

    public static StorageSettings Load()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<StorageSettings>(File.ReadAllText(FilePath)) ?? new StorageSettings(); }
        catch { return new StorageSettings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            string temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(this));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch { }
    }
}

// Extensiones que admite cada sistema de ES-DE: las de su systeminfo.txt si existe (puede estar personalizado) o las oficiales de es_systems.xml.
internal static class SystemFormats
{
    public static HashSet<string>? AllowedExtensions(string root, string folder)
    {
        string info = Path.Combine(root, "Roms", folder, "systeminfo.txt");
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
            text.AppendLine("System name:").AppendLine(folder).AppendLine();
            text.AppendLine("Supported file extensions:");
            text.AppendLine(string.Join(" ", extensions.SelectMany(e => new[] { e, e.ToUpperInvariant() }).Distinct())).AppendLine();
            File.WriteAllText(info, text.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }
}

internal static class RomStorage
{
    private static bool IsArchive(string path) => Path.GetExtension(path).ToLowerInvariant() is ".zip" or ".7z" or ".rar";

    // 7-Zip para crear archivos .7z (.NET no sabe escribirlos): primero el 7za.exe que lleva incorporado la aplicación y, si no está, el 7-Zip instalado.
    public static string? SevenZipPath()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "7z", "7za.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe")
        ];
        foreach (var path in candidates) if (File.Exists(path)) return path;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            try { string path = Path.Combine(dir.Trim(), "7z.exe"); if (File.Exists(path)) return path; } catch { }
        return null;
    }

    // Formato con el que se guardará realmente la ROM: el pedido si el sistema lo admite y se puede hacer; si no, el más parecido que sí, con el motivo.
    public static (StorageFormat Format, string Note) Plan(RomIdentity id, StorageOptions options, HashSet<string>? allowed)
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
    public static void Store(RomIdentity id, string destinationDir, StorageFormat format, StorageLevel level, bool move, CancellationToken token)
    {
        Directory.CreateDirectory(destinationDir);
        switch (format)
        {
            case StorageFormat.Keep:
                foreach (var file in id.Files) CopyAtomic(file, Path.Combine(destinationDir, Path.GetFileName(file)));
                break;
            case StorageFormat.Raw:
                foreach (var file in id.Files)
                {
                    if (IsArchive(file)) ExtractAll(file, destinationDir, token);
                    else CopyAtomic(file, Path.Combine(destinationDir, Path.GetFileName(file)));
                }
                break;
            default:
                Pack(id, destinationDir, format, level, token);
                break;
        }
        if (move) foreach (var file in id.Files) try { File.Delete(file); } catch { }
    }

    private static void CopyAtomic(string source, string target)
    {
        if (File.Exists(target)) return;
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.Copy(source, temp); File.Move(temp, target); }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    // Extrae todos los archivos de un zip/7z/rar a una carpeta, sin subcarpetas y sin sobrescribir.
    private static void ExtractAll(string archive, string directory, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        void Write(string name, Func<Stream> open)
        {
            token.ThrowIfCancellationRequested();
            string target = Path.Combine(directory, Path.GetFileName(name));
            if (Path.GetFileName(name).Length == 0 || File.Exists(target)) return;
            string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var from = open())
                using (var to = File.Create(temp)) from.CopyTo(to);
                File.Move(temp, target);
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
    private static void Pack(RomIdentity id, string destinationDir, StorageFormat format, StorageLevel level, CancellationToken token)
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
            // Archivos a comprimir (nombres sueltos dentro de la carpeta «stage»).
            foreach (var file in id.Files)
            {
                if (IsArchive(file)) ExtractAll(file, stage, token);
                else File.Copy(file, Path.Combine(stage, Path.GetFileName(file)), overwrite: true);
            }
            var names = Directory.EnumerateFiles(stage).Select(Path.GetFileName).Select(n => n!).ToList();
            if (names.Count == 0) throw new InvalidDataException("No hay archivos que comprimir.");

            if (format == StorageFormat.Zip)
            {
                var zipLevel = level switch { StorageLevel.Store => CompressionLevel.NoCompression, StorageLevel.Fast => CompressionLevel.Fastest, StorageLevel.Max => CompressionLevel.SmallestSize, _ => CompressionLevel.Optimal };
                using (var stream = File.Create(temp))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                    foreach (var name in names)
                    {
                        token.ThrowIfCancellationRequested();
                        zip.CreateEntryFromFile(Path.Combine(stage, name), name, zipLevel);
                    }
            }
            else
            {
                string exe = SevenZipPath() ?? throw new FileNotFoundException("No se encuentra 7-Zip (7z.exe).");
                int mx = level switch { StorageLevel.Store => 0, StorageLevel.Fast => 1, StorageLevel.Max => 9, _ => 5 };
                var start = new ProcessStartInfo(exe) { WorkingDirectory = stage, CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "a", "-t7z", $"-mx={mx}", "-y", "-bso0", "-bsp0", temp }.Concat(names)) start.ArgumentList.Add(arg);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("No se pudo ejecutar 7-Zip.");
                process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException($"7-Zip terminó con error {process.ExitCode}. {error}".Trim());
            }
            File.Move(temp, target);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            try { Directory.Delete(stage, recursive: true); } catch { }
        }
    }
}
