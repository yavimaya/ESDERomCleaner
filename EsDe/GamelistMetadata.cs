using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ESDERomCleaner;

// Metadatos de un juego tal como los guarda ES-DE en el gamelist.xml. Un campo vacío significa «no tocar lo que ya hay».
internal sealed record GamelistMetadata(string Name, string Desc, string Rating, string ReleaseDate, string Developer, string Publisher, string Genre, string Players)
{
    // Idioma en que se descargan los textos (descripción, categoría): el de la interfaz primero, para que los gamelists que se generen a partir de ahora salgan en ese idioma.
    private static string[] Languages => Loc.English ? ["en", "es", "fr", "de", "it", "pt"] : ["es", "en", "fr", "de", "it", "pt"];

    public IEnumerable<(string Field, string Value)> Fields()
    {
        yield return ("name", Name);
        yield return ("desc", Desc);
        yield return ("rating", Rating);
        yield return ("releasedate", ReleaseDate);
        yield return ("developer", Developer);
        yield return ("publisher", Publisher);
        yield return ("genre", Genre);
        yield return ("players", Players);
    }

    // Convierte lo que devuelve ScreenScraper al formato de ES-DE, eligiendo el idioma y la región más adecuados para la ROM.
    public static GamelistMetadata From(SsGame game, IReadOnlyList<string> romRegions)
    {
        string[] nameOrder = romRegions.Concat(["wor", "us", "eu", "jp", "ss"]).ToArray();
        string[] dateOrder = romRegions.Concat(["wor", "us", "eu", "jp"]).ToArray();
        return new GamelistMetadata(
            Clean(game.Names.Count > 0 ? SsPick.Text(game.Names, nameOrder) : game.Name),
            Clean(game.Synopsis.Count > 0 ? SsPick.Text(game.Synopsis, Languages) : ""),
            ConvertRating(game.Rating),
            ReleaseDateOf(game.Dates.Count > 0 ? SsPick.Text(game.Dates, dateOrder) : ""),
            Clean(game.Developer),
            Clean(game.Publisher),
            Clean(ComposeGenre(game)),
            Clean(game.Players));
    }

    // ScreenScraper puntúa sobre 20 y ES-DE usa de 0 a 1 con un decimal (17/20 → 0.9).
    private static string ConvertRating(string note)
    {
        if (!double.TryParse(note, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value <= 0) return "";
        return (Math.Round(Math.Clamp(value / 20.0, 0, 1) * 10, MidpointRounding.AwayFromZero) / 10).ToString("0.0", CultureInfo.InvariantCulture);
    }

    // «1995-03-15», «1995-03» o «1995» → «19950315T000000» (los datos que faltan se completan con 01).
    private static string ReleaseDateOf(string text)
    {
        var m = Regex.Match(text.Trim(), @"^(\d{4})(?:-(\d{2}))?(?:-(\d{2}))?");
        if (!m.Success) return "";
        string month = m.Groups[2].Success ? m.Groups[2].Value : "01";
        string day = m.Groups[3].Success ? m.Groups[3].Value : "01";
        return $"{m.Groups[1].Value}{month}{day}T000000";
    }

    // Igual que en tus gamelists: «Categoría / Subgénero» (p. ej. «Deportes / Fútbol»), o solo la categoría si no hay subgénero.
    private static string ComposeGenre(SsGame game)
    {
        if (game.Genres.Count == 0) return "";
        string NameOf(SsGenre genre) => SsPick.Text(genre.Names, Languages);
        var child = game.Genres.FirstOrDefault(g => g.ParentId.Length > 0 && g.ParentId != "0");
        if (child is null) return NameOf(game.Genres.FirstOrDefault(g => g.Principal) ?? game.Genres[0]);
        var parent = game.Genres.FirstOrDefault(g => g.Id == child.ParentId);
        // ScreenScraper ya suele dar el subgénero con el nombre del padre delante («Plataformas / Corre y Salta»).
        string childName = NameOf(child);
        if (parent is null || childName.Contains('/')) return childName;
        return NameOf(parent) + " / " + childName;
    }

    // Sin caracteres que el XML no admite.
    private static string Clean(string text) => new string(text.Where(c => XmlConvert.IsXmlChar(c) || char.IsSurrogate(c)).ToArray()).Trim();
}

// Escribe metadatos en un gamelist.xml respetando el resto del archivo byte a byte: el contenido se envuelve en una raíz
// sintética (los gamelists de ES-DE pueden tener varios elementos de primer nivel), se conserva el espacio en blanco y se
// escribe de forma atómica tras volver a leer el resultado para validarlo.
internal static class GamelistMetadataWriter
{
    // Devuelve cuántos juegos se actualizaron. «byPath» va indexado por la ruta relativa normalizada (PathRules.Relative).
    public static int Apply(string gamelistPath, IReadOnlyDictionary<string, GamelistMetadata> byPath, bool backup, List<string> errors)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(gamelistPath))!;
        if (DeletionService.HasReparsePointInPath(gamelistPath, directory))
            throw new InvalidOperationException("El gamelist.xml es un enlace simbólico o unión; no se modifica.");

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        var doc = Load(File.ReadAllText(gamelistPath), settings, out string declaration);
        var games = doc.Descendants()
            .Where(g => g.Name.LocalName == "game")
            .GroupBy(g => PathRules.Relative(g.Elements().FirstOrDefault(e => e.Name.LocalName == "path")?.Value))
            .ToDictionary(g => g.Key, g => g.ToList());

        int updated = 0;
        foreach (var (relative, metadata) in byPath)
        {
            if (!games.TryGetValue(relative, out var matches) || matches.Count != 1)
            {
                errors.Add($"{relative}: {(matches?.Count ?? 0)} entradas en el gamelist (se necesita exactamente una)");
                continue;
            }
            foreach (var (field, value) in metadata.Fields())
                if (value.Length > 0) SetField(matches[0], field, value);
            updated++;
        }
        if (updated == 0) return 0;

        string xml = Serialize(doc, declaration);
        _ = Load(xml, settings, out _);
        if (backup) File.Copy(gamelistPath, gamelistPath + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N"), overwrite: false);

        string temp = Path.Combine(directory, ".gamelist_" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, xml, new UTF8Encoding(false));
            File.Move(temp, gamelistPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) try { File.Delete(temp); } catch { }
        }
        return updated;
    }

    // Pone los mismos campos (p. ej. nogamecount y nomultiscrape = true) en los juegos con esas rutas relativas. Mismo cuidado que Apply: raíz sintética,
    // formato intacto, escritura atómica y comprobación del resultado. Devuelve cuántos juegos cambió.
    public static int SetFields(string gamelistPath, IEnumerable<string> relativePaths, IReadOnlyDictionary<string, string> fields, bool backup, List<string> errors)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(gamelistPath))!;
        if (DeletionService.HasReparsePointInPath(gamelistPath, directory))
            throw new InvalidOperationException("El gamelist.xml es un enlace simbólico o unión; no se modifica.");

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        var doc = Load(File.ReadAllText(gamelistPath), settings, out string declaration);
        var games = doc.Descendants()
            .Where(g => g.Name.LocalName == "game")
            .GroupBy(g => PathRules.Relative(g.Elements().FirstOrDefault(e => e.Name.LocalName == "path")?.Value))
            .ToDictionary(g => g.Key, g => g.ToList());

        int updated = 0;
        foreach (string relative in relativePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!games.TryGetValue(relative, out var matches) || matches.Count != 1)
            {
                errors.Add($"{relative}: {(matches?.Count ?? 0)} entradas en el gamelist (se necesita exactamente una)");
                continue;
            }
            foreach (var (field, value) in fields) SetField(matches[0], field, value);
            updated++;
        }
        if (updated == 0) return 0;

        string xml = Serialize(doc, declaration);
        _ = Load(xml, settings, out _);
        if (backup) File.Copy(gamelistPath, gamelistPath + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N"), overwrite: false);
        string temp = Path.Combine(directory, ".gamelist_" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, xml, new UTF8Encoding(false));
            File.Move(temp, gamelistPath, overwrite: true);
        }
        finally { if (File.Exists(temp)) try { File.Delete(temp); } catch { } }
        return updated;
    }

    private static void SetField(XElement game, string field, string value)
    {
        var existing = game.Elements().FirstOrDefault(e => e.Name.LocalName == field);
        if (existing is not null) { existing.Value = value; return; }

        // El campo nuevo se coloca al final con la misma sangría que el último elemento del juego.
        var last = game.Elements().LastOrDefault();
        var element = new XElement(field, value);
        if (last is null) { game.Add(element); return; }
        string indent = last.PreviousNode is XText text && string.IsNullOrWhiteSpace(text.Value) ? text.Value : "\n\t\t";
        last.AddAfterSelf(new XText(indent), element);
    }

    private static XDocument Load(string xml, XmlReaderSettings settings, out string declaration)
    {
        var match = Regex.Match(xml, @"^\s*<\?xml\b.*?\?>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        declaration = match.Success ? match.Value : "";
        string content = match.Success ? xml[match.Length..] : xml;
        using var input = new StringReader("<__esde_document>" + content + "</__esde_document>");
        using var reader = XmlReader.Create(input, settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static string Serialize(XDocument document, string declaration)
    {
        if (document.Root is null || document.Root.Name.LocalName != "__esde_document")
            throw new InvalidOperationException("La estructura interna del gamelist no es válida.");
        return declaration + string.Concat(document.Root.Nodes().Select(node => node.ToString(SaveOptions.DisableFormatting)));
    }
}
