namespace ESDERomCleaner;

// Sistemas custom: las colecciones personalizadas de ES-DE (ES-DE\collections\custom-<nombre>.cfg, una ruta de ROM por línea) aparecen al final de
// la lista de sistemas con una marca delante. Son solo una lista de referencias: las ROMs, gamelists y medios siguen en su sistema original.
public sealed partial class RomCleanerForm
{
    private const string CustomMark = "★ ";

    private static bool IsCustomSystem(string name) => name.StartsWith(CustomMark, StringComparison.Ordinal);

    // Nombres (con marca) de los sistemas custom de la colección cargada.
    private List<string> CustomSystemNames()
    {
        var names = new List<string>();
        try
        {
            string dir = Path.Combine(root.Text.Trim().Trim('"'), "ES-DE", "collections");
            if (!Directory.Exists(dir)) return names;
            foreach (string file in Directory.EnumerateFiles(dir, "custom-*.cfg"))
            {
                string name = Path.GetFileNameWithoutExtension(file)["custom-".Length..];
                if (name.Length > 0) names.Add(CustomMark + name);
            }
        }
        catch { }
        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Añade los sistemas custom detrás de los reales.
    private void AddCustomSystems()
    {
        foreach (string name in CustomSystemNames()) systems.Items.Add(name);
    }

    // ROMs de un sistema (real o custom) tal como las muestra la lista.
    private List<RomRow> RowsOfSystem(string system)
    {
        if (!IsCustomSystem(system)) return all.Where(r => r.System.Equals(system, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.RomName, StringComparer.OrdinalIgnoreCase).ToList();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string rootPath = root.Text.Trim().Trim('"');
            string romsDir = Path.Combine(rootPath, "Roms");
            string file = Path.Combine(rootPath, "ES-DE", "collections", $"custom-{system[CustomMark.Length..]}.cfg");
            foreach (string raw in File.ReadLines(file))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                line = line.StartsWith("%ROMPATH%", StringComparison.OrdinalIgnoreCase) ? romsDir + line["%ROMPATH%".Length..] : line;
                paths.Add(Path.GetFullPath(line.Replace('/', '\\')));
            }
        }
        catch { }
        return all.Where(r => r.Path.Length > 0 && IsVisibleSystem(r.System) && paths.Contains(r.Path)).OrderBy(r => r.RomName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
