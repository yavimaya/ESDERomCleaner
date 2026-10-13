using System.Text;
using System.Text.RegularExpressions;

namespace ESDERomCleaner;

internal static class PathRules
{
    public static string Relative(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = Regex.Replace(text.Trim().Trim('"').Replace('\\', '/'), @"^\./+", "");
        return Regex.Replace(s, @"/+", "/").Trim('/').ToLowerInvariant();
    }
    public static string Name(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = Path.GetFileNameWithoutExtension(text.Trim()).Replace('\\', ' ').Replace('/', ' ').Replace('_', ' ').Normalize(NormalizationForm.FormD);
        s = new string(s.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        return Regex.Replace(s.ToLowerInvariant(), @"\s+", " ").Trim();
    }
    public static string AccessoryMatchKey(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/').Trim('/');
        string directory = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? "";
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(normalized);
        // Conserva literalmente el nombre; solo el directorio se normaliza.
        return Relative(directory) + "/" + fileNameWithoutExtension;
    }
    public static bool Inside(string child, string directory)
    {
        var c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var d = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return c.StartsWith(d, StringComparison.OrdinalIgnoreCase);
    }
}
