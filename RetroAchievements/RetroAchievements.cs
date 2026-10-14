using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ESDERomCleaner;

// Usuario y clave de la Web API de RetroAchievements (retroachievements.org › Ajustes › Claves). Se guardan cifradas con DPAPI.
internal sealed record RaCredentials(string UserName, string ApiKey)
{
    public static RaCredentials Empty { get; } = new("", "");
    public bool IsComplete => !string.IsNullOrWhiteSpace(UserName) && !string.IsNullOrWhiteSpace(ApiKey);
}

internal static class RaCredentialStore
{
    private static string FilePath => Path.Combine(AppPaths.Data, "retroachievements.credentials");

    public static RaCredentials Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return RaCredentials.Empty;
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<RaCredentials>(Encoding.UTF8.GetString(plain)) ?? RaCredentials.Empty;
        }
        catch { return RaCredentials.Empty; }
    }

    public static void Save(RaCredentials credentials)
    {
        string path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(credentials)), null, DataProtectionScope.CurrentUser);
        string temporary = path + ".tmp_" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temporary, protectedBytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) try { File.Delete(temporary); } catch { } }
    }
}

internal sealed class RaException(string message) : Exception(message);

internal sealed record RaGame(int Id, string Title, int ConsoleId, string ConsoleName, int Achievements, int Points, string[] Hashes);

internal sealed record RaAchievement(int Id, string Title, string Description, int Points, string BadgeName, int NumAwarded, int NumAwardedHardcore, string DateEarned, string DateHardcore,
    int TrueRatio = 0, string Author = "", string Type = "", string Created = "", string Modified = "")
{
    // DateEarned es la primera vez que se consiguió (en cualquier modo); DateHardcore, solo si se consiguió en modo hardcore.
    public bool Hardcore => DateHardcore.Length > 0;
    public bool Earned => DateEarned.Length > 0 || Hardcore;
    public string When => DateHardcore.Length > 0 ? DateHardcore : DateEarned;
}

internal sealed record RaUserInfo(int HardcorePoints, int SoftcorePoints, int Rank, int TotalRanked);

// Progreso del usuario en un juego: logros conseguidos (en cualquier modo), conseguidos en hardcore y el mayor premio de RetroAchievements
// («mastered», «completed», «beaten-hardcore» o «beaten-softcore»; vacío si no tiene).
internal sealed class RaGameProgress
{
    public int Awarded { get; set; }
    public int Hardcore { get; set; }
    public string Kind { get; set; } = "";
    // Cuándo se leyó de RetroAchievements (para refrescarlo pasado un tiempo).
    public DateTime FetchedAt { get; set; }

    // Los que solo se han conseguido en softcore (los de hardcore ya cuentan en el otro grupo).
    public int SoftcoreOnly => Math.Max(0, Awarded - Hardcore);

    private static int Rank(string kind) => kind switch { "mastered" => 4, "completed" => 3, "beaten-hardcore" => 2, "beaten-softcore" => 1, _ => 0 };
    public static string Best(string a, string b) => Rank(a) >= Rank(b) ? a : b;

    // Calcula el progreso a partir de la lista de logros del juego con las fechas de este usuario.
    public static RaGameProgress FromAchievements(IReadOnlyList<RaAchievement> list, string highestAward = "")
    {
        int total = list.Count, won = list.Count(a => a.Earned), hard = list.Count(a => a.Hardcore);
        var progression = list.Where(a => a.Type == "progression").ToList();
        var winConditions = list.Where(a => a.Type == "win_condition").ToList();
        bool Beaten(Func<RaAchievement, bool> ok) => progression.Count + winConditions.Count > 0 && progression.All(ok) && (winConditions.Count == 0 || winConditions.Any(ok));
        string kind = total > 0 && hard == total ? "mastered" : total > 0 && won == total ? "completed"
            : Beaten(a => a.Hardcore) ? "beaten-hardcore" : Beaten(a => a.Earned) ? "beaten-softcore" : "";
        // Si RetroAchievements ya dice qué premio tiene el usuario (mastered, beaten…), se respeta aunque los logros del juego no estén clasificados por tipo.
        return new RaGameProgress { Awarded = won, Hardcore = hard, Kind = Best(kind, highestAward), FetchedAt = DateTime.Now };
    }
}

// HighestAward es el premio que RetroAchievements da a este usuario en el juego («mastered», «completed», «beaten-hardcore», «beaten-softcore» o vacío).
internal sealed record RaGameDetail(int Id, string Title, string ConsoleName, string Developer, string Genre, string Released, int Players, List<RaAchievement> Achievements, string HighestAward = "");

// Cliente de la Web API de RetroAchievements (https://api-docs.retroachievements.org). Las claves de la consulta son «z» (usuario) y «y» (clave).
internal sealed class RaClient
{
    private const string BaseUrl = "https://retroachievements.org/API/";
    private static readonly HttpClient Http = CreateHttp();
    private readonly RaCredentials credentials;

    public RaClient(RaCredentials credentials) { this.credentials = credentials; }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ESDERomCleaner/1.0");
        return http;
    }

    private async Task<JsonDocument> GetAsync(string endpoint, CancellationToken token, params (string Key, string Value)[] extra)
    {
        string url = BaseUrl + endpoint + "?z=" + Uri.EscapeDataString(credentials.UserName) + "&y=" + Uri.EscapeDataString(credentials.ApiKey)
            + string.Concat(extra.Select(p => "&" + p.Key + "=" + Uri.EscapeDataString(p.Value)));
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await Http.GetAsync(url, token);
                if ((int)response.StatusCode == 429 && attempt < 3) { await Task.Delay(TimeSpan.FromSeconds(3 * (attempt + 1)), token); continue; }
                string body = await response.Content.ReadAsStringAsync(token);
                if ((int)response.StatusCode is 401 or 403) throw new RaException("RetroAchievements rechazó el usuario o la clave de la Web API. Revísalos en Scrapeo › Credenciales de RetroAchievements…");
                if (!response.IsSuccessStatusCode) throw new RaException($"RetroAchievements respondió con el error HTTP {(int)response.StatusCode}.");
                return JsonDocument.Parse(body.TrimStart('﻿', ' ', '\r', '\n', '\t'));
            }
            catch (HttpRequestException ex) { throw new RaException("No se pudo conectar con RetroAchievements: " + ex.Message.Split('?')[0]); }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { throw new RaException("RetroAchievements tardó demasiado en responder."); }
            catch (JsonException) { throw new RaException("Respuesta inesperada de RetroAchievements."); }
        }
    }

    internal static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

    internal static int Int(JsonElement e, string name) => int.TryParse(Str(e, name), out int n) ? n : 0;

    // Juegos de una consola que tienen logros, con los hashes de las ROMs que admiten.
    public async Task<List<RaGame>> GetGamesAsync(int consoleId, CancellationToken token)
    {
        using var doc = await GetAsync("API_GetGameList.php", token, ("i", consoleId.ToString()), ("h", "1"), ("f", "1"));
        var list = new List<RaGame>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var g in doc.RootElement.EnumerateArray())
        {
            var hashes = g.TryGetProperty("Hashes", out var h) && h.ValueKind == JsonValueKind.Array
                ? h.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : [];
            list.Add(new RaGame(Int(g, "ID"), Str(g, "Title"), Int(g, "ConsoleID"), Str(g, "ConsoleName"), Int(g, "NumAchievements"), Int(g, "Points"), hashes));
        }
        return list;
    }

    // Logros de un juego; si hay usuario, con la fecha en que los conseguiste.
    public async Task<RaGameDetail> GetGameAsync(int gameId, bool withProgress, CancellationToken token)
    {
        using var doc = withProgress
            ? await GetAsync("API_GetGameInfoAndUserProgress.php", token, ("g", gameId.ToString()), ("u", credentials.UserName))
            : await GetAsync("API_GetGameExtended.php", token, ("i", gameId.ToString()));
        var root = doc.RootElement;
        var achievements = new List<RaAchievement>();
        if (root.TryGetProperty("Achievements", out var list) && list.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            var items = list.ValueKind == JsonValueKind.Object ? list.EnumerateObject().Select(p => p.Value) : list.EnumerateArray();
            foreach (var a in items)
            {
                achievements.Add(new RaAchievement(Int(a, "ID"), Str(a, "Title"), Str(a, "Description"), Int(a, "Points"), Str(a, "BadgeName"),
                    Int(a, "NumAwarded"), Int(a, "NumAwardedHardcore"), Str(a, "DateEarned"), Str(a, "DateEarnedHardcore"),
                    Int(a, "TrueRatio"), Str(a, "Author"), Str(a, "type"), Str(a, "DateCreated"), Str(a, "DateModified")));
            }
        }
        achievements = achievements.OrderBy(a => a.Id).ToList();
        return new RaGameDetail(Int(root, "ID"), Str(root, "Title"), Str(root, "ConsoleName"), Str(root, "Developer"), Str(root, "Genre"), Str(root, "Released"),
            Int(root, "NumDistinctPlayers"), achievements, Str(root, "HighestAwardKind"));
    }

    // Juegos de tu cuenta con tu progreso en cada uno (idJuego → conseguidos, de ellos en hardcore, y mayor premio).
    public async Task<Dictionary<int, RaGameProgress>> GetUserProgressAsync(CancellationToken token)
    {
        var result = new Dictionary<int, RaGameProgress>();
        for (int offset = 0; ; offset += 500)
        {
            using var doc = await GetAsync("API_GetUserCompletionProgress.php", token, ("u", credentials.UserName), ("c", "500"), ("o", offset.ToString()));
            if (!doc.RootElement.TryGetProperty("Results", out var results) || results.ValueKind != JsonValueKind.Array) break;
            int count = 0;
            foreach (var g in results.EnumerateArray())
            {
                count++;
                int id = Int(g, "GameID");
                // Si el juego aparece dos veces (softcore y hardcore) se queda con lo mayor de cada cosa.
                if (id <= 0) continue;
                var found = result.TryGetValue(id, out var previous) ? previous : new RaGameProgress();
                found.Awarded = Math.Max(found.Awarded, Int(g, "NumAwarded"));
                found.Hardcore = Math.Max(found.Hardcore, Int(g, "NumAwardedHardcore"));
                found.Kind = RaGameProgress.Best(found.Kind, Str(g, "HighestAwardKind"));
                result[id] = found;
            }
            if (count < 500 || offset > 20000) break;
        }
        return result;
    }

    // Puntos hardcore y softcore del usuario y su puesto en la clasificación.
    public async Task<RaUserInfo?> GetUserInfoAsync(CancellationToken token)
    {
        try
        {
            using var doc = await GetAsync("API_GetUserRankAndScore.php", token, ("u", credentials.UserName));
            var r = doc.RootElement;
            return new RaUserInfo(Int(r, "Score"), Int(r, "SoftcoreScore"), Int(r, "Rank"), Int(r, "TotalRanked"));
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    // Avatar del usuario de las credenciales (se guarda en la caché y se renueva si tiene más de un día); null si no se puede.
    public async Task<string?> GetAvatarAsync(CancellationToken token)
    {
        try
        {
            string folder = Path.Combine(AppPaths.Data, "media_cache", "retroachievements");
            string file = Path.Combine(folder, "avatar_" + string.Concat(credentials.UserName.Where(char.IsLetterOrDigit)) + ".png");
            if (File.Exists(file) && DateTime.Now - File.GetLastWriteTime(file) < TimeSpan.FromDays(1)) return file;
            using var doc = await GetAsync("API_GetUserProfile.php", token, ("u", credentials.UserName));
            string pic = Str(doc.RootElement, "UserPic");
            if (pic.Length == 0) return File.Exists(file) ? file : null;
            Directory.CreateDirectory(folder);
            byte[] bytes = await Http.GetByteArrayAsync("https://media.retroachievements.org" + (pic.StartsWith('/') ? pic : "/" + pic), token);
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, token);
            File.Move(temp, file, true);
            return file;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    // Descarga (una vez) la insignia de un logro a la caché y devuelve su ruta; null si no se puede.
    public static async Task<string?> GetBadgeAsync(string badgeName, bool locked, CancellationToken token)
    {
        if (badgeName.Length == 0) return null;
        string folder = Path.Combine(AppPaths.Data, "media_cache", "retroachievements");
        string file = Path.Combine(folder, badgeName + (locked ? "_lock" : "") + ".png");
        if (File.Exists(file)) return file;
        try
        {
            Directory.CreateDirectory(folder);
            byte[] bytes = await Http.GetByteArrayAsync($"https://media.retroachievements.org/Badge/{badgeName}{(locked ? "_lock" : "")}.png", token);
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, token);
            File.Move(temp, file, true);
            return file;
        }
        catch { return null; }
    }
}

// Carpeta de ES-DE → consola de RetroAchievements. Solo las que tienen sentido en RetroAchievements.
internal static class RaConsoles
{
    private static readonly Dictionary<string, int> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["megadrive"] = 1, ["genesis"] = 1, ["megadrivejp"] = 1, ["genesiswide"] = 1, ["n64"] = 2, ["n64dd"] = 2, ["snes"] = 3, ["sfc"] = 3, ["snesna"] = 3,
        ["gb"] = 4, ["gba"] = 5, ["gbc"] = 6, ["nes"] = 7, ["famicom"] = 7, ["pcengine"] = 8, ["tg16"] = 8, ["segacd"] = 9, ["megacd"] = 9, ["megacdjp"] = 9,
        ["sega32x"] = 10, ["mastersystem"] = 11, ["psx"] = 12, ["atarilynx"] = 13, ["ngp"] = 14, ["ngpc"] = 14, ["gamegear"] = 15, ["gc"] = 16,
        ["atarijaguar"] = 17, ["nds"] = 18, ["wii"] = 19, ["wiiu"] = 20, ["ps2"] = 21, ["xbox"] = 22, ["videopac"] = 23, ["odyssey2"] = 23, ["pokemini"] = 24,
        ["atari2600"] = 25, ["dos"] = 26, ["arcade"] = 27, ["mame"] = 27, ["fbneo"] = 27, ["fba"] = 27, ["cps1"] = 27, ["cps2"] = 27, ["cps3"] = 27, ["neogeo"] = 27,
        ["virtualboy"] = 28, ["msx"] = 29, ["msx2"] = 29, ["c64"] = 30, ["zx81"] = 31, ["sg-1000"] = 33, ["amstradcpc"] = 37, ["apple2"] = 38, ["saturn"] = 39,
        ["dreamcast"] = 40, ["psp"] = 41, ["3do"] = 43, ["colecovision"] = 44, ["intellivision"] = 45, ["vectrex"] = 46, ["pcfx"] = 49, ["atari7800"] = 51,
        ["wonderswan"] = 53, ["wonderswancolor"] = 53, ["neogeocd"] = 56, ["channelf"] = 57, ["zxspectrum"] = 59, ["3ds"] = 62, ["supervision"] = 63,
        ["megaduck"] = 69, ["arduboy"] = 71, ["pcenginecd"] = 76, ["tg-cd"] = 76, ["atarijaguarcd"] = 77, ["nintendods"] = 18
    };

    public static int? Of(string folder) => Map.TryGetValue(folder.Trim(), out int id) ? id : null;
}

// Hash de una ROM tal como lo calcula RetroAchievements (rhash) para las consolas de cartucho (los discos, en DiscImage.cs): el MD5 del contenido sin cabeceras de
// copiador, o el nombre del juego en las recreativas. Para los discos y las consolas con un hash complejo devuelve null y se usa el nombre.
internal static class RaHasher
{
    private const long MaxBytes = 96L * 1024 * 1024;
    private static readonly HashSet<int> PlainMd5 = [1, 4, 5, 6, 10, 11, 14, 15, 17, 23, 24, 25, 28, 33, 44, 45, 46, 53, 57, 63, 69];

    public static string? Compute(int consoleId, string path)
    {
        try
        {
            if (RaDiscHasher.Handles(consoleId, path)) return RaDiscHasher.Compute(consoleId, path);
            if (consoleId == 27) return Md5(Encoding.ASCII.GetBytes(Path.GetFileNameWithoutExtension(path).ToLowerInvariant()));
            bool known = PlainMd5.Contains(consoleId) || consoleId is 2 or 3 or 7 or 8 or 13 or 51;
            if (!known) return null;
            byte[]? data = Load(path);
            if (data is null || data.Length == 0) return null;
            int skip = 0;
            switch (consoleId)
            {
                case 7 when data.Length > 16 && data[0] == 'N' && data[1] == 'E' && data[2] == 'S' && data[3] == 0x1A: skip = 16; break;
                case 3 when data.Length % 1024 == 512: skip = 512; break;
                case 8 when data.Length % 8192 == 512: skip = 512; break;
                case 13 when data.Length > 64 && data[0] == 'L' && data[1] == 'Y' && data[2] == 'N' && data[3] == 'X': skip = 64; break;
                case 51 when data.Length > 128 && Encoding.ASCII.GetString(data, 1, 9) == "ATARI7800": skip = 128; break;
                case 2: ToBigEndian(data); break;
            }
            using var md5 = MD5.Create();
            return Convert.ToHexString(md5.ComputeHash(data, skip, data.Length - skip)).ToLowerInvariant();
        }
        catch { return null; }
    }

    private static string Md5(byte[] bytes) => Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();

    // El archivo o, si es un comprimido (zip, 7z, rar), su archivo más grande.
    private static byte[]? Load(string path)
    {
        if (Directory.Exists(path) || !File.Exists(path)) return null;
        if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.Entries.Where(e => e.Length > 0).OrderByDescending(e => e.Length).FirstOrDefault();
            if (entry is null || entry.Length > MaxBytes) return null;
            using var stream = entry.Open();
            using var memory = new MemoryStream((int)entry.Length);
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        if (Path.GetExtension(path).ToLowerInvariant() is ".7z" or ".rar")
        {
            using var packed = SharpCompress.Archives.ArchiveFactory.OpenArchive(path);
            var item = packed.Entries.Where(e => !e.IsDirectory && e.Size > 0).OrderByDescending(e => e.Size).FirstOrDefault();
            if (item is null || item.Size > MaxBytes) return null;
            using var itemStream = item.OpenEntryStream();
            using var buffer = new MemoryStream((int)item.Size);
            itemStream.CopyTo(buffer);
            return buffer.ToArray();
        }
        if (Path.GetExtension(path).ToLowerInvariant() is ".chd" or ".cue" or ".iso") return null;
        var info = new FileInfo(path);
        return info.Length > MaxBytes ? null : File.ReadAllBytes(path);
    }

    // Las ROMs de N64 pueden estar en tres órdenes de bytes; RetroAchievements usa el «z64» (big endian).
    private static void ToBigEndian(byte[] d)
    {
        if (d.Length < 4) return;
        if (d[0] == 0x37 && d[1] == 0x80) for (int i = 0; i + 1 < d.Length; i += 2) (d[i], d[i + 1]) = (d[i + 1], d[i]); // .v64
        else if (d[0] == 0x40 && d[1] == 0x12) for (int i = 0; i + 3 < d.Length; i += 4) { (d[i], d[i + 3]) = (d[i + 3], d[i]); (d[i + 1], d[i + 2]) = (d[i + 2], d[i + 1]); } // .n64
    }
}

internal sealed class RaMatch
{
    public int GameId { get; set; }
    public string Title { get; set; } = "";
    public int Achievements { get; set; }
    public int Points { get; set; }
    // «hash» (seguro) o «nombre» (probable: puede ser otra versión de la ROM).
    public string By { get; set; } = "hash";
}

// Resultado del cotejo con RetroAchievements, propio de la aplicación (no se escribe en los gamelists): qué ROMs tienen logros, qué
// sistemas ya se analizaron y los logros que has conseguido en cada juego.
internal sealed class RaIndex
{
    private sealed class Data
    {
        public Dictionary<string, RaMatch> Matches { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DateTime> Systems { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, int> Progress { get; set; } = new();
        public Dictionary<int, RaGameProgress> Detail { get; set; } = new();
        // Versión del formato del detalle: si es menor que la actual se descarta y se vuelve a leer de RetroAchievements al enfocar cada ROM.
        public int DetailVersion { get; set; }
        public DateTime ProgressAt { get; set; }
    }

    private readonly object gate = new();
    private Data data = new();
    private static string StorePath => Path.Combine(AppPaths.Data, "retroachievements.json");

    public static RaIndex Load()
    {
        var index = new RaIndex();
        try
        {
            if (File.Exists(StorePath) && JsonSerializer.Deserialize<Data>(File.ReadAllText(StorePath)) is { } loaded)
            {
                loaded.Matches = new Dictionary<string, RaMatch>(loaded.Matches, StringComparer.OrdinalIgnoreCase);
                loaded.Systems = new Dictionary<string, DateTime>(loaded.Systems, StringComparer.OrdinalIgnoreCase);
                if (loaded.DetailVersion < 3 || loaded.Detail is null) { loaded.Detail = new(); loaded.DetailVersion = 3; loaded.ProgressAt = default; } // formato nuevo: se vuelve a leer el progreso de la cuenta
                index.data = loaded;
            }
        }
        catch { }
        return index;
    }

    public bool IsEmpty { get { lock (gate) return data.Systems.Count == 0; } }
    // Fecha del último análisis (el más reciente de los sistemas cotejados); null si nunca se analizó.
    public DateTime? LastScan { get { lock (gate) return data.Systems.Count == 0 ? null : data.Systems.Values.Max(); } }
    public bool SystemChecked(string system) { lock (gate) return data.Systems.ContainsKey(system); }
    public RaMatch? Match(RomRow row) { lock (gate) return data.Matches.GetValueOrDefault(RomKey.Of(row)); }
    public int? Awarded(int gameId) { lock (gate) return data.Progress.TryGetValue(gameId, out int n) ? n : null; }
    // Progreso detallado del usuario en el juego (hardcore, softcore y premio); null si no se conoce.
    public RaGameProgress? Progress(int gameId) { lock (gate) return data.Detail.TryGetValue(gameId, out var p) ? p : null; }
    public bool HasProgress { get { lock (gate) return data.ProgressAt != default; } }
    // Cuándo se leyó por última vez el progreso de la cuenta (default si nunca).
    public DateTime ProgressTime { get { lock (gate) return data.ProgressAt; } }

    public void SetMatch(string key, RaMatch? match)
    {
        lock (gate) { if (match is null) data.Matches.Remove(key); else data.Matches[key] = match; }
    }

    public void MarkSystem(string system) { lock (gate) data.Systems[system] = DateTime.Now; }
    public void SetGameProgress(int gameId, RaGameProgress progress) { lock (gate) { data.Progress[gameId] = progress.Awarded; data.Detail[gameId] = progress; data.DetailVersion = 3; } }
    public void SetProgress(Dictionary<int, RaGameProgress> progress)
    {
        lock (gate)
        {
            foreach (var p in progress.Values) p.FetchedAt = DateTime.Now;
            data.Detail = progress;
            data.DetailVersion = 3;
            data.Progress = progress.ToDictionary(p => p.Key, p => p.Value.Awarded);
            data.ProgressAt = DateTime.Now;
        }
    }

    public void Save()
    {
        string? temp = null;
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                temp = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(data), new UTF8Encoding(false));
                File.Move(temp, StorePath, overwrite: true);
            }
        }
        catch { }
        finally { if (temp is not null && File.Exists(temp)) try { File.Delete(temp); } catch { } }
    }

    // Texto de una línea para el panel de datos de la ROM; vacío si nunca se analizó su sistema.
    public string Describe(RomRow row)
    {
        if (RaConsoles.Of(row.System) is null || !SystemChecked(row.System)) return "";
        var match = Match(row);
        if (match is null) return "RETROACHIEVEMENTS: sin logros";
        string text = $"RETROACHIEVEMENTS: {match.Achievements} logros · {match.Points} puntos";
        if (Awarded(match.GameId) is int won) text += $" · conseguidos {won}";
        return text + (match.By == "nombre" ? " (por nombre; puede ser otra versión)" : "");
    }
}

// Cotejo de las ROMs de la colección con los juegos de RetroAchievements: por hash cuando se puede y, si no, por el nombre.
internal static class RaScanner
{
    // Diagnóstico del cotejo (se escribe en la traza); null si no se quiere.
    public static Action<string>? Log { get; set; }

    public sealed record Result(int Systems, int Roms, int ByHash, int ByName, int WithoutAchievements, int Skipped);

    public static async Task<Result> RunAsync(IReadOnlyList<RomRow> rows, IReadOnlyList<string> systems, RaCredentials credentials, RaIndex index,
        IProgress<string> report, IProgress<int> percent, CancellationToken token, IProgress<(int ByHash, int ByName, int None)>? live = null)
    {
        var client = new RaClient(credentials);
        int doneSystems = 0, totalRoms = 0, byHash = 0, byName = 0, without = 0, skipped = 0;
        foreach (string system in systems)
        {
            token.ThrowIfCancellationRequested();
            report.Report($"{system}: descargando la lista de juegos con logros…");
            int consoleId = RaConsoles.Of(system)!.Value;
            var games = await client.GetGamesAsync(consoleId, token);
            var byHashMap = new Dictionary<string, RaGame>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in games) foreach (string h in g.Hashes) byHashMap.TryAdd(h, g);
            var byTitle = new Dictionary<string, List<RaGame>>();
            foreach (var g in games.Where(g => !g.Title.StartsWith('~')))
            {
                string key = NormalizeTitle(g.Title);
                if (key.Length == 0) continue;
                if (!byTitle.TryGetValue(key, out var list)) byTitle[key] = list = [];
                list.Add(g);
            }

            var systemRows = rows.Where(r => r.System.Equals(system, StringComparison.OrdinalIgnoreCase) && r.Status == RomStatus.Ok && r.Path.Length > 0).ToList();
            int done = 0, hashNull = 0, hashMiss = 0;
            Log?.Invoke($"{system}: consola {consoleId}; juegos con logros={games.Count}; hashes de RA={byHashMap.Count}; ejemplo={string.Join(",", byHashMap.Keys.Take(3))}");
            await Parallel.ForEachAsync(systemRows, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1)), CancellationToken = token },
                (row, _) =>
                {
                    RaGame? game = null; string by = "hash";
                    string? hash = RaHasher.Compute(consoleId, row.Path);
                    if (hash is null) Interlocked.Increment(ref hashNull);
                    if (hash is not null && byHashMap.TryGetValue(hash, out var hit)) game = hit;
                    else
                    {
                        if (hash is not null && Interlocked.Increment(ref hashMiss) <= 3) Log?.Invoke($"{system}: sin coincidencia de hash · {row.RomName} · {hash}");
                        by = "nombre";
                        game = UniqueByTitle(byTitle, row.GameName) ?? UniqueByTitle(byTitle, Path.GetFileNameWithoutExtension(row.RomName));
                        if (hash is null && game is null) Interlocked.Increment(ref skipped);
                    }
                    string romKey = RomKey.Of(row);
                    index.SetMatch(romKey, game is null ? null : new RaMatch { GameId = game.Id, Title = game.Title, Achievements = game.Achievements, Points = game.Points, By = by });
                    if (game is null) Interlocked.Increment(ref without);
                    else if (by == "hash") Interlocked.Increment(ref byHash); else Interlocked.Increment(ref byName);
                    int n = Interlocked.Increment(ref done);
                    if (n % 5 == 0) live?.Report((Volatile.Read(ref byHash), Volatile.Read(ref byName), Volatile.Read(ref without)));
                    if (n % 5 == 0) report.Report($"{system}: {n:N0} de {systemRows.Count:N0} ROMs…");
                    return ValueTask.CompletedTask;
                });
            live?.Report((byHash, byName, without));
            Log?.Invoke($"{system}: ROMs={systemRows.Count}; hash no calculado={hashNull}; hash sin coincidencia={hashMiss}");
            totalRoms += systemRows.Count;
            index.MarkSystem(system);
            doneSystems++;
            percent.Report(doneSystems * 100 / Math.Max(1, systems.Count));
            index.Save();
        }

        if (credentials.IsComplete)
        {
            report.Report("Leyendo tu progreso de logros…");
            try { index.SetProgress(await client.GetUserProgressAsync(token)); } catch (RaException) { /* sin progreso: se sigue con lo demás */ }
            index.Save();
        }
        return new Result(doneSystems, totalRoms, byHash, byName, without, skipped);
    }

    private static RaGame? UniqueByTitle(Dictionary<string, List<RaGame>> map, string title)
    {
        string key = NormalizeTitle(title);
        return key.Length > 0 && map.TryGetValue(key, out var list) && list.Count == 1 ? list[0] : null;
    }

    // Título sin etiquetas, artículo «The», acentos ni signos: «Legend of Zelda, The (USA)» y «The Legend of Zelda» dan la misma clave.
    internal static string NormalizeTitle(string title)
    {
        string t = Regex.Replace(title, @"\([^)]*\)|\[[^\]]*\]|~[^~]*~", " ");
        t = Regex.Replace(t, @",\s*the\b", " ", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\bthe\b", " ", RegexOptions.IgnoreCase);
        string d = t.Normalize(NormalizationForm.FormD).ToLowerInvariant();
        return new string(d.Where(c => char.IsLetterOrDigit(c) && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
    }
}
