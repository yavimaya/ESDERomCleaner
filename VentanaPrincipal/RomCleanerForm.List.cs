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
    private bool scanStarting;

    // Bloqueo de toda la ventana principal: desactivar el formulario no basta (el menú y el buscador seguían respondiendo), así que se desactiva de forma explícita
    // cada control, menús y buscador incluidos, y se recuerda su estado para devolverlo tal cual al terminar.
    // Con «keep» (p. ej. el botón de cancelar) ese control y sus contenedores se dejan activos para poder usarlo.
    private List<(Control Control, bool Was)> LockUi(Control? keep = null)
    {
        IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(All(c)));
        var exempt = new HashSet<Control>();
        for (var c = keep; c is not null && c != this; c = c.Parent) exempt.Add(c);
        var saved = All(this).Select(c => (c, c.Enabled)).ToList();
        foreach (var (control, _) in saved) if (!exempt.Contains(control)) control.Enabled = false;
        if (keep is null) Enabled = false;
        return saved;
    }

    private void UnlockUi(List<(Control Control, bool Was)> saved)
    {
        Enabled = true;
        foreach (var (control, was) in saved) control.Enabled = was;
    }

    private async Task ScanAsync(bool offerUnclassified = true)
    {
        List<string>? unclassified = null;
        // «scanStarting» cubre las esperas previas (aviso de gamelists): sin él, un segundo análisis lanzado entonces pisaba el cts del primero
        // y al terminar uno de los dos fallaba con una referencia nula.
        if (cts != null || scanStarting) return;
        scanStarting = true;
        string rootPath;
        CancellationToken token;
        try
        {
            if (!EnsureCollectionRoot()) { status.Text = "Ruta raíz no válida."; return; }
            rootPath = root.Text.Trim().Trim('"');
            if (!ValidateRoot(rootPath, out string problem))
            {
                status.Text = "Ruta raíz no válida.";
                QuietMessage.Show(this, "Ruta raíz no válida", problem);
                return;
            }
            // Para analizar una carpeta hace falta una colección con ella (se ofrece crearla).
            if (!EnsureActiveCollection()) return;
            root.Text = rootPath;
            // El análisis solo lista los juegos que figuran en un gamelist: si hay ROMs sin gamelist (sistema nuevo o ROMs recién copiadas), se ofrece añadirlas.
            await CreateGamelistsAsync(scanAfter: false, quiet: true, reason: "Hay ROMs en Roms\\<sistema> que no figuran en ningún gamelist.xml, y el análisis solo lista los juegos de los gamelists.");
            selected.Clear(); cts = new CancellationTokenSource(); token = cts.Token;
        }
        finally { scanStarting = false; }
        scan.Enabled = browse.Enabled = root.Enabled = systems.Enabled = deleteSelected.Enabled = false; cancel.Enabled = true; progress.Style = ProgressBarStyle.Blocks; progress.Value = 0;
        trace.Add("scan_started", root.Text);
        sizesCalculating = true;
        headline.SetValues("…", "…");
        liveCounters.Text = "Ambiguas: calculando | Ficheros: calculando\r\nROMS: calculando | Media: calculando";
        Task<long> mediaSizeTask = Task.Run(() => CollectionScanner.CalculateMediaBytes(root.Text, token), token);
        // Al terminar de listar las ROMs la lista ya se ve, pero aún se calculan los tamaños y los huecos: desde ahí y hasta que acaben, la ventana se bloquea
        // (menos «Cancelar») y se muestra un aviso, para que no parezca que ya se puede trabajar.
        List<(Control Control, bool Was)>? scanLock = null;
        BusyNotice? scanNotice = null;
        void FinishScanUi() { scan.Enabled = browse.Enabled = root.Enabled = systems.Enabled = true; deleteSelected.Enabled = selected.Count > 0; cancel.Enabled = false; progress.Style = ProgressBarStyle.Blocks; progress.Value = 0; }
        try
        {
            var reporter = new Progress<ScanProgress>(p => { status.Text = p.Message; if (p.Total > 0) progress.Value = Math.Clamp(p.Current * 100 / p.Total, 0, 100); });
            var result = await Task.Run(() => new CollectionScanner().Scan(root.Text, reporter, token), token);
            all = result.Rows;
            loadedRoot = rootPath; UpdateScanButton();
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
            scanNotice = BusyNotice.Show(this, "Calculando tamaños y huecos…");
            scanLock = LockUi(keep: cancel);
            status.Text = "Archivos consultados; calculando tamaños…";
            gapsTask = RefreshGapsAsync(); // se espera justo antes del aviso de sistemas sin categoría, que también cuenta las ROMs sin imagen y sin vídeo
            await mediaSizeTask;
            mediaBytes = mediaSizeTask.Result;
            sizesCalculating = false;
            UpdateCollectionCounters();
            status.Text = $"Escaneo completado: {identified:N0} ROMs · {systems.Items.Count} sistemas · {result.Gamelists} gamelists.";
            // Sistemas con alguna ROM sin categoría (no se han scrapeado, o no se reconocieron).
            unclassified = all.Where(r => r.Status == RomStatus.Ok && IsVisibleSystem(r.System))
                .GroupBy(r => r.System, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Any(NoCategory))
                .Select(g => g.Key).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            trace.Add("scan_completed", $"rows={all.Count}; systems={result.SystemCount}; files={result.FileCount}; identified={identified}; ambiguous={ambiguous}; accessories={result.Accessories.Count}; gamelists={result.Gamelists}; cache={result.CachePath}; extensions={string.Join(" | ", result.ExtensionCounts.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"))}");
        }
        catch (OperationCanceledException) { sizesCalculating = false; status.Text = "Cancelado. No se modificó ningún archivo."; trace.Add("scan_cancelled", root.Text); }
        catch (Exception ex) { sizesCalculating = false; status.Text = "Error de escaneo."; trace.Add("scan_error", ex.ToString()); MessageBox.Show(this, ex.Message, "No se pudo analizar", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { try { await mediaSizeTask; } catch { } cts?.Dispose(); cts = null; if (scanLock is null) FinishScanUi(); }

        // El cálculo de huecos (sin categoría, sin imagen y sin vídeo) puede seguir un buen rato en segundo plano: la ventana sigue bloqueada hasta que acabe.
        if (scanLock is not null)
        {
            if (gapsTask is { IsCompleted: false }) { string previousStatus = status.Text; status.Text = "Calculando huecos…"; try { await gapsTask; } catch { } status.Text = previousStatus; }
            UnlockUi(scanLock); scanNotice?.Dispose(); FinishScanUi();
        }

        if (categorizeAfterScan is { Count: > 0 } toCategorize)
        {
            categorizeAfterScan = null;
            await CategorizeAsync(toCategorize);
        }
        else if (scrapeAfterScan is { Count: > 0 } chosen)
        {
            // El usuario ya aceptó categorizar los sistemas nuevos al añadir sus ROMs: se abre la descarga masiva con ellos marcados.
            scrapeAfterScan = null;
            await OpenBulkMediaDialogAsync(chosen);
        }
        else if (skipUnclassifiedOffer) skipUnclassifiedOffer = false;
        else if (offerUnclassified && unclassified is { Count: > 0 })
        {
            // Mientras se calculan los huecos y se monta el aviso, el análisis parece terminado pero aún no lo está: la ventana principal no admite acciones hasta que se muestre.
            var notice = BusyNotice.Show(this, "Preparando la ventana…");
            var lockedUi = LockUi();
            UnclassifiedSystemsDialog? offer = null;
            try
            {
            if (gapsTask is not null && !gapsTask.IsCompleted) { string previousStatus = status.Text; status.Text = "Calculando huecos…"; try { await gapsTask; } catch { } status.Text = previousStatus; } // el aviso se quita al terminar
            var listed = unclassified.Select(s => (s, all.Count(r => r.Status == RomStatus.Ok && r.System.Equals(s, StringComparison.OrdinalIgnoreCase) && NoCategory(r)),
                gaps is not null && gaps.TryGetValue(s, out var gap) ? gap.NoImage : 0, gaps is not null && gaps.TryGetValue(s, out var gap2) ? gap2.NoVideo : 0)).ToList();
            var gapRowsCache = new Dictionary<string, GapRows>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<string> GapNames(string system, int kind)
            {
                if (!gapRowsCache.TryGetValue(system, out var set))
                    gapRowsCache[system] = set = MediaGaps.Rows(root.Text, ScrapableRows().Where(r => r.System.Equals(system, StringComparison.OrdinalIgnoreCase)));
                var paths = kind == 0 ? set.NoMetadata : kind == 1 ? set.NoImage : set.NoVideo;
                return paths.Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).Order(StringComparer.OrdinalIgnoreCase).ToList();
            }
            offer = new UnclassifiedSystemsDialog(listed, GapNames);
            offer.Shown += (_, _) => notice.Dispose(); // el aviso se quita cuando el formulario aparece
            }
            catch { notice.Dispose(); throw; }
            finally { UnlockUi(lockedUi); }
            UnclassifiedChoice choice;
            using (offer) { offer.ShowDialog(this); choice = offer.Choice; }
            // «Categorizar» solo descarga los metadatos (que traen la categoría), con una ventana de progreso y sin abrir el scrapeo; «Categorizar y scrapear» abre la descarga masiva con los medios elegidos la última vez.
            if (choice == UnclassifiedChoice.Categorize) await CategorizeAsync(unclassified, onlyMissingCategory: true);
            else if (choice == UnclassifiedChoice.CategorizeAndScrape)
            {
                // Se scrapean todas las ROMs a las que les falte la categoría, la imagen o el vídeo (no solo las que no tienen categoría), de los sistemas del aviso.
                var candidates = ScrapableRows().Where(r => unclassified.Contains(r.System, StringComparer.OrdinalIgnoreCase)).ToList();
                string gapsRoot = root.Text;
                var gap = await Task.Run(() => MediaGaps.Rows(gapsRoot, candidates));
                var rows = candidates.Where(r => gap.NoMetadata.Contains(r.Path) || gap.NoImage.Contains(r.Path) || gap.NoVideo.Contains(r.Path)).ToList();
                trace.Add("scrape_missing", $"scope=unclassified_offer; kind=any; roms={rows.Count}");
                if (rows.Count > 0) await OpenBulkMediaDialogAsync(onlyRows: rows, preset: BulkPreset.WithMetadata);
            }
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

    // Filas que se pueden scrapear o cotejar: de sistemas visibles y cuyo archivo tiene una extensión oficial de su sistema en ES-DE
    // (así no se consultan, por ejemplo, las pistas .pcm de MSU-1 que alguna vez se colaron en un gamelist).
    private IEnumerable<RomRow> ScrapableRows() => all.Where(r => IsVisibleSystem(r.System) && EsDeSystemExtensions.IsGameFile(r.System, r.Path));

    private bool AllScope => searchInAll.Checked;
    private string? scopeSystem;   // sistema que se estaba viendo antes de elegir «Todos», para volver a él

    // Con la búsqueda en «Todos» no importa el sistema: se vacían y desactivan el selector de sistema, las categorías y los contadores del
    // sistema (la colección entera sigue contando). Al volver a «este sistema» se recupera el que se estaba viendo.
    private void ApplySearchScope()
    {
        if (AllScope)
        {
            if (systems.SelectedItem is string chosen) scopeSystem = chosen;
            changing = true;
            try { systems.SelectedIndex = -1; categories.Items.Clear(); current = new(); }
            finally { changing = false; }
            systems.Enabled = false; categories.Enabled = false;
            systemSummary.Text = "";
            UpdateGapLabels();
            return;
        }
        systems.Enabled = true; categories.Enabled = true;
        if (systems.Items.Count == 0) return;
        int index = scopeSystem is null ? -1 : systems.Items.IndexOf(scopeSystem);
        if (index < 0) index = 0;
        changing = true;
        try { systems.SelectedIndex = index; }
        finally { changing = false; }
        ChangeSystem();
    }

    // Rellena la lista de sistemas; si el sistema que se estaba viendo sigue disponible, se conserva sin recargar nada.
    private void RefreshSystemsList()
    {
        string? keep = systems.SelectedItem as string;
        var names = all.Select(r => r.System).Distinct(StringComparer.OrdinalIgnoreCase).Where(IsVisibleSystem).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        names.AddRange(CustomSystemNames());
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
        if (AllScope) { ApplySearchScope(); return; }
        string? previousCategory = (categories.SelectedItem as CategoryItem)?.Genre;
        current = RowsOfSystem(system);
        systemSummary.Text = $"ROMs encontradas: {current.Count(r => r.Status == RomStatus.Ok):N0}";
        UpdateGapLabels();
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

    private static bool NoCategory(RomRow r) => string.IsNullOrWhiteSpace(r.Genre) || r.Genre == "Sin categoría";
    private static string Genre(RomRow r) => string.IsNullOrWhiteSpace(r.Genre) ? "Sin categoría" : r.Genre;
    private void ChangeSystem()
    {
        if (AllScope) { ApplySearchScope(); return; }
        if (systems.SelectedItem is not string s) return;
        current = RowsOfSystem(s);
        int identified = current.Count(r => r.Status == RomStatus.Ok);
        systemSummary.Text = $"ROMs encontradas: {identified:N0}";
        UpdateGapLabels();
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
        Func<string, bool>? textMatch = null; // cómo se busca el texto, para saber en qué parte de cada ROM aparece
        if (q.Length >= 3)
        {
            // El texto se busca siempre como expresión regular; si no es una expresión válida (o tarda demasiado), se busca como texto literal.
            var source = searchInAll.Checked ? all.Where(r => r.Status == RomStatus.Ok && IsVisibleSystem(r.System)) : current;
            try
            {
                var re = new Regex(q, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                rows = source.Where(r => re.IsMatch(r.RomName) || re.IsMatch(r.GameName) || re.IsMatch(r.Genre)).ToList();
                textMatch = s => { try { return re.IsMatch(s); } catch (RegexMatchTimeoutException) { return false; } };
            }
            catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException)
            {
                rows = source.Where(r => r.RomName.Contains(q, StringComparison.OrdinalIgnoreCase) || r.GameName.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Genre.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            textMatch ??= s => s.Contains(q, StringComparison.OrdinalIgnoreCase);
            status.Text = searchInAll.Checked ? $"Búsqueda en todos los sistemas: {q}" : $"Búsqueda en el sistema: {q}";
        }
        else if (categories.SelectedItem is CategoryItem category) rows = current.Where(r => Genre(r).Equals(category.Genre, StringComparison.OrdinalIgnoreCase));
        else rows = Array.Empty<RomRow>();
        if (raOnlyWith.Checked) rows = rows.Where(HasRaAchievements);
        var visibleRows = rows.OrderBy(r => r.RomName, StringComparer.OrdinalIgnoreCase).ToArray();
        roms.BeginUpdate(); changing = true;
        try
        {
            roms.Items.Clear();
            foreach (var r in visibleRows)
            {
                // Si el texto buscado no está en el nombre del juego y sí en el del archivo, se muestra el archivo entre paréntesis para saber por qué ha salido.
                string? fileHint = textMatch is not null && !textMatch(r.Display) && textMatch(r.RomName) ? r.RomName : null;
                int i = roms.Items.Add(new RomItem(r, searchInAll.Checked && q.Length >= 3, fileHint));
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
    // Lista de las ROMs seleccionadas (por sistema), con casilla para quitar de la selección las que no se quieran.
    private void ShowSelectedRoms()
    {
        var rows = all.Where(r => r.Path.Length > 0 && selected.Contains(r.Path)).ToList();
        if (rows.Count == 0) { QuietMessage.Show(this, "ROMs seleccionadas", "No hay ROMs seleccionadas."); return; }
        trace.Add("selected_roms_opened", $"roms={rows.Count}");
        using var dialog = BusyNotice.Create(this, () => new SelectedRomsDialog(root.Text, rows, FormatSize, (row, on) =>
        {
            ApplyRomCheck(row.Path, on);
            SyncVisibleRomChecks();
        }));
        dialog.ShowDialog(this);
    }

    // La lista de la ventana principal refleja la selección sin reconstruirse (para no interrumpir el medio que se esté viendo).
    private void SyncVisibleRomChecks()
    {
        changing = true;
        try { for (int i = 0; i < roms.Items.Count; i++) if (roms.Items[i] is RomItem item && item.Row.Path.Length > 0) roms.SetItemChecked(i, selected.Contains(item.Row.Path)); }
        finally { changing = false; }
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

    private void UpdateSelection() { selectionLabel.Text = $"ROMs seleccionadas: {selected.Count:N0}\r\nTamaño: {FormatSize(all.Where(r => selected.Contains(r.Path)).Sum(r => r.Size))}"; clearSelection.Enabled = selected.Count > 0; deleteSelected.Enabled = selected.Count > 0 && cts is null; excludeButton.Enabled = selected.Count > 0 && cts is null; excludeButton.Text = selected.Count > 0 ? $"Excluir seleccionados ({selected.Count:N0})" : "Excluir seleccionados"; }
    private void ClearSelection() { if (selected.Count == 0) return; var answer = MessageBox.Show(this, $"Se van a desmarcar {selected.Count:N0} ROMs seleccionadas. No se borrará ningún archivo.\r\n\r\n¿Deseas continuar?", "Vaciar selección", MessageBoxButtons.YesNo, MessageBoxIcon.Question); if (answer != DialogResult.Yes) return; int count = selected.Count; selected.Clear(); RefreshList(); UpdateSelection(); trace.Add("clear_selection", $"count={count}"); }
    private static string FormatSize(long n) { string[] u = ["B", "KB", "MB", "GB", "TB"]; double x = n; int i = 0; while (x >= 1024 && i < 4) { x /= 1024; i++; } return i == 0 ? $"{n} B" : $"{x:0.##} {u[i]}"; }
}
