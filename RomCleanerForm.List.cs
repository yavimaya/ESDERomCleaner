using System.Net;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ESDERomCleaner;

public sealed partial class RomCleanerForm : Form
{
    private async Task ScanAsync(bool offerUnclassified = true)
    {
        List<string>? unclassified = null;
        if (cts != null) return;
        if (!EnsureCollectionRoot()) { status.Text = "Ruta raíz no válida."; return; }
        string rootPath = root.Text.Trim().Trim('"');
        if (!ValidateRoot(rootPath, out string problem))
        {
            status.Text = "Ruta raíz no válida.";
            QuietMessage.Show(this, "Ruta raíz no válida", problem);
            return;
        }
        root.Text = rootPath;
        // El análisis solo lista los juegos que figuran en un gamelist: si hay ROMs sin gamelist (sistema nuevo o ROMs recién copiadas), se ofrece añadirlas.
        await CreateGamelistsAsync(scanAfter: false, quiet: true, reason: "Hay ROMs en Roms\\<sistema> que no figuran en ningún gamelist.xml, y el análisis solo lista los juegos de los gamelists.");
        selected.Clear(); cts = new CancellationTokenSource(); var token = cts.Token;
        scan.Enabled = browse.Enabled = root.Enabled = systems.Enabled = deleteSelected.Enabled = false; cancel.Enabled = true; progress.Style = ProgressBarStyle.Blocks; progress.Value = 0;
        trace.Add("scan_started", root.Text);
        sizesCalculating = true;
        liveCounters.Text = "Sistemas: calculando | ROMs: calculando | Ambiguas: calculando | Ficheros: calculando\r\nROMS: calculando | Media: calculando";
        Task<long> mediaSizeTask = Task.Run(() => CollectionScanner.CalculateMediaBytes(root.Text, token), token);
        try
        {
            var reporter = new Progress<ScanProgress>(p => { status.Text = p.Message; if (p.Total > 0) progress.Value = Math.Clamp(p.Current * 100 / p.Total, 0, 100); });
            var result = await Task.Run(() => new CollectionScanner().Scan(root.Text, reporter, token), token);
            all = result.Rows;
            accessories = result.Accessories;
            systemFileCounts = result.SystemFileCounts;
            gamelistCount = result.Gamelists;
            cachePath = result.CachePath;
            mediaBytes = 0;
            UpdateCollectionCounters();
            int identified = all.Count(r => r.Status == RomStatus.Ok); int ambiguous = all.Count(r => r.Status == RomStatus.Ambiguous);

            RefreshSystemsList();
            // Solo se recuerda una ruta con la que el análisis ha funcionado.
            if (!lastRootPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase)) { lastRootPath = rootPath; SaveOptions(); }
            status.Text = "Archivos consultados; calculando tamaños…";
            await mediaSizeTask;
            mediaBytes = mediaSizeTask.Result;
            sizesCalculating = false;
            UpdateCollectionCounters();
            status.Text = $"Escaneo completado: {all.Count:N0} filas · {systems.Items.Count} sistemas · {result.Gamelists} gamelists.";
            // Sistemas cuyas ROMs no tienen ninguna categoría (nunca se han scrapeado).
            unclassified = all.Where(r => r.Status == RomStatus.Ok && IsVisibleSystem(r.System))
                .GroupBy(r => r.System, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.All(r => string.IsNullOrWhiteSpace(r.Genre) || r.Genre == "Sin categoría"))
                .Select(g => g.Key).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            trace.Add("scan_completed", $"rows={all.Count}; systems={result.SystemCount}; files={result.FileCount}; identified={identified}; ambiguous={ambiguous}; accessories={result.Accessories.Count}; gamelists={result.Gamelists}; cache={result.CachePath}; extensions={string.Join(" | ", result.ExtensionCounts.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"))}");
        }
        catch (OperationCanceledException) { sizesCalculating = false; status.Text = "Cancelado. No se modificó ningún archivo."; trace.Add("scan_cancelled", root.Text); }
        catch (Exception ex) { sizesCalculating = false; status.Text = "Error de escaneo."; trace.Add("scan_error", ex.ToString()); MessageBox.Show(this, ex.Message, "No se pudo analizar", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { try { await mediaSizeTask; } catch { } cts.Dispose(); cts = null; scan.Enabled = browse.Enabled = root.Enabled = systems.Enabled = true; deleteSelected.Enabled = selected.Count > 0; cancel.Enabled = false; progress.Style = ProgressBarStyle.Blocks; progress.Value = 0; }

        if (scrapeAfterScan is { Count: > 0 } chosen)
        {
            // El usuario ya aceptó categorizar los sistemas nuevos al añadir sus ROMs: se abre la descarga masiva con ellos marcados.
            scrapeAfterScan = null;
            await OpenBulkMediaDialogAsync(chosen);
        }
        else if (skipUnclassifiedOffer) skipUnclassifiedOffer = false;
        else if (offerUnclassified && unclassified is { Count: > 0 })
        {
            var listed = unclassified.Select(s => (s, all.Count(r => r.Status == RomStatus.Ok && r.System.Equals(s, StringComparison.OrdinalIgnoreCase)))).ToList();
            UnclassifiedChoice choice;
            using (var dialog = new UnclassifiedSystemsDialog(listed)) { dialog.ShowDialog(this); choice = dialog.Choice; }
            // «Categorizar» abre la descarga masiva solo con los metadatos marcados (traen la categoría); «Categorizar y scrapear» añade los medios elegidos la última vez.
            if (choice == UnclassifiedChoice.Categorize) await OpenBulkMediaDialogAsync(unclassified, preset: BulkPreset.MetadataOnly);
            else if (choice == UnclassifiedChoice.CategorizeAndScrape) await OpenBulkMediaDialogAsync(unclassified, preset: BulkPreset.WithMetadata);
        }
    }
    // La ruta raíz debe ser la carpeta que contiene Roms y ES-DE\gamelists. Se comprueba antes de empezar para dar un mensaje claro.
    private static bool ValidateRoot(string path, out string problem)
    {
        problem = "";
        if (path.Length == 0)
        {
            problem = "Indica la ruta raíz de la colección: la carpeta que contiene Roms y ES-DE.\r\n\r\nPuedes escribirla o pulsar «Examinar…».";
            return false;
        }
        if (!Path.IsPathRooted(path) || !Directory.Exists(path))
        {
            problem = $"La carpeta «{path}» no existe.\r\n\r\nRevisa la ruta raíz (por ejemplo C:\\Temp\\NOVA) o pulsa «Examinar…» para elegirla.";
            return false;
        }
        if (!Directory.Exists(Path.Combine(path, "Roms")))
        {
            problem = $"La carpeta «{path}» existe, pero no contiene una carpeta «Roms».\r\n\r\nLa ruta raíz debe ser la que contiene Roms y ES-DE; quizá tengas que subir o bajar un nivel.";
            return false;
        }
        if (!Directory.Exists(Path.Combine(path, "ES-DE", "gamelists")))
        {
            problem = $"En «{path}» no se encuentra «ES-DE\\gamelists».\r\n\r\nLa ruta raíz debe contener las carpetas Roms y ES-DE.";
            return false;
        }
        return true;
    }

    // Carpetas que no son sistemas de juegos (lanzadores de apps, emuladores…): solo se muestran con la opción «Mostrar sistemas accesorios».
    private static readonly HashSet<string> AccessorySystemNames = new(StringComparer.OrdinalIgnoreCase) { "androidapps", "emulators", "desktop" };

    private bool IsVisibleSystem(string system) => showAccessory.Checked || !AccessorySystemNames.Contains(system);

    // Rellena la lista de sistemas; si el sistema que se estaba viendo sigue disponible, se conserva sin recargar nada.
    private void RefreshSystemsList()
    {
        string? keep = systems.SelectedItem as string;
        var names = all.Select(r => r.System).Distinct(StringComparer.OrdinalIgnoreCase).Where(IsVisibleSystem).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        bool keepSelection = keep is not null && names.Contains(keep, StringComparer.OrdinalIgnoreCase);
        changing = keepSelection;
        try
        {
            systems.Items.Clear();
            foreach (string s in names) systems.Items.Add(s);
            if (keepSelection) systems.SelectedIndex = names.FindIndex(n => n.Equals(keep, StringComparison.OrdinalIgnoreCase));
        }
        finally { changing = false; }
        if (keepSelection) { ReloadCurrentSystem(keep!); return; }
        if (systems.Items.Count > 0) systems.SelectedIndex = 0; else { current.Clear(); categories.Items.Clear(); roms.Items.Clear(); details.Clear(); }
    }

    // Tras un nuevo análisis, vuelve a cargar las ROMs y categorías del sistema que se estaba viendo (conservando la categoría enfocada).
    private void ReloadCurrentSystem(string system)
    {
        string? previousCategory = (categories.SelectedItem as CategoryItem)?.Genre;
        current = all.Where(r => r.System.Equals(system, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.RomName, StringComparer.OrdinalIgnoreCase).ToList();
        systemSummary.Text = $"{system} : {current.Count(r => r.Status == RomStatus.Ok):N0} ROMs identificadas en el gamelist.";
        changing = true;
        try
        {
            categories.Items.Clear();
            foreach (var group in current.GroupBy(Genre, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
                categories.Items.Add(new CategoryItem(group.Key, group.Count(row => row.Status == RomStatus.Ok)));
            int index = categories.Items.Cast<CategoryItem>().ToList().FindIndex(c => c.Genre.Equals(previousCategory, StringComparison.OrdinalIgnoreCase));
            if (index < 0 && categories.Items.Count > 0) index = 0;
            categories.SelectedIndex = index;
        }
        finally { changing = false; }
        RefreshList();
        UpdateSelection();
    }

    private static string Genre(RomRow r) => string.IsNullOrWhiteSpace(r.Genre) ? "Sin categoría" : r.Genre;
    private void ChangeSystem()
    {
        if (systems.SelectedItem is not string s) return;
        current = all.Where(r => r.System.Equals(s, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.RomName, StringComparer.OrdinalIgnoreCase).ToList();
        int identified = current.Count(r => r.Status == RomStatus.Ok);
        systemSummary.Text = $"{s} : {identified:N0} ROMs identificadas en el gamelist.";
        categories.Items.Clear();
        foreach (var group in current.GroupBy(Genre, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            categories.Items.Add(new CategoryItem(group.Key, group.Count(row => row.Status == RomStatus.Ok)));
        if (categories.Items.Count > 0) categories.SelectedIndex = 0; UpdateSelection(); trace.Add("system_changed", s);
    }
    private void CategoryChanged(int index, bool check)
    {
        if (changing || index < 0 || index >= categories.Items.Count) return;
        var genre = ((CategoryItem)categories.Items[index]!).Genre;
        foreach (var r in current.Where(r => Genre(r).Equals(genre, StringComparison.OrdinalIgnoreCase) && r.Path.Length > 0))
        { if (check) selected.Add(r.Path); else selected.Remove(r.Path); }
        RefreshList(); UpdateSelection(); trace.Add("category_selection", $"{genre}; selected={check}");
    }
    private void ClearSearchForContextChange()
    {
        if (search.TextLength > 0) search.Clear();
    }
    private void RefreshList(string? preferredFocusPath = null)
    {
        string? focusedPath = preferredFocusPath ?? (roms.SelectedItem as RomItem)?.Row.Path;
        RomRow? previousRow = (roms.SelectedItem as RomItem)?.Row;
        var q = search.Text.Trim(); IEnumerable<RomRow> rows;
        if (q.Length >= 3)
        {
            try
            {
                if (regex.Checked) { var re = new Regex(q, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); rows = current.Where(r => re.IsMatch(r.RomName) || re.IsMatch(r.GameName) || re.IsMatch(r.Genre)); }
                else rows = current.Where(r => r.RomName.Contains(q, StringComparison.OrdinalIgnoreCase) || r.GameName.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Genre.Contains(q, StringComparison.OrdinalIgnoreCase));
                status.Text = $"Búsqueda en el sistema: {q}";
            }
            catch (ArgumentException e) { status.Text = $"Regex no válida: {e.Message}"; rows = Array.Empty<RomRow>(); }
        }
        else if (categories.SelectedItem is CategoryItem category) rows = current.Where(r => Genre(r).Equals(category.Genre, StringComparison.OrdinalIgnoreCase));
        else rows = Array.Empty<RomRow>();
        var visibleRows = rows.OrderBy(r => r.RomName, StringComparer.OrdinalIgnoreCase).ToArray();
        roms.BeginUpdate(); changing = true;
        try
        {
            roms.Items.Clear();
            foreach (var r in visibleRows)
            {
                int i = roms.Items.Add(new RomItem(r));
                if (r.Path.Length > 0 && selected.Contains(r.Path)) roms.SetItemChecked(i, true);
            }
            if (roms.Items.Count > 0)
            {
                int focusedIndex = string.IsNullOrWhiteSpace(focusedPath)
                    ? -1
                    : roms.Items.Cast<RomItem>().ToList().FindIndex(item => item.Row.Path.Equals(focusedPath, StringComparison.OrdinalIgnoreCase));
                roms.SelectedIndex = focusedIndex >= 0 ? focusedIndex : 0;
            }
        }
        finally { changing = false; roms.EndUpdate(); }
        if (roms.Items.Count > 0)
        {
            // Si la ROM enfocada sigue siendo la misma y su vídeo se está reproduciendo,
            // se conserva la reproducción en vez de reiniciar la vista previa.
            RomRow? newRow = (roms.SelectedItem as RomItem)?.Row;
            if (videoView.Visible && previousRow is not null && previousRow.Equals(newRow)) UpdateDataPanel();
            else ShowDetails();
        }
        else { preview.Image?.Dispose(); preview.Image = null; details.Clear(); }
        UpdateCategoryChecks(); UpdateSelection();
    }
    private void UpdateCategoryChecks()
    {
        var selectedGenres = new HashSet<string>(
            current.Where(r => r.Path.Length > 0 && selected.Contains(r.Path)).Select(Genre),
            StringComparer.OrdinalIgnoreCase);
        changing = true;
        try
        {
            for (int i = 0; i < categories.Items.Count; i++)
            {
                var genre = ((CategoryItem)categories.Items[i]!).Genre;
                bool shouldBeChecked = selectedGenres.Contains(genre);
                if (categories.GetItemChecked(i) != shouldBeChecked)
                    categories.SetItemChecked(i, shouldBeChecked);
            }
        }
        finally { changing = false; }
    }
    private void SetVisibleSelection(bool add)
    {
        foreach (var item in roms.Items.Cast<RomItem>())
        {
            if (item.Row.Path.Length == 0) continue;
            if (add) selected.Add(item.Row.Path);
            else selected.Remove(item.Row.Path);
        }
        RefreshList();
        UpdateSelection();
        trace.Add(add ? "select_visible" : "deselect_visible", $"count={roms.Items.Count}");
    }
    private void ApplyRomCheck(string path, bool isChecked)
    {
        if (isChecked) selected.Add(path);
        else selected.Remove(path);
        UpdateCategoryChecks();
        UpdateSelection();
        UpdateDataPanel();
        trace.Add("selection_toggled", path);
    }

    private void ToggleFocused()
    {
        if (roms.SelectedItem is not RomItem item || item.Row.Path.Length == 0) return;
        int index = roms.SelectedIndex;
        if (index < 0) return;

        bool checkedNow = roms.GetItemChecked(index);
        bool wasSelected = selected.Contains(item.Row.Path);
        // A double-click already toggles the check in ManualDoubleClickCheckedListBox.
        // Context-menu selection does not, so toggle the visible check only in that case.
        if (checkedNow == wasSelected)
        {
            checkedNow = !wasSelected;
            roms.SetItemChecked(index, checkedNow);
        }

        if (checkedNow) selected.Add(item.Row.Path);
        else selected.Remove(item.Row.Path);

        UpdateCategoryChecks();
        UpdateSelection();
        UpdateDataPanel();
        trace.Add("selection_toggled", item.Row.Path);
    }

    private void UpdateSelection() { selectionLabel.Text = $"ROMs seleccionadas: {selected.Count:N0}    |    Tamaño: {FormatSize(all.Where(r => selected.Contains(r.Path)).Sum(r => r.Size))}"; clearSelection.Enabled = selected.Count > 0; deleteSelected.Enabled = selected.Count > 0 && cts is null; }
    private void ClearSelection() { if (selected.Count == 0) return; var answer = MessageBox.Show(this, $"Se van a desmarcar {selected.Count:N0} ROMs seleccionadas. No se borrará ningún archivo.\r\n\r\n¿Deseas continuar?", "Vaciar selección", MessageBoxButtons.YesNo, MessageBoxIcon.Question); if (answer != DialogResult.Yes) return; int count = selected.Count; selected.Clear(); RefreshList(); UpdateSelection(); trace.Add("clear_selection", $"count={count}"); }
    private static string FormatSize(long n) { string[] u = ["B", "KB", "MB", "GB", "TB"]; double x = n; int i = 0; while (x >= 1024 && i < 4) { x /= 1024; i++; } return i == 0 ? $"{n} B" : $"{x:0.##} {u[i]}"; }
}
