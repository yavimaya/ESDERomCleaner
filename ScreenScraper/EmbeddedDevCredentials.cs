using System.Reflection;

namespace ESDERomCleaner;

// Credenciales de desarrollador de ScreenScraper incrustadas al compilar (ver DevCredentials.props.example). Van como metadatos
// del ensamblado, en claro: se pueden extraer del exe. Si no se incrustaron, el usuario las escribe a mano.
internal static class EmbeddedDevCredentials
{
    private static readonly Dictionary<string, string> Values = Read();

    public static string DevId => Get("ssd");
    public static string DevPassword => Get("ssp");
    public static string SoftName => Get("sss");
    public static bool Available => DevId.Length > 0 && DevPassword.Length > 0;

    private static string Get(string key) => Values.TryGetValue(key, out string? value) ? value : "";

    private static Dictionary<string, string> Read()
    {
        var result = new Dictionary<string, string>();
        foreach (var attribute in typeof(EmbeddedDevCredentials).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (attribute.Key is not ("ssd" or "ssp" or "sss") || string.IsNullOrEmpty(attribute.Value)) continue;
            result[attribute.Key] = attribute.Value;
        }
        return result;
    }
}
