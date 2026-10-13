using System.Text.Json;

namespace ESDERomCleaner;

// Carpetas de trabajo de la aplicación. Por defecto: datos en %LOCALAPPDATA%\ESDERomCleaner y temporales en %TEMP%\ESDERomCleaner.
// Se pueden cambiar desde el menú Opciones; la elección se guarda en «folders.json» dentro de la carpeta de datos por defecto
// (el único archivo que no se mueve, para poder encontrar el resto al arrancar).
internal static class AppPaths
{
    private sealed record Choice(string? DataFolder, string? TempFolder);

    public static string DefaultData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ESDERomCleaner");
    public static string DefaultTemp => Path.Combine(Path.GetTempPath(), "ESDERomCleaner");
    private static string ChoiceFile => Path.Combine(DefaultData, "folders.json");

    private static Choice choice = Load();

    // Carpeta donde se guardan ajustes, cachés, DAT, trazas, perfiles, copias de seguridad de medios…
    public static string Data => string.IsNullOrWhiteSpace(choice.DataFolder) ? DefaultData : choice.DataFolder;
    // Carpeta para los archivos temporales (lo que se extrae de los packs al añadir ROMs).
    public static string Temp => string.IsNullOrWhiteSpace(choice.TempFolder) ? DefaultTemp : choice.TempFolder;

    private static Choice Load()
    {
        try { return JsonSerializer.Deserialize<Choice>(File.ReadAllText(ChoiceFile)) ?? new Choice(null, null); }
        catch { return new Choice(null, null); }
    }

    private static void Save()
    {
        Directory.CreateDirectory(DefaultData);
        string temp = ChoiceFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(choice, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, ChoiceFile, overwrite: true);
    }

    // null o la carpeta por defecto restauran el valor de fábrica.
    public static void SetData(string? folder)
    {
        choice = choice with { DataFolder = Same(folder, DefaultData) ? null : folder };
        Save();
    }

    public static void SetTemp(string? folder)
    {
        choice = choice with { TempFolder = Same(folder, DefaultTemp) ? null : folder };
        Save();
    }

    private static bool Same(string? a, string b) =>
        string.IsNullOrWhiteSpace(a) || string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    // Carpetas que debe haber en la de datos para trabajar (el resto de archivos se crean solos cuando hacen falta).
    public static readonly string[] DataSubfolders = ["dats", "media_cache", "media_backups"];

    // ¿Es una carpeta de datos «vacía», es decir, sin ningún archivo ni carpeta propios de la aplicación?
    public static bool IsEmptyFolder(string folder) => !Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any();
}
