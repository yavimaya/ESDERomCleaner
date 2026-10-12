using System.Net;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ESDERomCleaner;

public sealed partial class RomCleanerForm : LocalizedForm
{
    private async Task DeleteSelectedAsync()
    {
        if (cts is not null) return;
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "No hay ROMs seleccionadas. Marca primero las ROMs que quieras eliminar.", "Eliminar", MessageBoxButtons.OK, MessageBoxIcon.None);
            return;
        }

        var selectedRows = all
            .Where(row => row.Status == RomStatus.Ok && row.Path.Length > 0 && selected.Contains(row.Path))
            .ToArray();
        if (selectedRows.Length == 0)
        {
            MessageBox.Show(this, "No hay ROMs válidas seleccionadas para eliminar.", "Eliminar", MessageBoxButtons.OK, MessageBoxIcon.None);
            return;
        }

        await DeleteRowsAsync(selectedRows);
    }

    // Borrado seguro de unas ROMs concretas (plan, resumen con confirmación, ejecución y actualización de los sistemas afectados).
    // Lo usan tanto la selección de la ventana principal como el formulario de curado.
    private async Task DeleteRowsAsync(RomRow[] selectedRows)
    {
        if (cts is not null) return;
        var rowPaths = selectedRows.Select(row => row.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool backupsEnabledForOperation = createGamelistBackups;

        var service = new DeletionService();
        DeletionPlan plan;
        var planningCancellation = new CancellationTokenSource();
        cts = planningCancellation;
        SetOperationUi(busy: true, canCancel: true);
        progress.Style = ProgressBarStyle.Marquee;
        status.Text = "Preparando el borrado…";
        trace.Add("deletion_plan_started", $"roms={selectedRows.Length}");

        try
        {
            var planningProgress = new Progress<string>(message => status.Text = message);
            plan = await Task.Run(
                () => service.BuildPlan(root.Text, selectedRows, accessories, planningProgress, planningCancellation.Token),
                planningCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            trace.Add("deletion_plan_cancelled", $"roms={selectedRows.Length}");
            status.Text = "Preparación cancelada; no se modificó ningún archivo.";
            return;
        }
        catch (Exception ex)
        {
            trace.Add("deletion_plan_error", ex.ToString());
            MessageBox.Show(this, ex.Message, "No se pudo preparar el borrado", MessageBoxButtons.OK, MessageBoxIcon.None);
            status.Text = "No se pudo preparar el borrado; no se modificó ningún archivo.";
            return;
        }
        finally
        {
            planningCancellation.Dispose();
            cts = null;
            cancel.Enabled = false;
            SetOperationUi(busy: false, canCancel: false);
            progress.Style = ProgressBarStyle.Blocks;
            progress.Value = 0;
        }

        string planSummary = BuildDeletionPlanSummary(plan, backupsEnabledForOperation);
        trace.Add("deletion_plan_ready", planSummary);
        var confirmData = new DeleteConfirmData(plan.Items.Count, FormatSize(plan.RomBytes), plan.Accessories.Count, FormatSize(plan.AccessoryBytes), plan.Media.Count,
            FormatSize(plan.MediaBytes), plan.Gamelists.Count, FormatSize(plan.RomBytes + plan.AccessoryBytes + plan.MediaBytes), backupsEnabledForOperation);
        bool confirmed;
        using (var confirm = new DeleteConfirmDialog(confirmData)) confirmed = confirm.ShowDialog(this) == DialogResult.OK;
        if (!confirmed)
        {
            trace.Add("deletion_cancelled_by_user", $"roms={plan.Items.Count}");
            status.Text = "Borrado cancelado; no se modificó ningún archivo.";
            return;
        }

        try
        {
            SetOperationUi(busy: true, canCancel: false);
            progress.Style = ProgressBarStyle.Marquee;
            status.Text = "Eliminando archivos y actualizando gamelists…";
            var executionProgress = new Progress<string>(message =>
            {
                status.Text = message;
                trace.Add("deletion_progress", message);
            });

            var result = await Task.Run(() => service.Execute(plan, executionProgress, backupsEnabledForOperation));
            string finalSummary = BuildDeletionResultSummary(plan, result);
            details.Text = finalSummary;
            trace.Add("deletion_completed", finalSummary);
            MessageBox.Show(this, finalSummary, "Resultado de la eliminación", MessageBoxButtons.OK,
                MessageBoxIcon.None);

            selected.RemoveWhere(path => rowPaths.Contains(path));
            status.Text = $"Borrado terminado: {result.DeletedRomCount}/{result.RomCountPlanned} ROMs. Actualizando sistemas afectados…";
            try
            {
                await RefreshSystemsAfterDeletionAsync(plan.Items.Select(item => item.Row.System).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            }
            catch (Exception refreshError)
            {
                string refreshMessage = "El borrado terminó, pero no se pudo actualizar la vista de los sistemas afectados. Analiza esos sistemas manualmente.\r\n\r\n" + refreshError.Message;
                trace.Add("deletion_partial_rescan_error", refreshError.ToString());
                status.Text = "Borrado terminado; falló la actualización de la vista.";
                MessageBox.Show(this, refreshMessage, "Borrado finalizado; vista sin actualizar", MessageBoxButtons.OK, MessageBoxIcon.None);
            }
        }
        catch (Exception ex)
        {
            trace.Add("deletion_error", ex.ToString());
            MessageBox.Show(this, ex.Message, "Error durante la eliminación", MessageBoxButtons.OK, MessageBoxIcon.None);
            status.Text = "El borrado terminó con un error; revisa la trazabilidad.";
        }
        finally
        {
            cts?.Dispose();
            cts = null;
            SetOperationUi(busy: false, canCancel: false);
            progress.Style = ProgressBarStyle.Blocks;
            progress.Value = 0;
            UpdateSelection();
        }
    }

    private void SetOperationUi(bool busy, bool canCancel)
    {
        scan.Enabled = browse.Enabled = root.Enabled = systems.Enabled = !busy;
        clearSelection.Enabled = !busy && selected.Count > 0;
        deleteSelected.Enabled = !busy && selected.Count > 0;
        excludeButton.Enabled = !busy && selected.Count > 0;
        cancel.Enabled = busy && canCancel;
    }

    private string BuildDeletionPlanSummary(DeletionPlan plan, bool createBackups)
    {
        var text = new StringBuilder();
        text.AppendLine("SE VA A ELIMINAR:");
        text.AppendLine();
        text.AppendLine($"ROMs: {plan.Items.Count:N0}");
        text.AppendLine($"Espacio ROMs: {FormatSize(plan.RomBytes)}");
        text.AppendLine($"Archivos accesorios asociados: {plan.Accessories.Count:N0} — {FormatSize(plan.AccessoryBytes)}");
        text.AppendLine($"Media ES-DE: {plan.Media.Count:N0} archivos — {FormatSize(plan.MediaBytes)}");
        text.AppendLine($"Gamelists: {plan.Gamelists.Count:N0}");
        text.AppendLine("Copia de seguridad de cada gamelist: " + (createBackups ? "SÍ" : "NO"));
        text.AppendLine();

        text.AppendLine(createBackups
            ? "ROMs, accesorios y media se eliminarán permanentemente. Se crearán copias .bak_ de los gamelist.xml afectados."
            : "ROMs, accesorios, media y cambios en gamelist.xml serán permanentes. No se crearán copias .bak_.");
        text.AppendLine();
        text.Append("¿Confirmas la eliminación?");
        return text.ToString();
    }

    private static string BuildDeletionResultSummary(DeletionPlan plan, DeletionResult result)
    {
        var text = new StringBuilder();
        text.AppendLine("RESUMEN DE LA ELIMINACIÓN");
        text.AppendLine("=========================");
        text.AppendLine();
        text.AppendLine($"ROMs eliminadas: {result.DeletedRomCount:N0} / {result.RomCountPlanned:N0}");
        text.AppendLine($"Espacio ROMs eliminado: {FormatSize(result.DeletedRomBytes)}");
        text.AppendLine($"Accesorios eliminados: {result.DeletedAccessoryCount:N0} / {result.AccessoryCountPlanned:N0}");
        text.AppendLine($"Espacio accesorios eliminado: {FormatSize(result.DeletedAccessoryBytes)}");
        text.AppendLine($"Media eliminada: {result.DeletedMediaCount:N0} / {result.MediaCountPlanned:N0}");
        text.AppendLine($"Espacio media eliminado: {FormatSize(result.DeletedMediaBytes)}");
        text.AppendLine($"ESPACIO TOTAL DE FICHEROS BORRADOS: {FormatSize(result.TotalDeletedBytes)}");
        text.AppendLine($"Entradas <game> eliminadas: {result.GameEntriesRemoved:N0}");
        text.AppendLine($"Copias de seguridad creadas: {result.Backups.Count:N0}");
        text.AppendLine();
        if (result.Errors.Count == 0)
        {
            text.AppendLine("ERRORES: 0");
            text.AppendLine("La operación ha finalizado correctamente.");
        }
        else
        {
            text.AppendLine($"ERRORES: {result.Errors.Count:N0}");
            text.AppendLine();
            text.AppendLine("DETALLE DE ERRORES:");
            foreach (string error in result.Errors) text.AppendLine("• " + error);
        }
        text.AppendLine();
        text.AppendLine("COPIAS DE SEGURIDAD DE GAMELIST:");
        if (result.Backups.Count == 0) text.AppendLine("No se crearon copias de seguridad.");
        else foreach (string backup in result.Backups) text.AppendLine(backup);
        text.AppendLine();
        text.Append("A continuación se actualizarán solo los sistemas afectados.");
        return text.ToString();
    }

    private async Task RefreshSystemsAfterDeletionAsync(IReadOnlyCollection<string> affectedSystems)
    {
        ClearSearchForContextChange();
        if (affectedSystems.Count == 0) return;
        progress.Style = ProgressBarStyle.Blocks;
        progress.Value = 0;
        progress.Maximum = 100;
        sizesCalculating = true;
        Task<long> mediaSizeTask = Task.Run(() => CollectionScanner.CalculateMediaBytes(root.Text, CancellationToken.None));
        var reporter = new Progress<ScanProgress>(p =>
        {
            status.Text = "Actualizando " + p.Message;
            if (p.Total > 0) progress.Value = Math.Clamp(p.Current * 100 / p.Total, 0, 100);
        });
        var updated = await Task.Run(() => new CollectionScanner().Scan(
            root.Text, reporter, CancellationToken.None, affectedSystems, writeCache: false));

        var affected = new HashSet<string>(affectedSystems, StringComparer.OrdinalIgnoreCase);
        all = all.Where(row => !affected.Contains(row.System)).Concat(updated.Rows).ToList();
        accessories = accessories.Where(file => !affected.Contains(file.System)).Concat(updated.Accessories).ToList();
        foreach (string system in affectedSystems) systemFileCounts.Remove(system);
        foreach (var pair in updated.SystemFileCounts) systemFileCounts[pair.Key] = pair.Value;


        cachePath = CollectionScanner.WriteCache(root.Text, all);
        UpdateCollectionCounters();
        _ = RefreshGapsAsync();

        string? previousSystem = systems.SelectedItem as string;
        changing = true;
        systems.BeginUpdate();
        try
        {
            systems.Items.Clear();
            foreach (string system in all.Select(row => row.System).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                systems.Items.Add(system);
            int systemIndex = systems.Items.IndexOf(previousSystem);
            if (systemIndex < 0 && systems.Items.Count > 0) systemIndex = 0;
            systems.SelectedIndex = systemIndex;
        }
        finally
        {
            systems.EndUpdate();
            changing = false;
        }

        if (systems.SelectedItem is string selectedSystem) ChangeSystem();
        else
        {
            current.Clear();
            categories.Items.Clear();
            roms.Items.Clear();
            details.Clear();
        }
        mediaBytes = await mediaSizeTask;
        sizesCalculating = false;
        UpdateCollectionCounters();
        status.Text = $"Actualizados los sistemas afectados: {string.Join(", ", affectedSystems)}.";
        trace.Add("deletion_partial_rescan_completed", $"systems={string.Join(",", affectedSystems)}; rows={updated.Rows.Count}; cache={cachePath}");
    }

    private void UpdateCollectionCounters()
    {
        UpdateScanButton();
        UpdateActiveCollectionStats();
        SaveAnalysisCache(); // también mientras se calculan los tamaños: si la ventana se cierra a medias, al volver se ve el análisis nuevo y no uno viejo
        int identified = all.Count(row => row.Status == RomStatus.Ok);
        int ambiguous = all.Count(row => row.Status == RomStatus.Ambiguous);
        int systemCount = all.Where(row => row.Status == RomStatus.Ok).Select(row => row.System).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int fileCount = systemFileCounts.Values.Sum();
        long romBytes = all.Where(row => row.Status == RomStatus.Ok).Sum(row => row.Size);
        liveCounters.Text = $"Sistemas: {systemCount:N0} | ROMs: {identified:N0} | Ambiguas: {ambiguous:N0} | Ficheros: {fileCount:N0}\r\nROMS: {(sizesCalculating ? "calculando" : FormatSize(romBytes))} | Media: {(sizesCalculating ? "calculando" : FormatSize(mediaBytes))}";

    }
    private IReadOnlyList<AccessoryFile> GetRelatedAccessoryFiles(RomRow rom)
    {
        string systemRoot = Path.Combine(Path.GetFullPath(root.Text), "Roms", rom.System);
        string relativePath = PathRules.Relative(Path.GetRelativePath(systemRoot, rom.Path));
        string key = PathRules.AccessoryMatchKey(relativePath);
        return accessories
            .Where(file => file.System.Equals(rom.System, StringComparison.OrdinalIgnoreCase) &&
                           file.MatchKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}
