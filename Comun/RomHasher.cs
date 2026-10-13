using System.IO.Compression;
using System.Security.Cryptography;

namespace ESDERomCleaner;

internal sealed record RomHashes(string Crc32, string Md5, string Sha1, long Size, string Basis);

internal static class RomHasher
{
    // archiveContents = true: en un .zip se calcula el hash del archivo más grande de su interior.
    public static RomHashes Compute(string path, bool archiveContents, IProgress<double>? progress, CancellationToken token)
    {
        if (archiveContents && Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.Entries.Where(e => e.Name.Length > 0).OrderByDescending(e => e.Length).FirstOrDefault()
                ?? throw new InvalidOperationException("El zip no contiene archivos.");
            using var entryStream = entry.Open();
            return ComputeStream(entryStream, entry.Length, "contenido de " + entry.Name, progress, token);
        }
        if (archiveContents && Path.GetExtension(path).ToLowerInvariant() is ".7z" or ".rar")
        {
            using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(path);
            var entry = archive.Entries.Where(e => !e.IsDirectory && e.Size > 0).OrderByDescending(e => e.Size).FirstOrDefault()
                ?? throw new InvalidOperationException("El comprimido no contiene archivos.");
            using var entryStream = entry.OpenEntryStream();
            return ComputeStream(entryStream, entry.Size, "contenido de " + Path.GetFileName(entry.Key ?? "archivo"), progress, token);
        }
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return ComputeStream(file, file.Length, "archivo completo", progress, token);
    }

    private static RomHashes ComputeStream(Stream stream, long length, string basis, IProgress<double>? progress, CancellationToken token)
    {
        using var md5 = MD5.Create();
        using var sha1 = SHA1.Create();
        uint crc = 0xFFFFFFFF;
        var buffer = new byte[1 << 20];
        long done = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            crc = Crc32Calculator.Update(crc, buffer, read);
            md5.TransformBlock(buffer, 0, read, null, 0);
            sha1.TransformBlock(buffer, 0, read, null, 0);
            done += read;
            if (length > 0) progress?.Report((double)done / length);
        }
        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return new RomHashes((crc ^ 0xFFFFFFFF).ToString("X8"), Convert.ToHexString(md5.Hash!), Convert.ToHexString(sha1.Hash!), done, basis);
    }
}

internal static class Crc32Calculator
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320u : value >> 1;
            table[i] = value;
        }
        return table;
    }

    public static uint Update(uint crc, byte[] buffer, int count)
    {
        for (int i = 0; i < count; i++) crc = Table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
