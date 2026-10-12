using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SharpCompress.Compressors.LZMA;

namespace ESDERomCleaner;

// Imagen de disco de la que se leen sectores de datos de 2048 bytes (el contenido del sistema de archivos ISO 9660).
internal interface IDiscImage : IDisposable
{
    // Los 2048 bytes de datos del sector lógico; null si no existe o no es un sector de datos.
    byte[]? ReadUserSector(long lba);
}

// Lector de CHD versión 5 de CD (códecs cdlz y cdzl, y zlib/lzma de bloque entero). Solo lee los sectores que se piden: no extrae el disco.
// Los CHD con padre, con huff/flac/zstd en los bloques de datos o de versiones antiguas no se admiten (NotSupportedException).
internal sealed class ChdDisc : IDiscImage
{
    private const int FrameSize = 2448, SectorSize = 2352, SubcodeSize = 96;
    private const uint Zlib = 0x7a6c6962, Lzma = 0x6c7a6d61, CdLz = 0x63646c7a, CdZl = 0x63647a6c;
    private const byte TypeNone = 4, TypeSelf = 5, TypeParent = 6, TypeRleSmall = 7, TypeRleLarge = 8, TypeSelf0 = 9, TypeSelf1 = 10;

    private readonly FileStream file;
    private readonly uint[] compressors = new uint[4];
    private readonly int hunkBytes, hunkCount;
    private readonly byte[] mapType;
    private readonly int[] mapLength;
    private readonly long[] mapOffset;
    private readonly Dictionary<int, byte[]> cache = new();
    private int framesPerHunk;
    private string trackType = "MODE1_RAW";

    private ChdDisc(FileStream file, long logicalBytes, long mapOffsetInFile, long metaOffset, int hunkBytes)
    {
        this.file = file; this.hunkBytes = hunkBytes;
        hunkCount = (int)((logicalBytes + hunkBytes - 1) / hunkBytes);
        mapType = new byte[hunkCount]; mapLength = new int[hunkCount]; mapOffset = new long[hunkCount];
        framesPerHunk = Math.Max(1, hunkBytes / FrameSize);
        ReadMap(mapOffsetInFile);
        ReadTrackMetadata(metaOffset);
    }

    public static ChdDisc Open(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
        try
        {
            var h = new byte[124];
            if (file.Read(h, 0, 124) != 124 || Encoding.ASCII.GetString(h, 0, 8) != "MComprHD") throw new NotSupportedException("No es un CHD.");
            if (U32(h, 12) != 5) throw new NotSupportedException("Solo se admiten CHD de versión 5.");
            long logical = (long)U64(h, 32), mapOff = (long)U64(h, 40), metaOff = (long)U64(h, 48);
            int hunk = (int)U32(h, 56);
            if (hunk <= 0) throw new NotSupportedException("Tamaño de bloque no válido.");
            var disc = new ChdDisc(file, logical, mapOff, metaOff, hunk);
            for (int i = 0; i < 4; i++) disc.compressors[i] = U32(h, 16 + 4 * i);
            if (disc.compressors[0] == 0) throw new NotSupportedException("CHD sin compresión.");
            return disc;
        }
        catch { file.Dispose(); throw; }
    }

    public void Dispose() => file.Dispose();

    private static uint U32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
    private static ulong U64(byte[] b, int o) => (ulong)U32(b, o) << 32 | U32(b, o + 4);

    private byte[] ReadAt(long offset, int count)
    {
        var buffer = new byte[count];
        file.Seek(offset, SeekOrigin.Begin);
        int done = 0;
        while (done < count) { int n = file.Read(buffer, done, count - done); if (n <= 0) throw new EndOfStreamException(); done += n; }
        return buffer;
    }

    // ---- Mapa de bloques (v5 comprimido con Huffman)

    private sealed class Bits(byte[] data, int start)
    {
        private long position = (long)start * 8;
        public uint Read(int count)
        {
            uint value = 0;
            for (int i = 0; i < count; i++)
            {
                long byteIndex = position >> 3;
                int bit = byteIndex < data.Length ? (data[byteIndex] >> (7 - (int)(position & 7))) & 1 : 0;
                value = value << 1 | (uint)bit;
                position++;
            }
            return value;
        }
    }

    private sealed class Huffman
    {
        private readonly Dictionary<(int Length, uint Code), int> codes = new();
        private readonly int maxBits;

        public Huffman(Bits bits, int numCodes, int maxBits)
        {
            this.maxBits = maxBits;
            int numBits = maxBits >= 16 ? 5 : maxBits >= 8 ? 4 : 3;
            var lengths = new int[numCodes];
            for (int node = 0; node < numCodes;)
            {
                int value = (int)bits.Read(numBits);
                if (value != 1) { lengths[node++] = value; continue; }
                value = (int)bits.Read(numBits);
                if (value == 1) { lengths[node++] = value; continue; }
                int repeat = (int)bits.Read(numBits) + 3;
                while (repeat-- > 0 && node < numCodes) lengths[node++] = value;
            }
            var histogram = new uint[33];
            foreach (int l in lengths) { if (l > maxBits) throw new NotSupportedException("Mapa del CHD no válido."); histogram[l]++; }
            uint start = 0;
            for (int length = 32; length > 0; length--)
            {
                uint next = (start + histogram[length]) >> 1;
                histogram[length] = start;
                start = next;
            }
            for (int symbol = 0; symbol < numCodes; symbol++)
                if (lengths[symbol] > 0) codes[(lengths[symbol], histogram[lengths[symbol]]++)] = symbol;
        }

        public int Decode(Bits bits)
        {
            uint code = 0;
            for (int length = 1; length <= maxBits; length++)
            {
                code = code << 1 | bits.Read(1);
                if (codes.TryGetValue((length, code), out int symbol)) return symbol;
            }
            throw new NotSupportedException("Mapa del CHD no válido.");
        }
    }

    private void ReadMap(long mapStart)
    {
        var head = ReadAt(mapStart, 16);
        int mapBytes = (int)U32(head, 0);
        long firstOffset = (long)(U64(head, 2) & 0xFFFFFFFFFFFFUL);
        int lengthBits = head[12], selfBits = head[13];
        var data = ReadAt(mapStart + 16, mapBytes);
        var bits = new Bits(data, 0);
        var decoder = new Huffman(bits, 16, 8);

        // Primero el tipo de compresión de cada bloque (con repeticiones) y después, de cada uno, su longitud y su posición.
        byte last = 0; int repeat = 0;
        for (int i = 0; i < hunkCount; i++)
        {
            if (repeat > 0) { mapType[i] = last; repeat--; continue; }
            int value = decoder.Decode(bits);
            if (value == TypeRleSmall) { mapType[i] = last; repeat = 2 + decoder.Decode(bits); }
            else if (value == TypeRleLarge) { mapType[i] = last; repeat = 2 + 16 + (decoder.Decode(bits) << 4); repeat += decoder.Decode(bits); }
            else mapType[i] = last = (byte)value;
        }
        long current = firstOffset; long lastSelf = 0;
        for (int i = 0; i < hunkCount; i++)
        {
            switch (mapType[i])
            {
                case <= 3:
                    mapLength[i] = (int)bits.Read(lengthBits); mapOffset[i] = current; current += mapLength[i]; bits.Read(16); break;
                case TypeNone:
                    mapLength[i] = hunkBytes; mapOffset[i] = current; current += hunkBytes; bits.Read(16); break;
                case TypeSelf: mapOffset[i] = lastSelf = bits.Read(selfBits); break;
                case TypeSelf1: lastSelf++; mapType[i] = TypeSelf; mapOffset[i] = lastSelf; break;
                case TypeSelf0: mapType[i] = TypeSelf; mapOffset[i] = lastSelf; break;
                default: throw new NotSupportedException("CHD con padre.");
            }
        }
    }

    // ---- Metadatos: tipo de la primera pista (cada «frame» de un CHD de CD ocupa 2448 bytes)

    private void ReadTrackMetadata(long metaOffset)
    {
        for (int guard = 0; metaOffset != 0 && guard < 512; guard++)
        {
            var head = ReadAt(metaOffset, 16);
            uint tag = U32(head, 0);
            int length = (int)(U32(head, 4) & 0xFFFFFF);
            if (tag is 0x43485432 or 0x43485452) // CHT2, CHTR
            {
                string text = Encoding.ASCII.GetString(ReadAt(metaOffset + 16, length)).TrimEnd('\0');
                var m = Regex.Match(text, @"TRACK:(\d+)\s+TYPE:(\S+)");
                if (m.Success && m.Groups[1].Value == "1") { trackType = m.Groups[2].Value; return; }
            }
            metaOffset = (long)U64(head, 8);
        }
    }

    // ---- Bloques

    private byte[] GetHunk(int number)
    {
        if (number < 0 || number >= hunkCount) throw new NotSupportedException("Bloque fuera del CHD.");
        if (cache.TryGetValue(number, out var cached)) return cached;
        byte[] hunk;
        byte type = mapType[number];
        if (type == TypeSelf) hunk = GetHunk((int)mapOffset[number]);
        else if (type == TypeNone) hunk = ReadAt(mapOffset[number], hunkBytes);
        else if (type <= 3) hunk = Decompress(compressors[type], ReadAt(mapOffset[number], mapLength[number]));
        else throw new NotSupportedException("CHD con padre.");
        if (cache.Count > 32) cache.Clear();
        cache[number] = hunk;
        return hunk;
    }

    private byte[] Decompress(uint codec, byte[] data)
    {
        switch (codec)
        {
            case Zlib: return Inflate(data, 0, data.Length, hunkBytes);
            case Lzma: return Unlzma(data, 0, data.Length, hunkBytes);
            case CdLz or CdZl:
                int frames = hunkBytes / FrameSize;
                int eccBytes = (frames + 7) / 8, lengthBytes = hunkBytes < 65536 ? 2 : 3, header = eccBytes + lengthBytes;
                int baseLength = data[eccBytes] << 8 | data[eccBytes + 1];
                if (lengthBytes == 3) baseLength = baseLength << 8 | data[eccBytes + 2];
                byte[] sectors = codec == CdLz ? Unlzma(data, header, baseLength, frames * SectorSize) : Inflate(data, header, baseLength, frames * SectorSize);
                byte[] subcode = Inflate(data, header + baseLength, data.Length - header - baseLength, frames * SubcodeSize);
                var hunk = new byte[hunkBytes];
                for (int f = 0; f < frames; f++)
                {
                    Buffer.BlockCopy(sectors, f * SectorSize, hunk, f * FrameSize, SectorSize);
                    Buffer.BlockCopy(subcode, f * SubcodeSize, hunk, f * FrameSize + SectorSize, SubcodeSize);
                }
                return hunk;
            default: throw new NotSupportedException("Códec de CHD no admitido: " + Encoding.ASCII.GetString(BitConverter.GetBytes(codec).Reverse().ToArray()));
        }
    }

    private static byte[] Inflate(byte[] data, int offset, int count, int outputSize)
    {
        var output = new byte[outputSize];
        using var stream = new DeflateStream(new MemoryStream(data, offset, count), CompressionMode.Decompress);
        int done = 0;
        while (done < outputSize) { int n = stream.Read(output, done, outputSize - done); if (n <= 0) break; done += n; }
        return output;
    }

    // LZMA «crudo» de chdman: sin cabecera ni marca de fin; lc=3, lp=0, pb=2. El diccionario solo necesita cubrir un bloque.
    private static byte[] Unlzma(byte[] data, int offset, int count, int outputSize)
    {
        byte[] props = [0x5D, 0x00, 0x00, 0x04, 0x00]; // diccionario de 1 MB
        var output = new byte[outputSize];
        using var stream = LzmaStream.Create(props, new MemoryStream(data, offset, count), count, outputSize, false);
        int done = 0;
        while (done < outputSize) { int n = stream.Read(output, done, outputSize - done); if (n <= 0) break; done += n; }
        return output;
    }

    public byte[]? ReadUserSector(long lba)
    {
        if (lba < 0) return null;
        long hunkNumber = lba / framesPerHunk;
        if (hunkNumber >= hunkCount) return null;
        byte[] hunk = GetHunk((int)hunkNumber);
        int frame = (int)(lba % framesPerHunk) * FrameSize;
        int dataOffset;
        switch (trackType)
        {
            case "MODE1" or "MODE2_FORM1": dataOffset = 0; break;
            case "MODE2" or "MODE2_FORM_MIX": dataOffset = 8; break;
            case "MODE1_RAW" or "MODE2_RAW":
                dataOffset = hunk[frame + 15] switch { 1 => 16, 2 => 24, _ => -1 };
                break;
            default: return null;
        }
        if (dataOffset < 0) return null;
        var sector = new byte[2048];
        Buffer.BlockCopy(hunk, frame + dataOffset, sector, 0, 2048);
        return sector;
    }
}

// .iso (2048 o 2352 bytes por sector) y .bin de una pista (2352), con el .cue que lo describe.
internal sealed class BinDisc : IDiscImage
{
    private readonly FileStream file;
    private readonly int sectorSize;

    private BinDisc(FileStream file, int sectorSize) { this.file = file; this.sectorSize = sectorSize; }

    public static BinDisc Open(string path)
    {
        string data = path;
        int size = 0;
        if (Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase))
        {
            string cue = File.ReadAllText(path);
            var f = Regex.Match(cue, "FILE\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (!f.Success) throw new NotSupportedException("Cue sin archivo.");
            data = Path.Combine(Path.GetDirectoryName(path)!, f.Groups[1].Value);
            var t = Regex.Match(cue, @"TRACK\s+0*1\s+MODE\d/(\d+)", RegexOptions.IgnoreCase);
            if (t.Success) size = int.Parse(t.Groups[1].Value);
        }
        var file = new FileStream(data, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
        if (size == 0) size = file.Length % 2352 == 0 && file.Length % 2048 != 0 ? 2352 : 2048;
        return new BinDisc(file, size);
    }

    public void Dispose() => file.Dispose();

    public byte[]? ReadUserSector(long lba)
    {
        long position = lba * sectorSize;
        if (lba < 0 || position + sectorSize > file.Length) return null;
        var buffer = new byte[sectorSize];
        file.Seek(position, SeekOrigin.Begin);
        if (file.Read(buffer, 0, sectorSize) != sectorSize) return null;
        if (sectorSize == 2048) return buffer;
        int offset = buffer[15] switch { 1 => 16, 2 => 24, _ => -1 };
        if (offset < 0 || sectorSize < offset + 2048) return null;
        var sector = new byte[2048];
        Buffer.BlockCopy(buffer, offset, sector, 0, 2048);
        return sector;
    }
}

// Lectura mínima del sistema de archivos ISO 9660 de un disco.
internal static class Iso9660
{
    public static (long Lba, long Size)? Find(IDiscImage disc, string path)
    {
        var pvd = disc.ReadUserSector(16);
        if (pvd is null || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001") return null;
        long lba = BitConverter.ToUInt32(pvd, 156 + 2), size = BitConverter.ToUInt32(pvd, 156 + 10);
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            var entry = FindEntry(disc, lba, size, parts[i]);
            if (entry is null) return null;
            (lba, size) = (entry.Value.Lba, entry.Value.Size);
        }
        return (lba, size);
    }

    private static (long Lba, long Size)? FindEntry(IDiscImage disc, long lba, long size, string name)
    {
        long sectors = (size + 2047) / 2048;
        for (long s = 0; s < sectors; s++)
        {
            var sector = disc.ReadUserSector(lba + s);
            if (sector is null) return null;
            int pos = 0;
            while (pos < 2048)
            {
                int length = sector[pos];
                if (length == 0) break;
                int nameLength = sector[pos + 32];
                string entry = Encoding.ASCII.GetString(sector, pos + 33, Math.Min(nameLength, 2048 - pos - 33));
                int semicolon = entry.IndexOf(';');
                if (semicolon >= 0) entry = entry[..semicolon];
                if (entry.Equals(name, StringComparison.OrdinalIgnoreCase)) return (BitConverter.ToUInt32(sector, pos + 2), BitConverter.ToUInt32(sector, pos + 10));
                pos += length;
            }
        }
        return null;
    }

    public static byte[]? ReadFile(IDiscImage disc, (long Lba, long Size) file, long maxBytes)
    {
        long size = Math.Min(file.Size, maxBytes);
        var data = new byte[size];
        for (long done = 0; done < size; done += 2048)
        {
            var sector = disc.ReadUserSector(file.Lba + done / 2048);
            if (sector is null) return null;
            Buffer.BlockCopy(sector, 0, data, (int)done, (int)Math.Min(2048, size - done));
        }
        return data;
    }
}

// Hash de RetroAchievements para discos: PlayStation y PlayStation 2 (nombre del ejecutable de arranque + su contenido) y Sega CD / Saturn
// (los primeros 512 bytes del disco). Devuelve null si el formato del disco no se puede leer.
internal static class RaDiscHasher
{
    public static bool Handles(int consoleId, string path) =>
        consoleId is 9 or 12 or 21 or 39 && Path.GetExtension(path).ToLowerInvariant() is ".chd" or ".iso" or ".cue" or ".bin";

    public static string? Compute(int consoleId, string path)
    {
        try
        {
            using IDiscImage disc = Path.GetExtension(path).Equals(".chd", StringComparison.OrdinalIgnoreCase) ? ChdDisc.Open(path) : BinDisc.Open(path);
            return consoleId switch
            {
                9 or 39 => SegaHash(disc),
                12 => BootHash(disc, "BOOT", 2 * 1024 * 1024, psx: true),
                21 => BootHash(disc, "BOOT2", 32 * 1024 * 1024, psx: false),
                _ => null
            };
        }
        catch { return null; }
    }

    private static string? SegaHash(IDiscImage disc)
    {
        var first = disc.ReadUserSector(0);
        if (first is null || Encoding.ASCII.GetString(first, 0, 4) != "SEGA") return null;
        return Convert.ToHexString(MD5.HashData(first.AsSpan(0, 512))).ToLowerInvariant();
    }

    private static string? BootHash(IDiscImage disc, string key, long maxBytes, bool psx)
    {
        string? name = null;
        if (Iso9660.Find(disc, "SYSTEM.CNF") is { } cnf && Iso9660.ReadFile(disc, cnf, 4096) is { } text)
        {
            var m = Regex.Match(Encoding.ASCII.GetString(text), @"^\s*" + key + @"\s*=\s*cdrom\d?:[\\/]*([^;\s]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (m.Success) name = m.Groups[1].Value;
        }
        name ??= psx ? "PSX.EXE" : null;
        if (name is null || Iso9660.Find(disc, name) is not { } exe) return null;
        long length = exe.Size;
        if (psx)
        {
            var head = disc.ReadUserSector(exe.Lba);
            if (head is null || Encoding.ASCII.GetString(head, 0, 8) != "PS-X EXE") return null;
            length = Math.Min(exe.Size, 2048L + BitConverter.ToUInt32(head, 28));
        }
        if (Iso9660.ReadFile(disc, (exe.Lba, length), maxBytes) is not { } content) return null;
        using var md5 = MD5.Create();
        string fileName = name[(name.LastIndexOfAny(['\\', '/']) + 1)..];
        md5.TransformBlock(Encoding.ASCII.GetBytes(fileName), 0, fileName.Length, null, 0);
        md5.TransformFinalBlock(content, 0, content.Length);
        return Convert.ToHexString(md5.Hash!).ToLowerInvariant();
    }
}
