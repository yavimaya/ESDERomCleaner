using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ESDERomCleaner;

internal static class GamelistParser
{
    public static List<GameEntry> Parse(string file, CancellationToken token)
    {
        var result = new List<GameEntry>();
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true };
            using var reader = XmlReader.Create(file, settings);
            var doc = XDocument.Load(reader);
            foreach (var game in doc.Descendants().Where(e => e.Name.LocalName.Equals("game", StringComparison.OrdinalIgnoreCase)))
            {
                token.ThrowIfCancellationRequested();
                string? Field(string name) => game.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
                Add(result, Field("path"), Field("name"), Field("genre"), Field("desc"));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Fallback tolerante de v79 para gamelists con XML mal formado.
            result.Clear();
            foreach (Match game in Regex.Matches(File.ReadAllText(file), @"(?is)<game\b[^>]*>(.*?)</game\s*>"))
            {
                token.ThrowIfCancellationRequested();
                string? Field(string tag)
                {
                    var m = Regex.Match(game.Groups[1].Value, $@"(?is)<{tag}\b[^>]*>\s*(.*?)\s*</{tag}\s*>");
                    return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : null;
                }
                Add(result, Field("path"), Field("name"), Field("genre"), Field("desc"));
            }
        }
        return result;
    }
    private static void Add(List<GameEntry> result, string? rawPath, string? rawName, string? rawGenre, string? rawDesc)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return;
        var path = WebUtility.HtmlDecode(rawPath.Trim());
        var relative = PathRules.Relative(path);
        if (relative.Length == 0) return;
        var key = PathRules.Name(path);
        result.Add(new GameEntry(path, string.IsNullOrWhiteSpace(rawName) ? key : WebUtility.HtmlDecode(rawName),
            string.IsNullOrWhiteSpace(rawGenre) ? "Sin género" : WebUtility.HtmlDecode(rawGenre),
            rawDesc is null ? "" : WebUtility.HtmlDecode(rawDesc), relative, key));
    }
}
