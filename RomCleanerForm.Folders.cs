namespace ESDERomCleaner;

public sealed partial class RomCleanerForm
{
    private readonly ToolStripMenuItem dataFolderMenu = new("Carpeta de datos…") { ToolTipText = "Dónde guarda la aplicación sus ajustes, bases de datos de hashes, cachés, copias de seguridad de medios y trazas." };
    private readonly ToolStripMenuItem tempFolderMenu = new("Carpeta temporal…") { ToolTipText = "Dónde se extraen los archivos temporales (por ejemplo, los packs de ROMs al añadirlas)." };

    private void WireFolderMenus()
    {
        dataFolderMenu.Click += (_, _) => ChangeDataFolder();
        tempFolderMenu.Click += (_, _) => ChangeTempFolder();
        // El motor de vídeo se prepara en segundo plano con la ventana ya visible.
        Shown += (_, _) => { if (showVideos.Checked) WarmUpVideoEngine(); };
    }

    private void ChangeDataFolder()
    {
        string current = AppPaths.Data;
        using var dialog = new FolderChoiceDialog("Carpeta de datos",
            "Carpeta donde la aplicación guarda sus ajustes, las bases de datos de hashes, las cachés, las copias de seguridad de medios y las trazas. " +
            "Al cambiarla no se mueven los datos actuales.", current, AppPaths.DefaultData);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string target = dialog.Folder;
        if (target.Length == 0 || !Path.IsPathRooted(target)) { QuietMessage.Show(this, "Carpeta de datos", "Indica una ruta completa, por ejemplo D:\\Datos\\ESDERomCleaner."); return; }
        target = Path.GetFullPath(target);
        if (string.Equals(target.TrimEnd('\\'), Path.GetFullPath(current).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;

        bool empty;
        try { empty = AppPaths.IsEmptyFolder(target); }
        catch (Exception ex) { QuietMessage.Show(this, "Carpeta de datos", $"No se puede usar esa carpeta.\r\n\r\n{ex.Message}"); return; }

        bool hasSettings = File.Exists(Path.Combine(target, "settings.json"));
        if (empty)
        {
            if (!QuietMessage.Confirm(this, "Carpeta de datos",
                $"La carpeta «{target}» está vacía.\r\n\r\n¿Crear en ella los archivos mínimos de trabajo (carpetas {string.Join(", ", AppPaths.DataSubfolders)} y los ajustes actuales)? Lo demás se irá generando al usarlo; las bases de datos de hashes habrá que volver a descargarlas.",
                "Crear y usar")) return;
        }
        else if (!hasSettings)
        {
            if (!QuietMessage.Confirm(this, "Carpeta de datos",
                $"La carpeta «{target}» no está vacía y no parece una carpeta de datos de la aplicación.\r\n\r\nSe usará tal cual y se crearán en ella los archivos que falten. ¿Continuar?", "Usar esta carpeta")) return;
        }

        try
        {
            Directory.CreateDirectory(target);
            AppPaths.SetData(target);
            foreach (var sub in AppPaths.DataSubfolders) Directory.CreateDirectory(Path.Combine(target, sub));
            // Carpeta vacía o sin ajustes: se escriben los actuales. Si ya tenía los suyos, se respetan y se cargan al reiniciar.
            if (!hasSettings) SaveOptions();
            RomDatabase.Invalidate();
            trace.Add("data_folder_changed", $"from={current}; to={target}; empty={empty}");
            status.Text = hasSettings
                ? "Carpeta de datos cambiada. Reinicia la aplicación para cargar los ajustes de esa carpeta."
                : $"Carpeta de datos cambiada a {target}.";
        }
        catch (Exception ex)
        {
            AppPaths.SetData(current);
            QuietMessage.Show(this, "Carpeta de datos", $"No se pudo preparar la carpeta; se mantiene la anterior.\r\n\r\n{ex.Message}");
        }
    }

    private void ChangeTempFolder()
    {
        string current = AppPaths.Temp;
        using var dialog = new FolderChoiceDialog("Carpeta temporal",
            "Carpeta para los archivos temporales de las acciones (por ejemplo, lo que se extrae de los packs de ROMs al añadirlas). " +
            "Se vacía sola al terminar cada acción; conviene que tenga espacio libre suficiente.", current, AppPaths.DefaultTemp);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string target = dialog.Folder;
        if (target.Length == 0 || !Path.IsPathRooted(target)) { QuietMessage.Show(this, "Carpeta temporal", "Indica una ruta completa, por ejemplo D:\\Temporales."); return; }
        target = Path.GetFullPath(target);
        if (string.Equals(target.TrimEnd('\\'), Path.GetFullPath(current).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            Directory.CreateDirectory(target);
            // Se comprueba que se puede escribir antes de quedarse con ella.
            string probe = Path.Combine(target, ".prueba_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            // Los restos de extracciones en la carpeta anterior ya no hacen falta.
            try { string old = Path.Combine(current, "expand"); if (Directory.Exists(old)) Directory.Delete(old, recursive: true); } catch { }
            AppPaths.SetTemp(target);
            trace.Add("temp_folder_changed", $"from={current}; to={target}");
            status.Text = $"Carpeta temporal cambiada a {target}.";
        }
        catch (Exception ex)
        {
            QuietMessage.Show(this, "Carpeta temporal", $"No se puede usar esa carpeta; se mantiene la anterior.\r\n\r\n{ex.Message}");
        }
    }
}

public sealed partial class RomCleanerForm
{
    // Versión de la aplicación (la de «Version» en el csproj, p. ej. «v0.1.1») para el título de la ventana.
    private static string AppVersion()
    {
        var info = typeof(RomCleanerForm).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        string version = (info ?? typeof(RomCleanerForm).Assembly.GetName().Version?.ToString(3) ?? "").Split('+')[0];
        return version.Length > 0 ? "v" + version : "";
    }
}

public sealed partial class RomCleanerForm
{
    // Opciones › Idioma: español o inglés, con cambio inmediato (sin reiniciar). Los nombres de los idiomas no se traducen.
    private readonly ToolStripMenuItem languageMenu = new("Idioma");
    private readonly ToolStripMenuItem systemItem = new("Predeterminado del sistema");
    private readonly ToolStripMenuItem spanishItem = new("Español") { Tag = "noloc", CheckOnClick = false };
    private readonly ToolStripMenuItem englishItem = new("English") { Tag = "noloc", CheckOnClick = false };

    private void WireLanguageMenu()
    {
        languageMenu.DropDownItems.Add(systemItem);
        languageMenu.DropDownItems.Add(new ToolStripSeparator());
        languageMenu.DropDownItems.Add(spanishItem);
        languageMenu.DropDownItems.Add(englishItem);
        void UpdateChecks() { systemItem.Checked = Loc.Setting == "system"; spanishItem.Checked = Loc.Setting == "es"; englishItem.Checked = Loc.Setting == "en"; }
        UpdateChecks();
        roms.Tag = "noloc"; // la lista de ROMs muestra nombres de juegos, no texto de la interfaz
        systemItem.Click += (_, _) => { Loc.SetLanguage("system"); UpdateChecks(); trace.Add("language_changed", "system"); if (Loc.English) _ = GenreNames.EnsureAsync(trace); };
        spanishItem.Click += (_, _) => { Loc.SetLanguage("es"); UpdateChecks(); trace.Add("language_changed", "es"); };
        englishItem.Click += (_, _) => { Loc.SetLanguage("en"); UpdateChecks(); trace.Add("language_changed", "en"); _ = GenreNames.EnsureAsync(trace); };
        // Con la interfaz ya en inglés al arrancar, se completa la lista de géneros si falta.
        Shown += (_, _) => { if (Loc.English) _ = GenreNames.EnsureAsync(trace); };
    }
}
