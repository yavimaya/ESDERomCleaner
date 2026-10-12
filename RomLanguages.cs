using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ESDERomCleaner;

// Clave estable de una ROM dentro de su sistema (la ruta que figura en el gamelist, normalizada).
internal static class RomKey
{
    public static string Of(RomRow row) =>
        $"{row.System}|{(row.GamelistPath.Length > 0 ? PathRules.Relative(row.GamelistPath) : row.RomName.ToLowerInvariant())}";
}

// Idiomas y regiones de una ROM tal como los devuelve ScreenScraper. Una lista de idiomas vacía significa «ScreenScraper no los indica».
internal sealed class RomLanguageInfo
{
    public List<string> Languages { get; set; } = new();
    public List<string> Regions { get; set; } = new();
    public DateTime FetchedAt { get; set; }
}

// Índice propio de la aplicación (no se escribe nada en los gamelists, que no tienen campo de idioma) que se rellena desde la
// descarga masiva con la opción «Idiomas» y se usa en las reglas de curado.
internal sealed class RomLanguageIndex
{
    private readonly object gate = new();
    private Dictionary<string, RomLanguageInfo> map = new(StringComparer.OrdinalIgnoreCase);
    private bool dirty;

    private static string StorePath => Path.Combine(AppPaths.Data, "rom_languages.json");

    public static RomLanguageIndex Load()
    {
        var index = new RomLanguageIndex();
        try
        {
            if (!File.Exists(StorePath)) return index;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, RomLanguageInfo>>(File.ReadAllText(StorePath));
            if (loaded is not null) index.map = new Dictionary<string, RomLanguageInfo>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return index;
    }

    public int Count { get { lock (gate) return map.Count; } }

    public bool Has(string key) { lock (gate) return map.ContainsKey(key); }

    public bool TryGet(string key, out RomLanguageInfo info)
    {
        lock (gate) return map.TryGetValue(key, out info!);
    }

    public void Set(string key, RomLanguageInfo info)
    {
        lock (gate) { map[key] = info; dirty = true; }
    }

    // Escritura atómica; solo si hay cambios.
    public void Save()
    {
        lock (gate)
        {
            if (!dirty) return;
            string? temp = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                temp = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(map), new UTF8Encoding(false));
                File.Move(temp, StorePath, overwrite: true);
                dirty = false;
            }
            catch { }
            finally { if (temp is not null && File.Exists(temp)) try { File.Delete(temp); } catch { } }
        }
    }
}

internal static class LanguageCatalog
{
    // Idiomas que se ofrecen al elegir cuáles se entienden, con su nombre en español.
    public static readonly (string Code, string Name)[] Common =
    [
        ("es", "Español"), ("en", "Inglés"), ("fr", "Francés"), ("de", "Alemán"), ("it", "Italiano"), ("pt", "Portugués"),
        ("ca", "Catalán"), ("ja", "Japonés"), ("ko", "Coreano"), ("zh", "Chino"), ("nl", "Neerlandés"), ("sv", "Sueco"),
        ("ru", "Ruso"), ("pl", "Polaco")
    ];

    public static string NameOf(string code) =>
        Common.FirstOrDefault(c => c.Code.Equals(code, StringComparison.OrdinalIgnoreCase)).Name ?? code.ToUpperInvariant();
}

// Averigua en qué idiomas está una ROM: primero lo que dice ScreenScraper; si no hay datos y se permite, las etiquetas del nombre del
// archivo ((En,Fr,De), (Japan), (USA)…) y, como último recurso, el idioma que suele corresponder a la región.
internal static class LanguageResolver
{
    private static readonly HashSet<string> KnownCodes = new(StringComparer.OrdinalIgnoreCase)
        { "en", "fr", "de", "es", "it", "pt", "ja", "ko", "zh", "nl", "sv", "ru", "da", "no", "fi", "pl", "ca", "tr", "cs", "hu", "ar", "el" };

    private static readonly Dictionary<string, string> RegionWordToLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["usa"] = "en", ["world"] = "en", ["europe"] = "en", ["uk"] = "en", ["australia"] = "en", ["canada"] = "en",
        ["spain"] = "es", ["france"] = "fr", ["germany"] = "de", ["italy"] = "it", ["japan"] = "ja", ["korea"] = "ko",
        ["china"] = "zh", ["taiwan"] = "zh", ["brazil"] = "pt", ["netherlands"] = "nl", ["sweden"] = "sv", ["russia"] = "ru"
    };

    private static readonly Dictionary<string, string> RegionCodeToLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["us"] = "en", ["wor"] = "en", ["eu"] = "en", ["uk"] = "en", ["au"] = "en", ["ca"] = "en", ["sp"] = "es", ["es"] = "es",
        ["fr"] = "fr", ["de"] = "de", ["it"] = "it", ["jp"] = "ja", ["kr"] = "ko", ["cn"] = "zh", ["tw"] = "zh", ["br"] = "pt",
        ["nl"] = "nl", ["se"] = "sv", ["ru"] = "ru"
    };

    public static (List<string> Languages, string Source) Resolve(RomRow row, RomLanguageIndex index, bool useFilename)
    {
        RomLanguageInfo? info = index.TryGet(RomKey.Of(row), out var found) ? found : null;
        if (info is { Languages.Count: > 0 }) return (info.Languages, "ScreenScraper");
        if (!useFilename) return (new(), "");

        // Etiquetas del nombre del archivo.
        string name = Path.GetFileNameWithoutExtension(row.RomName);
        var explicitLanguages = new List<string>();
        var fromRegion = new List<string>();
        foreach (Match tag in Regex.Matches(name, @"\(([^)]*)\)"))
            foreach (string part in tag.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Length == 2 && KnownCodes.Contains(part)) { if (!explicitLanguages.Contains(part.ToLowerInvariant())) explicitLanguages.Add(part.ToLowerInvariant()); }
                else if (RegionWordToLanguage.TryGetValue(part, out var language) && !fromRegion.Contains(language)) fromRegion.Add(language);
            }
        if (explicitLanguages.Count > 0) return (explicitLanguages, "nombre del archivo");
        if (fromRegion.Count > 0) return (fromRegion, "región del nombre del archivo");

        // Regiones que indicó ScreenScraper aunque no diera idiomas.
        if (info is { Regions.Count: > 0 })
        {
            var languages = info.Regions.Select(r => RegionCodeToLanguage.GetValueOrDefault(r)).Where(l => l is not null).Select(l => l!).Distinct().ToList();
            if (languages.Count > 0) return (languages, "región (ScreenScraper)");
        }
        return (new(), "");
    }
}

// Qué idiomas entiende el usuario y cómo se aplica la regla de curado por idioma.
internal sealed class CurationLanguageSettings
{
    public List<string> Understood { get; set; } = new() { "es", "en" };
    // Si es true, solo se señalan los géneros que dependen del texto (rol, aventura, novela visual, estrategia…).
    public bool OnlyTextGenres { get; set; }
    public bool UseFilename { get; set; } = true;

    public static CurationLanguageSettings Load()
    {
        return AppSettings.Get("CurationLanguages", new CurationLanguageSettings());
    }

    public void Save() => AppSettings.Set("CurationLanguages", this);

    private static readonly Regex TextGenres = new("rol|rpg|aventura|novela|estrategia|t[aá]ctico|quiz|trivia|educaci|ingenio|juego de mesa|construcci[oó]n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsTextGenre(string genre) => TextGenres.IsMatch(genre);
}
