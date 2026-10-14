namespace ESDERomCleaner;

// Añadir ROMs a la colección: se arrastran archivos al formulario, se identifican (hash o nombre con los DAT, o por extensión)
// y se copian a la carpeta de su sistema en Roms. Las que no se pueden decidir solas van a una lista aparte.
internal sealed class AddRomsDialog : LocalizedForm
{
    private readonly string root;
    private readonly TraceLog trace;
    private readonly DataGridView grid;      // identificadas: listas para añadir
    private readonly DataGridView doubtGrid; // con dudas: hay que elegir la carpeta
    private readonly Label doubtLabel = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft, Font = null };
    private readonly ProgressBar progress = new();
    private readonly Label storageSummary = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText };
    private StorageOptions storageOptions = new(StorageFormat.Keep, StorageLevel.Normal);
    private StorageOptions CurrentStorage => storageOptions;

    // Resumen de lo elegido en Opciones › Cómo guardar las ROMs…
    private void UpdateStorageSummary()
    {
        string text = storageOptions.Format switch
        {
            StorageFormat.Keep => "Se guardarán como llegan, sin tocarlas.",
            StorageFormat.Raw => "Se guardarán sin comprimir (se extrae lo comprimido).",
            StorageFormat.Zip => $"Se guardarán en ZIP, compresión {RomStorage.LevelName(storageOptions.Level)}.",
            _ => $"Se guardarán en 7z, compresión {RomStorage.LevelName(storageOptions.Level)}."
        };
        if (storageOptions.Format == StorageFormat.SevenZip && RomStorage.SevenZipPath() is null) text += " (No se encuentra 7za.exe: se usará zip.)";
        storageSummary.Text = text + " Si el sistema no admite el formato, se usa el más parecido. Cámbialo en Opciones.";
    }

    private void ChooseStorage()
    {
        using var dialog = new StorageOptionsDialog(storageOptions);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        storageOptions = dialog.Result;
        new StorageSettings { Format = storageOptions.Format, Level = storageOptions.Level }.Save();
        UpdateStorageSummary();
        foreach (var row in grid.Rows.Cast<DataGridViewRow>().ToList()) UpdateRowState(row);
    }
    private readonly Label identifiedLabel = new();
    private readonly Label status = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    private readonly CheckBox moveFiles = new() { Text = "Mover los archivos en lugar de copiarlos", AutoSize = true };
    private readonly Button addButton = new() { Text = "Añadir a la colección", AutoSize = true, MinimumSize = new Size(150, 28), Enabled = false };
    private readonly Button curateButton = new() { Text = "Curar y añadir", AutoSize = true, MinimumSize = new Size(130, 28), Enabled = false };
    private readonly Button browseButton = new() { Text = "Examinar…", AutoSize = true, MinimumSize = new Size(100, 28) };
    private readonly Button removeButton = new() { Text = "Quitar seleccionadas", AutoSize = true, MinimumSize = new Size(130, 28), Enabled = false };
    private readonly Button clearButton = new() { Text = "Vaciar listas", AutoSize = true, MinimumSize = new Size(100, 28) };
    private readonly Button closeButton = new() { Text = "Cerrar", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
    private readonly List<RomIdentity> items = [];
    private readonly HashSet<string> known = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> existingFolders;
    private readonly CancellationTokenSource cts = new();
    // Carpeta temporal donde se extraen los packs; se borra al cerrar. packCounts: cuántas ROMs de cada pack siguen en la lista.
    private readonly string tempRoot = Path.Combine(AppPaths.Temp, "expand", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, int> packCounts = new(StringComparer.OrdinalIgnoreCase);
    private bool working;
    private bool syncingColumns;
    private int lastIgnored;

    // Ejecuta una acción sobre la interfaz desde cualquier hilo. Si el formulario ya se ha cerrado (o aún no tiene ventana), se ignora en vez de provocar una excepción.
    private void Ui(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!IsDisposed) action(); }); }
        catch (InvalidOperationException) { }
    }

    // Carpeta que agrupa los temporales de todas las sesiones del formulario (los de sesiones anteriores que no se limpiaron, por un cierre brusco).
    private static string ExpandRoot => Path.Combine(AppPaths.Temp, "expand");

    private static void DeleteFolder(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* en uso o sin permiso: se reintentará la próxima vez */ }
    }

    // Borra lo extraído de los packs: los temporales de esta sesión y, al abrir, los que dejara una sesión anterior.
    private void CleanTemp() => DeleteFolder(tempRoot);
    private static void CleanLeftovers() => DeleteFolder(ExpandRoot);

    // ---- Duplicadas: ROMs que ya existen en la colección

    private readonly StaticGrid dupGrid;
    private readonly Label dupLabel = new();
    private TableLayoutPanel middle = null!;
    private readonly Label dupCount = new() { AutoSize = true, Margin = new Padding(2, 8, 10, 0), Text = "0 duplicadas" };
    private readonly Button dupButton = new() { Text = "⛔", Width = 32, Height = 28, Font = new Font("Segoe UI Symbol", 11f), UseCompatibleTextRendering = true, Margin = new Padding(3, 3, 0, 3) };
    private bool dupVisible;

    // Índice de lo que ya hay en la carpeta de un sistema: por nombre de fichero (sin extensión) y por nombre de ROM normalizado (archivos y gamelist).
    private sealed class FolderIndex
    {
        public Dictionary<string, string> Stems { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, FolderIndex> folderIndexes = new(StringComparer.OrdinalIgnoreCase);

    private FolderIndex GetFolderIndex(string folder)
    {
        if (folderIndexes.TryGetValue(folder, out var cached)) return cached;
        var index = new FolderIndex();
        string dir = Path.Combine(root, "Roms", folder);
        try
        {
            if (Directory.Exists(dir))
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (GamelistGenerator.IsNotRom(file)) continue;
                    index.Stems.TryAdd(Path.GetFileNameWithoutExtension(file), file);
                    index.Names.TryAdd(PathRules.Name(file), file);
                }
            string list = Path.Combine(root, "ES-DE", "gamelists", folder, "gamelist.xml");
            if (File.Exists(list))
                foreach (var entry in GamelistParser.Parse(list, CancellationToken.None))
                    index.Names.TryAdd(PathRules.Name(entry.Name + ".x"), Path.Combine(dir, entry.RelativeKey.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch { /* si no se puede leer la carpeta, simplemente no se detectan duplicadas en ella */ }
        folderIndexes[folder] = index;
        return index;
    }

    private (string Path, string Kind)? FindExisting(RomIdentity identity, string folder)
    {
        var index = GetFolderIndex(folder);
        if (index.Stems.TryGetValue(Path.GetFileNameWithoutExtension(identity.Path), out var byFile)) return (byFile, "Nombre de fichero");
        foreach (var name in new[] { PathRules.Name(identity.Game + ".x"), PathRules.Name(identity.Path) })
            if (name.Length > 0 && index.Names.TryGetValue(name, out var byName)) return (byName, "Nombre de ROM");
        return null;
    }

    // Si la ROM ya existe en la carpeta elegida, pasa a la lista de duplicadas y se quita de la suya. Devuelve true si es duplicada.
    private bool TryDuplicate(DataGridViewRow row, string folder)
    {
        if (row.Cells["state"].Value as string == "Duplicada" || row.Tag is not RomIdentity identity) return row.Cells["state"].Value as string == "Duplicada";
        var existing = FindExisting(identity, folder);
        if (existing is null) return false;
        row.Cells["state"].Value = "Duplicada";
        string relative = Path.GetRelativePath(Path.Combine(root, "Roms", folder), existing.Value.Path);
        dupGrid.Rows.Add(Path.GetFileName(identity.Path), identity.Game, folder, relative, existing.Value.Kind);
        items.Remove(identity);
        var owner = row.DataGridView;
        Ui(() =>
        {
            try { if (row.DataGridView == owner && owner is not null) owner.Rows.Remove(row); } catch (InvalidOperationException) { }
            UpdateAddButton(); UpdateDoubtLabel(); UpdateDupUi();
        });
        UpdateDupUi();
        return true;
    }

    private void UpdateDupUi()
    {
        dupCount.Text = dupGrid.Rows.Count == 1 ? "1 duplicada" : $"{dupGrid.Rows.Count} duplicadas";
        dupButton.ForeColor = dupGrid.Rows.Count > 0 ? Color.Firebrick : SystemColors.ControlText;
        dupLabel.Text = $"Ya existen en la colección ({dupGrid.Rows.Count}), no se añaden: se comparan por nombre de fichero y por nombre de ROM";
    }

    private void ToggleDuplicates()
    {
        dupVisible = !dupVisible;
        middle.RowStyles[4].Height = dupVisible ? 24 : 0;
        middle.RowStyles[5].Height = dupVisible ? 130 : 0;
        dupLabel.Visible = dupGrid.Visible = dupVisible;
    }

    public bool CollectionChanged { get; private set; }
    // El usuario ha aceptado scrapear las ROMs que acaba de añadir: la ventana principal abre directamente el scrapeo con ellas.
    public bool ScrapeRequested { get; private set; }
    // Rutas completas de los archivos que se han creado en Roms durante esta sesión (para proponer scrapear solo esas ROMs).
    public List<string> AddedPaths { get; } = [];

    public AddRomsDialog(string root, TraceLog trace)
    {
        this.root = root;
        this.trace = trace;
        string romRoot = Path.Combine(root, "Roms");
        existingFolders = Directory.Exists(romRoot)
            ? Directory.EnumerateDirectories(romRoot).Select(d => Path.GetFileName(d)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            : [];

        Text = "Añadir ROMs";
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(960, 680); MinimumSize = new Size(800, 520);
        BackColor = RaUi.Body;
        AllowDrop = true;
        CancelButton = closeButton;

        grid = MakeGrid();
        doubtGrid = MakeGrid();
        doubtLabel.Font = Font;
        identifiedLabel.Dock = DockStyle.Fill; identifiedLabel.TextAlign = ContentAlignment.BottomLeft;
        doubtLabel.Text = "Sin decidir: elige la carpeta de destino y pasarán a la lista de arriba (doble clic para quitarlas)";

        // Tercera lista, oculta por defecto: las ROMs que ya existen en la colección (no se añaden). Se muestra con el botón de «prohibido».
        dupGrid = new StaticGrid { Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
        dupGrid.ApplyInfoStyle(18, 20);
        RaUi.StyleGrid(dupGrid, 22, 22, selectionVisible: false);
        const DataGridViewAutoSizeColumnMode fill = DataGridViewAutoSizeColumnMode.Fill;
        dupGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Archivo nuevo", AutoSizeMode = fill, FillWeight = 28, MinimumWidth = 80, SortMode = DataGridViewColumnSortMode.NotSortable });
        dupGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ROM", AutoSizeMode = fill, FillWeight = 24, MinimumWidth = 80, SortMode = DataGridViewColumnSortMode.NotSortable });
        dupGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Carpeta", AutoSizeMode = fill, FillWeight = 10, MinimumWidth = 70, SortMode = DataGridViewColumnSortMode.NotSortable });
        dupGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Ya existe como", AutoSizeMode = fill, FillWeight = 28, MinimumWidth = 80, SortMode = DataGridViewColumnSortMode.NotSortable });
        dupGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Coincide por", AutoSizeMode = fill, FillWeight = 14, MinimumWidth = 80, SortMode = DataGridViewColumnSortMode.NotSortable });
        dupGrid.Columns[2].DefaultCellStyle.Padding = new Padding(28, 0, 6, 0);
        dupGrid.CellPainting += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 2) { e.Paint(e.ClipBounds, DataGridViewPaintParts.All); if (e.Value is string { Length: > 0 } f) e.Graphics!.DrawImage(PixelSystemIcons.Get(f, 18), new Rectangle(e.CellBounds.X + 5, e.CellBounds.Y + (e.CellBounds.Height - 18) / 2, 18, 18)); e.Handled = true; } };
        dupLabel.Dock = DockStyle.Fill; dupLabel.TextAlign = ContentAlignment.BottomLeft; dupLabel.Visible = false; dupGrid.Visible = false;
        dupLabel.Text = "Ya existen en la colección (no se añaden): se comparan por nombre de fichero y por nombre de ROM";
        foreach (var caption in new[] { identifiedLabel, doubtLabel, dupLabel })
        {
            caption.Font = new Font(Font, FontStyle.Bold); caption.ForeColor = RaUi.Dark; caption.Padding = new Padding(4, 0, 0, 3);
        }

        middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, BackColor = RaUi.Body, Padding = new Padding(10, 6, 10, 4) };
        middle.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        middle.RowStyles.Add(new RowStyle(SizeType.Percent, 72));
        middle.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        middle.RowStyles.Add(new RowStyle(SizeType.Percent, 28));
        middle.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        middle.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        middle.Controls.Add(identifiedLabel, 0, 0); middle.Controls.Add(Framed(grid), 0, 1);
        middle.Controls.Add(doubtLabel, 0, 2); middle.Controls.Add(Framed(doubtGrid), 0, 3);
        middle.Controls.Add(dupLabel, 0, 4); middle.Controls.Add(Framed(dupGrid), 0, 5);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 134, ColumnCount = 1, RowCount = 4, BackColor = RaUi.FooterBack, Padding = new Padding(10, 0, 10, 0) };
        bottom.Paint += (_, e) => { using var pen = new Pen(RaUi.Border); e.Graphics.DrawLine(pen, 0, 0, bottom.Width, 0); };
        RaUi.StyleButton(addButton, true, Color.FromArgb(84, 208, 112)); // el mismo verde que el «+» del icono
        RaUi.StyleButton(curateButton, true);                               // naranja (el dorado de la marca)
        foreach (var plain in new[] { browseButton, removeButton, clearButton, closeButton }) RaUi.StyleButton(plain, false);
        RaUi.StyleButton(dupButton, false);
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Cómo se guardan las ROMs: se elige en Opciones › Cómo guardar las ROMs… (se recuerda); aquí solo se muestra el resumen.
        var storage = StorageSettings.Load();
        storageOptions = new StorageOptions(storage.Format, storage.Level);
        storageSummary.Dock = DockStyle.Fill; storageSummary.Padding = new Padding(6, 0, 0, 0);
        UpdateStorageSummary();
        status.Dock = DockStyle.Fill;
        progress.Dock = DockStyle.Fill; progress.Margin = new Padding(6, 2, 6, 2); progress.Minimum = 0; progress.Maximum = 1000; progress.Visible = false;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6, 4, 6, 4) };
        buttons.Controls.Add(closeButton); buttons.Controls.Add(addButton); buttons.Controls.Add(curateButton);
        buttons.Controls.Add(clearButton); buttons.Controls.Add(removeButton); buttons.Controls.Add(browseButton);
        // Botón de «prohibido el paso» con el número de duplicadas al lado: muestra u oculta la lista de las que ya existen.
        buttons.Controls.Add(dupCount); buttons.Controls.Add(dupButton); buttons.Controls.Add(moveFiles);
        Loc.SetToolTip(new ToolTip(), dupButton, "Mostrar u ocultar las ROMs que ya existen en la colección");
        dupButton.Click += (_, _) => ToggleDuplicates();
        bottom.Controls.Add(status, 0, 0); bottom.Controls.Add(progress, 0, 1); bottom.Controls.Add(storageSummary, 0, 2); bottom.Controls.Add(buttons, 0, 3);

        // Cabecera oscura como en el formulario de ROMs seleccionadas, con un cartucho en pixel art.
        var banner = new RaHeader("Añadir ROMs", "Arrastra las ROMs o elígelas con «Examinar…»: se identifican y se guardan en la carpeta de su sistema.\r\nDe las carpetas se recorren como máximo 2 niveles de subcarpetas", 80, 48, logo: false);
        // El chip de la aplicación con un «+»: queda quieto y solo late mientras se están añadiendo las ROMs.
        chipFrames = RaUi.ChipPlusFrames();
        chipBox = banner.Icon;
        chipBox.Image = chipFrames[0];
        chipTimer = new System.Windows.Forms.Timer { Interval = 75 };
        chipTimer.Tick += (_, _) => { chipFrame = (chipFrame + 1) % chipFrames.Length; chipBox.Image = chipFrames[chipFrame]; };
        chipBox.Disposed += (_, _) => chipTimer.Dispose();
        Controls.Add(middle); Controls.Add(bottom); Controls.Add(banner); Controls.Add(new CpuUsageStrip()); // barra de CPU debajo del todo, como en el resto de formularios

        // Menú superior: las bases de datos de ROMs se gestionan desde aquí.
        var menuBar = new ClickThroughMenuStrip();
        var optionsMenu = new ToolStripMenuItem("Opciones");
        optionsMenu.DropDownItems.Add("Cómo guardar las ROMs…", null, (_, _) => ChooseStorage());
        optionsMenu.DropDownItems.Add("Bases de datos de ROMs…", null, (_, _) => OpenDatManager());
        menuBar.Items.Add(optionsMenu);
        MainMenuStrip = menuBar;
        Controls.Add(menuBar);

        foreach (var control in new Control[] { this, grid, doubtGrid, middle })
        {
            control.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
            control.DragDrop += async (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) await HandleDropAsync(paths); };
        }
        // Examinar: archivos sueltos o una carpeta entera, con el mismo análisis que al arrastrar.
        var browseMenu = new ContextMenuStrip { ShowImageMargin = false };
        browseMenu.Items.Add("Archivos…", null, async (_, _) =>
        {
            using var open = new OpenFileDialog { Title = Loc.T("Elige las ROMs a añadir"), Multiselect = true, Filter = Loc.T("Todos los archivos|*.*") };
            if (open.ShowDialog(this) == DialogResult.OK) await HandleDropAsync(open.FileNames);
        });
        browseMenu.Items.Add("Carpeta…", null, async (_, _) =>
        {
            using var folder = new FolderBrowserDialog { Description = Loc.T("Elige la carpeta con las ROMs (se recorren hasta 2 niveles de subcarpetas)"), ShowNewFolderButton = false };
            if (folder.ShowDialog(this) == DialogResult.OK) await HandleDropAsync([folder.SelectedPath]);
        });
        browseButton.Click += (_, _) => { if (!working) browseMenu.Show(browseButton, new Point(0, browseButton.Height)); };
        removeButton.Click += (_, _) => RemoveSelected();
        clearButton.Click += (_, _) => ClearLists();
        addButton.Click += async (_, _) => await AddAsync();
        curateButton.Click += async (_, _) => await CurateAndAddAsync();
        FormClosing += (_, _) => cts.Cancel();
        CleanLeftovers();
        FormClosed += (_, _) => CleanTemp();

        // Elegir carpeta en la lista de dudas: la ROM pasa a la lista de identificadas.
        doubtGrid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != doubtGrid.Columns["folder"]!.Index) return;
            if (doubtGrid.Rows[e.RowIndex].Cells["folder"].Value is not string { Length: > 0 }) return;
            Ui(() => MoveToDecided(doubtGrid.Rows[e.RowIndex]));
        };
        grid.CellValueChanged += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == grid.Columns["folder"]!.Index) UpdateRowState(grid.Rows[e.RowIndex]); };
        UpdateDoubtLabel();

        if (RomDatabase.InstalledCount() > 0) Shown += async (_, _) => await PrepareIndexAsync("", announceOk: true);
        else status.Text = "No hay bases de datos descargadas: sin ellas solo se identifica por extensión. Descárgalas en Opciones › Bases de datos de ROMs.";
    }

    // Marco fino de color alrededor de una tabla.
    private static Panel Framed(Control content)
    {
        var frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = RaUi.Border, Margin = new Padding(0, 0, 0, 4) };
        content.Dock = DockStyle.Fill;
        frame.Controls.Add(content);
        return frame;
    }

    // Icono del sistema de destino dentro de la celda de su carpeta (la columna deja sitio a la izquierda con su relleno).
    private static void PaintFolderIcon(DataGridView host, string columnName, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || host.Columns[columnName] is not { } column || e.ColumnIndex != column.Index) return;
        e.Paint(e.ClipBounds, DataGridViewPaintParts.All);
        if (e.Value is string { Length: > 0 } folder)
            e.Graphics!.DrawImage(PixelSystemIcons.Get(folder, 18), new Rectangle(e.CellBounds.X + 5, e.CellBounds.Y + (e.CellBounds.Height - 18) / 2, 18, 18));
        e.Handled = true;
    }

    // Tabla compacta, sin foco ni selección visibles; el doble clic (salvo en el desplegable de la carpeta) quita la fila.
    private DataGridView MakeGrid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, AllowUserToResizeColumns = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true, BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle, EditMode = DataGridViewEditMode.EditOnEnter, EnableHeadersVisualStyles = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, ColumnHeadersHeight = 22
        };
        RaUi.StyleGrid(g, 22, 22);
        // Las columnas se reparten el ancho de la ventana según su peso (se ven bien también maximizada) y se pueden ajustar a mano.
        g.AllowUserToResizeColumns = true;
        const DataGridViewAutoSizeColumnMode fill = DataGridViewAutoSizeColumnMode.Fill;
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "file", HeaderText = "Archivo", AutoSizeMode = fill, FillWeight = 30, MinimumWidth = 90, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "game", HeaderText = "Juego", AutoSizeMode = fill, FillWeight = 26, MinimumWidth = 90, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "method", HeaderText = "Identificación", AutoSizeMode = fill, FillWeight = 22, MinimumWidth = 90, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        g.Columns.Add(new DataGridViewComboBoxColumn { Name = "folder", HeaderText = "Destino", AutoSizeMode = fill, FillWeight = 12, MinimumWidth = 90, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox, SortMode = DataGridViewColumnSortMode.NotSortable });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "state", HeaderText = "Estado", AutoSizeMode = fill, FillWeight = 10, MinimumWidth = 90, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        // Las dos tablas mantienen las mismas proporciones: al ajustar una columna en una, se copia el ancho a la otra.
        g.ColumnWidthChanged += (_, e) =>
        {
            var other = ReferenceEquals(g, grid) ? doubtGrid : grid;
            if (syncingColumns || other is null || other.Columns.Count != g.Columns.Count) return;
            syncingColumns = true;
            try { other.Columns[e.Column.Index].Width = e.Column.Width; }
            finally { syncingColumns = false; }
        };

        g.Columns["folder"]!.DefaultCellStyle.Padding = new Padding(28, 0, 6, 0);
        g.CellPainting += (_, e) => PaintFolderIcon(g, "folder", e);
        g.CurrentCellDirtyStateChanged += (_, _) => { if (g.IsCurrentCellDirty) g.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        g.DataError += (_, e) => e.ThrowException = false;
        g.SelectionChanged += (_, _) => UpdateRemoveButton();
        // Doble clic (salvo en el desplegable de la carpeta): quita la selección si la fila pulsada forma parte de ella, o solo esa fila.
        g.CellDoubleClick += (_, e) =>
        {
            if (working || e.RowIndex < 0 || e.ColumnIndex == g.Columns["folder"]!.Index) return;
            var row = g.Rows[e.RowIndex];
            RemoveRows(g, row.Selected ? g.SelectedRows.Cast<DataGridViewRow>().ToList() : [row]);
        };
        // Menú contextual: actúa solo sobre las filas seleccionadas de esta lista. El botón derecho selecciona la fila pulsada si no lo estaba.
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        var removeItem = new ToolStripMenuItem("Quitar de la lista");
        var assignItem = new ToolStripMenuItem("Asignar a carpeta");
        var reidentifyItem = new ToolStripMenuItem("Re-identificar");
        menu.Items.Add(assignItem); menu.Items.Add(reidentifyItem); menu.Items.Add(removeItem);
        g.ContextMenuStrip = menu;
        ((ToolStripDropDownMenu)assignItem.DropDown).ShowImageMargin = false;
        g.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0 || working) return;
            if (!g.Rows[e.RowIndex].Selected) { g.ClearSelection(); g.Rows[e.RowIndex].Selected = true; }
        };
        removeItem.Click += (_, _) => RemoveRows(g, g.SelectedRows.Cast<DataGridViewRow>().ToList());
        reidentifyItem.Click += (_, _) => Reidentify(g.SelectedRows.Cast<DataGridViewRow>().ToList());
        menu.Opening += (_, e) =>
        {
            var selected = g.SelectedRows.Cast<DataGridViewRow>().ToList();
            if (working || selected.Count == 0) { e.Cancel = true; return; }
            removeItem.Text = selected.Count == 1 ? "Quitar de la lista" : $"Quitar {selected.Count} de la lista";
            assignItem.Text = selected.Count == 1 ? "Asignar a carpeta" : $"Asignar {selected.Count} a carpeta";
            assignItem.DropDownItems.Clear();
            // Todas las carpetas, por bloques: las candidatas de las ROMs seleccionadas, las que ya existen en la colección y el resto de sistemas de ES-DE.
            var candidates = selected.Select(r => r.Tag).OfType<RomIdentity>().SelectMany(i => i.Candidates).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var existing = existingFolders.Where(f => !candidates.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
            var rest = EsDeStructure.Systems.Where(s => !candidates.Contains(s, StringComparer.OrdinalIgnoreCase) && !existing.Contains(s, StringComparer.OrdinalIgnoreCase))
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            void AddBlock(IEnumerable<string> folders, bool bold)
            {
                var list = folders.ToList();
                if (list.Count == 0) return;
                if (assignItem.DropDownItems.Count > 0) assignItem.DropDownItems.Add(new ToolStripSeparator());
                foreach (var folder in list)
                {
                    var item = new ToolStripMenuItem(folder) { Font = bold ? new Font(menu.Font, FontStyle.Bold) : menu.Font };
                    item.Click += (_, _) => AssignFolder(g, selected, folder);
                    assignItem.DropDownItems.Add(item);
                }
            }
            AddBlock(candidates, bold: true);
            AddBlock(existing, bold: false);
            AddBlock(rest, bold: false);
            if (assignItem.DropDownItems.Count > 0) assignItem.DropDownItems.Add(new ToolStripSeparator());
            assignItem.DropDownItems.Add("Buscar carpeta…", null, (_, _) =>
            {
                using var picker = new FolderPickDialog(existingFolders, EsDeStructure.Systems, selected.Count);
                if (picker.ShowDialog(this) == DialogResult.OK && picker.Selected is { } chosen) AssignFolder(g, selected, chosen);
            });
        };
        // Supr quita las filas seleccionadas (si no se está eligiendo carpeta en un desplegable).
        g.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Delete || working || g.IsCurrentCellInEditMode) return;
            RemoveRows(g, g.SelectedRows.Cast<DataGridViewRow>().ToList());
            e.Handled = true;
        };
        return g;
    }

    // Quita filas de una lista (y de los registros internos). Se hace fuera del evento en curso: una fila en edición dispararía eventos reentrantes.
    private void RemoveRows(DataGridView g, List<DataGridViewRow> rows)
    {
        if (rows.Count == 0) return;
        foreach (var row in rows)
            if (row.Tag is RomIdentity identity)
            {
                items.Remove(identity);
                known.Remove(Path.GetFullPath(identity.Path));
                // Una ROM extraída de un pack ya no hace falta: se borra su copia temporal.
                if (identity.Origin is not null && PathRules.Inside(identity.Path, tempRoot))
                    try { File.Delete(identity.Path); } catch { }
                // Si era la última ROM de un pack, el pack se puede volver a arrastrar.
                if (identity.Origin is not null && packCounts.TryGetValue(identity.Origin, out int left))
                {
                    if (left <= 1) { packCounts.Remove(identity.Origin); known.Remove(identity.Origin); }
                    else packCounts[identity.Origin] = left - 1;
                }
            }
        var indexes = rows.Select(r => r.Index).Where(i => i >= 0).OrderByDescending(i => i).ToList();
        Ui(() =>
        {
            try { g.CancelEdit(); } catch (InvalidOperationException) { }
            foreach (int i in indexes) if (i < g.Rows.Count) g.Rows.RemoveAt(i);
            UpdateAddButton();
            UpdateDoubtLabel();
            UpdateRemoveButton();
            status.Text = $"{items.Count} archivo(s) en la lista.";
        });
    }

    // Vuelve a identificar las ROMs seleccionadas de cualquiera de las dos listas (p. ej. tras instalar o importar un DAT) y las recoloca según el nuevo resultado.
    private async void Reidentify(List<DataGridViewRow> rows)
    {
        var olds = rows.Select(r => r.Tag).OfType<RomIdentity>().ToList();
        if (olds.Count == 0 || working) return;
        working = true; UpdateAddButton(); UpdateRemoveButton();
        progress.Style = ProgressBarStyle.Marquee; progress.Visible = true;
        status.Text = $"Re-identificando {olds.Count} archivo(s)…";
        try
        {
            if (!RomDatabase.IndexReady && RomDatabase.InstalledCount() > 0) await PrepareIndexAsync("");
            progress.Style = ProgressBarStyle.Marquee; progress.Visible = true;
            var results = new RomIdentity?[olds.Count];
            int degree = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
            await Parallel.ForEachAsync(Enumerable.Range(0, olds.Count), new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cts.Token },
                async (i, ct) => results[i] = await RomIdentifier.IdentifyAsync(olds[i].Path, ct));
            if (IsDisposed) return;
            int resolved = 0;
            for (int i = 0; i < olds.Count; i++)
            {
                var old = olds[i]; var fresh = results[i]!;
                var row = doubtGrid.Rows.Cast<DataGridViewRow>().Concat(grid.Rows.Cast<DataGridViewRow>()).FirstOrDefault(r => ReferenceEquals(r.Tag, old));
                if (row is null) continue;
                fresh.Origin = old.Origin;
                int at = items.IndexOf(old);
                if (at >= 0) items[at] = fresh; else items.Add(fresh);
                var owner = row.DataGridView!;
                try { owner.CancelEdit(); } catch (InvalidOperationException) { }
                owner.Rows.Remove(row);
                AddRow(fresh);
                if (grid.Rows.Cast<DataGridViewRow>().Any(r => ReferenceEquals(r.Tag, fresh))) resolved++;
            }
            UpdateDoubtLabel();
            status.Text = $"Re-identificadas {olds.Count}: {resolved} identificadas, {olds.Count - resolved} sin decidir.";
        }
        catch (OperationCanceledException) { }
        finally { working = false; if (!IsDisposed) { progress.Visible = false; progress.Style = ProgressBarStyle.Blocks; UpdateAddButton(); UpdateRemoveButton(); } }
    }

    // Asigna la carpeta de destino a las filas indicadas (de una sola lista). En la lista de dudas, cada fila pasa a la de identificadas.
    private void AssignFolder(DataGridView g, List<DataGridViewRow> rows, string folder)
    {
        foreach (var row in rows.ToList())
        {
            if (row.DataGridView != g) continue;
            var combo = (DataGridViewComboBoxCell)row.Cells["folder"];
            if (!combo.Items.Contains(folder)) combo.Items.Add(folder);
            combo.Value = folder;
        }
        UpdateAddButton();
        UpdateRemoveButton();
    }

    private void UpdateRemoveButton() =>
        removeButton.Enabled = !working && (grid.SelectedRows.Count > 0 || doubtGrid.SelectedRows.Count > 0);

    private void RemoveSelected()
    {
        RemoveRows(grid, grid.SelectedRows.Cast<DataGridViewRow>().ToList());
        RemoveRows(doubtGrid, doubtGrid.SelectedRows.Cast<DataGridViewRow>().ToList());
    }

    private void ClearLists()
    {
        if (working || (grid.Rows.Count == 0 && doubtGrid.Rows.Count == 0)) return;
        if (!QuietMessage.Confirm(this, "Vaciar listas", "¿Vaciar las dos listas? No se borra ni se copia ningún archivo.", "Vaciar")) return;
        grid.Rows.Clear(); doubtGrid.Rows.Clear(); dupGrid.Rows.Clear(); UpdateDupUi();
        items.Clear(); known.Clear(); packCounts.Clear();
        CleanTemp();
        UpdateAddButton(); UpdateDoubtLabel(); UpdateRemoveButton();
        status.Text = "Listas vacías. Arrastra más ROMs cuando quieras.";
    }

    // Títulos de las dos listas con su número de elementos.
    private void UpdateDoubtLabel()
    {
        identifiedLabel.Text = $"Identificadas ({grid.Rows.Count}): selecciona filas y pulsa Supr, doble clic o «Quitar seleccionadas»";
        doubtLabel.Text = doubtGrid.Rows.Count == 0
            ? "Sin decidir (0)"
            : $"Sin decidir ({doubtGrid.Rows.Count}): elige la carpeta de destino y pasarán a la lista de arriba";
    }

    private async void OpenDatManager()
    {
        using (var dialog = new DatManagerDialog(trace, root))
            dialog.ShowDialog(this);
        if (RomDatabase.InstalledCount() > 0) await PrepareIndexAsync("", announceOk: true);
    }

    // Carga (o reconstruye) el índice de hashes mostrando su avance en la barra de progreso y en el texto de estado.
    private async Task PrepareIndexAsync(string prefix, bool announceOk = false)
    {
        bool wasVisible = progress.Visible;
        progress.Style = ProgressBarStyle.Blocks; progress.Value = 0; progress.Visible = true;
        try
        {
            var loading = RomDatabase.GetIndexAsync();
            while (!loading.IsCompleted)
            {
                progress.Value = Math.Clamp((int)(RomDatabase.IndexProgress * 1000), 0, 1000);
                status.Text = $"{prefix}Base de datos de hashes: {RomDatabase.IndexPhase}… ({RomDatabase.IndexProgress:P0})";
                await Task.WhenAny(loading, Task.Delay(100));
            }
            await loading;
            if (announceOk) status.Text = $"{prefix}Base de datos de hashes OK!";
        }
        catch { status.Text = "No se pudo cargar la base de datos de hashes: se identificará solo por extensión."; }
        finally { progress.Visible = wasVisible; }
    }

    // Al elegir o arrastrar una carpeta se recorren como mucho dos niveles de subcarpetas.
    private const int SubfolderLevels = 2;
    private static int MaxDepth() => SubfolderLevels;

    // Archivos de una carpeta y de sus subcarpetas hasta el nivel indicado; las carpetas sin acceso se saltan.
    private static IEnumerable<string> EnumerateFiles(string directory, int depth)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(directory).ToList(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { files = []; }
        foreach (var file in files) yield return file;
        if (depth <= 0) yield break;
        IEnumerable<string> subdirectories;
        try { subdirectories = Directory.EnumerateDirectories(directory).ToList(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { yield break; }
        foreach (var sub in subdirectories)
            foreach (var file in EnumerateFiles(sub, depth - 1)) yield return file;
    }

    // Recorre las rutas arrastradas o elegidas y devuelve los archivos candidatos (sin lo que no es ROM ni las pistas de un .cue, que van con él).
    private static (List<string> Files, int NotRoms) CollectFiles(string[] paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) files.AddRange(EnumerateFiles(path, MaxDepth()));
            else if (File.Exists(path)) files.Add(path);
        }
        // Las pistas de un .cue se añaden junto a él: se descartan como archivos sueltos.
        var tracks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cue in files.Where(f => f.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)))
            try
            {
                string dir = Path.GetDirectoryName(cue)!;
                foreach (var line in File.ReadLines(cue))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line.Trim(), "^FILE\\s+\"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success) tracks.Add(Path.GetFullPath(Path.Combine(dir, m.Groups[1].Value)));
                }
            }
            catch { }
        // Lo que no es una ROM (xml, imágenes, vídeos, partidas guardadas…) se descarta sin más, no se lista.
        int notRoms = files.Count(GamelistGenerator.IsNotRom);
        return (files.Where(f => !GamelistGenerator.IsNotRom(f) && !tracks.Contains(Path.GetFullPath(f))).ToList(), notRoms);
    }

    private async Task HandleDropAsync(string[] paths)
    {
        if (working) return;
        if (RomDatabase.InstalledCount() == 0 &&
            QuietMessage.Confirm(this, "Bases de datos", "Todavía no hay bases de datos descargadas, y sin ellas solo se puede identificar por extensión (los .zip, .7z o .bin no se reconocerían).\r\n\r\n¿Quieres descargarlas ahora?", "Abrir descargas"))
            OpenDatManager();

        // Feedback inmediato: recorrer carpetas grandes puede tardar, y se hace fuera del hilo de la interfaz.
        working = true; addButton.Enabled = false; UpdateRemoveButton();
        progress.Style = ProgressBarStyle.Marquee; progress.Visible = true;
        status.Text = "Paso 1 de 4: Buscando archivos…";
        try
        {
            List<string> files; int notRoms;
            try { (files, notRoms) = await Task.Run(() => CollectFiles(paths), cts.Token); }
            catch (OperationCanceledException) { return; }
            files = files.Where(f => known.Add(Path.GetFullPath(f))).ToList();
            if (files.Count == 0)
            {
                status.Text = notRoms > 0 ? $"Se han ignorado {notRoms} archivo(s) que no son ROMs." : "No hay archivos nuevos que añadir.";
                return;
            }
            lastIgnored = notRoms;

            // Packs (comprimidos con otros comprimidos dentro): se extraen recursivamente hasta las ROMs finales, que son las que se identifican.
            var work = new List<(string File, string? Origin)>();

            // Paso 2: cargar las bases de datos en memoria (solo tarda la primera vez o tras cambiarlas).
            if (RomDatabase.IndexReady) status.Text = "Paso 2 de 4: Base de datos de hashes OK!";
            else await PrepareIndexAsync("Paso 2 de 4: ");

            // Los pasos 3 y 4 trabajan en paralelo (varios archivos a la vez). Un temporizador de la interfaz refleja el avance sin saturarla.
            int degree = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
            var token = cts.Token;
            string progressText = ""; int progressValue = 0; bool marquee = false;
            Func<int>? flushRows = null;
            using var tick = new System.Windows.Forms.Timer { Interval = 120 };
            tick.Tick += (_, _) =>
            {
                if (IsDisposed) { tick.Stop(); return; }
                status.Text = progressText;
                if (marquee) progress.Style = ProgressBarStyle.Marquee;
                else { progress.Style = ProgressBarStyle.Blocks; progress.Value = Math.Clamp(progressValue, 0, 1000); }
                flushRows?.Invoke();
            };
            tick.Start();
            try
            {
                // Paso 3: examinar (extraer packs).
                var expanded = new List<string>?[files.Count];
                int examinedDone = 0;
                marquee = files.Count == 1; // con un solo archivo (posible pack grande) no hay porcentaje que dar
                progressText = $"Paso 3 de 4: Examinando 0/{files.Count}…";
                await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = token }, (i, ct) =>
                {
                    expanded[i] = ArchiveExpander.Expand(files[i], tempRoot, ct);
                    int d = Interlocked.Increment(ref examinedDone);
                    progressText = $"Paso 3 de 4: Examinando {d}/{files.Count} ({d * 100 / files.Count} %): {Path.GetFileName(files[i])}";
                    progressValue = d * 1000 / files.Count;
                    return ValueTask.CompletedTask;
                });
                for (int i = 0; i < files.Count; i++)
                {
                    var parts = expanded[i]!;
                    if (parts.Count == 1 && parts[0] == files[i]) { work.Add((files[i], null)); continue; }
                    string origin = Path.GetFullPath(files[i]);
                    packCounts[origin] = packCounts.GetValueOrDefault(origin) + parts.Count;
                    foreach (var part in parts) work.Add((part, origin));
                }

                // Paso 4: identificar. Las filas se añaden en el orden original según terminan.
                marquee = false;
                var identities = new RomIdentity?[work.Count];
                int identified = 0, nextRow = 0;
                flushRows = () =>
                {
                    int added = 0;
                    while (nextRow < work.Count && Volatile.Read(ref identities[nextRow]) is { } id && added < 150)
                    {
                        id.Origin = work[nextRow].Origin;
                        items.Add(id);
                        AddRow(id);
                        nextRow++; added++;
                    }
                    return added;
                };
                progressText = $"Paso 4 de 4: Identificando 0/{work.Count}…"; progressValue = 0;
                await Parallel.ForEachAsync(Enumerable.Range(0, work.Count), new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = token }, async (i, ct) =>
                {
                    var identity = await RomIdentifier.IdentifyAsync(work[i].File, ct);
                    Volatile.Write(ref identities[i], identity);
                    int d = Interlocked.Increment(ref identified);
                    progressText = $"Paso 4 de 4: Identificando {d}/{work.Count} ({d * 100 / work.Count} %): {Path.GetFileName(work[i].File)}";
                    progressValue = d * 1000 / work.Count;
                });
                tick.Stop();
                while (flushRows() > 0) { } // las filas que falten por añadir
            }
            catch (OperationCanceledException) { return; }
            finally { tick.Stop(); }
            int doubts = doubtGrid.Rows.Count;
            long itemBytes = 0;
            foreach (var item in items) { try { itemBytes += new FileInfo(item.Path).Length; } catch { } }
            status.Text = $"{items.Count} archivo(s) · {ScreenScraperDialog.FormatBytes(itemBytes)} (tamaño original)." + (lastIgnored > 0 ? $" {lastIgnored} ignorado(s) por no ser ROMs." : "") + (doubts > 0 ? $" {doubts} sin decidir: elige su carpeta." : "");
        }
        finally { working = false; if (!IsDisposed) { progress.Visible = false; progress.Style = ProgressBarStyle.Blocks; UpdateAddButton(); } }
    }

    // Carpeta que se propone para la ROM, o null si hay dudas.
    private string? ProposeFolder(RomIdentity identity, out string note)
    {
        note = "";
        var existing = identity.Candidates.Where(c => existingFolders.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        bool hash = identity.Method.StartsWith("Hash", StringComparison.Ordinal) || identity.Method.StartsWith("Nombre", StringComparison.Ordinal);
        // Extensiones genéricas (contenedores o imágenes de disco): sirven para decenas de sistemas, no se deduce nada de ellas.
        string[] generic = [".zip", ".7z", ".rar", ".bin", ".iso", ".img", ".chd", ".cue", ".gdi", ".m3u", ".cso", ".rom", ".dsk"];
        bool isGeneric = generic.Contains(identity.InnerExtension ?? Path.GetExtension(identity.Path).ToLowerInvariant());
        if (identity.Candidates.Count == 1) return identity.Candidates[0];
        if (hash)
            // Siempre la carpeta genérica del sistema (la primera de la lista: megadrive, sega32x, snes…), exista ya o se vaya a crear.
            return existing.Count > 0 ? existing[0] : identity.Candidates.FirstOrDefault();
        if (!isGeneric && existing.Count == 1)
        {
            note = " · única carpeta existente";
            return existing[0];
        }
        return null;
    }

    private void AddRow(RomIdentity identity)
    {
        if (IsDisposed) return; // el formulario se cerró mientras se identificaba: sus tablas ya no existen
        AddRowCore(identity);
    }

    private void AddRowCore(RomIdentity identity)
    {
        string? folder = ProposeFolder(identity, out string note);
        var target = folder is null ? doubtGrid : grid;
        // Opciones del desplegable: primero las candidatas, luego las carpetas que ya existen y por último el resto de sistemas de ES-DE.
        var options = identity.Candidates.Concat(existingFolders).Concat(EsDeStructure.Systems).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int index = target.Rows.Add();
        var row = target.Rows[index];
        var combo = (DataGridViewComboBoxCell)row.Cells["folder"];
        combo.Items.AddRange(options.Cast<object>().ToArray());
        row.Cells["file"].Value = Path.GetFileName(identity.Path);
        row.Cells["file"].ToolTipText = identity.Origin is null ? identity.Path : $"{identity.Path}\r\n(de {identity.Origin})";
        row.Cells["game"].Value = identity.Game;
        row.Cells["method"].Value = identity.Method + note;
        row.Tag = identity;
        if (folder is not null) combo.Value = folder;
        UpdateRowState(row);
        UpdateDoubtLabel();
    }

    // Una ROM de la lista de dudas con carpeta ya elegida pasa a la de identificadas.
    private void MoveToDecided(DataGridViewRow doubtRow)
    {
        if (doubtRow.DataGridView != doubtGrid || doubtRow.Tag is not RomIdentity identity) return;
        string folder = (string)doubtRow.Cells["folder"].Value!;
        var options = ((DataGridViewComboBoxCell)doubtRow.Cells["folder"]).Items.Cast<object>().ToArray();
        string method = (string)doubtRow.Cells["method"].Value! + " · carpeta elegida";
        doubtGrid.Rows.Remove(doubtRow);
        int index = grid.Rows.Add();
        var row = grid.Rows[index];
        var combo = (DataGridViewComboBoxCell)row.Cells["folder"];
        combo.Items.AddRange(options);
        row.Cells["file"].Value = Path.GetFileName(identity.Path);
        row.Cells["file"].ToolTipText = identity.Path;
        row.Cells["game"].Value = identity.Game;
        row.Cells["method"].Value = method;
        row.Tag = identity;
        combo.Value = folder;
        UpdateRowState(row);
        UpdateDoubtLabel();
    }

    private void UpdateRowState(DataGridViewRow row)
    {
        var identity = (RomIdentity)row.Tag!;
        string? folder = row.Cells["folder"].Value as string;
        if (string.IsNullOrEmpty(folder))
        {
            row.Cells["state"].Value = identity.Candidates.Count > 1 ? "Elige la carpeta" : "No identificada: elige la carpeta";
            row.DefaultCellStyle.BackColor = Color.FromArgb(255, 244, 205);
        }
        else
        {
            // Si ya existe en la colección, no se añade: pasa a la lista de duplicadas.
            if (TryDuplicate(row, folder)) return;
            bool created = !existingFolders.Contains(folder, StringComparer.OrdinalIgnoreCase);
            // Formato con el que se guardará (según lo que admita el sistema) y nombre que tendrá el archivo de destino.
            var (format, note) = RomStorage.Plan(identity, CurrentStorage, SystemFormats.AllowedExtensions(root, folder));
            string stem = Path.GetFileNameWithoutExtension(identity.Path);
            string targetName = format switch { StorageFormat.Zip => stem + ".zip", StorageFormat.SevenZip => stem + ".7z", _ => Path.GetFileName(identity.Path) };
            string destination = Path.Combine(root, "Roms", folder, targetName);
            string how = " · " + RomStorage.Describe(format, CurrentStorage.Level) + (note.Length > 0 ? $" ({note})" : "");
            row.Cells["state"].Value = File.Exists(destination) ? "Ya existe en la carpeta" : (created ? "Lista (se creará la carpeta)" : "Lista") + how;
            row.DefaultCellStyle.BackColor = Color.Empty; // sin color propio: vale el fondo alterno de la tabla
        }
        UpdateAddButton();
    }

    private void UpdateAddButton()
    {
        addButton.Enabled = !working && grid.Rows.Cast<DataGridViewRow>().Any(r => r.Cells["state"].Value is string s && s.StartsWith("Lista", StringComparison.Ordinal));
        curateButton.Enabled = addButton.Enabled;
    }

    // «Curar y añadir»: abre el formulario de curado solo con las ROMs identificadas (las que están listas para añadir). Descartar candidatas las quita de la
    // lista de añadir (no se borra ningún archivo) y «Añadir a la colección» cierra el curado y añade las que queden.
    private readonly Dictionary<string, string> temporaryGenres = new(StringComparer.OrdinalIgnoreCase); // categoría temporal de cada ROM (ruta de origen → categoría)

    private async Task CurateAndAddAsync()
    {
        var ready = grid.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is RomIdentity && r.Cells["state"].Value is string s && s.StartsWith("Lista", StringComparison.Ordinal)).ToList();
        if (ready.Count == 0) { QuietMessage.Show(this, "Curar y añadir", "No hay ROMs identificadas listas para curar."); return; }
        // La comprobación de requisitos (cuenta, conexión) puede tardar un par de segundos: mientras tanto el botón lo indica para que se vea que se ha pulsado.
        string curateText = curateButton.Text;
        curateButton.Enabled = false; curateButton.Text = "Comprobando…"; UseWaitCursor = true; status.Text = "Preparando el curado…";
        bool requirementsMet;
        try { requirementsMet = await Requirements.CheckAsync(this, trace, "Curar y añadir", Need.ScreenScraper | Need.Internet); }
        finally { UseWaitCursor = false; curateButton.Text = curateText; UpdateAddButton(); }
        if (!requirementsMet) return;
        // Las ROMs aún no tienen gamelist, así que su categoría se consulta antes a ScreenScraper (solo en memoria, sin escribir nada) para poder curarlas.
        var request = ready.Select(r => new TemporaryCategorizer.Item(((RomIdentity)r.Tag!).Path, (string)r.Cells["folder"].Value!, Path.GetFileName(((RomIdentity)r.Tag!).Path))).ToList();
        if (!await TemporaryCategorizer.RunAsync(this, trace, request, temporaryGenres)) return;
        // Cada ROM identificada se presenta al curado como una ROM más de su sistema de destino, con su categoría temporal.
        var pseudoRows = ready.Select(r =>
        {
            var identity = (RomIdentity)r.Tag!;
            long size = 0;
            try { size = new FileInfo(identity.Path).Length; } catch { }
            return new RomRow((string)r.Cells["folder"].Value!, identity.Path, Path.GetFileName(identity.Path), size, identity.Game, temporaryGenres.GetValueOrDefault(identity.Path, ""), "", RomStatus.Ok, "", "");
        }).ToList();
        trace.Add("curate_and_add_opened", $"roms={pseudoRows.Count}");
        bool addNow;
        using (var curation = new CurationDialog(pseudoRows, trace, addMode: true))
        {
            curation.DeleteHandler = (rows, owner, _) =>
            {
                if (!QuietMessage.Confirm(owner, "Descartar candidatas", $"Se van a descartar {rows.Length:N0} ROMs de la lista de añadir. No se borra ningún archivo: solo dejan de añadirse.", "Descartar"))
                    return Task.FromResult<HashSet<string>?>(null);
                var paths = rows.Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var discard = grid.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is RomIdentity id && paths.Contains(id.Path)).ToList();
                RemoveRows(grid, discard);
                trace.Add("curate_and_add_discarded", $"roms={paths.Count}");
                return Task.FromResult<HashSet<string>?>(paths);
            };
            addNow = curation.ShowDialog(this) == DialogResult.OK;
        }
        UpdateAddButton(); UpdateDoubtLabel();
        if (addNow) await AddAsync();
    }

    private Bitmap[]? chipFrames;
    private PictureBox? chipBox;
    private System.Windows.Forms.Timer? chipTimer;
    private int chipFrame;

    private void SetBeating(bool on)
    {
        if (chipTimer is null || chipBox is null || chipFrames is null || IsDisposed) return;
        chipFrame = 0; chipBox.Image = chipFrames[0];
        if (on) chipTimer.Start(); else chipTimer.Stop();
    }

    private async Task AddAsync()
    {
        var ready = grid.Rows.Cast<DataGridViewRow>().Where(r => r.Cells["state"].Value is string s && s.StartsWith("Lista", StringComparison.Ordinal)).ToList();
        if (ready.Count == 0) return;
        bool move = moveFiles.Checked;
        working = true; addButton.Enabled = false; curateButton.Enabled = false; UpdateRemoveButton();
        SetBeating(true);
        int added = 0, failed = 0, done = 0;
        progress.Style = ProgressBarStyle.Blocks; progress.Value = 0; progress.Visible = true;

        // Los datos de cada fila se leen aquí, en el hilo de la interfaz; el trabajo pesado (copiar, extraer, comprimir) va en paralelo.
        var jobs = ready.Select(r => (Row: r, Identity: (RomIdentity)r.Tag!, Folder: (string)r.Cells["folder"].Value!)).ToList();
        var options = CurrentStorage;
        int cores = Environment.ProcessorCount;
        // Copiar depende del disco (pocas a la vez); zip usa un núcleo por ROM; 7z ya usa 2 hilos por proceso.
        int degree = options.Format switch { StorageFormat.Keep or StorageFormat.Raw => 3, StorageFormat.Zip => Math.Clamp(cores / 2, 2, 8), _ => Math.Clamp(cores / 3, 2, 4) };
        var addedLock = new object();
        string progressText = ""; int progressValue = 0;
        using var tick = new System.Windows.Forms.Timer { Interval = 120 };
        tick.Tick += (_, _) => { if (IsDisposed) { tick.Stop(); return; } status.Text = progressText; progress.Value = Math.Clamp(progressValue, 0, 1000); };
        tick.Start();
        string verb = move ? "Moviendo" : "Copiando";
        progressText = $"Paso 1 de 1: {verb} 0/{jobs.Count}…";
        try
        {
            await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cts.Token }, async (job, ct) =>
            {
                var (row, identity, folder) = job;
                Ui(() => row.Cells["state"].Value = "Copiando…");
                try
                {
                    await Task.Run(() =>
                    {
                        // Se deja el systeminfo.txt del sistema si falta y se decide el formato real según lo que admita (su lista de extensiones).
                        SystemFormats.EnsureSystemInfo(root, folder);
                        var (format, _) = RomStorage.Plan(identity, options, SystemFormats.AllowedExtensions(root, folder));
                        var created = RomStorage.Store(identity, Path.Combine(root, "Roms", folder), format, options.Level, move, ct);
                        lock (addedLock) AddedPaths.AddRange(created.Select(Path.GetFullPath));
                        Interlocked.Increment(ref added);
                        trace.Add("rom_added", $"system={folder}; file={Path.GetFileName(identity.Path)}; method={identity.Method}; move={move}");
                        Ui(() =>
                        {
                            folderIndexes.Remove(folder); // la carpeta ha cambiado: se vuelve a indexar la próxima vez
                            row.Cells["state"].Value = (move ? "Movida" : "Copiada") + " · " + RomStorage.Describe(format, options.Level);
                            row.DefaultCellStyle.BackColor = Color.FromArgb(226, 244, 231);
                            row.Cells["folder"].ReadOnly = true;
                        });
                    }, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    Ui(() => row.Cells["state"].Value = "Error: " + ex.Message);
                }
                finally
                {
                    int d = Interlocked.Increment(ref done);
                    progressText = $"Paso 1 de 1: {verb} {d}/{jobs.Count} ({d * 100 / jobs.Count} %): {Path.GetFileName(identity.Path)}";
                    progressValue = d * 1000 / jobs.Count;
                }
            });
        }
        catch (OperationCanceledException) { }
        finally { tick.Stop(); working = false; SetBeating(false); if (!IsDisposed) { progress.Visible = false; UpdateAddButton(); UpdateRemoveButton(); } }
        if (added > 0) CollectionChanged = true; // aunque se haya cerrado el formulario, las ROMs ya añadidas deben pasar a los gamelists
        if (IsDisposed) return;
        status.Text = $"{added} ROM(s) añadidas a la colección" + (failed > 0 ? $", {failed} con error" : "") + ". Al cerrar se actualizarán los gamelists.";

        // Nada más terminar de añadir: se pregunta por scrapear justo esas ROMs y, si se acepta, se cierra el formulario y se abre el scrapeo.
        if (added > 0)
        {
            int systems = AddedPaths.Select(p => Path.GetFileName(Path.GetDirectoryName(p)) ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (RaConfirmDialog.Ask(this, "Scrapear las ROMs añadidas", $"Se han añadido {added:N0} ROM(s) de {systems:N0} sistema(s).",
                "¿Quieres categorizarlas y descargar sus medios con ScreenScraper?\r\nSe abrirá la descarga masiva solo con esas ROMs.", RaUi.MagnifierIcon(), "Scrapear"))
            {
                ScrapeRequested = true;
                Close();
            }
        }
    }
}
