using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ESDERomCleaner;

// Todas las preferencias de la aplicación en un único archivo, «settings.json» de la carpeta de datos: cada preferencia es una clave de primer nivel
// (las opciones de siempre, el idioma, la imagen por defecto…) o una sección con su propio objeto (almacenamiento, bases de datos, descarga masiva…).
// Los archivos sueltos de versiones anteriores (language.txt, storage_options.json…) se incorporan una vez y se borran.
internal static class AppSettings
{
    private static readonly object Gate = new();
    private static JsonObject? root;
    private static string? loadedFrom;

    private static string FilePath => Path.Combine(AppPaths.Data, "settings.json");

    // Archivos antiguos → clave en settings.json (los .txt guardan un texto; los .json, un objeto).
    private static readonly (string File, string Key, bool Text)[] Legacy =
    [
        ("language.txt", "Language", true), ("default_image.txt", "DefaultImage", true), ("storage_options.json", "Storage", false),
        ("dat_settings.json", "Dats", false), ("bulk_media.json", "BulkMedia", false), ("curation_languages.json", "CurationLanguages", false)
    ];

    private static JsonObject Root()
    {
        string path = FilePath;
        if (root is not null && string.Equals(loadedFrom, path, StringComparison.OrdinalIgnoreCase)) return root;
        JsonObject loaded;
        try { loaded = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject() : new JsonObject(); }
        catch { loaded = new JsonObject(); }
        root = loaded; loadedFrom = path;
        Migrate();
        return root;
    }

    private static void Migrate()
    {
        var imported = new List<string>();
        foreach (var (file, key, text) in Legacy)
        {
            string path = Path.Combine(AppPaths.Data, file);
            try
            {
                if (!File.Exists(path)) continue;
                if (!root!.ContainsKey(key))
                {
                    string content = File.ReadAllText(path);
                    JsonNode? node = text ? (content.Trim().Length > 0 ? JsonValue.Create(content.Trim()) : null) : JsonNode.Parse(content);
                    if (node is not null) root[key] = node;
                }
                imported.Add(path);
            }
            catch { /* un archivo ilegible se deja donde está */ }
        }
        if (imported.Count == 0) return;
        if (!Save()) return;
        foreach (string path in imported) try { File.Delete(path); } catch { }
    }

    private static bool Save()
    {
        string? temp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch { return false; }
        finally { if (temp is not null && File.Exists(temp)) try { File.Delete(temp); } catch { } }
    }

    // Valor de una clave o sección; si no existe o no se puede leer, el valor por defecto.
    public static T Get<T>(string key, T fallback)
    {
        lock (Gate)
        {
            try { return Root()[key] is { } node && node.Deserialize<T>() is { } value ? value : fallback; }
            catch { return fallback; }
        }
    }

    public static void Set<T>(string key, T value)
    {
        lock (Gate)
        {
            try { Root()[key] = JsonSerializer.SerializeToNode(value); Save(); }
            catch { }
        }
    }
}
