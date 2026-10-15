using System.Net.Http;
using System.Text.RegularExpressions;

namespace ESDERomCleaner;

// Una base de datos DAT y las carpetas de ES-DE que le corresponden, de la más a la menos habitual.
// Kind: «no-intro», «redump», «tosec», «mame», «fbneo» (se descargan de libretro) u «other» (las importa el usuario).
// File: ruta del DAT importado. NameMatch: se identifica por el nombre del archivo (MAME, FBNeo) en vez de por hash.
// Remote: ruta del DAT dentro de libretro-database/metadat (por defecto «<kind>/<nombre>.dat»).
internal sealed record DatSource(string Kind, string Name, string[] Folders, string? File = null, bool NameMatch = false, string? Remote = null)
{
    public string Key => $"{Kind}/{Name}";
    public string KindLabel => Kind switch { "redump" => "Redump", "tosec" => "TOSEC", "mame" => "MAME", "fbneo" => "FBNeo", "other" => "Otra", _ => "No-Intro" };
    public string Label => $"{KindLabel} · {Name}";
    public bool Imported => File is not null;
}

internal sealed record DatHit(DatSource Source, string Game);

// Índice en memoria para identificar: por (CRC32, tamaño) y por nombre de juego (MAME/FBNeo).
internal sealed class DatIndex
{
    public Dictionary<(uint Crc, long Size), List<DatHit>> Hash { get; } = new();
    public Dictionary<string, List<DatHit>> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
    // Nombre de cada juego de los DAT por hash: sirve para reconocer un archivo que se llama igual que el juego aunque su hash no coincida.
    public Dictionary<string, List<DatHit>> GameNames { get; } = new(StringComparer.OrdinalIgnoreCase);
}

// DAT que el usuario ha importado (los que genera clrmamepro, MAME, FBNeo u otros programas).
internal sealed record DatImport(string Id, string Name, string Kind, string[] Folders, bool NameMatch);

// Qué orígenes de DAT se usan al identificar.
internal sealed class DatSettings
{
    // Solo para leer ajustes antiguos: antes se activaban clases enteras. Ahora se desactiva cada DAT por separado (DisabledSources).
    public List<string> Kinds { get; set; } = ["no-intro", "redump", "tosec", "mame", "fbneo", "other"];
    // Claves (DatSource.Key) de los DAT que el usuario ha desactivado; el resto se usa.
    public List<string> DisabledSources { get; set; } = [];
    public bool Migrated { get; set; }

    public bool IsEnabled(DatSource source) => !DisabledSources.Contains(source.Key);

    public void SetEnabled(DatSource source, bool enabled)
    {
        if (enabled) DisabledSources.Remove(source.Key);
        else if (!DisabledSources.Contains(source.Key)) DisabledSources.Add(source.Key);
    }

    public static DatSettings Load()
    {
        var settings = AppSettings.Get("Dats", new DatSettings());
        if (!settings.Migrated)
        {
            // Las clases que estaban desmarcadas pasan a tener todos sus DAT desactivados.
            foreach (var source in RomDatabase.Sources.Where(s => !settings.Kinds.Contains(s.Kind))) settings.SetEnabled(source, false);
            settings.Kinds = ["no-intro", "redump", "tosec", "mame", "fbneo", "other"];
            settings.Migrated = true;
            settings.Save();
        }
        return settings;
    }

    public void Save()
    {
        AppSettings.Set("Dats", this);
    }
}

// Bases de datos para identificar ROMs por hash (CRC32 + tamaño). Se descargan del repositorio libretro-database,
// que mantiene los DAT de No-Intro (cartuchos) y Redump (discos) listos para usar.
internal static class RomDatabase
{
    private const string BaseUrl = "https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/";

    private static DatSource N(string name, params string[] folders) => new("no-intro", name, folders);
    private static DatSource R(string name, params string[] folders) => new("redump", name, folders);
    private static DatSource T(string name, params string[] folders) => new("tosec", name, folders);
    // Arcade: el archivo es el nombre del juego, así que se identifica por nombre y no por hash.
    private static DatSource M(string name, string remoteFile) => new("mame", name, ["mame", "arcade"], NameMatch: true, Remote: "mame/" + remoteFile);
    private static DatSource F(string name, string remoteFile) => new("fbneo", name, ["fbneo", "arcade"], NameMatch: true, Remote: "fbneo-split/" + remoteFile);

    private static readonly DatSource[] BuiltIn =
    [
        N("Arduboy Inc - Arduboy", "arduboy"), N("Atari - 2600", "atari2600"), N("Atari - 5200", "atari5200"), N("Atari - 7800", "atari7800"),
        N("Atari - 8-bit Family", "atari800", "atarixe"), N("Atari - Jaguar", "atarijaguar"), N("Atari - Lynx", "atarilynx"), N("Atari - ST", "atarist"),
        N("Bandai - WonderSwan Color", "wonderswancolor"), N("Bandai - WonderSwan", "wonderswan"), N("Casio - PV-1000", "pv1000"),
        N("Coleco - ColecoVision", "colecovision"), N("Commodore - 64", "c64"), N("Commodore - Amiga", "amiga", "amiga600", "amiga1200"),
        N("Commodore - Plus-4", "plus4"), N("Commodore - VIC-20", "vic20"), N("Emerson - Arcadia 2001", "arcadia"),
        N("Epoch - Super Cassette Vision", "scv"), N("Fairchild - Channel F", "channelf"), N("Funtech - Super Acan", "supracan"),
        N("GCE - Vectrex", "vectrex"), N("Hartung - Game Master", "gmaster"), N("Magnavox - Odyssey2", "odyssey2"),
        N("Mattel - Intellivision", "intellivision"), N("Microsoft - MSX", "msx", "msx1"), N("Microsoft - MSX2", "msx2"),
        N("Mobile - J2ME", "j2me"), N("Mobile - Palm OS", "palm"), N("Mobile - Symbian", "symbian"),
        N("NEC - PC Engine - TurboGrafx 16", "pcengine", "tg16"), N("NEC - PC Engine SuperGrafx", "supergrafx"),
        N("Nintendo - Family Computer Disk System", "fds"), N("Nintendo - Game Boy Advance", "gba"), N("Nintendo - Game Boy Color", "gbc"),
        N("Nintendo - Game Boy", "gb"), N("Nintendo - Nintendo 3DS", "n3ds"), N("Nintendo - Nintendo 64", "n64"), N("Nintendo - Nintendo 64DD", "n64dd"),
        N("Nintendo - Nintendo DS", "nds"), N("Nintendo - Nintendo DSi", "nds"), N("Nintendo - Nintendo Entertainment System", "nes", "famicom"),
        N("Nintendo - Pokemon Mini", "pokemini"), N("Nintendo - Satellaview", "satellaview"), N("Nintendo - Sufami Turbo", "sufami"),
        N("Nintendo - Super Nintendo Entertainment System", "snes", "sfc", "snesna"), N("Nintendo - Virtual Boy", "virtualboy"),
        N("Philips - Videopac+", "videopac"), N("SNK - Neo Geo Pocket Color", "ngpc"), N("SNK - Neo Geo Pocket", "ngp"),
        N("Sega - 32X", "sega32x", "sega32xjp", "sega32xna"), N("Sega - Game Gear", "gamegear"), N("Sega - Master System - Mark III", "mastersystem", "mark3"),
        N("Sega - Mega Drive - Genesis", "megadrive", "genesis", "megadrivejp"), N("Sega - SG-1000", "sg-1000"), N("Sharp - X1", "x1"),
        N("Sharp - X68000", "x68000"), N("Sinclair - ZX Spectrum +3", "zxspectrum"), N("Sony - PlayStation Portable", "psp"),
        N("Sony - PlayStation Vita", "psvita"), N("Tiger - Game.com", "gamecom"), N("VTech - V.Smile", "vsmile"), N("Watara - Supervision", "supervision"),
        R("Atari - Jaguar CD", "atarijaguarcd"), R("Commodore - CD32", "amigacd32"), R("Commodore - CDTV", "cdtv"), R("Microsoft - Xbox", "xbox"),
        R("Microsoft - Xbox 360", "xbox360"), R("NEC - PC Engine CD - TurboGrafx-CD", "pcenginecd", "tg-cd"), R("NEC - PC-98", "pc98"),
        R("NEC - PC-FX", "pcfx"), R("Nintendo - GameCube", "gc"), R("Nintendo - Wii", "wii"), R("Philips - CD-i", "cdimono1"), R("SNK - Neo Geo CD", "neogeocd", "neogeocdjp"),
        R("Sega - Dreamcast", "dreamcast"), R("Sega - Mega-CD - Sega CD", "segacd", "megacd", "megacdjp"), R("Sega - Naomi 2", "naomi2"), R("Sega - Naomi", "naomi"),
        R("Sega - Saturn", "saturn", "saturnjp"), R("Sony - PlayStation 2", "ps2"), R("Sony - PlayStation 3", "ps3"), R("Sony - PlayStation", "psx"),
        R("The 3DO Company - 3DO", "3do"),
        T("Amstrad - CPC", "amstradcpc"), T("Amstrad - GX4000", "gx4000"), T("Apple - II", "apple2"), T("Atari - 2600", "atari2600"), T("Atari - 5200", "atari5200"),
        T("Atari - 7800", "atari7800"), T("Atari - 8-bit Family", "atari800", "atarixe"), T("Atari - Jaguar", "atarijaguar"), T("Atari - Lynx", "atarilynx"),
        T("Atari - ST", "atarist"), T("Commodore - Amiga", "amiga", "amiga600", "amiga1200"), T("Infocom - Z-Machine", "zmachine"),
        T("Mattel - Intellivision", "intellivision"), T("NEC - PC-88", "pc88"), T("NEC - PC-98", "pc98"), T("Nintendo - Game Boy Advance", "gba"),
        T("Nintendo - Game Boy Color", "gbc"), T("Nintendo - Game Boy", "gb"), T("Nintendo - Nintendo Entertainment System", "nes", "famicom"),
        T("Nintendo - Super Nintendo Entertainment System", "snes", "sfc", "snesna"), T("Sega - 32X", "sega32x", "sega32xjp", "sega32xna"),
        T("Sega - Game Gear", "gamegear"), T("Sega - Master System - Mark III", "mastersystem", "mark3"),
        T("Sega - Mega Drive - Genesis", "megadrive", "genesis", "megadrivejp"), T("Sega - SG-1000", "sg-1000"), T("Sharp - X1", "x1"),
        T("Sharp - X68000", "x68000"), T("Sinclair - ZX Spectrum", "zxspectrum"), T("Spectravideo - SVI-318 - SVI-328", "spectravideo"), T("Thomson - MOTO", "moto"),
        M("MAME (actual)", "MAME.dat"), M("MAME 2003", "MAME 2003 XML.xml"), M("MAME 2003-Plus", "MAME 2003-Plus XML.xml"), M("MAME 2010", "MAME 2010 XML.xml"), M("MAME 2015", "MAME 2015 XML.zip"), M("MAME 2000", "MAME 2000 XML.dat"),
        M("MAME BIOS", "MAME BIOS.dat"), F("FBNeo - Arcade Games", "FBNeo - Arcade Games.dat")
    ];

    public static string DatsFolder => Path.Combine(AppPaths.Data, "dats");
    public static string PathOf(DatSource source) => source.File ?? Path.Combine(DatsFolder, source.Kind, source.Name + ".dat");
    public static bool IsInstalled(DatSource source) => File.Exists(PathOf(source));

    // Todas las bases de datos conocidas (las de libretro más las importadas) y las que se usan según la configuración.
    public static IReadOnlyList<DatSource> Sources => [.. BuiltIn, .. LoadImports().Select(ToSource)];
    public static IReadOnlyList<DatSource> Allowed(DatSettings settings) => Sources.Where(settings.IsEnabled).ToList();
    public static int InstalledCount() => Allowed(DatSettings.Load()).Count(IsInstalled);

    // ---- DAT importados por el usuario (clrmamepro, MAME, FBNeo…)

    private static string ImportsFile => Path.Combine(AppPaths.Data, "dat_imports.json");
    private static string CustomFolder => Path.Combine(DatsFolder, "custom");

    private static DatSource ToSource(DatImport import) =>
        new(import.Kind, import.Name, import.Folders, Path.Combine(CustomFolder, import.Id + ".dat"), import.NameMatch);

    private static List<DatImport> LoadImports()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<DatImport>>(File.ReadAllText(ImportsFile)) ?? []; }
        catch { return []; }
    }

    private static void SaveImports(List<DatImport> imports)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ImportsFile)!);
        string temp = ImportsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(imports, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, ImportsFile, overwrite: true);
    }

    public static void Import(string file, string name, string kind, string[] folders, bool nameMatch)
    {
        var imports = LoadImports();
        var import = new DatImport(Guid.NewGuid().ToString("N"), name, kind, folders, nameMatch);
        Directory.CreateDirectory(CustomFolder);
        File.Copy(file, Path.Combine(CustomFolder, import.Id + ".dat"), overwrite: true);
        imports.Add(import);
        SaveImports(imports);
        Invalidate();
    }

    public static void RemoveImport(DatSource source)
    {
        var imports = LoadImports();
        var import = imports.FirstOrDefault(i => ToSource(i).File == source.File);
        if (import is null) return;
        imports.Remove(import);
        SaveImports(imports);
        try { File.Delete(PathOf(source)); } catch { }
        Invalidate();
    }

    // Lee el nombre del DAT y si parece de arcade (MAME/FBNeo, que se identifica por el nombre del archivo) sin cargarlo entero.
    public static (string Name, bool LooksArcade) Peek(string file)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        bool arcade = false;
        using var reader = new StreamReader(file);
        var head = new char[8192];
        int n = reader.Read(head, 0, head.Length);
        string text = new(head, 0, n);
        var nameMatch = Regex.Match(text, "<name>([^<]+)</name>|\\bname \"([^\"]+)\"");
        if (nameMatch.Success) name = (nameMatch.Groups[1].Success ? nameMatch.Groups[1].Value : nameMatch.Groups[2].Value).Trim();
        arcade = text.Contains("<mame", StringComparison.OrdinalIgnoreCase) || text.Contains("<machine", StringComparison.OrdinalIgnoreCase)
                 || Regex.IsMatch(name + " " + Path.GetFileName(file), "mame|fbneo|finalburn|fba", RegexOptions.IgnoreCase);
        return (name, arcade);
    }

    // ---- Descarga

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ESDERomCleaner");
        return http;
    }

    // «report» recibe los bytes descargados hasta ahora y el total si el servidor lo indica.
    public static async Task DownloadAsync(DatSource source, CancellationToken token, Action<long, long?>? report = null)
    {
        var http = Http;
        string remote = source.Remote ?? $"{source.Kind}/{source.Name}.dat";
        int slash = remote.IndexOf('/');
        string url = BaseUrl + remote[..slash] + "/" + Uri.EscapeDataString(remote[(slash + 1)..]);
        string target = PathOf(source);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = File.Create(temp);
                long? total = response.Content.Headers.ContentLength;
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    received += read;
                    report?.Invoke(received, total);
                }
            }
            // Algunos DAT (p. ej. MAME 2015) se publican dentro de un .zip: se extrae el XML que lleva.
            if (remote.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                string unpacked = temp + ".xml";
                using (var zip = System.IO.Compression.ZipFile.OpenRead(temp))
                {
                    var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException("El zip descargado no contiene ningún DAT.");
                    using (var from = entry.Open())
                    using (var to = File.Create(unpacked))
                        from.CopyTo(to);
                }
                File.Delete(temp);
                File.Move(unpacked, temp);
            }
            // Se comprueba que realmente es un DAT antes de sustituir el anterior.
            using (var reader = new StreamReader(temp))
            {
                string head = (reader.ReadLine() ?? "").TrimStart('﻿', ' ', '\t');
                if (!head.StartsWith("clrmamepro", StringComparison.OrdinalIgnoreCase) && !head.StartsWith('<'))
                    throw new InvalidDataException("El archivo descargado no es un DAT válido.");
            }
            File.Move(temp, target, overwrite: true);
            Invalidate();
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    // ---- Índice en memoria: (CRC32, tamaño) → juego

    private static readonly object Gate = new();
    private static Task<DatIndex>? indexTask;

    public static void Invalidate() { lock (Gate) { indexTask = null; cloneParents = null; } }

    // ---- Clones de arcade: juego → su juego padre, leído de los DAT por nombre (MAME, FBNeo…) que se usan. Sirve para consultar solo al padre.
    private static Dictionary<string, string>? cloneParents;

    public static Dictionary<string, string> CloneParents()
    {
        lock (Gate)
        {
            if (cloneParents is not null) return cloneParents;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in Allowed(DatSettings.Load()).Where(s => s.NameMatch && IsInstalled(s)))
            {
                try { ReadClones(source, map); }
                catch (Exception) { /* un DAT dañado no impide usar los demás */ }
            }
            return cloneParents = map;
        }
    }

    private static void ReadClones(DatSource source, Dictionary<string, string> map)
    {
        string path = PathOf(source);
        bool xml;
        using (var peek = new StreamReader(path))
        {
            int c;
            while ((c = peek.Read()) >= 0 && char.IsWhiteSpace((char)c)) { }
            xml = c == '<' || c == 0xFEFF && peek.Peek() == '<';
        }
        if (xml)
        {
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true };
            using var reader = System.Xml.XmlReader.Create(path, settings);
            while (reader.Read())
            {
                if (reader.NodeType != System.Xml.XmlNodeType.Element || reader.LocalName is not ("game" or "machine")) continue;
                string name = reader.GetAttribute("name") ?? "", parent = reader.GetAttribute("cloneof") ?? "";
                if (name.Length > 0 && parent.Length > 0) map.TryAdd(name, parent);
            }
            return;
        }
        string game = ""; bool inGame = false;
        foreach (var raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith("game (", StringComparison.Ordinal) || line.StartsWith("machine (", StringComparison.Ordinal)) { inGame = true; game = ""; continue; }
            if (!inGame) continue;
            if (game.Length == 0 && line.StartsWith("name ", StringComparison.Ordinal)) { game = line[5..].Trim('"', ' '); continue; }
            if (game.Length > 0 && line.StartsWith("cloneof ", StringComparison.Ordinal))
            {
                string parent = line[8..].Trim('"', ' ');
                if (parent.Length > 0) map.TryAdd(game, parent);
            }
        }
    }

    public static Task<DatIndex> GetIndexAsync()
    {
        lock (Gate)
        {
            if (indexTask is not null) return indexTask;
            var task = Task.Run(LoadOrBuild);
            indexTask = task;
            // Si falla, no se queda guardado el fallo: la próxima vez se vuelve a intentar.
            task.ContinueWith(t => { lock (Gate) if (ReferenceEquals(indexTask, t)) indexTask = null; }, TaskContinuationOptions.OnlyOnFaulted);
            return task;
        }
    }

    // true si el índice ya está cargado en memoria (no hay que prepararlo).
    public static bool IndexReady { get { lock (Gate) return indexTask is { IsCompletedSuccessfully: true }; } }

    // ---- Caché del índice en disco (junto a los DAT, no en la carpeta temporal): solo se rehace si cambian los DAT o los orígenes activos.

    private static string CachePath => Path.Combine(DatsFolder, "index.cache");
    private const string CacheMagic = "ESDEIDX2";

    private static string Signature(List<DatSource> sources)
    {
        var text = string.Join("|", sources.Select(s => $"{s.Key}:{s.NameMatch}:{new FileInfo(PathOf(s)).Length}:{File.GetLastWriteTimeUtc(PathOf(s)).Ticks}"));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    // Avance de la carga o construcción del índice (0..1) y qué se está haciendo; la interfaz lo consulta mientras espera.
    public static double IndexProgress { get; private set; }
    public static string IndexPhase { get; private set; } = "";

    private static DatIndex LoadOrBuild()
    {
        IndexProgress = 0; IndexPhase = "Comprobando las bases de datos";
        var sources = Allowed(DatSettings.Load()).Where(IsInstalled).ToList();
        string signature = Signature(sources);
        var cached = TryLoadCache(sources, signature);
        if (cached is not null) { IndexProgress = 1; return cached; }
        var built = BuildIndex(sources);
        IndexPhase = "Guardando el índice"; IndexProgress = 0.95;
        TrySaveCache(built, sources, signature);
        IndexProgress = 1;
        return built;
    }

    private static DatIndex? TryLoadCache(List<DatSource> sources, string signature)
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            using var reader = new BinaryReader(File.OpenRead(CachePath), System.Text.Encoding.UTF8);
            if (reader.ReadString() != CacheMagic || reader.ReadString() != signature) return null;
            IndexPhase = "Leyendo el índice guardado";
            var strings = new string[reader.ReadInt32()];
            for (int i = 0; i < strings.Length; i++)
            {
                strings[i] = reader.ReadString();
                if ((i & 0x3FFF) == 0) IndexProgress = 0.3 * i / Math.Max(1, strings.Length);
            }
            var index = new DatIndex();
            List<DatHit> ReadHits()
            {
                int n = reader.ReadByte();
                var hits = new List<DatHit>(n);
                for (int i = 0; i < n; i++) hits.Add(new DatHit(sources[reader.ReadUInt16()], strings[reader.ReadInt32()]));
                return hits;
            }
            int hashCount = reader.ReadInt32();
            index.Hash.EnsureCapacity(hashCount);
            for (int i = 0; i < hashCount; i++)
            {
                uint crc = reader.ReadUInt32(); long size = reader.ReadInt64(); index.Hash[(crc, size)] = ReadHits();
                if ((i & 0x7FFF) == 0) IndexProgress = 0.3 + 0.4 * i / Math.Max(1, hashCount);
            }
            IndexProgress = 0.7;
            int nameCount = reader.ReadInt32();
            for (int i = 0; i < nameCount; i++) { string key = strings[reader.ReadInt32()]; index.Names[key] = ReadHits(); }
            int gameCount = reader.ReadInt32();
            index.GameNames.EnsureCapacity(gameCount);
            for (int i = 0; i < gameCount; i++)
            {
                string key = strings[reader.ReadInt32()]; index.GameNames[key] = ReadHits();
                if ((i & 0x7FFF) == 0) IndexProgress = 0.7 + 0.3 * i / Math.Max(1, gameCount);
            }
            return index;
        }
        catch { return null; } // caché ilegible o de otra versión: se rehace
    }

    private static void TrySaveCache(DatIndex index, List<DatSource> sources, string signature)
    {
        try
        {
            Directory.CreateDirectory(DatsFolder);
            var sourceIndex = sources.Select((s, i) => (s, i)).ToDictionary(p => p.s, p => (ushort)p.i);
            var stringIds = new Dictionary<string, int>();
            var strings = new List<string>();
            int Id(string text)
            {
                if (!stringIds.TryGetValue(text, out int id)) { id = strings.Count; stringIds[text] = id; strings.Add(text); }
                return id;
            }
            // Se reservan los ids antes de escribir, porque la tabla de textos va al principio del archivo.
            foreach (var hits in index.Hash.Values) foreach (var h in hits) Id(h.Game);
            foreach (var pair in index.Names) { Id(pair.Key); foreach (var h in pair.Value) Id(h.Game); }
            foreach (var pair in index.GameNames) { Id(pair.Key); foreach (var h in pair.Value) Id(h.Game); }

            string temp = CachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var writer = new BinaryWriter(File.Create(temp), System.Text.Encoding.UTF8))
            {
                writer.Write(CacheMagic); writer.Write(signature);
                writer.Write(strings.Count);
                foreach (var s in strings) writer.Write(s);
                void WriteHits(List<DatHit> hits)
                {
                    writer.Write((byte)Math.Min(hits.Count, 255));
                    foreach (var h in hits.Take(255)) { writer.Write(sourceIndex[h.Source]); writer.Write(Id(h.Game)); }
                }
                writer.Write(index.Hash.Count);
                foreach (var pair in index.Hash) { writer.Write(pair.Key.Crc); writer.Write(pair.Key.Size); WriteHits(pair.Value); }
                writer.Write(index.Names.Count);
                foreach (var pair in index.Names) { writer.Write(Id(pair.Key)); WriteHits(pair.Value); }
                writer.Write(index.GameNames.Count);
                foreach (var pair in index.GameNames) { writer.Write(Id(pair.Key)); WriteHits(pair.Value); }
            }
            File.Move(temp, CachePath, overwrite: true);
        }
        catch { /* sin caché solo se pierde tiempo la próxima vez */ }
    }

    private static readonly Regex SizeRx = new(@"\bsize (\d+)", RegexOptions.Compiled);
    private static readonly Regex CrcRx = new(@"\bcrc ([0-9A-Fa-f]{8})", RegexOptions.Compiled);

    private static void AddHash(DatIndex index, DatSource source, string game, uint crc, long size)
    {
        if (!index.Hash.TryGetValue((crc, size), out var list)) index.Hash[(crc, size)] = list = new List<DatHit>(1);
        if (!list.Any(h => h.Source == source)) list.Add(new DatHit(source, game));
        if (game.Length > 0)
        {
            if (!index.GameNames.TryGetValue(game, out var byName)) index.GameNames[game] = byName = new List<DatHit>(1);
            if (!byName.Any(h => h.Source == source)) byName.Add(new DatHit(source, game));
        }
    }

    private static void AddName(DatIndex index, DatSource source, string game)
    {
        if (!index.Names.TryGetValue(game, out var list)) index.Names[game] = list = new List<DatHit>(1);
        if (!list.Any(h => h.Source == source)) list.Add(new DatHit(source, game));
    }

    private static DatIndex BuildIndex(List<DatSource> sources)
    {
        var index = new DatIndex();
        int done = 0;
        foreach (var source in sources)
        {
            IndexPhase = $"Construyendo el índice ({done + 1}/{sources.Count}): {source.Name}";
            IndexProgress = 0.9 * done++ / Math.Max(1, sources.Count);
            try { ReadDat(index, source); }
            catch (Exception) { /* un DAT dañado no debe impedir usar los demás */ }
        }
        return index;
    }

    private static void ReadDat(DatIndex index, DatSource source)
    {
        string path = PathOf(source);
        bool xml;
        using (var peek = new StreamReader(path))
        {
            int c;
            while ((c = peek.Read()) >= 0 && char.IsWhiteSpace((char)c)) { }
            xml = c == '<' || c == 0xFEFF && peek.Peek() == '<';
        }
        if (xml) ReadXml(index, source, path); else ReadClrMamePro(index, source, path);
    }

    // Formato de texto de clrmamepro (el que publican No-Intro y Redump).
    private static void ReadClrMamePro(DatIndex index, DatSource source, string path)
    {
        string game = "";
        bool inGame = false;
        foreach (var raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith("game (", StringComparison.Ordinal) || line.StartsWith("machine (", StringComparison.Ordinal) || line.StartsWith("resource (", StringComparison.Ordinal))
            { inGame = true; game = ""; continue; }
            if (!inGame) continue;
            if (game.Length == 0 && line.StartsWith("name \"", StringComparison.Ordinal))
            {
                int end = line.LastIndexOf('"');
                if (end > 6) game = line[6..end];
                if (source.NameMatch && game.Length > 0) AddName(index, source, game);
                continue;
            }
            if (source.NameMatch || !line.StartsWith("rom (", StringComparison.Ordinal)) continue;
            var size = SizeRx.Match(line);
            var crc = CrcRx.Match(line);
            if (size.Success && crc.Success) AddHash(index, source, game, Convert.ToUInt32(crc.Groups[1].Value, 16), long.Parse(size.Groups[1].Value));
        }
    }

    // Formato XML: Logiqx (clrmamepro, FBNeo, TOSEC…) y la salida «-listxml» de MAME (<machine>).
    private static void ReadXml(DatIndex index, DatSource source, string path)
    {
        var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true };
        using var reader = System.Xml.XmlReader.Create(path, settings);
        string game = "";
        while (reader.Read())
        {
            if (reader.NodeType != System.Xml.XmlNodeType.Element) continue;
            string element = reader.LocalName;
            if (element is "game" or "machine" or "software")
            {
                game = reader.GetAttribute("name") ?? "";
                if (source.NameMatch && game.Length > 0) AddName(index, source, game);
            }
            else if (element == "rom" && !source.NameMatch)
            {
                string? crc = reader.GetAttribute("crc"), size = reader.GetAttribute("size");
                if (crc is { Length: 8 } && long.TryParse(size, out long bytes) && uint.TryParse(crc, System.Globalization.NumberStyles.HexNumber, null, out uint value))
                    AddHash(index, source, game, value, bytes);
            }
        }
    }
}

