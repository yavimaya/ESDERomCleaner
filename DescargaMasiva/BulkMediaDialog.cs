using System.Diagnostics;
using System.Text;

namespace ESDERomCleaner;

// Descarga masiva de medios de ScreenScraper para uno o varios sistemas de la colección.
// Qué se marca al abrir la descarga masiva: lo último que se eligió, solo los metadatos (categorizar), o lo último que se eligió más los metadatos.
internal enum BulkPreset { Saved, MetadataOnly, WithMetadata, MediaOnly, ImageOnly, VideoOnly }

internal sealed class BulkMediaDialog : LocalizedForm
{
    private readonly IReadOnlyList<RomRow> roms;
    private readonly string root;
    private readonly TraceLog trace;
    private readonly bool defaultNormalizedVideo;
    private readonly bool gamelistBackups;
    private ScreenScraperCredentials credentials;
    private List<SsSystem> ssSystems = new();
    private CancellationTokenSource? cts;
    private bool running;
    private bool updating;
    private readonly Stopwatch clock = new();
    // Avance total: se mide sobre las ROMs que tienen trabajo (las ya completas no cuentan) y se refresca cada segundo.
    private int plannedWork;
    private BulkMediaRunner? activeRunner;
    private string baseTitle = "";
    private readonly System.Windows.Forms.Timer uiTimer = new() { Interval = 1000 };

    private readonly DataGridView systemsGrid = new();
    private readonly GroupBox systemsGroupBox = new() { Text = "Sistemas a los que se aplica", Dock = DockStyle.Fill };
    private readonly Button selectAllButton = new() { Text = "Seleccionar todos", AutoSize = true };
    private readonly Button selectNoneButton = new() { Text = "Deseleccionar todos", AutoSize = true };
    private readonly Dictionary<string, CheckBox> folderChecks = new();
    private readonly CheckBox allMediaCheck = new() { Text = "Todos", AutoSize = true, Margin = new Padding(6, 3, 3, 3) };
    private readonly RadioButton normalizedRadio = new() { Text = "Vídeo normalizado (más ligero, mejor para Android)", AutoSize = true, Margin = new Padding(26, 0, 3, 0) };
    private readonly RadioButton originalRadio = new() { Text = "Vídeo original (el de mayor calidad y tamaño)", AutoSize = true, Margin = new Padding(26, 0, 3, 0) };
    // Por cada elemento (metadatos y cada carpeta de medios, menos los idiomas): cómo se sustituye si ya existe en destino.
    private readonly Dictionary<string, (RadioButton Always, RadioButton Smaller, RadioButton Larger)> replaceRadios = new();
    // Cuadro «Sobrescribir» sobre las columnas de sustitución; se pone en gris mientras se descarga, como el resto.
    private Label replaceHeader = null!;
    private readonly CheckBox onlyNoMediaCheck = new() { Text = "Solo las ROMs que no tienen imagen o vídeo", AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(6, 3, 3, 3) };
    private GapRows? gapRows;
    private readonly CheckBox searchNameCheck = new() { Text = "Si no se reconoce por hash, buscar por el nombre exacto del juego en su sistema", AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(6, 3, 3, 3) };
    // Los hilos los fija la membresía de ScreenScraper (se leen del servidor), no el usuario.
    private readonly Label threadsInfo = new() { AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(6, 6, 3, 3) };
    private readonly Button startButton = new() { Text = "Scrapear", AutoSize = true, MinimumSize = new Size(130, 30) };
    private readonly Button cancelButton = new() { Text = "Cancelar", AutoSize = true, MinimumSize = new Size(90, 30), Enabled = false };
    private readonly Button viewSummaryButton = new() { Text = "Ver resumen", AutoSize = true, MinimumSize = new Size(90, 30), Enabled = false };
    private readonly BulkDebugLog debugLog = new();
    private BulkDebugDialog? debugWindow;
    private readonly Button debugButton = new() { Text = "Registro…", AutoSize = true, MinimumSize = new Size(90, 30) };
    private readonly Button exportButton = new() { Text = "Exportar incidencias…", AutoSize = true, MinimumSize = new Size(90, 30), Enabled = false };
    private readonly Button closeButton = new() { Text = "Cerrar", AutoSize = true, MinimumSize = new Size(90, 30), DialogResult = DialogResult.Cancel };
    private readonly ScreenScraperQuotaStrip quotaStrip = new(showPending: false);
    private readonly Label summaryLabel = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Label statusLabel = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly ProgressBar progressBar = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
    private readonly DataGridView issuesGrid = new();

    public bool CollectionChanged { get; private set; }

    // Abre el scrapeo de una sola ROM (doble clic en una incidencia) sobre este formulario; devuelve si cambió algún medio o metadato.
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<RomRow, IWin32Window, (bool Media, bool Metadata)>? OpenSingleHandler { get; set; }
    public bool MetadataChanged { get; private set; }

    private readonly HashSet<string> preselectedSystems;

    public BulkMediaDialog(IReadOnlyList<RomRow> roms, string root, TraceLog trace, bool defaultNormalizedVideo, bool gamelistBackups, IEnumerable<string>? preselected = null, BulkPreset preset = BulkPreset.Saved)
    {
        preselectedSystems = new HashSet<string>(preselected ?? [], StringComparer.OrdinalIgnoreCase);
        this.gamelistBackups = gamelistBackups;
        this.roms = roms;
        this.root = root;
        this.trace = trace;
        this.defaultNormalizedVideo = defaultNormalizedVideo;
        credentials = ScreenScraperCredentialStore.Load();
        Text = baseTitle = "Descarga masiva de medios — ScreenScraper";
        uiTimer.Tick += (_, _) => RefreshProgress();
        FormClosed += (_, _) => uiTimer.Dispose();
        Width = 1220; Height = 790; MinimumSize = new Size(1000, 720);
        StartPosition = FormStartPosition.CenterParent;
        CancelButton = closeButton;
        BuildLayout();
        ApplySettings(BulkMediaSettings.Load(defaultNormalizedVideo));
        if (preset == BulkPreset.MetadataOnly) foreach (var pair in folderChecks) pair.Value.Checked = pair.Key == "metadata";
        else if (preset == BulkPreset.WithMetadata) folderChecks["metadata"].Checked = true;
        else if (preset == BulkPreset.ImageOnly) foreach (var pair in folderChecks) pair.Value.Checked = pair.Key == ImageSettings.Folder;
        else if (preset == BulkPreset.VideoOnly) foreach (var pair in folderChecks) pair.Value.Checked = pair.Key == "videos";
        else if (preset == BulkPreset.MediaOnly) foreach (var pair in folderChecks) pair.Value.Checked = pair.Key == ImageSettings.Folder || pair.Key == "videos";
        FillSystems();

        // Doble clic en cualquier parte de la fila (menos en su casilla, que ya responde a cada clic) marca o desmarca el sistema.
        systemsGrid.CellDoubleClick += (_, e) =>
        {
            if (running || e.RowIndex < 0 || e.ColumnIndex == 0) return;
            var cell = systemsGrid.Rows[e.RowIndex].Cells["use"];
            if (!cell.ReadOnly) cell.Value = cell.Value is not true;
        };
        selectAllButton.Click += (_, _) => SetAllSystems(true);
        selectNoneButton.Click += (_, _) => SetAllSystems(false);
        systemsGrid.CurrentCellDirtyStateChanged += (_, _) => { if (systemsGrid.IsCurrentCellDirty) systemsGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        systemsGrid.CellValueChanged += (_, e) => { if (!updating && e.RowIndex >= 0 && e.ColumnIndex == 0) UpdateSystemsCount(); };
        foreach (var element in folderChecks.Values) element.CheckedChanged += (_, _) => UpdateDependentControls();
        allMediaCheck.Click += (_, _) =>
        {
            // Sin esta protección cada casilla marcada volvía a recalcular la general y el resto se hacía una a una.
            bool value = allMediaCheck.Checked;
            bulkChanging = true;
            try { foreach (var check in folderChecks.Values) check.Checked = value; }
            finally { bulkChanging = false; }
            SyncAllMediaCheck();
        };
        foreach (var check in folderChecks.Values) check.CheckedChanged += (_, _) => SyncAllMediaCheck();
        SyncAllMediaCheck();
        startButton.Click += async (_, _) => await StartAsync();
        cancelButton.Click += (_, _) => { cts?.Cancel(); statusLabel.Text = "Cancelando…"; };
        exportButton.Click += (_, _) => ExportIssues();
        debugButton.Click += (_, _) => OpenDebugWindow();
        viewSummaryButton.Click += (_, _) => ShowResult();
        Shown += async (_, _) =>
        {
            // Sin credenciales se ofrece registrarlas (o darse de alta) antes de seguir.
            credentials = ScreenScraperAccount.Ensure(this, trace, credentials);
            await LoadSsSystemsAsync();
        };
        FormClosing += (_, e) =>
        {
            if (!running) return;
            cts?.Cancel();
            statusLabel.Text = "Terminando el elemento en curso; cierra de nuevo cuando acabe.";
            e.Cancel = true;
        };
    }

    private void BuildLayout()
    {
        // ---- Sistemas
        systemsGrid.Dock = DockStyle.Fill;
        systemsGrid.AllowUserToAddRows = false;
        systemsGrid.AllowUserToDeleteRows = false;
        systemsGrid.AllowUserToResizeRows = false;
        systemsGrid.RowHeadersVisible = false;
        systemsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        systemsGrid.MultiSelect = false;
        systemsGrid.BackgroundColor = SystemColors.Window;
        systemsGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "use", HeaderText = "", Width = 34 });
        systemsGrid.Columns.Add(SystemIconColumn.Create(30));
        systemsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "system", HeaderText = "Carpeta de ES-DE", Width = 150, ReadOnly = true });
        systemsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ss", HeaderText = "Sistema en ScreenScraper", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        systemsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "roms", HeaderText = "ROMs", Width = 70, ReadOnly = true, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter }, HeaderCell = { Style = { Alignment = DataGridViewContentAlignment.MiddleCenter } } });

        var systemsButtons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, WrapContents = false, Margin = Padding.Empty };
        systemsButtons.Controls.Add(selectAllButton);
        systemsButtons.Controls.Add(selectNoneButton);
        var systemsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        systemsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        systemsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        systemsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        systemsLayout.Controls.Add(systemsButtons, 0, 0);
        systemsLayout.Controls.Add(systemsGrid, 0, 1);
        var systemsGroup = systemsGroupBox;
        systemsGroup.Controls.Add(systemsLayout);

        // ---- Medios (las carpetas de ES-DE)
        // Una fila por elemento: a la izquierda su casilla y a la derecha cómo se sustituye si ya existe en destino (Siempre / Nuevo es menor /
        // Nuevo es mayor). Los idiomas no llevan opciones de sustitución. El vídeo ocupa tres filas: la suya y, debajo, Normalizado y Original.
        var mediaFlow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = false, Margin = Padding.Empty, Padding = new Padding(0, 2, 0, 0) };
        mediaFlow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        mediaFlow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mediaFlow.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); // fila elástica superior (centra los controles)
        int mediaRow = 1;
        void AddElement(Control left, Control? right)
        {
            mediaFlow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.Anchor = AnchorStyles.Left;
            mediaFlow.Controls.Add(left, 0, mediaRow);
            if (right is not null) { right.Anchor = AnchorStyles.Left; mediaFlow.Controls.Add(right, 1, mediaRow); }
            else mediaFlow.SetColumnSpan(left, 2);
            mediaRow++;
        }
        FlowLayoutPanel ReplaceOptions(string element)
        {
            var always = new RadioButton { Text = "Siempre", AutoSize = true, Margin = new Padding(0, 0, 12, 0) };
            var smaller = new RadioButton { Text = "Nuevo es menor", AutoSize = true, Checked = true, Margin = new Padding(0, 0, 12, 0) };
            var larger = new RadioButton { Text = "Nuevo es mayor", AutoSize = true, Margin = Padding.Empty };
            replaceRadios[element] = (always, smaller, larger);
            var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
            panel.Controls.Add(always);
            panel.Controls.Add(smaller);
            panel.Controls.Add(larger);
            return panel;
        }
        allMediaCheck.Margin = new Padding(6, 2, 3, 2);
        // Cabecera de las columnas de sustitución: un cuadro con borde fino y el texto centrado, tan ancho como las tres opciones.
        replaceHeader = new Label
        {
            Text = "Sobrescribir", AutoSize = false, Size = new Size(328, 22), TextAlign = ContentAlignment.MiddleCenter,
            BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 2, 0, 2)
        };
        AddElement(allMediaCheck, replaceHeader);
        // Los metadatos son un elemento más de la lista.
        folderChecks["metadata"] = new CheckBox { Text = "Metadatos", AutoSize = true, Margin = new Padding(6, 2, 12, 2) };
        AddElement(folderChecks["metadata"], ReplaceOptions("metadata"));
        // Los idiomas de cada ROM se consultan a ScreenScraper y se guardan solo en la aplicación, para las reglas de curado por idioma.
        folderChecks["languages"] = new CheckBox { Text = "Idiomas (para el curado)", AutoSize = true, Margin = new Padding(6, 2, 12, 2) };
        AddElement(folderChecks["languages"], null);
        // El vídeo va el último: junto a su casilla, a la derecha, sus dos opciones (Normalizado y Original) una encima de la otra.
        foreach (var folder in EsDeMediaInstaller.Folders.Where(f => f.Folder != "videos").Concat(EsDeMediaInstaller.Folders.Where(f => f.Folder == "videos")))
        {
            string description = folder.Description.Replace(" (cartucho, disco)", "");
            string text = folder.Folder == "miximages"
                ? $"{description} ({folder.Folder}) — Recalbox v2"
                : folder.Folder == "videos" ? "Vídeo" : $"{description} ({folder.Folder})";
            var check = new CheckBox { Text = text, AutoSize = true, Margin = new Padding(6, 2, 12, 2) };
            folderChecks[folder.Folder] = check;
            if (folder.Folder != "videos") { AddElement(check, ReplaceOptions(folder.Folder)); continue; }
            normalizedRadio.Text = "Normalizado";
            originalRadio.Text = "Original";
            normalizedRadio.Margin = new Padding(0, 1, 3, 1);
            originalRadio.Margin = new Padding(0, 1, 3, 1);
            var videoOptions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
            videoOptions.Controls.Add(normalizedRadio);
            videoOptions.Controls.Add(originalRadio);
            check.Anchor = AnchorStyles.Left;
            videoOptions.Anchor = AnchorStyles.Left;
            var videoCell = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
            videoCell.Controls.Add(check);
            videoCell.Controls.Add(videoOptions);
            AddElement(videoCell, ReplaceOptions(folder.Folder));
        }
        mediaFlow.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); // fila elástica inferior: con la superior, centra los controles verticalmente
        mediaFlow.RowCount = mediaRow + 1;
        var mediaGroup = new GroupBox { Text = "Medios a descargar", Dock = DockStyle.Fill };
        mediaGroup.Controls.Add(mediaFlow);

        // ---- Opciones
        var optionsFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = false };
        optionsFlow.Controls.Add(onlyNoMediaCheck);
        onlyNoMediaCheck.CheckedChanged += (_, _) => RefreshRomFilter();
        optionsFlow.Controls.Add(searchNameCheck);
        optionsFlow.Controls.Add(threadsInfo);
        var optionsGroup = new GroupBox { Text = "Opciones", Dock = DockStyle.Fill };
        // Los controles de las opciones van centrados verticalmente en su panel.
        optionsFlow.Dock = DockStyle.None; optionsFlow.AutoSize = true; optionsFlow.Anchor = AnchorStyles.Left;
        var optionsCenter = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Margin = Padding.Empty };
        optionsCenter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        optionsCenter.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        optionsCenter.Controls.Add(optionsFlow, 0, 0);
        optionsGroup.Controls.Add(optionsCenter);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        right.Controls.Add(mediaGroup, 0, 0);
        right.Controls.Add(optionsGroup, 0, 1);

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
        content.Controls.Add(systemsGroup, 0, 0);
        content.Controls.Add(right, 1, 0);

        // ---- Incidencias
        issuesGrid.Dock = DockStyle.Fill;
        issuesGrid.ReadOnly = true;
        issuesGrid.AllowUserToAddRows = false;
        issuesGrid.AllowUserToDeleteRows = false;
        issuesGrid.RowHeadersVisible = false;
        issuesGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        issuesGrid.BackgroundColor = SystemColors.Window;
        // Lista de incidencias más pequeña: letra de 8 pt, filas y cabecera bajas.
        var smallFont = new Font(Font.FontFamily, 8F);
        issuesGrid.DefaultCellStyle.Font = smallFont;
        issuesGrid.ColumnHeadersDefaultCellStyle.Font = smallFont;
        issuesGrid.RowTemplate.Height = 18;
        issuesGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        issuesGrid.ColumnHeadersHeight = 20;
        issuesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "system", HeaderText = "Sistema", Width = 110 });
        issuesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "rom", HeaderText = "ROM", Width = 330 });
        issuesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "result", HeaderText = "Resultado", Width = 150 });
        issuesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "detail", HeaderText = "Detalle", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        var issuesGroup = new GroupBox { Text = "Incidencias (ROMs no encontradas, encontradas por nombre, medios no disponibles y errores)", Dock = DockStyle.Fill };
        issuesGroup.Controls.Add(issuesGrid);
        issuesGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenSingleScrape(issuesGrid.Rows[e.RowIndex]); };

        // ---- Botones
        var centerButtons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, WrapContents = false, Margin = Padding.Empty };
        centerButtons.Controls.Add(startButton);
        centerButtons.Controls.Add(cancelButton);
        centerButtons.Controls.Add(debugButton);
        centerButtons.Controls.Add(exportButton);
        centerButtons.Controls.Add(viewSummaryButton);
        centerButtons.Controls.Add(closeButton);
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Margin = Padding.Empty };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.Controls.Add(centerButtons, 0, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(8, 6, 8, 4) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(content, 0, 0);
        layout.Controls.Add(summaryLabel, 0, 1);
        layout.Controls.Add(progressBar, 0, 2);
        layout.Controls.Add(statusLabel, 0, 3);
        layout.Controls.Add(issuesGroup, 0, 4);
        layout.Controls.Add(buttons, 0, 5);
        Controls.Add(layout);
        Controls.Add(quotaStrip);
    }

    private void ApplySettings(BulkMediaSettings settings)
    {
        foreach (var (folder, check) in folderChecks) check.Checked = settings.Folders.Contains(folder);
        normalizedRadio.Checked = settings.NormalizedVideo;
        originalRadio.Checked = !settings.NormalizedVideo;
        foreach (var (element, radios) in replaceRadios)
        {
            var policy = settings.Replace is not null && settings.Replace.TryGetValue(element, out var saved) && Enum.TryParse<BulkReplace>(saved, out var parsed) ? parsed : BulkReplace.NewSmaller;
            radios.Always.Checked = policy == BulkReplace.Always;
            radios.Smaller.Checked = policy == BulkReplace.NewSmaller;
            radios.Larger.Checked = policy == BulkReplace.NewLarger;
        }
        searchNameCheck.Checked = settings.SearchByName;
        UpdateThreadsInfo();
        UpdateDependentControls();
    }

    // «Overwrite» queda por compatibilidad con lo ya guardado; la sustitución real va por elemento (Replace).
    private BulkMediaSettings CollectSettings() => new(
        folderChecks.Where(p => p.Value.Checked).Select(p => p.Key).ToArray(),
        normalizedRadio.Checked, BulkOverwrite.IfSizeDiffers, false, searchNameCheck.Checked,
        [])
    { Replace = replaceRadios.ToDictionary(p => p.Key, p => ReplaceOf(p.Key).ToString()) };

    private BulkReplace ReplaceOf(string element) =>
        !replaceRadios.TryGetValue(element, out var radios) ? BulkReplace.NewSmaller
        : radios.Always.Checked ? BulkReplace.Always : radios.Larger.Checked ? BulkReplace.NewLarger : BulkReplace.NewSmaller;

    // La casilla general refleja si todos los elementos (medios y metadatos) están marcados.
    private bool bulkChanging;

    private void SyncAllMediaCheck() { if (!bulkChanging) allMediaCheck.Checked = folderChecks.Values.All(c => c.Checked); }

    private void UpdateDependentControls()
    {
        bool videos = folderChecks["videos"].Checked && !running;
        normalizedRadio.Enabled = originalRadio.Enabled = videos;
        // Las opciones de sustitución de cada elemento solo están activas si el elemento está marcado.
        foreach (var (element, radios) in replaceRadios)
            radios.Always.Enabled = radios.Smaller.Enabled = radios.Larger.Enabled = folderChecks[element].Checked && !running;
    }

    // ---- Sistemas

    // ROMs a las que se aplica la descarga: todas, o solo las que no tienen metadatos y/o las que no tienen imagen o vídeo (si se marcan las dos, las que les falte alguna cosa).
    private IReadOnlyList<RomRow> ActiveRoms
    {
        get
        {
            if (!onlyNoMediaCheck.Checked) return roms;
            gapRows ??= MediaGaps.Rows(root, roms);
            var gap = gapRows;
            return roms.Where(r => gap.NoMedia.Contains(r.Path)).ToList();
        }
    }

    // Al cambiar el filtro se recalcula la lista de sistemas conservando los que estaban marcados.
    private void RefreshRomFilter()
    {
        if (updating) return;
        foreach (DataGridViewRow row in systemsGrid.Rows)
        {
            string system = (string)row.Cells["system"].Value!;
            if (row.Cells["use"].Value is true) preselectedSystems.Add(system); else preselectedSystems.Remove(system);
        }
        FillSystems();
    }

    private void FillSystems()
    {
        updating = true;
        try
        {
            systemsGrid.Rows.Clear();
            foreach (var group in ActiveRoms.Where(r => r.Status == RomStatus.Ok && r.Path.Length > 0)
                                      .GroupBy(r => r.System, StringComparer.OrdinalIgnoreCase)
                                      .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                systemsGrid.Rows.Add(preselectedSystems.Contains(group.Key), SystemIconColumn.Icon(group.Key, 18), group.Key, "", group.Count());
        }
        finally { updating = false; }
        UpdateSystemsCount();
    }

    private async Task LoadSsSystemsAsync()
    {
        if (!credentials.HasDeveloper)
        {
            statusLabel.Text = "Faltan las credenciales de ScreenScraper: se configuran en Opciones › Credenciales de ScreenScraper.";
            return;
        }
        try
        {
            statusLabel.Text = "Cargando la lista de sistemas de ScreenScraper…";
            ssSystems = await new ScreenScraperClient(credentials).GetSystemsAsync(false, CancellationToken.None);
            await RefreshAccountAsync();
            updating = true;
            try
            {
                foreach (DataGridViewRow row in systemsGrid.Rows)
                {
                    var match = SsSystemResolver.Find(ssSystems, (string)row.Cells["system"].Value!);
                    row.Cells["ss"].Value = match is null ? "No reconocido: se omitirá" : $"{match.Name}";
                    if (match is null)
                    {
                        row.Cells["use"].Value = false;
                        row.Cells["use"].ReadOnly = true;
                        row.DefaultCellStyle.ForeColor = SystemColors.GrayText;
                    }
                }
            }
            finally { updating = false; }
            UpdateSystemsCount();
            statusLabel.Text = "Marca los sistemas y los medios que quieres descargar y pulsa «Scrapear».";
        }
        catch (Exception ex) { statusLabel.Text = "No se pudo cargar la lista de sistemas: " + ex.Message; }
    }

    // Lee del servidor cuántos hilos permite hoy la cuenta (la lectura guardada puede ser de antes de ampliarla).
    private async Task RefreshAccountAsync()
    {
        try { await new ScreenScraperClient(credentials).RefreshUserAsync(CancellationToken.None); }
        catch { /* si falla, se sigue con lo último que se leyó */ }
        UpdateThreadsInfo();
    }

    private static int Threads() => Math.Max(1, ScreenScraperQuota.MaxThreads);

    private void UpdateThreadsInfo()
    {
        int allowed = ScreenScraperQuota.MaxThreads;
        threadsInfo.Text = allowed > 0
            ? $"Peticiones en paralelo: {allowed} (según tu membresía de ScreenScraper)"
            : "Peticiones en paralelo: se leerán de tu cuenta al conectar (por ahora 1)";
    }

    private void SetAllSystems(bool value)
    {
        updating = true;
        try
        {
            foreach (DataGridViewRow row in systemsGrid.Rows)
                if (!row.Cells["use"].ReadOnly) row.Cells["use"].Value = value;
        }
        finally { updating = false; }
        UpdateSystemsCount();
    }

    private List<string> SelectedSystems() => systemsGrid.Rows.Cast<DataGridViewRow>()
        .Where(r => r.Cells["use"].Value is true && !r.Cells["use"].ReadOnly)
        .Select(r => (string)r.Cells["system"].Value!).ToList();

    private void UpdateSystemsCount()
    {
        var rows = systemsGrid.Rows.Cast<DataGridViewRow>().Where(r => r.Cells["use"].Value is true && !r.Cells["use"].ReadOnly).ToList();
        int romCount = rows.Sum(r => Convert.ToInt32(r.Cells["roms"].Value));
        systemsGroupBox.Text = rows.Count == 0 ? "Sistemas a los que se aplica" : $"Sistemas a los que se aplica — {rows.Count} marcados · {romCount:N0} ROMs";
    }

    // ---- Ejecución

    private BulkMediaOptions BuildOptions(List<string> systems)
    {
        var folders = EsDeMediaInstaller.Folders.Where(f => folderChecks[f.Folder].Checked).ToList();
        // La sustitución va por elemento (metadatos y cada carpeta de medios); «Overwrite» solo importa para el modo antiguo, que aquí no se usa.
        var replace = replaceRadios.Keys.ToDictionary(k => k, ReplaceOf);
        return new BulkMediaOptions(root, ActiveRoms, ssSystems, systems, folders, normalizedRadio.Checked, BulkOverwrite.IfSizeDiffers, false, searchNameCheck.Checked, Threads(), folderChecks["metadata"].Checked, gamelistBackups, folderChecks["languages"].Checked)
        { Replace = replace };
    }

    private async Task StartAsync()
    {
        if (!credentials.HasDeveloper) { QuietMessage.Show(this, "Descarga masiva", "Faltan las credenciales de ScreenScraper: se configuran en Opciones › Credenciales de ScreenScraper de la ventana principal."); return; }
        if (ssSystems.Count == 0) { QuietMessage.Show(this, "Descarga masiva", "Todavía no se ha cargado la lista de sistemas de ScreenScraper. Espera un momento o revisa las credenciales."); return; }
        var systems = SelectedSystems();
        if (systems.Count == 0) { QuietMessage.Show(this, "Descarga masiva", "Marca al menos un sistema."); return; }
        if (!folderChecks.Values.Any(c => c.Checked)) { QuietMessage.Show(this, "Descarga masiva", "Marca al menos un tipo de medio."); return; }

        CollectSettings().Save();
        // Los hilos dependen de la membresía: se vuelven a leer del servidor por si ha cambiado desde que se abrió el formulario.
        statusLabel.Text = "Leyendo los datos de tu cuenta de ScreenScraper…";
        await RefreshAccountAsync();
        var options = BuildOptions(systems);

        statusLabel.Text = "Calculando el trabajo pendiente…";
        SetRunning(true, planning: true);
        BulkPlan plan;
        try { plan = await Task.Run(() => BulkMediaRunner.Plan(options)); }
        finally { SetRunning(false); }

        plannedWork = plan.RomsWithWork;
        statusLabel.Text = "Trabajo calculado."; // sin esto se queda el texto «Calculando…» si luego no se descarga nada
        if (plan.Roms == 0)
        {
            statusLabel.Text = "Los sistemas marcados no tienen ROMs que procesar.";
            return;
        }
        var counts = ActiveRoms.Where(r => r.Status == RomStatus.Ok && r.Path.Length > 0)
            .GroupBy(r => r.System, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var rows = systems.Select(s => (s, counts.GetValueOrDefault(s))).ToList();
        // Elementos marcados agrupados por cómo se sustituyen si ya existen en destino.
        var marked = options.Folders.Select(f => f.Folder).Concat(options.Metadata ? ["metadata"] : []).ToList();
        string Group(BulkReplace policy) => string.Join(", ", marked.Where(e => options.ReplaceFor(e) == policy).Select(e => e == "metadata" ? "metadatos" : e));
        var replaceLines = new List<string>();
        if (Group(BulkReplace.Always).Length > 0) replaceLines.Add($"SE SUSTITUYE SIEMPRE: {Group(BulkReplace.Always)}.");
        if (Group(BulkReplace.NewSmaller).Length > 0) replaceLines.Add($"Se sustituye solo si el nuevo es menor: {Group(BulkReplace.NewSmaller)}.");
        if (Group(BulkReplace.NewLarger).Length > 0) replaceLines.Add($"Se sustituye solo si el nuevo es mayor: {Group(BulkReplace.NewLarger)}.");
        var footer = new List<string>
        {
            "Si ya existe en destino (lo sustituido no se conserva):",
            $"Si no se reconoce por hash: {(options.SearchByName ? "se busca por el nombre del juego" : "se anota como no encontrada")}."
        };
        footer.InsertRange(1, replaceLines);
        if (plan.UnresolvedSystems > 0) footer.Add($"{plan.UnresolvedSystems} sistema(s) no reconocidos en ScreenScraper se omiten.");
        if (plan.RomsWithWork == 0)
        {
            footer.Add("No hay nada pendiente.");
            using var info = new BulkConfirmDialog(rows, footer, "Aceptar", confirm: false);
            info.ShowDialog(this);
            statusLabel.Text = "No hay nada pendiente: lo pedido ya está en destino (o en el índice de idiomas).";
            return;
        }
        using (var confirmDialog = new BulkConfirmDialog(rows, footer, "Scrapear", confirm: true))
            if (confirmDialog.ShowDialog(this) != DialogResult.OK) { statusLabel.Text = "Descarga cancelada: no se ha pedido nada."; return; }

        cts?.Dispose();
        cts = new CancellationTokenSource();
        issuesGrid.Rows.Clear(); UpdateExportButton();
        clock.Restart();
        etaComputedAtSeconds = -1000;
        etaText = "calculando…";
        SetRunning(true);
        var status = new Progress<BulkStatus>(OnStatus);
        var issues = new Progress<BulkIssue>(i =>
        {
            // El detalle completo queda también como tooltip por si no cabe en la columna.
            var gridRow = issuesGrid.Rows[issuesGrid.Rows.Add(i.System, i.Rom, i.Result, i.Detail)];
            gridRow.Cells["detail"].ToolTipText = i.Detail;
            gridRow.Cells["rom"].ToolTipText = i.Rom;
            UpdateExportButton();
        });
        var runner = new BulkMediaRunner(options, credentials, trace, status, issues) { Debug = debugLog.Add };
        activeRunner = runner;
        uiTimer.Start();
        try { await runner.RunAsync(cts.Token); }
        catch (Exception ex)
        {
            trace.Add("bulk_media_error", ex.Message);
            statusLabel.Text = "Error: " + ex.Message;
        }
        finally
        {
            clock.Stop();
            uiTimer.Stop();
            activeRunner = null;
            CollectionChanged |= runner.CollectionChanged;
            MetadataChanged |= runner.MetadataChanged;
            SetRunning(false);
        }
        Text = baseTitle;
        summaryLabel.Text = SummaryText(runner.Summary);
        string result = runner.Summary.StoppedReason.Length > 0 ? runner.Summary.StoppedReason : "Descarga terminada.";
        statusLabel.Text = result;
        progressBar.Value = runner.Summary.StoppedReason.Length == 0 ? 100 : Percent(runner.Summary);
        lastResult = (result, SummaryText(runner.Summary), runner.Summary);
        viewSummaryButton.Enabled = true;
        ShowResult();
    }

    private (string Headline, string Counters, BulkSummary Summary)? lastResult;

    private void ShowResult()
    {
        if (lastResult is not { } r) return;
        using var dialog = new BulkResultDialog(r.Headline, r.Counters, r.Summary);
        dialog.ShowDialog(this);
    }

    private void OnStatus(BulkStatus s)
    {
        statusLabel.Text = s.Text;
        RefreshProgress();
    }

    // ROMs con trabajo ya terminadas sobre las que tenían trabajo al empezar.
    private int FinishedWork(BulkSummary s) => Math.Max(0, s.RomsFinished - s.AlreadyComplete);

    private int Percent(BulkSummary s) => plannedWork > 0 ? Math.Clamp(FinishedWork(s) * 100 / plannedWork, 0, 100) : 0;

    // Resumen, barra de progreso y título de la ventana con el avance total.
    private void RefreshProgress()
    {
        if (activeRunner is null) return;
        var s = activeRunner.Summary;
        int percent = Percent(s);
        progressBar.Value = percent;
        summaryLabel.Text = SummaryText(s);
        Text = $"{baseTitle} — {percent} %";
    }

    private static string FormatSpan(TimeSpan span) => $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}";

    // Tiempo restante a partir del ritmo medio de las ROMs con trabajo ya terminadas; hasta tener datos suficientes, «calculando…».
    private string RemainingText(TimeSpan elapsed, int finished)
    {
        if (plannedWork - finished <= 0) return "0:00:00";
        if (finished < 2 || elapsed.TotalSeconds < 5) return "calculando…";
        // La estimación varía mucho de un momento a otro: se recalcula solo cada 5 segundos y entre medias se mantiene el último valor.
        if (elapsed.TotalSeconds - etaComputedAtSeconds >= 5)
        {
            etaText = "~" + FormatSpan(TimeSpan.FromSeconds(elapsed.TotalSeconds * (plannedWork - finished) / finished));
            etaComputedAtSeconds = elapsed.TotalSeconds;
        }
        return etaText;
    }

    private double etaComputedAtSeconds = -1000;
    private string etaText = "calculando…";

    private string SummaryText(BulkSummary s)
    {
        var parts = new List<string>
        {
            plannedWork > 0 ? $"ROMs {FinishedWork(s):N0}/{plannedWork:N0}" : $"ROMs {s.RomsDone:N0}/{s.RomsTotal:N0}",
            $"archivos guardados {s.FilesSaved:N0}" + (s.FilesReplaced > 0 ? $" ({s.FilesReplaced:N0} sustituidos)" : ""),
            $"descargado {ScreenScraperDialog.FormatBytes(s.BytesDownloaded)}",
            $"ya completas {s.AlreadyComplete:N0}"
        };
        if (s.MetadataUpdated > 0 || s.MetadataFailed > 0) parts.Add($"metadatos {s.MetadataUpdated:N0}" + (s.MetadataFailed > 0 ? $" ({s.MetadataFailed:N0} fallidos)" : ""));
        if (s.LanguagesStored > 0) parts.Add($"idiomas {s.LanguagesStored:N0}");
        if (s.FilesIdentical > 0) parts.Add($"idénticos {s.FilesIdentical:N0}");
        if (s.FoldersUnavailable > 0) parts.Add($"sin medio disponible {s.FoldersUnavailable:N0}");
        if (s.NotFound > 0) parts.Add($"no encontradas {s.NotFound:N0}");
        if (s.FoundByName > 0) parts.Add($"por nombre {s.FoundByName:N0}");
        if (s.WithErrors > 0) parts.Add($"con errores {s.WithErrors:N0}");
        if (s.SkippedSystems > 0 || s.SkippedNotFile > 0) parts.Add($"omitidas {s.SkippedNotFile:N0} (no son archivo)");
        var elapsed = clock.Elapsed;
        // El tiempo va en la barra inferior, a la derecha.
        quotaStrip.TimeText = $"tiempo {FormatSpan(elapsed)}" + (running && plannedWork > 0 ? $" · restante {RemainingText(elapsed, FinishedWork(s))}" : "");
        return string.Join(" · ", parts);
    }

    private void SetRunning(bool value, bool planning = false)
    {
        running = value && !planning;
        bool locked = value;
        systemsGrid.Enabled = !locked;
        selectAllButton.Enabled = selectNoneButton.Enabled = !locked;
        foreach (var check in folderChecks.Values) check.Enabled = !locked;
        allMediaCheck.Enabled = !locked;
        searchNameCheck.Enabled = onlyNoMediaCheck.Enabled = replaceHeader.Enabled = !locked;
        startButton.Enabled = closeButton.Enabled = !locked; UpdateExportButton();
        viewSummaryButton.Enabled = !locked && lastResult is not null;
        cancelButton.Enabled = running;
        if (!value) UpdateDependentControls();
        else
        {
            normalizedRadio.Enabled = originalRadio.Enabled = false;
            foreach (var radios in replaceRadios.Values) radios.Always.Enabled = radios.Smaller.Enabled = radios.Larger.Enabled = false;
        }
    }

    // «Exportar incidencias» solo está disponible cuando hay alguna en la lista.
    // Ventana (no modal) con el registro de depuración en directo; si ya está abierta, se trae al frente.
    private void OpenDebugWindow()
    {
        if (debugWindow is { IsDisposed: false }) { debugWindow.Activate(); return; }
        debugWindow = new BulkDebugDialog(debugLog);
        debugWindow.FormClosed += (_, _) => debugWindow = null;
        debugWindow.Show(this);
    }

    // Doble clic en una incidencia: se abre el scrapeo de esa ROM sin cerrar este formulario y, al volver, se quitan las incidencias que ya no tienen sentido.
    private void OpenSingleScrape(DataGridViewRow gridRow)
    {
        if (OpenSingleHandler is null) return;
        if (running) { statusLabel.Text = "Espera a que termine la descarga para abrir el scrapeo de una ROM."; return; }
        string system = gridRow.Cells["system"].Value?.ToString() ?? "", rom = gridRow.Cells["rom"].Value?.ToString() ?? "";
        var row = roms.FirstOrDefault(r => r.System.Equals(system, StringComparison.OrdinalIgnoreCase) && r.RomName.Equals(rom, StringComparison.OrdinalIgnoreCase));
        if (row is null) return;
        trace.Add("bulk_issue_single_scrape", $"system={system}; rom={rom}");
        var (media, metadata) = OpenSingleHandler(row, this);
        CollectionChanged |= media;
        MetadataChanged |= metadata;
        Activate();
        if (!media && !metadata) return;

        // Un «medio no disponible» se da por resuelto si ya existen todos los medios que faltaban; el resto de incidencias (no encontrada, error…), si algo cambió.
        int removed = 0;
        for (int i = issuesGrid.Rows.Count - 1; i >= 0; i--)
        {
            var candidate = issuesGrid.Rows[i];
            if (!string.Equals(candidate.Cells["system"].Value?.ToString(), system, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(candidate.Cells["rom"].Value?.ToString(), rom, StringComparison.OrdinalIgnoreCase)) continue;
            bool solved = true;
            if (candidate.Cells["result"].Value?.ToString() == "Medio no disponible")
            {
                string detail = candidate.Cells["detail"].Value?.ToString() ?? "";
                var listed = EsDeMediaInstaller.Folders.Where(f => detail.Contains(f.Description, StringComparison.OrdinalIgnoreCase)).ToList();
                solved = listed.Count > 0 && listed.All(f =>
                {
                    try { return EsDeMediaInstaller.Existing(EsDeMediaInstaller.TargetBase(root, row, f.Folder)).Count > 0; }
                    catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException) { return false; }
                });
            }
            if (!solved) continue;
            issuesGrid.Rows.RemoveAt(i);
            removed++;
        }
        UpdateExportButton();
        statusLabel.Text = removed > 0 ? $"Incidencias resueltas de {rom}: {removed}." : $"Se actualizó {rom}, pero sigue habiendo incidencias.";
    }

    private void UpdateExportButton() => exportButton.Enabled = issuesGrid.Rows.Count > 0;

    private void ExportIssues()
    {
        if (issuesGrid.Rows.Count == 0) { QuietMessage.Show(this, "Exportar incidencias", "No hay incidencias que exportar."); return; }
        using var dialog = new SaveFileDialog
        {
            Title = Loc.T("Exportar incidencias"), Filter = Loc.T("CSV (*.csv)|*.csv"), DefaultExt = "csv",
            FileName = $"incidencias_screenscraper_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string Quote(object? value) => "\"" + (value?.ToString() ?? "").Replace("\"", "\"\"") + "\"";
        var lines = new List<string> { "Sistema;ROM;Resultado;Detalle" };
        foreach (DataGridViewRow row in issuesGrid.Rows)
            lines.Add(string.Join(";", row.Cells.Cast<DataGridViewCell>().Select(c => Quote(c.Value))));
        try { File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true)); statusLabel.Text = $"Incidencias exportadas a {dialog.FileName}"; }
        catch (Exception ex) { QuietMessage.Show(this, "Exportar incidencias", ex.Message); }
    }
}
