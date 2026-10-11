namespace ESDERomCleaner;

// Añadir ROMs a la colección: se arrastran archivos al formulario, se identifican (hash o nombre con los DAT, o por extensión)
// y se copian a la carpeta de su sistema en Roms. Las que no se pueden decidir solas van a una lista aparte.
internal sealed class AddRomsDialog : Form
{
    private readonly string root;
    private readonly TraceLog trace;
    private readonly Label dropZone = new();
    private readonly DataGridView grid;      // identificadas: listas para añadir
    private readonly DataGridView doubtGrid; // con dudas: hay que elegir la carpeta
    private readonly Label doubtLabel = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft, Font = null };
    private readonly ProgressBar progress = new();
    private readonly ComboBox formatBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly ComboBox levelBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly Label storageHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(14, 6, 0, 0) };
    private StorageOptions CurrentStorage => new((StorageFormat)Math.Max(0, formatBox.SelectedIndex), (StorageLevel)Math.Max(0, levelBox.SelectedIndex));
    private readonly Label identifiedLabel = new();
    private readonly Label status = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    private readonly CheckBox moveFiles = new() { Text = "Mover los archivos en lugar de copiarlos", AutoSize = true };
    private readonly Button addButton = new() { Text = "Añadir a la colección", AutoSize = true, MinimumSize = new Size(150, 28), Enabled = false };
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

    // Carpeta que agrupa los temporales de todas las sesiones del formulario (los de sesiones anteriores que no se limpiaron, por un cierre brusco).
    private static string ExpandRoot => Path.Combine(AppPaths.Temp, "expand");

    private static void DeleteFolder(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* en uso o sin permiso: se reintentará la próxima vez */ }
    }

    // Borra lo extraído de los packs: los temporales de esta sesión y, al abrir, los que dejara una sesión anterior.
    private void CleanTemp() => DeleteFolder(tempRoot);
    private static void CleanLeftovers() => DeleteFolder(ExpandRoot);

    public bool CollectionChanged { get; private set; }

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
        ClientSize = new Size(900, 620); MinimumSize = new Size(760, 480);
        AllowDrop = true;
        CancelButton = closeButton;

        dropZone.Text = "Arrastra aquí las ROMs (archivos o carpetas)\r\nDe las carpetas se recorren como máximo 2 niveles de subcarpetas";
        dropZone.Dock = DockStyle.Top; dropZone.Height = 56; dropZone.TextAlign = ContentAlignment.MiddleCenter;
        dropZone.BorderStyle = BorderStyle.FixedSingle; dropZone.BackColor = SystemColors.ControlLight; dropZone.AllowDrop = true;
        dropZone.Font = new Font(Font.FontFamily, Font.Size + 1, FontStyle.Bold);

        grid = MakeGrid();
        doubtGrid = MakeGrid();
        doubtLabel.Font = Font;
        identifiedLabel.Dock = DockStyle.Fill; identifiedLabel.TextAlign = ContentAlignment.BottomLeft;
        doubtLabel.Text = "Sin decidir: elige la carpeta de destino y pasarán a la lista de arriba (doble clic para quitarlas)";

        var middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        middle.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        middle.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        middle.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        middle.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        middle.Controls.Add(identifiedLabel, 0, 0); middle.Controls.Add(grid, 0, 1);
        middle.Controls.Add(doubtLabel, 0, 2); middle.Controls.Add(doubtGrid, 0, 3);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 128, ColumnCount = 1, RowCount = 4 };
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Cómo se guardan las ROMs en la colección: formato y nivel de compresión (se recuerdan para la próxima vez).
        var storage = StorageSettings.Load();
        formatBox.Items.AddRange(["Como llegan (sin tocarlas)", "Sin comprimir (extraer)", "ZIP", "7z"]);
        levelBox.Items.AddRange(["Sin compresión (almacenar)", "Rápida", "Normal", "Máxima"]);
        formatBox.SelectedIndex = (int)storage.Format; levelBox.SelectedIndex = (int)storage.Level;
        levelBox.Enabled = storage.Format is StorageFormat.Zip or StorageFormat.SevenZip;
        var storagePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(6, 2, 6, 0), WrapContents = false };
        storagePanel.Controls.Add(new Label { Text = "Guardar las ROMs:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        storagePanel.Controls.Add(formatBox);
        storagePanel.Controls.Add(new Label { Text = "Compresión:", AutoSize = true, Margin = new Padding(14, 6, 4, 0) });
        storagePanel.Controls.Add(levelBox);
        storagePanel.Controls.Add(storageHint);
        void StorageChanged()
        {
            levelBox.Enabled = CurrentStorage.Format is StorageFormat.Zip or StorageFormat.SevenZip;
            if (CurrentStorage.Format == StorageFormat.SevenZip && RomStorage.SevenZipPath() is null)
                storageHint.Text = "No se encuentra 7za.exe junto a la aplicación: se guardará en zip.";
            else storageHint.Text = "";
            new StorageSettings { Format = CurrentStorage.Format, Level = CurrentStorage.Level }.Save();
            foreach (var row in grid.Rows.Cast<DataGridViewRow>().ToList()) UpdateRowState(row);
        }
        formatBox.SelectedIndexChanged += (_, _) => StorageChanged();
        levelBox.SelectedIndexChanged += (_, _) => StorageChanged();
        status.Dock = DockStyle.Fill;
        progress.Dock = DockStyle.Fill; progress.Margin = new Padding(6, 2, 6, 2); progress.Minimum = 0; progress.Maximum = 1000; progress.Visible = false;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6, 4, 6, 4) };
        buttons.Controls.Add(closeButton); buttons.Controls.Add(addButton);
        buttons.Controls.Add(clearButton); buttons.Controls.Add(removeButton); buttons.Controls.Add(browseButton); buttons.Controls.Add(moveFiles);
        bottom.Controls.Add(status, 0, 0); bottom.Controls.Add(progress, 0, 1); bottom.Controls.Add(storagePanel, 0, 2); bottom.Controls.Add(buttons, 0, 3);

        Controls.Add(middle); Controls.Add(bottom); Controls.Add(dropZone);

        // Menú superior: las bases de datos de ROMs se gestionan desde aquí.
        var menuBar = new MenuStrip();
        var optionsMenu = new ToolStripMenuItem("Opciones");
        optionsMenu.DropDownItems.Add("Bases de datos de ROMs…", null, (_, _) => OpenDatManager());
        menuBar.Items.Add(optionsMenu);
        MainMenuStrip = menuBar;
        Controls.Add(menuBar);

        foreach (var control in new Control[] { this, dropZone, grid, doubtGrid, middle })
        {
            control.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
            control.DragDrop += async (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) await HandleDropAsync(paths); };
        }
        // Examinar: archivos sueltos o una carpeta entera, con el mismo análisis que al arrastrar.
        var browseMenu = new ContextMenuStrip();
        browseMenu.Items.Add("Archivos…", null, async (_, _) =>
        {
            using var open = new OpenFileDialog { Title = "Elige las ROMs a añadir", Multiselect = true, Filter = "Todos los archivos|*.*" };
            if (open.ShowDialog(this) == DialogResult.OK) await HandleDropAsync(open.FileNames);
        });
        browseMenu.Items.Add("Carpeta…", null, async (_, _) =>
        {
            using var folder = new FolderBrowserDialog { Description = "Elige la carpeta con las ROMs (se recorren hasta 2 niveles de subcarpetas)", ShowNewFolderButton = false };
            if (folder.ShowDialog(this) == DialogResult.OK) await HandleDropAsync([folder.SelectedPath]);
        });
        browseButton.Click += (_, _) => { if (!working) browseMenu.Show(browseButton, new Point(0, browseButton.Height)); };
        removeButton.Click += (_, _) => RemoveSelected();
        clearButton.Click += (_, _) => ClearLists();
        addButton.Click += async (_, _) => await AddAsync();
        FormClosing += (_, _) => cts.Cancel();
        CleanLeftovers();
        FormClosed += (_, _) => CleanTemp();

        // Elegir carpeta en la lista de dudas: la ROM pasa a la lista de identificadas.
        doubtGrid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != doubtGrid.Columns["folder"]!.Index) return;
            if (doubtGrid.Rows[e.RowIndex].Cells["folder"].Value is not string { Length: > 0 }) return;
            BeginInvoke(() => MoveToDecided(doubtGrid.Rows[e.RowIndex]));
        };
        grid.CellValueChanged += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == grid.Columns["folder"]!.Index) UpdateRowState(grid.Rows[e.RowIndex]); };
        UpdateDoubtLabel();

        if (RomDatabase.InstalledCount() > 0) Shown += async (_, _) => await PrepareIndexAsync("", announceOk: true);
        else status.Text = "No hay bases de datos descargadas: sin ellas solo se identifica por extensión. Descárgalas en Opciones › Bases de datos de ROMs.";
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
        g.RowTemplate.Height = 20;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = g.ColumnHeadersDefaultCellStyle.BackColor = SystemColors.Control;
        g.ColumnHeadersDefaultCellStyle.SelectionForeColor = g.ColumnHeadersDefaultCellStyle.ForeColor = SystemColors.ControlText;
        // Las columnas se reparten el ancho de la ventana según su peso (se ven bien también maximizada) y se pueden ajustar a mano.
        g.AllowUserToResizeColumns = true;
        const DataGridViewAutoSizeColumnMode fill = DataGridViewAutoSizeColumnMode.Fill;
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "file", HeaderText = "Archivo", AutoSizeMode = fill, FillWeight = 30, MinimumWidth = 90, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "game", HeaderText = "Juego", AutoSizeMode = fill, FillWeight = 26, MinimumWidth = 90, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "method", HeaderText = "Identificación", AutoSizeMode = fill, FillWeight = 22, MinimumWidth = 90, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        g.Columns.Add(new DataGridViewComboBoxColumn { Name = "folder", HeaderText = "Carpeta de destino", AutoSizeMode = fill, FillWeight = 12, MinimumWidth = 90, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox, SortMode = DataGridViewColumnSortMode.NotSortable });
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
        var menu = new ContextMenuStrip();
        var removeItem = new ToolStripMenuItem("Quitar de la lista");
        var assignItem = new ToolStripMenuItem("Asignar a carpeta");
        menu.Items.Add(assignItem); menu.Items.Add(removeItem);
        g.ContextMenuStrip = menu;
        g.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0 || working) return;
            if (!g.Rows[e.RowIndex].Selected) { g.ClearSelection(); g.Rows[e.RowIndex].Selected = true; }
        };
        removeItem.Click += (_, _) => RemoveRows(g, g.SelectedRows.Cast<DataGridViewRow>().ToList());
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
        BeginInvoke(() =>
        {
            try { g.CancelEdit(); } catch (InvalidOperationException) { }
            foreach (int i in indexes) if (i < g.Rows.Count) g.Rows.RemoveAt(i);
            UpdateAddButton();
            UpdateDoubtLabel();
            UpdateRemoveButton();
            status.Text = $"{items.Count} archivo(s) en la lista.";
        });
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
        grid.Rows.Clear(); doubtGrid.Rows.Clear();
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
            int examined = 0;

            // Paso 2: cargar las bases de datos en memoria (solo tarda la primera vez o tras cambiarlas).
            if (RomDatabase.IndexReady) status.Text = "Paso 2 de 4: Base de datos de hashes OK!";
            else await PrepareIndexAsync("Paso 2 de 4: ");

            foreach (var file in files)
            {
                examined++;
                status.Text = $"Paso 3 de 4: Examinando {examined}/{files.Count} ({(examined - 1) * 100 / files.Count} %): {Path.GetFileName(file)}…";
                // Con un solo archivo no hay porcentaje que dar (puede ser un pack grande): barra en movimiento.
                if (files.Count == 1) progress.Style = ProgressBarStyle.Marquee;
                else { progress.Style = ProgressBarStyle.Blocks; progress.Value = (examined - 1) * 1000 / files.Count; }
                List<string> parts;
                try { parts = await Task.Run(() => ArchiveExpander.Expand(file, tempRoot, cts.Token), cts.Token); }
                catch (OperationCanceledException) { return; }
                if (parts.Count == 1 && parts[0] == file) { work.Add((file, null)); continue; }
                string origin = Path.GetFullPath(file);
                packCounts[origin] = packCounts.GetValueOrDefault(origin) + parts.Count;
                foreach (var part in parts) work.Add((part, origin));
            }

            int n = 0;
            progress.Style = ProgressBarStyle.Blocks;
            foreach (var (file, origin) in work)
            {
                n++;
                status.Text = $"Paso 4 de 4: Identificando {n}/{work.Count} ({(n - 1) * 100 / work.Count} %): {Path.GetFileName(file)}…";
                progress.Value = (n - 1) * 1000 / work.Count;
                RomIdentity identity;
                try { identity = await RomIdentifier.IdentifyAsync(file, cts.Token); }
                catch (OperationCanceledException) { return; }
                identity.Origin = origin;
                items.Add(identity);
                AddRow(identity);
                Application.DoEvents();
            }
            int doubts = doubtGrid.Rows.Count;
            status.Text = $"{items.Count} archivo(s)." + (lastIgnored > 0 ? $" {lastIgnored} ignorado(s) por no ser ROMs." : "") + (doubts > 0 ? $" {doubts} sin decidir: elige su carpeta." : " Pulsa «Añadir a la colección».");
        }
        finally { working = false; progress.Visible = false; progress.Style = ProgressBarStyle.Blocks; UpdateAddButton(); }
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
            row.DefaultCellStyle.BackColor = Color.LightGoldenrodYellow;
        }
        else
        {
            bool created = !existingFolders.Contains(folder, StringComparer.OrdinalIgnoreCase);
            // Formato con el que se guardará (según lo que admita el sistema) y nombre que tendrá el archivo de destino.
            var (format, note) = RomStorage.Plan(identity, CurrentStorage, SystemFormats.AllowedExtensions(root, folder));
            string stem = Path.GetFileNameWithoutExtension(identity.Path);
            string targetName = format switch { StorageFormat.Zip => stem + ".zip", StorageFormat.SevenZip => stem + ".7z", _ => Path.GetFileName(identity.Path) };
            string destination = Path.Combine(root, "Roms", folder, targetName);
            string how = " · " + RomStorage.Describe(format, CurrentStorage.Level) + (note.Length > 0 ? $" ({note})" : "");
            row.Cells["state"].Value = File.Exists(destination) ? "Ya existe en la carpeta" : (created ? "Lista (se creará la carpeta)" : "Lista") + how;
            row.DefaultCellStyle.BackColor = SystemColors.Window;
        }
        UpdateAddButton();
    }

    private void UpdateAddButton()
    {
        addButton.Enabled = !working && grid.Rows.Cast<DataGridViewRow>().Any(r => r.Cells["state"].Value is string s && s.StartsWith("Lista", StringComparison.Ordinal));
    }

    private async Task AddAsync()
    {
        var ready = grid.Rows.Cast<DataGridViewRow>().Where(r => r.Cells["state"].Value is string s && s.StartsWith("Lista", StringComparison.Ordinal)).ToList();
        if (ready.Count == 0) return;
        bool move = moveFiles.Checked;
        working = true; addButton.Enabled = false;
        int added = 0, failed = 0, done = 0;
        progress.Style = ProgressBarStyle.Blocks; progress.Value = 0; progress.Visible = true;
        try
        {
            foreach (var row in ready)
            {
                var identity = (RomIdentity)row.Tag!;
                string folder = (string)row.Cells["folder"].Value!;
                string destinationDir = Path.Combine(root, "Roms", folder);
                row.Cells["state"].Value = "Copiando…";
                status.Text = $"Paso 1 de 1: {(move ? "Moviendo" : "Copiando")} {++done}/{ready.Count} ({(done - 1) * 100 / ready.Count} %): {Path.GetFileName(identity.Path)}…";
                progress.Value = (done - 1) * 1000 / ready.Count;
                try
                {
                    // Se deja el systeminfo.txt del sistema si falta y se decide el formato real según lo que admita (su lista de extensiones).
                    SystemFormats.EnsureSystemInfo(root, folder);
                    var (format, _) = RomStorage.Plan(identity, CurrentStorage, SystemFormats.AllowedExtensions(root, folder));
                    var level = CurrentStorage.Level;
                    await Task.Run(() => RomStorage.Store(identity, destinationDir, format, level, move, cts.Token));
                    row.Cells["state"].Value = (move ? "Movida" : "Copiada") + " · " + RomStorage.Describe(format, level);
                    row.DefaultCellStyle.BackColor = Color.Honeydew;
                    row.Cells["folder"].ReadOnly = true;
                    added++;
                    trace.Add("rom_added", $"system={folder}; file={Path.GetFileName(identity.Path)}; method={identity.Method}; move={move}");
                }
                catch (Exception ex) { row.Cells["state"].Value = "Error: " + ex.Message; failed++; }
            }
        }
        finally { working = false; progress.Visible = false; UpdateAddButton(); }
        if (added > 0) CollectionChanged = true;
        status.Text = $"{added} ROM(s) añadidas a la colección" + (failed > 0 ? $", {failed} con error" : "") + ". Al cerrar se actualizarán los gamelists.";
    }
}
