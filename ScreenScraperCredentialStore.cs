using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ESDERomCleaner;

// Guarda las credenciales cifradas con DPAPI (solo las puede descifrar tu usuario de Windows en este equipo).
internal static class ScreenScraperCredentialStore
{
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ESDERomCleaner", "screenscraper.credentials");

    public static ScreenScraperCredentials Load()
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
