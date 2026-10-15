using System.Text.RegularExpressions;

namespace ESDERomCleaner;

// Categorías de los juegos de arcade (MAME) sacadas de un catver.ini local, sin preguntar a ScreenScraper. Cada DAT de MAME usa el catver.ini de su propia
// versión (repositorios de libretro, con la lista de categorías de Progetto-SNAPS). Se descarga la primera vez que hace falta y se renueva cada seis meses. Las categorías (en inglés: «Shooter / Flying Vertical») se traducen a los géneros de ScreenScraper en español que usan tus gamelists
// («Disparos»), para que las reglas de curado las traten igual que a las demás.
internal static class ArcadeCategories
{
    // Cada DAT de MAME tiene su catver.ini en el repositorio de libretro de esa misma versión (las listas difieren: las versiones viejas solo traen los
    // juegos que ya existían). «MAME (actual)» usa el del repositorio más reciente de libretro (2016), el más cercano que publican.
    private static readonly Dictionary<string, string> Repos = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MAME 2000"] = "mame2000-libretro", ["MAME 2003"] = "mame2003-libretro", ["MAME 2003-Plus"] = "mame2003-plus-libretro",
        ["MAME 2010"] = "mame2010-libretro", ["MAME 2015"] = "mame2015-libretro", ["MAME (actual)"] = "mame2016-libretro"
    };

    private static string UrlOf(string repo) => $"https://raw.githubusercontent.com/libretro/{repo}/master/metadata/catver.ini";
    private static string FileOf(string repo) => Path.Combine(AppPaths.Data, "catver", repo + ".ini");
    private static readonly Dictionary<string, Dictionary<string, string>> cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    // ¿Se puede categorizar localmente lo identificado con este DAT? Solo si es un DAT de MAME con catver conocido y está seleccionado.
    public static bool Applies(string? datName) =>
        datName is not null && Repos.ContainsKey(datName) &&
        RomDatabase.Allowed(DatSettings.Load()).Any(s => s.Kind == "mame" && s.Name.Equals(datName, StringComparison.OrdinalIgnoreCase));

    // Mapa «nombre del juego → género» del catver de ese DAT. Descarga el archivo si falta o está viejo (más de seis meses); si no se puede y no hay copia,
    // devuelve un mapa vacío.
    public static async Task<Dictionary<string, string>> LoadAsync(string datName, CancellationToken token)
    {
        if (!Repos.TryGetValue(datName, out var repo)) return [];
        lock (Gate) if (cache.TryGetValue(repo, out var cached)) return cached;
        string file = FileOf(repo);
        try
        {
            bool stale = !File.Exists(file) || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(180);
            if (stale) await DownloadAsync(repo, file, token);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* sin conexión: se usa la copia que haya, si la hay */ }
        var map = await Task.Run(() => Parse(file), token);
        lock (Gate) cache[repo] = map;
        return map;
    }

    private static async Task DownloadAsync(string repo, string file, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ESDERomCleaner");
        byte[] data = await http.GetByteArrayAsync(UrlOf(repo), token);
        if (data.Length < 20_000) throw new InvalidDataException("El catver.ini descargado es demasiado pequeño."); // por si devuelven una página de error
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, data, token);
        File.Move(temp, file, overwrite: true);
    }

    private static Dictionary<string, string> Parse(string file)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(file)) return map;
        bool inCategory = false;
        foreach (var raw in File.ReadLines(file))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            if (line[0] == '[') { inCategory = line.Equals("[Category]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inCategory) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string name = line[..eq].Trim(), category = line[(eq + 1)..].Trim();
            string genre = Translate(category);
            if (name.Length > 0 && genre.Length > 0) map[name] = genre;
        }
        return map;
    }

    // «Categoría / Subcategoría» de catver → género de ScreenScraper en español (con subgénero cuando hay equivalente claro).
    internal static string Translate(string category)
    {
        category = Regex.Replace(category, @"\s*\*\s*Mature\s*\*", "", RegexOptions.IgnoreCase).Trim();
        var parts = category.Split(" / ", 2, StringSplitOptions.TrimEntries);
        string top = parts[0], sub = parts.Length > 1 ? parts[1] : "";
        switch (top)
        {
            case "Shooter": return sub.StartsWith("Gun", StringComparison.OrdinalIgnoreCase) ? "Disparos / Con periférico" : "Disparos";
            case "Fighter": return "Lucha";
            case "Platform": case "Climbing": return sub.StartsWith("Run Jump", StringComparison.OrdinalIgnoreCase) ? "Plataformas / Corre y Salta" : "Plataformas";
            case "Driving": return "Carreras";
            case "Puzzle": return "Puzzle";
            case "Maze": return "Laberinto";
            case "Casino": return "Casino";
            case "Tabletop": return "Juego de mesa";
            case "Quiz": return "Preguntas y respuestas";
            case "Rhythm": return "Música";
            case "Pinball": return "Pinball";
            case "Multiplay": return "Compilación";
            case "Sports": return SportName(sub) is { Length: > 0 } sport ? "Deportes / " + sport : "Deportes";
            case "Electromechanical": return sub.StartsWith("Pinball", StringComparison.OrdinalIgnoreCase) ? "Pinball" : "Varios";
            case "Ball & Paddle": case "Misc.": case "Print Club": case "Synth": case "Utilities": case "Not Classified": case "Multi-cart Board":
            case "System": case "Home Systems": case "Portable Systems": case "Professional Systems": case "Development Systems":
                return "Varios";
            default: return "Varios";
        }
    }

    private static string SportName(string sub)
    {
        string s = sub.ToLowerInvariant();
        return s switch
        {
            _ when s.StartsWith("soccer") => "Fútbol",
            _ when s.StartsWith("baseball") => "Béisbol",
            _ when s.StartsWith("basketball") => "Baloncesto",
            _ when s.StartsWith("golf") => "Golf",
            _ when s.StartsWith("tennis") => "Tenis",
            _ when s.StartsWith("football") => "Fútbol americano",
            _ when s.StartsWith("boxing") => "Boxeo",
            _ when s.StartsWith("bowling") => "Bolos",
            _ when s.StartsWith("volleyball") => "Voleibol",
            _ when s.StartsWith("wrestling") => "Lucha libre",
            _ when s.StartsWith("track") => "Atletismo",
            _ when s.StartsWith("skiing") => "Esquí",
            _ when s.StartsWith("billiard") || s.StartsWith("pool") => "Billar",
            _ when s.StartsWith("fishing") => "Pesca",
            _ when s.StartsWith("hunting") => "Caza",
            _ when s.StartsWith("cycling") => "Ciclismo",
            _ => ""
        };
    }
}
