using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ESDERomCleaner;

// Guarda las credenciales cifradas con DPAPI (solo las puede descifrar tu usuario de Windows en este equipo).
internal static class ScreenScraperCredentialStore
{
    private static string FilePath => Path.Combine(AppPaths.Data, "screenscraper.credentials");

    // Las credenciales de desarrollador propias del usuario mandan; si no hay, se usan las incrustadas al compilar (si las hay).
    public static ScreenScraperCredentials Load()
    {
        var stored = LoadStored();
        if (stored.HasDeveloper || !EmbeddedDevCredentials.Available) return stored;
        string soft = EmbeddedDevCredentials.SoftName.Length > 0 ? EmbeddedDevCredentials.SoftName : stored.SoftName;
        return stored with { DevId = EmbeddedDevCredentials.DevId, DevPassword = EmbeddedDevCredentials.DevPassword, SoftName = soft };
    }

    private static ScreenScraperCredentials LoadStored()
    {
        try
        {
            if (!File.Exists(FilePath)) return ScreenScraperCredentials.Empty;
            byte[] protectedBytes = File.ReadAllBytes(FilePath);
            byte[] plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<ScreenScraperCredentials>(Encoding.UTF8.GetString(plain)) ?? ScreenScraperCredentials.Empty;
        }
        catch { return ScreenScraperCredentials.Empty; }
    }

    public static void Save(ScreenScraperCredentials credentials)
    {
        // No se copian al disco las incrustadas: así una versión nueva con otras credenciales las toma sin más.
        if (EmbeddedDevCredentials.Available && credentials.DevId == EmbeddedDevCredentials.DevId && credentials.DevPassword == EmbeddedDevCredentials.DevPassword)
            credentials = credentials with { DevId = "", DevPassword = "" };
        string path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(credentials));
        byte[] protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        string temporary = path + ".tmp_" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) try { File.Delete(temporary); } catch { }
        }
    }
}
