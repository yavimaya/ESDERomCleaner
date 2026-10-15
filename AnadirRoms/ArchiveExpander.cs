using System.IO.Compression;
using SharpCompress.Archives;

namespace ESDERomCleaner;

// Packs de ROMs: un comprimido (zip/7z/rar) que lleva dentro otros comprimidos, que a su vez pueden llevar más. Se extraen recursivamente
// hasta llegar a los archivos finales (cada uno, una ROM o un zip de ROM), que son los que se identifican por hash.
internal static class ArchiveExpander
{
    private const int MaxDepth = 6;

    // Tope de bytes que se extraen de un mismo comprimido (contra bombas de descompresión).
    public const long MaxExtractBytes = 32L << 30;

    private static readonly string[] BlockedExtensions = [".exe", ".dll", ".bat", ".cmd", ".com", ".scr", ".pif", ".msi", ".hta", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".ps1", ".lnk", ".url", ".reg", ".jar", ".cpl"];

    // Archivos ejecutables o de acceso directo que no son ROMs: no se extraen a la carpeta de ROMs.
    public static bool IsBlocked(string name) => BlockedExtensions.Contains(System.IO.Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    // Copia contando los bytes realmente leídos (el tamaño declarado en el comprimido puede mentir); falla si se agota el presupuesto.
    public static void CopyLimited(Stream from, Stream to, ref long budget, int bufferSize = 1 << 20)
    {
        var buffer = new byte[bufferSize];
        int read;
        while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
        {
            budget -= read;
            if (budget < 0) throw new InvalidDataException("El comprimido se expande por encima del límite permitido.");
            to.Write(buffer, 0, read);
        }
    }

    private static bool IsArchive(string name) => System.IO.Path.GetExtension(name).ToLowerInvariant() is ".zip" or ".7z" or ".rar";

    // Devuelve el propio archivo si no es un pack, o los archivos finales extraídos en tempRoot.
    public static List<string> Expand(string path, string tempRoot, CancellationToken token, int depth = 0)
    {
        if (depth >= MaxDepth || !IsArchive(path)) return [path];
        List<string> nested;
        try { nested = ExtractNested(path, tempRoot, token); }
        catch (OperationCanceledException) { throw; }
        catch { return [path]; } // un comprimido que no se puede leer se trata como un archivo más
        if (nested.Count == 0) return [path];
        var result = new List<string>();
        foreach (var file in nested) result.AddRange(Expand(file, tempRoot, token, depth + 1));
        return result;
    }

    // Extrae solo los comprimidos que hay dentro (el resto de archivos del pack, como textos o imágenes, se ignoran).
    private static List<string> ExtractNested(string path, string tempRoot, CancellationToken token)
    {
        var extracted = new List<string>();
        long budget = MaxExtractBytes;
        string Target(string name)
        {
            string dir = System.IO.Path.Combine(tempRoot, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, System.IO.Path.GetFileName(name));
        }

        if (System.IO.Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries.Where(e => e.Length > 0 && IsArchive(e.Name)))
            {
                token.ThrowIfCancellationRequested();
                string target = Target(entry.Name);
                using (var from = entry.Open())
                using (var to = File.Create(target))
                    CopyLimited(from, to, ref budget);
                extracted.Add(target);
            }
        }
        else
        {
            using var archive = ArchiveFactory.OpenArchive(path);
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory && e.Size > 0 && IsArchive(e.Key ?? "")))
            {
                token.ThrowIfCancellationRequested();
                string target = Target(entry.Key ?? "archivo");
                using (var from = entry.OpenEntryStream())
                using (var to = File.Create(target))
                    CopyLimited(from, to, ref budget);
                extracted.Add(target);
            }
        }
        return extracted;
    }
}
