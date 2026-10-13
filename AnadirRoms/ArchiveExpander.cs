using System.IO.Compression;
using SharpCompress.Archives;

namespace ESDERomCleaner;

// Packs de ROMs: un comprimido (zip/7z/rar) que lleva dentro otros comprimidos, que a su vez pueden llevar más. Se extraen recursivamente
// hasta llegar a los archivos finales (cada uno, una ROM o un zip de ROM), que son los que se identifican por hash.
internal static class ArchiveExpander
{
    private const int MaxDepth = 6;

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
                    from.CopyTo(to);
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
                    from.CopyTo(to);
                extracted.Add(target);
            }
        }
        return extracted;
    }
}
