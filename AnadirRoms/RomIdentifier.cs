using System.IO.Compression;
using System.Text.RegularExpressions;
using SharpCompress.Archives;

namespace ESDERomCleaner;

// Resultado de identificar un archivo soltado: qué archivos hay que copiar, cómo se llama el juego y a qué carpetas de ES-DE podría ir.
internal sealed class RomIdentity
{
    public string Path { get; init; } = "";
    public string[] Files { get; init; } = [];
    public string Game { get; set; } = "";
    public string Method { get; set; } = "Sin identificar";
    // Carpetas candidatas de ES-DE (en orden de preferencia).
    public List<string> Candidates { get; set; } = [];
    public string? Error { get; set; }
    // Archivo contenedor del que se extrajo esta ROM (un pack de zips), si lo hay.
    public string? Origin { get; set; }
    // Extensión de los archivos de dentro cuando es un zip/7z/rar.
    public string? InnerExtension { get; init; }
}

internal static class RomIdentifier
{
    public static (uint Crc, long Size) HashFile(string path, CancellationToken token)
    {
        // CRC32 acelerado por hardware (System.IO.Hashing), mucho más rápido que el cálculo byte a byte con tabla.
        var crc = new System.IO.Hashing.Crc32();
        long size = 0;
        var buffer = new byte[1 << 20];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            crc.Append(buffer.AsSpan(0, read));
            size += read;
        }
        return (crc.GetCurrentHashAsUInt32(), size);
    }

    private static List<string> CueTracks(string cue)
    {
        var tracks = new List<string>();
        string dir = System.IO.Path.GetDirectoryName(cue)!;
        foreach (var line in File.ReadLines(cue))
        {
            var m = Regex.Match(line.Trim(), "^FILE\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            string full;
            try { full = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, m.Groups[1].Value)); }
            catch (ArgumentException) { continue; }
            // Una pista fuera de la carpeta del .cue (ruta absoluta o «..») se ignora.
            if (!PathRules.Inside(full, dir)) continue;
            if (File.Exists(full)) tracks.Add(full);
        }
        return tracks;
    }

    // Claves (CRC32, tamaño) del contenido de un archivo: lo que lleva dentro si es un zip/7z, las pistas si es un .cue, o él mismo.
    private static List<(uint Crc, long Size)> Keys(string path, CancellationToken token, out string[] files)
    {
        var keys = new List<(uint, long)>();
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        files = [path];
        switch (ext)
        {
            case ".zip":
                using (var zip = ZipFile.OpenRead(path))
                    foreach (var e in zip.Entries.Where(e => e.Length > 0 && !GamelistGenerator.IsNotRom(e.Name))) keys.Add((e.Crc32, e.Length));
                break;
            case ".7z":
                using (var archive = ArchiveFactory.OpenArchive(path))
                    foreach (var e in archive.Entries.Where(e => !e.IsDirectory && e.Size > 0 && !GamelistGenerator.IsNotRom(e.Key ?? ""))) keys.Add(((uint)e.Crc, e.Size));
                break;
            case ".cue":
                var tracks = CueTracks(path);
                files = new[] { path }.Concat(tracks).ToArray();
                foreach (var track in tracks) keys.Add(HashFile(track, token));
                break;
            case ".chd": // el hash del contenido no coincide con el DAT: se identifica por extensión
            case ".rvz":
            case ".gcz":
            case ".wbfs":
                break;
            default:
                keys.Add(HashFile(path, token));
                break;
        }
        return keys;
    }

    // Muchas ROMs de NES, FDS, Lynx, Atari 7800 o SNES llevan una cabecera de copiador/emulador que los DAT no incluyen en el hash.
    private static readonly Dictionary<string, int> HeaderSizes = new()
    {
        [".nes"] = 16, [".fds"] = 16, [".lnx"] = 64, [".a78"] = 128, [".smc"] = 512, [".sfc"] = 512, [".swc"] = 512, [".fig"] = 512
    };

    private static bool LooksHeadered(string ext, byte[] head, int read, long size)
    {
        static bool Starts(byte[] data, int read, string text, int offset = 0) =>
            read >= offset + text.Length && text.Select((c, i) => data[offset + i] == c).All(b => b);
        return ext switch
        {
            ".nes" => Starts(head, read, "NES\x1A"),
            ".fds" => Starts(head, read, "FDS\x1A"),
            ".lnx" => Starts(head, read, "LYNX"),
            ".a78" => Starts(head, read, "ATARI7800", 1),
            _ => size % 1024 == 512
        };
    }

    // CRC32 del contenido sin la cabecera, para cada archivo (suelto o dentro de un zip/7z) que parezca llevarla.
    private static List<(uint Crc, long Size)> HeaderlessKeys(string path, CancellationToken token)
    {
        var keys = new List<(uint, long)>();
        void Check(string name, long size, Func<Stream> open)
        {
            string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
            if (!HeaderSizes.TryGetValue(ext, out int skip) || size <= skip || size > 128L * 1024 * 1024) return;
            using var stream = open();
            var head = new byte[skip];
            int read = 0, n;
            while (read < skip && (n = stream.Read(head, read, skip - read)) > 0) read += n;
            if (read < skip || !LooksHeadered(ext, head, read, size)) return;
            var crc = new System.IO.Hashing.Crc32();
            long total = 0;
            var buffer = new byte[1 << 20];
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                crc.Append(buffer.AsSpan(0, n));
                total += n;
            }
            keys.Add((crc.GetCurrentHashAsUInt32(), total));
        }

        string outer = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (outer == ".zip")
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var e in zip.Entries.Where(e => e.Length > 0)) Check(e.Name, e.Length, e.Open);
        }
        else if (outer is ".7z" or ".rar")
        {
            using var archive = ArchiveFactory.OpenArchive(path);
            foreach (var e in archive.Entries.Where(e => !e.IsDirectory && e.Size > 0))
            {
                var entry = e;
                Check(System.IO.Path.GetFileName(entry.Key ?? ""), entry.Size, () => entry.OpenEntryStream());
            }
        }
        else Check(path, new FileInfo(path).Length, () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        return keys;
    }

    public static async Task<RomIdentity> IdentifyAsync(string path, CancellationToken token)
    {
        var identity = new RomIdentity { Path = path };
        string[] files = [path];
        try
        {
            var index = await RomDatabase.GetIndexAsync();
            List<(uint Crc, long Size)> keys = await Task.Run(() => { var k = Keys(path, token, out var f); files = f; return k; }, token);

            var hits = new List<DatHit>();
            foreach (var key in keys)
                if (index.Hash.TryGetValue(key, out var list)) hits.AddRange(list);
            string hashNote = "";
            if (hits.Count == 0)
            {
                // Sin coincidencia: se repite el hash sin la cabecera (NES, FDS, Lynx, 7800, SNES) por si el DAT no la incluye.
                var headerless = await Task.Run(() => HeaderlessKeys(path, token), token);
                foreach (var key in headerless)
                    if (index.Hash.TryGetValue(key, out var list)) hits.AddRange(list);
                if (hits.Count > 0) hashNote = " (sin cabecera)";
            }
            if (hits.Count == 0)
            {
                // Arcade (MAME, FBNeo): el nombre del archivo es el nombre del juego (sus ROMs internas se comparten entre juegos y el hash no basta).
                // No se usa si dentro hay una ROM de consola reconocible (.nes, .fds…): un juego de consola puede llamarse como uno de arcade.
                string? innerExt = InnerExtension(path);
                bool consoleRom = innerExt is not null && !GenericExtensions.Contains(innerExt)
                    && EsDeSystemExtensions.BySystem.Any(p => !ArcadeFolders.Contains(p.Key) && p.Value.Contains(innerExt));
                string stem = System.IO.Path.GetFileNameWithoutExtension(path);
                if (!consoleRom && index.Names.TryGetValue(stem, out var named) && named.Count > 0)
                {
                    var first = named[0];
                    return new RomIdentity
                    {
                        Path = path, Files = files, Game = first.Game, Method = $"Nombre · {first.Source.Label}",
                        Candidates = named.SelectMany(h => h.Source.Folders).Distinct().ToList(), InnerExtension = innerExt
                    };
                }
            }
            if (hits.Count == 0)
            {
                // Último recurso antes de la extensión: un archivo con el nombre exacto de un juego de los DAT (sets de TOSEC / No-Intro).
                string gameStem = System.IO.Path.GetFileNameWithoutExtension(path);
                if (index.GameNames.TryGetValue(gameStem, out var byGameName))
                {
                    hits.AddRange(byGameName);
                    hashNote = "NOMBRE";
                }
            }
            if (hits.Count > 0)
            {
                // Se toma el sistema que más claves reconocen (p. ej. un zip con varios archivos).
                // En empate se prefieren las bases más fiables: No-Intro, Redump, TOSEC y por último las demás.
                static int Priority(string kind) => kind switch { "no-intro" => 0, "redump" => 1, "tosec" => 2, _ => 3 };
                var best = hits.GroupBy(h => h.Source).OrderByDescending(g => g.Count()).ThenBy(g => Priority(g.Key.Kind)).First();
                identity.Game = best.First().Game;
                identity.Method = hashNote == "NOMBRE" ? $"Nombre · {best.Key.Label}" : $"Hash{hashNote} · {best.Key.Label}";
                identity.Candidates = best.Key.Folders.ToList();
                return new RomIdentity { Path = path, Files = files, Game = identity.Game, Method = identity.Method, Candidates = identity.Candidates };
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { identity.Error = ex.Message; }

        // Sin coincidencia por hash: se propone por extensión (varios sistemas si es ambigua).
        // En un archivo comprimido la extensión que vale es la del archivo de dentro, no la del zip/7z.
        string? inner = InnerExtension(path);
        string ext = inner ?? System.IO.Path.GetExtension(path).ToLowerInvariant();
        var byExtension = EsDeSystemExtensions.BySystem
            .Where(p => p.Value.Contains(ext) && (inner is not null || ext is not (".zip" or ".7z")))
            .Select(p => p.Key).ToList();
        // Si la extensión de dentro solo existe como contenedor (.zip, .7z), no aporta nada.
        string label = inner is null ? "Extensión" : $"Extensión interior ({inner})";
        return new RomIdentity
        {
            Path = path, Files = files, Game = System.IO.Path.GetFileNameWithoutExtension(path), InnerExtension = inner,
            Method = byExtension.Count == 0 ? "Sin identificar" : byExtension.Count == 1 ? label : label + " · ambigua",
            Candidates = byExtension, Error = identity.Error
        };
    }

    private static readonly HashSet<string> GenericExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".7z", ".rar", ".bin", ".iso", ".img", ".chd", ".cue", ".gdi", ".m3u", ".cso", ".rom", ".dsk" };

    private static readonly HashSet<string> ArcadeFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "mame", "mame-advmame", "arcade", "fba", "fbneo", "cps", "cps1", "cps2", "cps3", "neogeo", "naomi", "naomi2", "naomigd", "atomiswave",
        "model2", "model3", "stv", "triforce", "pcarcade", "consolearcade", "type-x", "lcdgames", "daphne", "mess", "gameandwatch"
    };

    private static readonly HashSet<string> IgnoredInner = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".xml", ".url", ".htm", ".html", ".pdf", ".ini", ".md5", ".sfv", ".sha1" };

    // Extensión predominante de los archivos que lleva dentro un zip/7z/rar (null si no es un contenedor o no se puede leer).
    public static string? InnerExtension(string path)
    {
        string outer = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (outer is not (".zip" or ".7z" or ".rar")) return null;
        try
        {
            IEnumerable<string> names;
            if (outer == ".zip")
            {
                using var zip = ZipFile.OpenRead(path);
                names = zip.Entries.Where(e => e.Length > 0).Select(e => e.Name).ToList();
            }
            else
            {
                using var archive = ArchiveFactory.OpenArchive(path);
                names = archive.Entries.Where(e => !e.IsDirectory).Select(e => System.IO.Path.GetFileName(e.Key ?? "")).ToList();
            }
            return names.Select(n => System.IO.Path.GetExtension(n).ToLowerInvariant())
                .Where(x => x.Length > 0 && !IgnoredInner.Contains(x))
                .GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        }
        catch { return null; }
    }
}
