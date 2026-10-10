using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ESDERomCleaner;

// Definición editable de una supracategoría: un grupo de géneros (por su nombre exacto) y de palabras del nombre.
internal sealed class SupraDefinition
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Genres { get; set; } = new();
    // Una palabra o expresión regular por elemento; se busca en el nombre sin distinguir mayúsculas.
    public List<string> Keywords { get; set; } = new();
    public bool DefaultOn { get; set; }

    public SupraDefinition Clone() => new()
    {
        Id = Id, Title = Title, Description = Description, DefaultOn = DefaultOn,
        Genres = new List<string>(Genres), Keywords = new List<string>(Keywords)
    };
}

// Una regla de curado detecta ROMs «candidatas» a descartar por su género (en español, como lo guarda el gamelist) o por palabras
// del nombre. Solo señala candidatas: no modifica nada.
internal sealed class CurationRule
{
    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    private readonly string? genrePattern;
    private readonly string? namePattern;
    private readonly Regex? genre;
    private readonly Regex? name;
    // Regla con criterio propio: devuelve el motivo si la ROM es candidata y null si no.
    private readonly Func<RomRow, string?>? custom;

    public CurationRule(string id, string title, string description, Func<RomRow, string?> custom)
    {
        Id = id; Title = title; Description = description; this.custom = custom;
    }

    public CurationRule(string id, string title, string description, string? genrePattern, string? namePattern)
    {
        Id = id; Title = title; Description = description;
        this.genrePattern = genrePattern; this.namePattern = namePattern;
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        genre = genrePattern is null ? null : new Regex(genrePattern, options);
        name = namePattern is null ? null : new Regex(namePattern, options);
    }

    // Criterio exacto, para consultarlo como ayuda.
    public string Criteria => custom is not null ? "Regla de idiomas: se configura con el botón «Idiomas…»" :
        (genrePattern is null ? "" : $"Géneros: {genrePattern}") +
        (genrePattern is not null && namePattern is not null ? "\r\n" : "") +
        (namePattern is null ? "" : $"Palabras del nombre: {namePattern}");

    // Regla que señala todas las ROMs de un género concreto (los géneros salen de la propia colección).
    public static CurationRule ForGenre(string genreName) =>
        new("genre:" + genreName, genreName, "", "^" + Regex.Escape(genreName) + "$", null);

    // Regla de una supracategoría: sus géneros por nombre exacto y sus palabras como expresiones regulares (si alguna no es una
    // expresión válida se trata como texto literal).
    public static CurationRule FromSupra(SupraDefinition def)
    {
        string? genres = def.Genres.Count > 0 ? "^(?:" + string.Join("|", def.Genres.Select(Regex.Escape)) + ")$" : null;
        var words = def.Keywords.Select(k => k.Trim()).Where(k => k.Length > 0).Select(SafeRegex).ToList();
        return new CurationRule(def.Id, def.Title, def.Description, genres, words.Count > 0 ? string.Join("|", words) : null);
    }

    public static bool IsValidRegex(string pattern)
    {
        try { _ = new Regex(pattern); return true; }
        catch (ArgumentException) { return false; }
    }

    private static string SafeRegex(string pattern) => IsValidRegex(pattern) ? pattern : Regex.Escape(pattern);

    // Igual que en la ventana principal: sin género en el gamelist se muestra «Sin género».
    public static string EffectiveGenre(RomRow row) => string.IsNullOrWhiteSpace(row.Genre) ? "Sin género" : row.Genre;

    public bool Matches(RomRow row, out string reason)
    {
        reason = "";
        if (custom is not null)
        {
            string? why = custom(row);
            if (why is null) return false;
            reason = why;
            return true;
        }
        if (genre is not null)
        {
            string genreName = EffectiveGenre(row);
            var m = genre.Match(genreName);
            if (m.Success) { reason = $"género «{genreName}»"; return true; }
        }
        if (name is not null)
        {
            var m = name.Match($"{row.GameName} {Path.GetFileNameWithoutExtension(row.RomName)}");
            if (m.Success) { reason = $"el nombre contiene «{m.Value.Trim()}»"; return true; }
        }
        return false;
    }
}

internal static class CurationRuleSet
{
    // Supracategorías de partida. Los géneros se definen con un patrón y se convierten en nombres exactos de los géneros que existen
    // en la colección; las palabras del nombre quedan como lista editable. Las marcadas por defecto son las de la idea original.
    public static List<SupraDefinition> Defaults(IEnumerable<string> collectionGenres)
    {
        var genres = collectionGenres.ToList();
        SupraDefinition Make(string id, string title, string description, string? genrePattern, string[] keywords, bool defaultOn = false)
        {
            var rx = genrePattern is null ? null : new Regex(genrePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return new SupraDefinition
            {
                Id = id, Title = title, Description = description, DefaultOn = defaultOn,
                Genres = rx is null ? new() : genres.Where(g => rx.IsMatch(g)).ToList(),
                Keywords = keywords.ToList()
            };
        }

        return
        [
            Make("mesa-jp", "Juegos de mesa japoneses", "pachinko, pachislot, mahjong, shogi, go, dominó, hanafuda, reversi…", "juego de mesa",
                ["pachi", "mahjong", "mah-?jong", "janjan", "shogi", "\\bigo\\b", "go-?ban", "hanafuda", "domino", "othello", "reversi", "karuta"], true),
            Make("dep-minoritarios", "Deportes minoritarios", "carreras de caballos, cricket, bolos, billar, tenis de mesa, dardos, ciclismo, esquí, skate, náutico…",
                "caballos|bowling|bolos|billar|ping-?pong|ciclismo|esqu[ií]|skate|n[aá]utico|extremo|con animales",
                ["keiba", "horse ?rac", "jockey", "derby stallion", "winning post", "cricket", "bowling", "billiard", "snooker", "table tennis", "ping ?pong", "\\bdarts?\\b"], true),
            Make("dep-mayoritarios", "Deportes mayoritarios", "fútbol, baloncesto, tenis, fútbol americano (NFL), hockey (NHL), béisbol, voleibol, balón prisionero, golf…",
                "f[uú]tbol|baloncesto|tenis(?! de mesa)|b[eé]isbol|hockey|americano|voleibol|bal[oó]n prisionero|balonmano|golf|multideporte",
                ["soccer", "\\bfifa\\b", "world cup", "\\bnba\\b", "basket", "dunk", "tennis", "\\bnfl\\b", "madden", "american football", "college football", "ncaa football",
                 "gridiron", "super bowl", "\\bnhl\\b", "hockey", "baseball", "\\bmlb\\b", "yakyu", "volley", "dodgeball", "\\bgolf\\b"]),
            Make("novelas", "Novelas gráficas (visuales)", "novelas visuales y juegos de lectura", "novela visual", ["visual novel", "sound novel", "\\bnovel\\b"]),
            Make("estrategia", "Juegos de estrategia", "estrategia, RPG táctico, construcción y gestión", "estrategia|t[aá]ctico|construcci[oó]n y gesti[oó]n", ["strateg", "tactics", "wargame"]),
            Make("quiz", "Quiz y concursos", "preguntas y respuestas, trivia", "quiz|trivia|concurso|preguntas", ["quiz", "trivia"], true),
            Make("educativos", "Educativos e infantiles", "aprender letras, números, ortografía…", "educaci|infantil",
                ["alphabet", "spelling", "educational", "\\blearn(ing)?\\b", "sesame street", "barney", "\\bmath(s|ematics)?\\b"], true),
            Make("casino", "Casino y azar", "tragaperras, ruleta, póker, bingo", "casino|azar|apuesta", ["casino", "roulette", "slot machine", "blackjack", "poker", "keno", "bingo"]),
            Make("combate", "Deportes de combate", "boxeo y lucha libre", "boxeo|lucha libre", ["boxing", "wrestl", "\\bwwf\\b", "\\bwwe\\b"]),
            Make("adultos", "Adultos", "contenido para adultos", "adultos", []),
            Make("ritmo", "Ritmo, música y baile", "juegos musicales y de baile", "ritmo|m[uú]sica|baile", ["dance", "karaoke", "\\bbeat\\b"]),
            Make("pesca-caza", "Pesca y caza", "simuladores de pesca y caza", "pesca|caza", ["fishing", "hunting", "bass"]),
            Make("pinball", "Pinball", "mesas de pinball", "pinball", ["pinball"]),
            Make("sin-clasificar", "Sin clasificar o varios", "sin género, varios, compilaciones", "sin g[eé]nero|^varios|compilaci", []),
        ];
    }
}

// Las supracategorías editadas por el usuario se guardan aparte; mientras no exista el archivo se usan las de partida.
internal static class SupraStore
{
    private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ESDERomCleaner", "curation_supracategories.json");

    public static List<SupraDefinition>? Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return null;
            var list = JsonSerializer.Deserialize<List<SupraDefinition>>(File.ReadAllText(StorePath));
            return list is { Count: > 0 } ? list : null;
        }
        catch { return null; }
    }

    public static void Save(IEnumerable<SupraDefinition> definitions)
    {
        string? temp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            temp = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(definitions, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temp, StorePath, overwrite: true);
        }
        catch { }
        finally { if (temp is not null && File.Exists(temp)) try { File.Delete(temp); } catch { } }
    }
}

// Perfil de curado: una instantánea con nombre de lo marcado, las ROMs excluidas a mano y las supracategorías vigentes.
internal sealed class CurationProfile
{
    public string Name { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public List<string> Checked { get; set; } = new();
    public List<string> Excluded { get; set; } = new();
    public List<SupraDefinition> Supra { get; set; } = new();
}

internal static class CurationProfileStore
{
    private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ESDERomCleaner", "curation_profiles.json");

    public static List<CurationProfile> Load()
    {
        try { return File.Exists(StorePath) ? JsonSerializer.Deserialize<List<CurationProfile>>(File.ReadAllText(StorePath)) ?? new() : new(); }
        catch { return new(); }
    }

    // Devuelve false si no se pudo escribir, para que el llamador lo avise.
    public static bool Save(IEnumerable<CurationProfile> profiles)
    {
        string? temp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            temp = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temp, StorePath, overwrite: true);
            return true;
        }
        catch { return false; }
        finally { if (temp is not null && File.Exists(temp)) try { File.Delete(temp); } catch { } }
    }
}
