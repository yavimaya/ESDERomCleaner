namespace ESDERomCleaner;

// «Eliminar sistema completo»: borra Roms\<sistema>, ES-DE\gamelists\<sistema> y ES-DE\downloaded_media\<sistema> (ver SystemRemovalService).
// Se lanza desde el botón junto al desplegable de sistemas, desde el clic derecho en ese panel y desde la pestaña «Por sistema» del resumen.
public sealed partial class RomCleanerForm
{
    private readonly Button removeSystemButton = new()
    {
        Text = "", Font = new Font("Segoe MDL2 Assets", 10f), ForeColor = Color.Firebrick, FlatStyle = FlatStyle.Standard, Tag = "noloc",
        Size = new Size(30, 26), Margin = Padding.Empty, TabStop = false
    };
    private readonly ContextMenuStrip systemMenu = new() { ShowImageMargin = false };

    private void WireRemoveSystem()
    {
        Loc.SetToolTip(new ToolTip(), removeSystemButton, "Eliminar sistema completo…");
        removeSystemButton.Click += async (_, _) => await RemoveSystemAsync(systems.SelectedItem as string);
        systems.EnabledChanged += (_, _) => removeSystemButton.Enabled = systems.Enabled;
        var openFolder = new ToolStripMenuItem("Abrir carpeta del sistema");
        var remove = new ToolStripMenuItem("Eliminar sistema completo…") { ForeColor = Color.Firebrick };
        openFolder.Click += (_, _) => OpenSystemFolder(systems.SelectedItem as string);
        remove.Click += async (_, _) => await RemoveSystemAsync(systems.SelectedItem as string);
        systemMenu.Items.AddRange([openFolder, new ToolStripSeparator(), remove]);
        systemMenu.Opening += (_, e) => { if (systems.SelectedItem is not string name || IsCustomSystem(name) || !systems.Enabled) e.Cancel = true; };
    }

    // Colecciones › Limpiar huérfanos: al cerrar, si se borró algo, se recalculan tamaños, contadores y huecos.
    private async Task OpenOrphanCleanerAsync()
    {
        if (cts is not null) return;
        string rootPath = root.Text.Trim().Trim('"');
        if (rootPath.Length == 0 || !Directory.Exists(rootPath)) return;
        trace.Add("orphans_dialog_opened", $"root={rootPath}");
        bool changed;
        using (var dialog = new OrphanCleanerDialog(rootPath, trace, createGamelistBackups, FormatSize))
        {
            dialog.ShowDialog(this);
            changed = dialog.Changed;
        }
        if (!changed) return;
        var busyCts = new CancellationTokenSource();
        cts = busyCts;
        SetOperationUi(busy: true, canCancel: false);
        status.Text = "Recalculando tamaños tras limpiar huérfanos…";
        try
        {
            string listsRoot = Path.Combine(rootPath, "ES-DE", "gamelists");
            gamelistCount = Directory.Exists(listsRoot) ? Directory.EnumerateFiles(listsRoot, "gamelist.xml", SearchOption.AllDirectories).Count() : 0;
            mediaBytes = await Task.Run(() => CollectionScanner.CalculateMediaBytes(rootPath, CancellationToken.None));
            UpdateCollectionCounters();
            _ = RefreshGapsAsync();
            status.Text = "Huérfanos eliminados.";
        }
        catch (Exception ex) { trace.Add("orphans_refresh_error", ex.ToString()); }
        finally { FinishOperationUi(); }
    }

    private void OpenSystemFolder(string? system)
    {
        if (system is null) return;
        string folder = Path.Combine(root.Text.Trim().Trim('"'), "Roms", system);
        if (Directory.Exists(folder)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    // Devuelve true si el sistema se ha borrado (aunque haya habido avisos). owner = el formulario sobre el que se muestran los avisos.
    private async Task<bool> RemoveSystemAsync(string? system, IWin32Window? owner = null)
    {
        owner ??= this;
        if (cts is not null || system is null) return false;
        if (IsCustomSystem(system))
        {
            QuietMessage.Show(owner, "Eliminar sistema completo", "Los sistemas con ★ son colecciones personalizadas de ES-DE, no carpetas de ROMs. No se pueden eliminar desde aquí.");
            return false;
        }
        string rootPath = root.Text.Trim().Trim('"');
        if (rootPath.Length == 0 || !Directory.Exists(rootPath)) return false;

        var busyCts = new CancellationTokenSource();
        cts = busyCts;
        SetOperationUi(busy: true, canCancel: false);
        Cursor = Cursors.WaitCursor;
        status.Text = $"Calculando lo que se borraría de «{system}»…";
        SystemRemovalPlan plan;
        try { plan = await Task.Run(() => SystemRemovalService.BuildPlan(rootPath, system)); }
        catch (Exception ex) { plan = new SystemRemovalPlan { Root = rootPath, System = system, Error = ex.Message }; }
        finally { Cursor = Cursors.Default; }

        int romCount = all.Count(r => r.System.Equals(system, StringComparison.OrdinalIgnoreCase) && r.Status == RomStatus.Ok);
        if (plan.Error.Length > 0)
        {
            trace.Add("delete_system_refused", $"system={system}; reason={plan.Error}");
            FinishOperationUi();
            status.Text = "No se borró nada.";
            QuietMessage.Show(owner, "Eliminar sistema completo", plan.Error);
            return false;
        }
        // El borrado lo ejecuta el propio formulario de confirmación (con la papelera animada); aquí se recoge su resultado.
        SystemRemovalResult? done = null;
        Exception? failure = null;
        bool keepMedia = false;
        Func<bool, Task> runDeletion = async keep =>
        {
            keepMedia = keep;
            trace.Add("delete_system_started", $"system={system}; keep_media={keep}; files={plan.TotalFiles}; bytes={plan.TotalBytes}; folders={string.Join(" | ", plan.Folders.Select(f => f.Path))}");
            progress.Style = ProgressBarStyle.Marquee;
            try
            {
                var reporter = new Progress<string>(text => status.Text = text);
                done = await Task.Run(() => SystemRemovalService.Execute(plan, createGamelistBackups, keep, reporter));
            }
            catch (Exception ex) { failure = ex; }
        };
        if (!SystemRemovalDialog.Ask(owner, plan, FormatSize, romCount, runDeletion))
        {
            trace.Add("delete_system_cancelled", $"system={system}");
            FinishOperationUi();
            status.Text = "Borrado cancelado; no se modificó ningún archivo.";
            return false;
        }
        if (failure is not null || done is null)
        {
            trace.Add("delete_system_error", failure?.ToString() ?? "sin resultado");
            FinishOperationUi();
            QuietMessage.Show(owner, "Eliminar sistema completo", failure?.Message ?? "El borrado no se ejecutó.");
            return false;
        }
        var result = done;

        trace.Add("delete_system_finished", $"system={system}; deleted={result.DeletedFiles}; bytes={result.DeletedBytes}; errors={result.Errors.Count}; backup={result.GamelistBackup}");
        bool gone = plan.Folders.Where(f => !(keepMedia && f.Label == @"ES-DE\downloaded_media")).All(f => f.IsGone());
        if (gone) SystemRemovalService.CleanStores(system);
        try
        {
            // El sistema sale de la colección cargada al momento, sin volver a analizar.
            var gonePaths = all.Where(r => r.System.Equals(system, StringComparison.OrdinalIgnoreCase)).Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            all = all.Where(r => !r.System.Equals(system, StringComparison.OrdinalIgnoreCase)).ToList();
            accessories = accessories.Where(a => !a.System.Equals(system, StringComparison.OrdinalIgnoreCase)).ToList();
            systemFileCounts.Remove(system);
            selected.RemoveWhere(gonePaths.Contains);
            var remaining = all;
            cachePath = await Task.Run(() => CollectionScanner.WriteCache(rootPath, remaining)); // database.csv de la colección, sin las ROMs del sistema
            raIndex = RaIndex.Load();
            ClearSearchForContextChange();
            RefreshSystemsList();
            UpdateCollectionCounters();
            UpdateSelection();
            _ = RefreshGapsAsync();
            mediaBytes = await Task.Run(() => CollectionScanner.CalculateMediaBytes(rootPath, CancellationToken.None));
            UpdateCollectionCounters();
        }
        catch (Exception ex) { trace.Add("delete_system_refresh_error", ex.ToString()); }
        FinishOperationUi();

        status.Text = result.Errors.Count == 0
            ? $"Sistema «{system}» eliminado: {result.DeletedFiles:N0} archivos, {FormatSize(result.DeletedBytes)}."
            : $"Sistema «{system}» eliminado con {result.Errors.Count:N0} avisos; revisa la trazabilidad.";
        if (result.Errors.Count > 0)
            QuietMessage.Show(owner, "Eliminar sistema completo", string.Join("\r\n", result.Errors.Take(10)) + (result.Errors.Count > 10 ? $"\r\n… y {result.Errors.Count - 10:N0} más." : ""));
        return true;
    }
}
