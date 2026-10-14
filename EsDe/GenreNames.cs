using System.Text.Json;

namespace ESDERomCleaner;

// Nombres de los géneros de ScreenScraper en español e inglés (lista oficial, genresListe.php), para mostrar en inglés las categorías que ya están
// escritas en español en los gamelists. NO modifica ningún gamelist: solo cambia cómo se ven. Los gamelists que se generen a partir de ahora
// se descargan directamente en el idioma elegido en la interfaz (ver GamelistMetadata).
internal static class GenreNames
{
    private static string CachePath => Path.Combine(AppPaths.Data, "screenscraper_genres.json");

    public static Dictionary<string, string> LoadCache()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath)) ?? []; }
        catch { return []; }
    }

    // Si la interfaz está en inglés, hay credenciales y la lista no está guardada (o tiene más de 90 días), la descarga y la aplica.
    public static async Task EnsureAsync(TraceLog trace, CancellationToken token = default)
    {
        if (!Loc.Translating) return;
        try
        {
            bool fresh = File.Exists(CachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) < TimeSpan.FromDays(90);
            if (fresh) return;
            var credentials = ScreenScraperCredentialStore.Load();
            if (!credentials.HasDeveloper) return;
            var map = await new ScreenScraperClient(credentials).GetGenreNamesAsync(token);
            trace.Add("genre_names_fetched", $"count={map.Count}");
            if (map.Count == 0) return; // formato inesperado: se sigue con la tabla incorporada
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            string temp = CachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(map), token);
            File.Move(temp, CachePath, overwrite: true);
            Loc.SetExtra(map);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            trace.Add("genre_names_error", ex.Message);
        }
    }
}
