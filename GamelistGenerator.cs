using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace ESDERomCleaner;

// Append: el gamelist ya tiene juegos y solo se añaden las ROMs nuevas; si no, se crea de cero.
public sealed record GeneratedGamelist(string System, string GamelistPath, List<string> RomPaths, bool Append);

// Crea gamelist.xml mínimos (path + name) a partir de los archivos de Roms\<sistema>, o añade a los que ya existen las ROMs que les faltan.
public static class GamelistGenerator
{
    private static readonly HashSet<string> IgnoredNames = new(StringComparer.OrdinalIgnoreCase)
        { "gamelist.xml", "systeminfo.txt", "noload.txt", "flatten.txt", "systems.txt", "thumbs.db", "desktop.ini" };

    // Extensiones que nunca son un juego: textos, imágenes, vídeos, partidas guardadas y archivos de ES-DE.
    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".xml", ".nfo", ".log", ".ini", ".db", ".pdf", ".commands", ".srm", ".sav", ".fs", ".rtc", ".mcr", ".mcd", ".eep", ".sra",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".mp4", ".avi", ".mkv", ".mp3", ".tmp", ".bak", ".old",
        ".state", ".dat", ".json", ".cfg", ".htm", ".html", ".url", ".md5", ".sfv", ".sha1", ".cht", ".diz", ".doc", ".rtf", ".lnk", ".ico", ".svg", ".wav", ".ogg", ".flac"
    };

    // Archivos que acompañan a las ROMs pero no lo son (textos, imágenes, vídeos, partidas guardadas, listas de ES-DE…): no se añaden.
    public static bool IsNotRom(string path)
    {
        string name = Path.GetFileName(path);
        string ext = Path.GetExtension(path);
        return IgnoredNames.Contains(name) || name.StartsWith('.') || IgnoredExtensions.Contains(ext)
               || System.Text.RegularExpressions.Regex.IsMatch(ext, @"^\.(state|st)\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public static List<GeneratedGamelist> Plan(string root, CancellationToken token)
    {
        var plan = new List<GeneratedGamelist>();
        string romRoot = Path.Combine(root, "Roms");
        foreach (var systemDir in Directory.EnumerateDirectories(romRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            string system = Path.GetFileName(systemDir);
            string listPath = Path.Combine(root, "ES-DE", "gamelists", system, "gamelist.xml");
            List<GameEntry> existing = [];
            if (File.Exists(listPath))
            {
                try { existing = GamelistParser.Parse(listPath, token); }
                catch (OperationCanceledException) { throw; }
                catch { continue; } // si no se puede leer, no se toca
            }
            var roms = FindRoms(systemDir, token);
            if (existing.Count == 0)
            {
                if (roms.Count > 0) plan.Add(new GeneratedGamelist(system, listPath, roms, Append: false));
                continue;
            }

            // Gamelist con juegos: solo se añaden las ROMs que no figuran (por ruta ni por nombre, igual que hace el análisis).
            var paths = new HashSet<string>(existing.Select(e => e.RelativeKey), StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(existing.Select(e => e.NameKey), StringComparer.OrdinalIgnoreCase);
            var missing = roms
                .Where(r => !paths.Contains(PathRules.Relative(Path.GetRelativePath(systemDir, r))) && !names.Contains(PathRules.Name(r)))
                .ToList();
            if (missing.Count > 0) plan.Add(new GeneratedGamelist(system, listPath, missing, Append: true));
        }
        return plan;
    }

    private static List<string> FindRoms(string systemDir, CancellationToken token)
    {
        var files = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(systemDir, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                files.Add(file);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        // Archivos que solo son parte de otro juego (pistas de un .cue/.gdi, discos de un .m3u).
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in files.Where(f => Path.GetExtension(f).Equals(".cue", StringComparison.OrdinalIgnoreCase)
                                           || Path.GetExtension(f).Equals(".gdi", StringComparison.OrdinalIgnoreCase)
                                           || Path.GetExtension(f).Equals(".m3u", StringComparison.OrdinalIgnoreCase)))
            foreach (var name in ReferencedFiles(list))
            {
                try { parts.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(list)!, name.Replace('/', Path.DirectorySeparatorChar)))); }
                catch { }
            }

        // Con el sistema reconocido se usan sus extensiones oficiales de ES-DE (es_systems.xml); si no, se descartan solo las que nunca son juegos.
        string system = Path.GetFileName(systemDir);
        bool known = EsDeSystemExtensions.BySystem.TryGetValue(system, out var allowed);
        bool IsRom(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return known ? allowed!.Contains(ext) : !IgnoredExtensions.Contains(ext);
        }

        // Carpetas cuyo nombre acaba en una extensión del sistema (p. ej. «Juego.ps3») son un juego en sí mismas.
        var gameDirs = new List<string>();
        if (known)
        {
            try
            {
                gameDirs = Directory.EnumerateDirectories(systemDir, "*", SearchOption.AllDirectories)
                    .Where(d => allowed!.Contains(Path.GetExtension(d).ToLowerInvariant()))
                    .Where(d => !gameDirs.Any(g => d.StartsWith(g + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        var roms = files
            .Where(f => !IgnoredNames.Contains(Path.GetFileName(f))
                     && !Path.GetFileName(f).StartsWith('.')
                     && IsRom(f)
                     && !parts.Contains(Path.GetFullPath(f))
                     && !gameDirs.Any(g => f.StartsWith(g + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .Concat(gameDirs)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return roms;
    }

    private static IEnumerable<string> ReferencedFiles(string listFile)
    {
        string[] lines;
        try { lines = File.ReadAllLines(listFile); }
        catch { yield break; }
        string ext = Path.GetExtension(listFile).ToLowerInvariant();
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (ext == ".m3u") { yield return line; continue; }
            if (ext == ".cue")
            {
                var m = Regex.Match(line, "^FILE\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (m.Success) yield return m.Groups[1].Value;
                continue;
            }
            foreach (Match m in Regex.Matches(line, "\"([^\"]+)\"|(\\S+\\.(?:bin|raw))", RegexOptions.IgnoreCase))
                yield return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        }
    }

    // Añade las ROMs al final de un gamelist existente sin tocar el resto del archivo.
    private static void AppendTo(string root, GeneratedGamelist item, bool backup)
    {
        string systemDir = Path.Combine(root, "Roms", item.System);
        string text = File.ReadAllText(item.GamelistPath);
        int close = text.LastIndexOf("</gameList>", StringComparison.OrdinalIgnoreCase);
        if (close < 0) throw new InvalidDataException($"El gamelist de «{item.System}» no tiene una etiqueta de cierre </gameList> estándar; no se modificó.");
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var snippet = new StringBuilder();
        foreach (var rom in item.RomPaths)
        {
            string relative = "./" + Path.GetRelativePath(systemDir, rom).Replace('\\', '/');
            snippet.Append('\t').Append("<game>").Append(nl)
                   .Append("\t\t<path>").Append(System.Security.SecurityElement.Escape(relative)).Append("</path>").Append(nl)
                   .Append("\t\t<name>").Append(System.Security.SecurityElement.Escape(Path.GetFileNameWithoutExtension(rom))).Append("</name>").Append(nl)
                   .Append('\t').Append("</game>").Append(nl);
        }
        string before = text[..close];
        if (before.Length > 0 && before[^1] != '\n') before += nl;
        string result = before + snippet + text[close..];

        string directory = Path.GetDirectoryName(item.GamelistPath)!;
        string temp = Path.Combine(directory, ".gamelist_" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, result, new UTF8Encoding(false));
            using (var reader = XmlReader.Create(temp, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                new XmlDocument { XmlResolver = null }.Load(reader);
            if (backup) File.Copy(item.GamelistPath, item.GamelistPath + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N"), overwrite: false);
            File.Move(temp, item.GamelistPath, overwrite: true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    public static void Write(string root, GeneratedGamelist item, bool backup = false)
    {
        if (item.Append) { AppendTo(root, item, backup); return; }
        string systemDir = Path.Combine(root, "Roms", item.System);
        Directory.CreateDirectory(Path.GetDirectoryName(item.GamelistPath)!);
        string temp = item.GamelistPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var settings = new XmlWriterSettings { Indent = true, IndentChars = "\t", Encoding = new UTF8Encoding(false), NewLineChars = "\n" };
            using (var writer = XmlWriter.Create(temp, settings))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("gameList");
                foreach (var rom in item.RomPaths)
                {
                    string relative = "./" + Path.GetRelativePath(systemDir, rom).Replace('\\', '/');
                    writer.WriteStartElement("game");
                    writer.WriteElementString("path", relative);
                    writer.WriteElementString("name", Path.GetFileNameWithoutExtension(rom));
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
                writer.WriteEndDocument();
            }
            var check = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(temp, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                check.Load(reader);
            File.Move(temp, item.GamelistPath, overwrite: true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }
}
