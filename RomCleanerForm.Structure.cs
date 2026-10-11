namespace ESDERomCleaner;

public sealed partial class RomCleanerForm
{
    // Garantiza que hay una ruta raíz usable: pide la carpeta si no se ha elegido ninguna y ofrece crear
    // la estructura de ES-DE si falta. Solo crea carpetas vacías que no existan; nunca toca lo que ya hay.
    private bool EnsureCollectionRoot()
    {
        string rootPath = root.Text.Trim().Trim('"');
        if (rootPath.Length == 0)
        {
            if (!QuietMessage.Confirm(this, "Carpeta de la colección",
                "No has indicado la carpeta raíz de la colección, y hace falta para continuar.\r\n\r\nElige la carpeta que contiene Roms y ES-DE. Si no tiene esa estructura, o es una carpeta nueva, te ofreceré crearla.",
                "Elegir carpeta")) return false;
            using var picker = new FolderBrowserDialog
            {
                Description = "Selecciona la carpeta de la colección de ES-DE (puedes crear una nueva).",
                ShowNewFolderButton = true
            };
            if (picker.ShowDialog(this) != DialogResult.OK) return false;
            rootPath = picker.SelectedPath;
            root.Text = rootPath;
        }

        if (ValidateRoot(rootPath, out string problem)) return true;
        if (!Path.IsPathRooted(rootPath))
        {
            QuietMessage.Show(this, "Ruta raíz no válida", problem);
            return false;
        }

        var missing = EsDeStructure.AllFolders().Where(f => !Directory.Exists(Path.Combine(rootPath, f))).ToArray();
        bool exists = Directory.Exists(rootPath);
        bool hasContent = exists && Directory.EnumerateFileSystemEntries(rootPath).Any();
        string text = (exists ? $"La carpeta «{rootPath}» no tiene la estructura de ES-DE." : $"La carpeta «{rootPath}» no existe.")
            + $"\r\n\r\n¿Quieres crear la estructura completa y vacía de ES-DE? Se crearán {missing.Length:N0} carpetas que faltan:\r\n"
            + $"  · Roms\\<sistema> para los {EsDeStructure.Systems.Length} sistemas oficiales de ES-DE\r\n"
            + "  · ES-DE\\gamelists\\<sistema>\r\n"
            + $"  · ES-DE\\downloaded_media\\<sistema>\\ con las {EsDeStructure.MediaFolders.Length} carpetas de medios\r\n"
            + "  · ES-DE\\collections, custom_systems, scripts y themes\r\n"
            + "\r\nLuego copia tus ROMs en su carpeta de sistema y analiza la colección."
            + (hasContent ? "\r\n\r\nLa carpeta ya contiene otros archivos; no se tocarán. Comprueba que no te falta subir o bajar un nivel." : "");
        if (!QuietMessage.Confirm(this, "Crear estructura de ES-DE", text, "Crear estructura")) return false;

        try
        {
            foreach (var folder in missing) Directory.CreateDirectory(Path.Combine(rootPath, folder));
            // Cada carpeta de sistema lleva su systeminfo.txt con las extensiones que admite (si falta; ES-DE lo regenera al arrancar).
            foreach (var system in EsDeStructure.Systems) SystemFormats.EnsureSystemInfo(rootPath, system);
        }
        catch (Exception ex)
        {
            trace.Add("structure_create_error", $"root={rootPath}; {ex.Message}");
            QuietMessage.Show(this, "No se pudo crear la estructura", ex.Message);
            return false;
        }

        root.Text = rootPath;
        trace.Add("structure_created", $"root={rootPath}; folders={missing.Length}");
        status.Text = $"Estructura de ES-DE creada ({missing.Length:N0} carpetas). Copia tus ROMs en Roms\\<sistema> y analiza la colección.";
        return true;
    }

    // Para las opciones que necesitan la colección ya analizada: si no lo está, asegura la ruta y la analiza.
    private async Task<bool> EnsureCollectionLoadedAsync()
    {
        if (all.Count > 0) return true;
        if (!EnsureCollectionRoot()) return false;
        await ScanAsync(offerUnclassified: false);
        return all.Count > 0;
    }
}
