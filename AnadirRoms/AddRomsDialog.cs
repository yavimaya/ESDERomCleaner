namespace ESDERomCleaner;

// Añadir ROMs a la colección: se arrastran archivos al formulario, se identifican (hash o nombre con los DAT, o por extensión)
// y se copian a la carpeta de su sistema en Roms. Las que no se pueden decidir solas van a una lista aparte.
internal sealed class AddRomsDialog : LocalizedForm
{
    private readonly string root;
    private readonly TraceLog trace;
    private readonly DataGridView grid;      // identificadas: listas para añadir
    private readonly DataGridView doubtGrid; // con dudas: hay que elegir la carpeta
    private readonly ProgressBar progress = new();
    // Estado de la base de hashes (a la derecha del resumen de DAT): vacío hasta que se carga.
    // Línea de las bases de datos: «bases en uso · estado de la base de hashes», en una sola etiqueta centrada.
    private string datText = "", dbStatusText = "";
    private readonly Label datSummary = new() { Tag = "noloc", AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.GrayText };
    private readonly Label storageSummary = new() { Tag = "noloc", AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.GrayText };
    private StorageOptions storageOptions = new(StorageFormat.Keep, StorageLevel.Normal);
    private StorageOptions CurrentStorage => storageOptions;

    // Resumen de lo elegido en Opciones › Cómo guardar las ROMs…
    private void UpdateStorageSummary()
    {
        string text = storageOptions.Format switch
        {
            StorageFormat.Keep => Loc.T("Se guardarán como llegan, sin tocarlas."),
            StorageFormat.Raw => Loc.T("Se guardarán sin comprimir (se extrae lo comprimido)."),
            StorageFormat.Zip => Loc.T("Se guardarán en ZIP, compresión {0}.", Loc.T(RomStorage.LevelName(storageOptions.Level))),
            _ => Loc.T("Se guardarán en 7z, compresión {0}.", Loc.T(RomStorage.LevelName(storageOptions.Level)))
        };
        if (storageOptions.Format == StorageFormat.SevenZip && RomStorage.SevenZipPath() is null) text += Loc.T(" (No se encuentra 7za.exe: se usará zip.)");
        storageSummary.Text = text + " " + Loc.T("Si el sistema no admite el formato, se usa el más parecido. Cámbialo en Opciones.");
    }

    // Cuántas bases de datos (DAT) se usan para identificar: el número y, si solo hay una, cuál es.
    private void UpdateDatSummary()
    {
        var used = RomDatabase.Allowed(DatSettings.Load()).ToList();
        datText = used.Count switch
        {
            0 => Loc.T("Bases de datos para identificar: ninguna activa (se identifica solo por extensión)."),
            1 => Loc.T("Bases de datos para identificar: 1 ({0}).", used[0].Label),
            _ => Loc.T("Bases de datos para identificar: {0}.", used.Count)
        };
        RefreshDbLine();
    }

    private void SetDbStatus(string text) { dbStatusText = text; RefreshDbLine(); }

    private void RefreshDbLine() => datSummary.Text = dbStatusText.Length == 0 ? datText : datText + "  ·  " + dbStatusText;

    private void ChooseStorage()
    {
        using var dialog = new StorageOptionsDialog(storageOptions);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        storageOptions = dialog.Result;
        var saved = StorageSettings.Load(); saved.Format = storageOptions.Format; saved.Level = storageOptions.Level; saved.Save();
        UpdateStorageSummary();
        foreach (var row in grid.Rows.Cast<DataGridViewRow>().ToList()) UpdateRowState(row);
    }
    private readonly Label status = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
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
    private string runName = ""; // operación en curso, para el aviso de «Detener»
    // Pausa real: mientras se pregunta si cancelar, los trabajadores no empiezan ningún elemento nuevo (los que están a medias terminan el suyo).
    private readonly ManualResetEventSlim pauseGate = new(true);

    private bool AskCancelPaused(string operation)
    {
        pauseGate.Reset();
        try { return CancelConfirm.Ask(this, operation); }
        finally { pauseGate.Set(); }
    }
    private CancellationTokenSource? runCts; // cancelación del paso de identificación en curso (botón «Detener»)
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

    private readonly DataGridView dupGrid;
    // Datos de cada fila de duplicadas y de clones, para poder compararlas y añadirlas a las identificadas.
    private sealed record DupInfo(RomIdentity Identity, string ExistingPath, string Folder);
    private sealed record CloneInfo(RomIdentity Identity, string Folder, bool Omitted);
    private readonly HashSet<RomIdentity> forced = []; // ROMs que el usuario ha pasado a mano a identificadas: no se vuelven a descartar como duplicada o clon
    // Mientras el filtro de clones está activo: mapa clon → padre, archivos de esta tanda y lo que ya estaba en la lista de identificadas (de tandas anteriores).
    private (Dictionary<string, string> Clones, Dictionary<string, string> Batch, Dictionary<string, string> Listed)? cloneCtx;
    private int cloneSkipped, cloneKept;
    // Clones que se han conservado porque su padre no estaba: al terminar cada tanda se vuelven a revisar por si el padre ha llegado después.
    private readonly List<(RomIdentity Identity, string Folder, string Parent, DataGridViewRow CloneRow)> keptClones = [];
    private readonly DataGridView cloneGrid; // clones de arcade detectados: quién es su padre y en qué situación está
    // Botón «?»: muestra u oculta la lista de ROMs desconocidas (sin decidir). Visible por defecto.
    private TableLayoutPanel middle = null!;
    // Una sola zona de tabla con tres pestañas (identificadas, desconocidas y duplicadas): solo se ve una lista a la vez.
    private readonly TabBar tabs = new();
    private readonly Label hintLabel = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText, Padding = new Padding(4, 0, 0, 0) };
    private Panel[] tabFrames = [];

    // Índice de lo que ya hay en la carpeta de un sistema: por nombre de fichero (sin extensión) y por nombre de ROM normalizado (archivos y gamelist).
    private sealed class FolderIndex
    {
        public Dictionary<string, string> Stems { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, FolderIndex> folderIndexes = new(StringComparer.OrdinalIgnoreCase);

    private FolderIndex GetFolderIndex(string folder)
    {
        lock (folderIndexes) return GetFolderIndexCore(folder);
    }

    private FolderIndex GetFolderIndexCore(string folder)
    {
        if (folderIndexes.TryGetValue(folder, out var cached)) return cached;
        var index = new FolderIndex();
        string dir = Path.Combine(root, "Roms", folder);
        try
        {
            // Solo cuentan los archivos con una extensión válida del sistema: así los de apoyo (.hi, .nv, .cfg, guardados…) no dan duplicadas falsas.
            var allowed = SystemFormats.AllowedExtensions(root, folder);
            if (Directory.Exists(dir))
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (GamelistGenerator.IsNotRom(file)) continue;
                    if (allowed is not null && !allowed.Contains(Path.GetExtension(file))) continue;
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

    // Nombre (sin extensión) con el que se guardará una ROM de arcade si la opción de renombrar está activa: el del juego en el DAT con el que se identificó.
    // Solo se usa si parece un nombre corto de set (sin espacios ni caracteres no válidos) y difiere del archivo original; si no, se conserva el original.
    private string? DatStem(RomIdentity identity, string folder)
    {
        if (!RomIdentifier.ArcadeFolders.Contains(folder) || !StorageSettings.Load().RenameArcadeToDat || ArcadeSets.DatOf(identity.Method) is null) return null;
        // Con una coincidencia parcial se usa el nombre del juego con más ROMs coincidentes (la carpeta ya la ha elegido el usuario a mano).
        string game = (identity.PartialMatch ? identity.PartialGame ?? "" : identity.Game).Trim();
        if (game.Length == 0 || game.Contains(' ') || game.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return game.Equals(Path.GetFileNameWithoutExtension(identity.Path), StringComparison.Ordinal) ? null : game;
    }

    private (string Path, string Kind)? FindExisting(RomIdentity identity, string folder)
    {
        var index = GetFolderIndex(folder);
        if (DatStem(identity, folder) is { } renamed && index.Stems.TryGetValue(renamed, out var byRenamed)) return (byRenamed, "Nombre de fichero");
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
        int dupIndex = dupGrid.Rows.Add(Path.GetFileName(identity.Path), identity.Game, folder, relative, existing.Value.Kind, "Comprobando…");
        dupGrid.Rows[dupIndex].Tag = new DupInfo(identity, existing.Value.Path, folder);
        _ = CompareContentAsync(identity.Path, existing.Value.Path, dupGrid.Rows[dupIndex]);
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

    private readonly SemaphoreSlim contentGate = new(3); // pocas lecturas de contenido a la vez (la comprobación es en segundo plano)

    // Compara el contenido de la ROM nueva con el del archivo que ya existe, aunque se llamen igual: entre versiones de un emulador (mame2003, mame2010…) el
    // mismo nombre puede llevar ROMs distintas. Solo informa en la columna «Contenido»: la duplicada no se añade en ningún caso.
    private async Task CompareContentAsync(string newPath, string existingPath, DataGridViewRow row)
    {
        string result;
        try
        {
            await contentGate.WaitAsync(cts.Token);
            try
            {
                result = await Task.Run(() =>
                {
                    var a = RomIdentifier.ContentKeys(newPath, cts.Token);
                    var b = RomIdentifier.ContentKeys(existingPath, cts.Token);
                    if (a is null || b is null || a.Count == 0 || b.Count == 0) return "No comprobado";
                    if (a.SetEquals(b)) return "Igual";
                    return a.IsSubsetOf(b) || b.IsSubsetOf(a) ? "Parcial (uno incluye al otro)" : "Distinto";
                }, cts.Token);
            }
            finally { contentGate.Release(); }
        }
        catch (OperationCanceledException) { return; }
        catch { result = "No comprobado"; }
        Ui(() =>
        {
            if (row.DataGridView is null) return;
            row.Cells[5].Value = result;
            if (result == "Distinto") { row.Cells[5].Style.ForeColor = Color.FromArgb(190, 90, 0); row.Cells[5].Style.Font = new Font(dupGrid.Font, FontStyle.Bold); }
            else if (result.StartsWith("Parcial", StringComparison.Ordinal)) row.Cells[5].Style.ForeColor = Color.FromArgb(190, 90, 0);
            UpdateDupContentHeader();
            // Opción «Añadir las nuevas cuando el duplicado es distinto» (Distinto o Parcial): la fila se pasa sola a identificadas (cuando no haya un proceso en marcha).
            if ((result == "Distinto" || result.StartsWith("Parcial", StringComparison.Ordinal)) && StorageSettings.Load().AddDifferentDuplicates && row.Tag is DupInfo) { autoAdd.Add(row); FlushAutoAdd(); }
        });
    }

    private readonly List<DataGridViewRow> autoAdd = []; // duplicadas distintas pendientes de pasar a identificadas
    private int autoAdded;

    // Pasa a identificadas las duplicadas con contenido distinto pendientes, en cuanto no haya un proceso en marcha (AddRow no debe mezclarse con el escaneo).
    private void FlushAutoAdd()
    {
        if (working || autoAdd.Count == 0) return;
        var rows = autoAdd.Where(r => r.DataGridView is not null && r.Tag is DupInfo).ToList();
        autoAdd.Clear();
        if (rows.Count == 0) return;
        AddDuplicates(rows, confirm: false, switchTab: false);
        autoAdded += rows.Count;
        status.Text = $"{autoAdded} duplicada(s) con contenido distinto pasadas a identificadas: reemplazarán al existente al añadirlas.";
    }

    // Cabecera «Contenido» con cuántas duplicadas tienen el mismo nombre pero contenido distinto, si las hay.
    private void UpdateDupContentHeader()
    {
        int different = dupGrid.Rows.Cast<DataGridViewRow>().Count(r => r.Cells[5].Value is string v && (v == "Distinto" || v.StartsWith("Parcial", StringComparison.Ordinal)));
        dupGrid.Columns[5].HeaderText = Loc.T("Contenido") + (different > 0 ? $" ({Loc.T("distintos")} {different})" : "");
    }

    private void UpdateDupUi() => UpdateTabs();

    // Texto y color de las pestañas con sus contadores, y la indicación de la lista activa.
    private void UpdateTabs()
    {
        grid.Invalidate(); // el mensaje de lista vacía depende de las filas y del proceso en marcha
        tabs.SetTab(0, "✨ " + Loc.T("Identificadas ({0})", grid.Rows.Count)); // el icono, como el de las otras dos pestañas
        tabs.SetTab(1, "❓ " + Loc.T("Desconocidas ({0})", doubtGrid.Rows.Count), doubtGrid.Rows.Count > 0 ? Color.DarkOrange : null);
        tabs.SetTab(2, "⛔ " + Loc.T("Duplicadas ({0})", dupGrid.Rows.Count), dupGrid.Rows.Count > 0 ? Color.Firebrick : null);
        tabs.SetTab(3, Loc.T("Clones ({0})", cloneGrid.Rows.Count), cloneGrid.Rows.Count > 0 ? Color.SteelBlue : null);
        // Cabecera de «Resultado» con el número de clones conservados (sin padre, que se añaden), si los hay.
        int keptNow = cloneGrid.Rows.Cast<DataGridViewRow>().Count(r => r.Cells[3].Value is string v && v.StartsWith("Conservado", StringComparison.Ordinal));
        cloneGrid.Columns[3].HeaderText = Loc.T("Resultado") + (keptNow > 0 ? $" ({Loc.T("Conservados")} {keptNow})" : "");
        // La indicación solo se muestra con el proceso parado y la lista con elementos: mientras trabaja cambiaría (y parpadearía) con cada fila.
        var rows = tabs.Selected switch { 0 => grid.Rows.Count, 1 => doubtGrid.Rows.Count, 2 => dupGrid.Rows.Count, _ => cloneGrid.Rows.Count };
        hintLabel.Text = working || rows == 0 ? "" : tabs.Selected switch
        {
            0 => "Selecciona filas y pulsa Supr, doble clic o «Quitar seleccionadas»",
            1 => "Elige la carpeta de destino y pasarán a la lista de identificadas (doble clic para quitarlas)",
            2 => "Ya existen en la colección, no se añaden: se comparan por nombre de fichero y por nombre de ROM",
            _ => "Clones de arcade: los omitidos tienen a su padre en el lote o en la colección; los conservados no lo tienen y se añaden"
        };
        // Sin indicación, la fila casi desaparece (un 20 % de su alto): solo ocupa sitio cuando hay algo que decir.
        middle.RowStyles[1].Height = hintLabel.Text.Length == 0 ? 4 : 22;
    }

    private void ShowTab()
    {
        for (int i = 0; i < tabFrames.Length; i++) tabFrames[i].Visible = i == tabs.Selected;
        UpdateTabs();
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

        // Tercera lista: las ROMs que ya existen en la colección (no se añaden).
        dupGrid = MakeInfoGrid();
        const DataGridViewAutoSizeColumnMode fill = DataGridViewAutoSizeColumnMode.Fill;
        dupGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Archivo nuevo", AutoSizeMode = fill, FillWeight = 28, MinimumWidth = 80, SortMode = DataGridViewColumnSortMode.Automatic });
        dupGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ROM", AutoSizeMode = fill, FillWeight = 24, MinimumWidth = 80, SortMode = DataGridViewColumnSortMode.Automatic });
        dupGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Carpeta", AutoSizeMode = fill, FillWeight = 10, MinimumWidth = 70, SortMode = DataGridViewColumnSortMode.Automatic });
        dupGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Ya existe como", AutoSizeMode = fill, FillWeight = 28, MinimumWidth = 80, SortMode = DataGridViewColumnSortMode.Automatic });
        dupGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Coincide por", AutoSizeMode = fill, FillWeight = 12, MinimumWidth = 80, SortMode = DataGridViewColumnSortMode.Automatic });
        dupGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Contenido", AutoSizeMode = fill, FillWeight = 14, MinimumWidth = 90, SortMode = DataGridViewColumnSortMode.Automatic });
        dupGrid.Columns[2].DefaultCellStyle.Padding = new Padding(28, 0, 6, 0);

        // Cuarta lista: los clones de arcade detectados al filtrar (omitidos y conservados) con su padre y la situación del padre.
        cloneGrid = MakeInfoGrid();
        cloneGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Clon", AutoSizeMode = fill, FillWeight = 22, MinimumWidth = 80, SortMode = DataGridViewColumnSortMode.Automatic });
        cloneGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Padre", AutoSizeMode = fill, FillWeight = 20, MinimumWidth = 80, SortMode = DataGridViewColumnSortMode.Automatic });
        cloneGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Situación del padre", AutoSizeMode = fill, FillWeight = 42, MinimumWidth = 120, SortMode = DataGridViewColumnSortMode.Automatic });
        cloneGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Resultado", AutoSizeMode = fill, FillWeight = 16, MinimumWidth = 80, SortMode = DataGridViewColumnSortMode.Automatic });
        dupGrid.CellPainting += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 2) { e.Paint(e.ClipBounds, DataGridViewPaintParts.All); if (e.Value is string { Length: > 0 } f) e.Graphics!.DrawImage(PixelSystemIcons.Get(f, 18), new Rectangle(e.CellBounds.X + 5, e.CellBounds.Y + (e.CellBounds.Height - 18) / 2, 18, 18)); e.Handled = true; } };

        middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = RaUi.Body, Padding = new Padding(10, 6, 10, 4) };
        middle.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        middle.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        middle.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        // Lista vacía: una indicación en el centro de la tabla de identificadas.
        grid.Paint += (_, e) =>
        {
            if (grid.Rows.Count > 0 || working) return;
            var area = new Rectangle(0, grid.ColumnHeadersHeight, grid.ClientSize.Width, Math.Max(0, grid.ClientSize.Height - grid.ColumnHeadersHeight));
            using var big = new Font(Font.FontFamily, 13f, FontStyle.Bold);
            var first = new Rectangle(area.X, area.Y + area.Height / 2 - 34, area.Width, 34);
            var second = new Rectangle(area.X, area.Y + area.Height / 2, area.Width, 26);
            const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(e.Graphics, Loc.T("Arrastra aquí las ROMs o carpetas para añadirlas"), big, first, Color.FromArgb(150, 154, 168), flags);
            TextRenderer.DrawText(e.Graphics, Loc.T("o pulsa «Examinar…»"), Font, second, Color.FromArgb(150, 154, 168), flags);
        };
        tabs.Add("Identificadas"); tabs.Add("Desconocidas"); tabs.Add("Duplicadas"); tabs.Add("Clones");
        tabs.SetIcon(3, RaUi.TwinsIcon());
        tabs.Dock = DockStyle.Fill; tabs.Margin = new Padding(0);
        hintLabel.Margin = new Padding(0);
        var host = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        tabFrames = [Framed(grid), Framed(doubtGrid), Framed(dupGrid), Framed(cloneGrid)];
        foreach (var frame in tabFrames) { frame.Margin = new Padding(0); host.Controls.Add(frame); }
        BuildInfoGridActions();
        tabs.SelectedChanged += ShowTab;
        middle.Controls.Add(tabs, 0, 0); middle.Controls.Add(hintLabel, 0, 1); middle.Controls.Add(host, 0, 2);
        ShowTab();

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 154, ColumnCount = 1, RowCount = 4, BackColor = RaUi.FooterBack, Padding = new Padding(10, 0, 10, 0) };
        bottom.Paint += (_, e) => { using var pen = new Pen(RaUi.Border); e.Graphics.DrawLine(pen, 0, 0, bottom.Width, 0); };
        RaUi.StyleButton(addButton, true, Color.FromArgb(84, 208, 112)); // el mismo verde que el «+» del icono
        RaUi.StyleButton(curateButton, true);                               // naranja (el dorado de la marca)
        foreach (var plain in new[] { browseButton, removeButton, clearButton, closeButton }) RaUi.StyleButton(plain, false);
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // las dos líneas de texto, centradas en vertical entre la barra y los botones
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));

        // Cómo se guardan las ROMs: se elige en Opciones › Cómo guardar las ROMs… (se recuerda); aquí solo se muestra el resumen.
        var storage = StorageSettings.Load();
        storageOptions = new StorageOptions(storage.Format, storage.Level);
        storageSummary.Dock = DockStyle.Fill;
        UpdateDatSummary();
        UpdateStorageSummary();
        status.Dock = DockStyle.Fill;
        progress.Dock = DockStyle.Fill; progress.Margin = new Padding(6, 2, 6, 2); progress.Minimum = 0; progress.Maximum = 1000; progress.Visible = false;
        // Botones centrados en horizontal (y en vertical) en la parte inferior del formulario.
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.None, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0), Margin = new Padding(0) };
        buttons.Controls.Add(browseButton); buttons.Controls.Add(removeButton); buttons.Controls.Add(clearButton);
        buttons.Controls.Add(curateButton); buttons.Controls.Add(addButton); buttons.Controls.Add(closeButton);
        // Línea bajo el resumen de guardado: las bases de datos con las que se identifica y, a continuación, el estado de la base de hashes («OK!»),
        // todo centrado en el formulario. Los «Paso x de y» siguen en la línea de arriba.
        datSummary.Dock = DockStyle.Fill;
        var texts = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0), Padding = new Padding(0) };
        texts.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); texts.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        texts.RowStyles.Add(new RowStyle(SizeType.Absolute, 20)); texts.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        texts.Controls.Add(storageSummary, 0, 1); texts.Controls.Add(datSummary, 0, 2);
        bottom.Controls.Add(status, 0, 0); bottom.Controls.Add(progress, 0, 1); bottom.Controls.Add(texts, 0, 2); bottom.Controls.Add(buttons, 0, 3);

        // Cabecera oscura como en el formulario de ROMs seleccionadas, con un cartucho en pixel art.
        var banner = new RaHeader("Añadir ROMs", "Arrastra las ROMs o elígelas con «Examinar…»: se identifican y se guardan en la carpeta de su sistema.\r\nDe las carpetas se recorren como máximo 2 niveles de subcarpetas", 80, 48, logo: false);
        // El chip de la aplicación con un «+»: queda quieto y solo late mientras se están añadiendo las ROMs.
        chipFrames = RaUi.ChipPlusFrames();
        chipScanFrames = RaUi.ChipScanFrames(); // con la lupa: mientras se analiza
        chipActive = chipFrames;
        chipBox = banner.Icon;
        chipBox.Image = chipFrames[0];
        chipTimer = new System.Windows.Forms.Timer { Interval = 75 };
        chipTimer.Tick += (_, _) => { chipFrame = (chipFrame + 1) % chipActive!.Length; chipBox.Image = chipActive[chipFrame]; };
        chipBox.Disposed += (_, _) => chipTimer.Dispose();
        var cpuStrip = new CpuUsageStrip(withTimer: true);
        var clock = System.Diagnostics.Stopwatch.StartNew(); clock.Reset();
        var clockTimer = new System.Windows.Forms.Timer { Interval = 500 };
        // Tiempo de la operación en curso (identificar o añadir) a la derecha de la barra inferior; al terminar queda el tiempo total.
        string remaining = ""; DateTime remainingAt = DateTime.MinValue;
        static string Span(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        clockTimer.Tick += (_, _) =>
        {
            if (working && !clock.IsRunning) { clock.Restart(); remaining = ""; remainingAt = DateTime.MinValue; }
            else if (!working && clock.IsRunning) { clock.Stop(); remaining = ""; }
            if (!(clock.ElapsedTicks > 0 || clock.IsRunning)) return;
            // Tiempo estimado: lo transcurrido en proporción a lo que falta, una vez hay un mínimo de avance; se recalcula cada pocos segundos para que no baile.
            if (working && progress.Visible && progress.Style == ProgressBarStyle.Blocks && DateTime.Now - remainingAt > TimeSpan.FromSeconds(3))
            {
                double fraction = progress.Value / 1000.0;
                if (fraction >= 0.01 && clock.Elapsed.TotalSeconds >= 5)
                {
                    remaining = " · " + Loc.T("restante ≈ {0}", Span(TimeSpan.FromSeconds(clock.Elapsed.TotalSeconds * (1 - fraction) / fraction)));
                    remainingAt = DateTime.Now;
                }
            }
            cpuStrip.TimeText = Loc.T("Tiempo: {0}", Span(clock.Elapsed)) + (working ? remaining : "");
        };
        clockTimer.Start();
        FormClosed += (_, _) => clockTimer.Dispose();
        Controls.Add(middle); Controls.Add(bottom); Controls.Add(banner); Controls.Add(cpuStrip); // barra de CPU debajo del todo, como en el resto de formularios

        // Menú superior: las bases de datos de ROMs se gestionan desde aquí.
        var menuBar = new ClickThroughMenuStrip();
        var optionsMenu = new ToolStripMenuItem("Opciones");
        optionsMenu.DropDownItems.Add("Cómo guardar las ROMs…", null, (_, _) => ChooseStorage());
        optionsMenu.DropDownItems.Add("Bases de datos de ROMs…", null, (_, _) => OpenDatManager());
        optionsMenu.DropDownOpening += (_, _) => optionsMenu.DropDownItems[1].Enabled = !working; // sin cambiar los DAT con el proceso en marcha
        optionsMenu.DropDownItems.Add(new ToolStripSeparator());
        // Clones de arcade: solo se omiten si su padre ya está en el lote o en la colección (los casos raros se indican al terminar).
        var skipClonesItem = new ToolStripMenuItem("No añadir clones (si su padre ya está)") { CheckOnClick = true, Checked = StorageSettings.Load().SkipClones };
        // La pestaña de clones solo existe con la opción activa; sin ella, los clones se tratan como cualquier otra ROM y van a las demás pestañas.
        skipClonesItem.CheckedChanged += (_, _) => { var st = StorageSettings.Load(); st.SkipClones = skipClonesItem.Checked; st.Save(); tabs.SetVisible(3, skipClonesItem.Checked); };
        tabs.SetVisible(3, skipClonesItem.Checked);
        // La misma opción que la casilla de abajo: las dos se mantienen sincronizadas.
        var moveItem = new ToolStripMenuItem("Mover los archivos en lugar de copiarlos") { CheckOnClick = true, Checked = moveFiles.Checked };
        moveItem.CheckedChanged += (_, _) => moveFiles.Checked = moveItem.Checked;
        moveFiles.CheckedChanged += (_, _) => moveItem.Checked = moveFiles.Checked;
        // Duplicadas con contenido distinto: se añade la nueva, que reemplaza al existente (al añadir a la colección, sin copia de seguridad).
        var differentItem = new ToolStripMenuItem("Añadir las nuevas cuando el duplicado es distinto") { CheckOnClick = true, Checked = StorageSettings.Load().AddDifferentDuplicates,
            ToolTipText = Loc.T("Si el contenido de una duplicada resulta distinto del existente, la ROM nueva pasa sola a identificadas y reemplaza al existente al añadirla a la colección (sin copia de seguridad).") };
        differentItem.CheckedChanged += (_, _) => { var st = StorageSettings.Load(); st.AddDifferentDuplicates = differentItem.Checked; st.Save();
            if (!differentItem.Checked) return;
            // Al activarla, también se pasan las duplicadas distintas que ya estaban en la lista.
            foreach (DataGridViewRow row in dupGrid.Rows) if (row.Tag is DupInfo && row.Cells[5].Value is string v && (v == "Distinto" || v.StartsWith("Parcial", StringComparison.Ordinal)) && !autoAdd.Contains(row)) autoAdd.Add(row);
            FlushAutoAdd();
        };
        // Arcade: al guardar, el archivo toma el nombre del juego en el DAT con el que se identificó (el nombre corto que esperan los emuladores y ScreenScraper).
        var renameItem = new ToolStripMenuItem("Renombrar las ROMs de arcade según el DAT") { CheckOnClick = true, Checked = StorageSettings.Load().RenameArcadeToDat,
            ToolTipText = Loc.T("Al añadir ROMs a una carpeta de arcade, el archivo toma el nombre del juego en el DAT con el que se identificó. El resto de sistemas conservan su nombre.") };
        renameItem.CheckedChanged += (_, _) => { var st = StorageSettings.Load(); st.RenameArcadeToDat = renameItem.Checked; st.Save(); };
        var zipOnlyItem = new ToolStripMenuItem("MAME: comprimir solo en zip (nunca 7z)") { CheckOnClick = true, Checked = StorageSettings.Load().ArcadeZipOnly,
            ToolTipText = Loc.T("En las carpetas de MAME, si se elige 7z como formato, las ROMs se guardan en zip.") };
        zipOnlyItem.CheckedChanged += (_, _) => { var st = StorageSettings.Load(); st.ArcadeZipOnly = zipOnlyItem.Checked; st.Save(); };
        optionsMenu.DropDownItems.Add(skipClonesItem);
        optionsMenu.DropDownItems.Add(differentItem);
        optionsMenu.DropDownItems.Add(renameItem);
        optionsMenu.DropDownItems.Add(zipOnlyItem);
        optionsMenu.DropDownItems.Add(moveItem);
        menuBar.Items.Add(optionsMenu);
        MainMenuStrip = menuBar;
        Controls.Add(menuBar);
        Controls.SetChildIndex(banner, Controls.Count - 1); // la cabecera se acopla la primera: el menú queda debajo de ella

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
        browseButton.Click += (_, _) => { if (!working) browseMenu.Show(browseButton, new Point(0, browseButton.Height)); else if (AskCancelPaused(runName)) runCts?.Cancel(); };
        removeButton.Click += (_, _) => RemoveSelected();
        clearButton.Click += (_, _) => ClearLists();
        addButton.Click += async (_, _) => await AddAsync();
        curateButton.Click += async (_, _) => await CurateAndAddAsync();
        // Ayuda emergente de los dos botones principales: qué hace cada uno.
        HelpTip.Attach(addButton, string.Join("\r\n",
            "Guarda las ROMs identificadas que están listas en la carpeta de su sistema de la colección.",
            "Las copia o las mueve según la opción elegida, con el formato de almacenamiento de Opciones.",
            "Las desconocidas, las duplicadas y los clones no se añaden."));
        HelpTip.Attach(curateButton, string.Join("\r\n",
            "Antes de añadir, consulta en ScreenScraper la categoría de las ROMs identificadas.",
            "Abre el curado para descartar candidatas (por género, palabras clave o idioma).",
            "Las descartadas dejan de añadirse, sin borrar ningún archivo.",
            "«Añadir a la colección» del curado añade las que queden.",
            "Necesita cuenta de ScreenScraper e internet."));
        FormClosing += (_, e) => { if (working && !AskCancelPaused("la operación sobre las ROMs")) { e.Cancel = true; return; } cts.Cancel(); };
        CleanLeftovers();
        FormClosed += (_, _) => CleanTemp();

        // Elegir carpeta en la lista de dudas: la ROM pasa a la lista de identificadas.
        doubtGrid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != doubtGrid.Columns["folder"]!.Index) return;
            var changed = doubtGrid.Rows[e.RowIndex];
            if (changed.Cells["folder"].Value is not string { Length: > 0 }) return;
            // Se guarda la fila, no su número: al asignar carpeta a muchas a la vez, cada una que pasa a Identificadas desplaza los números de las demás.
            Ui(() => { if (changed.DataGridView == doubtGrid) MoveToDecided(changed); });
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
    // Opciones del desplegable: primero las candidatas, luego las carpetas que ya existen y por último el resto de sistemas de ES-DE.
    // Cargarlas en cada fila al añadirla (más de 300 elementos por fila) frenaba la ventana: se cargan solo cuando se edita la celda.
    private void FillFolderOptions(DataGridViewRow row)
    {
        if (row.Tag is not RomIdentity identity || row.Cells["folder"] is not DataGridViewComboBoxCell combo || combo.Tag is "full") return;
        string? current = combo.Value as string;
        var options = identity.Candidates.Concat(existingFolders).Concat(EsDeStructure.Systems).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!string.IsNullOrEmpty(current) && !options.Contains(current, StringComparer.OrdinalIgnoreCase)) options.Add(current);
        combo.Items.Clear();
        combo.Items.AddRange(options.Cast<object>().ToArray());
        combo.Tag = "full";
    }

    // Tabla de solo lectura pero con selección (varias filas) para las pestañas de duplicadas y clones: tienen menú contextual y doble clic.
    private DataGridView MakeInfoGrid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            AllowUserToResizeColumns = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true,
            BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle, EnableHeadersVisualStyles = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, ColumnHeadersHeight = 22
        };
        RaUi.StyleGrid(g, 22, 22);
        Redraw.EnableDoubleBuffer(g);
        g.CellMouseDown += (_, e) =>
        {
            // Clic derecho sobre una fila que no estaba seleccionada: pasa a ser la selección.
            if (e.Button != MouseButtons.Right || e.RowIndex < 0 || g.Rows[e.RowIndex].Selected) return;
            g.ClearSelection(); g.Rows[e.RowIndex].Selected = true;
        };
        return g;
    }

    private void BuildInfoGridActions()
    {
        // Duplicadas: comparar con el existente (también con doble clic) y añadir a identificadas reemplazando el existente.
        var dupMenu = new ContextMenuStrip { ShowImageMargin = false };
        var compareItem = new ToolStripMenuItem("Comparar con el existente…");
        var addDupItem = new ToolStripMenuItem("Añadir a identificadas (reemplaza el existente)");
        dupMenu.Items.AddRange([compareItem, addDupItem]);
        dupGrid.ContextMenuStrip = dupMenu;
        dupMenu.Opening += (_, e) =>
        {
            var rows = SelectedInfoRows(dupGrid);
            if (working || rows.Count == 0) { e.Cancel = true; return; }
            compareItem.Enabled = rows.Count == 1;
        };
        compareItem.Click += (_, _) => { if (SelectedInfoRows(dupGrid).FirstOrDefault() is { } row) CompareDuplicate(row); };
        addDupItem.Click += (_, _) => AddDuplicates(SelectedInfoRows(dupGrid));
        dupGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && !working) CompareDuplicate(dupGrid.Rows[e.RowIndex]); };

        // Clones: añadir a identificadas los omitidos.
        var cloneMenu = new ContextMenuStrip { ShowImageMargin = false };
        var addCloneItem = new ToolStripMenuItem("Añadir a identificadas");
        cloneMenu.Items.Add(addCloneItem);
        cloneGrid.ContextMenuStrip = cloneMenu;
        cloneMenu.Opening += (_, e) =>
        {
            var rows = SelectedInfoRows(cloneGrid);
            if (working || rows.Count == 0) { e.Cancel = true; return; }
            addCloneItem.Enabled = rows.Any(r => r.Tag is CloneInfo { Omitted: true });
        };
        addCloneItem.Click += (_, _) => AddClones(SelectedInfoRows(cloneGrid));
    }

    private static List<DataGridViewRow> SelectedInfoRows(DataGridView g) => g.SelectedRows.Cast<DataGridViewRow>().OrderBy(r => r.Index).ToList();

    // Doble clic o menú: muestra qué archivos internos coinciden y cuáles difieren, y deja decidir entre mantener el existente o reemplazarlo por el nuevo.
    private void CompareDuplicate(DataGridViewRow row)
    {
        if (row.Tag is not DupInfo info) return;
        if (!File.Exists(info.ExistingPath)) { QuietMessage.Show(this, "Comparar contenido", "El archivo que ya existe no está en disco (solo figura en el gamelist.xml): no hay nada que comparar."); return; }
        string display = Path.GetRelativePath(Path.Combine(root, "Roms", info.Folder), info.ExistingPath);
        using var dialog = new RomCompareDialog(info.Identity.Path, info.ExistingPath, Path.Combine(info.Folder, display));
        if (dialog.ShowDialog(this) == DialogResult.OK) AddDuplicates([row], confirm: false);
    }

    // Pasa duplicadas a identificadas: al añadirlas a la colección, el archivo existente se sustituye por el nuevo (sin copia de seguridad).
    private void AddDuplicates(List<DataGridViewRow> rows, bool confirm = true, bool switchTab = true)
    {
        var chosen = rows.Where(r => r.Tag is DupInfo).ToList();
        if (chosen.Count == 0 || working) return;
        if (confirm && !QuietMessage.Confirm(this, "Añadir a identificadas",
                $"Se pasarán {chosen.Count} ROM(s) a identificadas. Al añadirlas a la colección, el archivo que ya existe se sustituirá por el nuevo y el anterior se borrará (sin copia de seguridad).",
                "Añadir")) return;
        foreach (var row in chosen)
        {
            var info = (DupInfo)row.Tag!;
            forced.Add(info.Identity);
            info.Identity.ReplaceExisting = info.ExistingPath;
            dupGrid.Rows.Remove(row);
            items.Add(info.Identity);
            AddRow(info.Identity);
            for (int i = grid.Rows.Count - 1; i >= 0; i--)
                if (ReferenceEquals(grid.Rows[i].Tag, info.Identity))
                {
                    if (grid.Rows[i].Cells["state"].Value is string st && st.StartsWith("Lista", StringComparison.Ordinal)) grid.Rows[i].Cells["state"].Value = st + " · reemplaza el existente";
                    break;
                }
        }
        UpdateAddButton(); UpdateDoubtLabel(); UpdateDupUi();
        if (switchTab) tabs.Selected = 0;
    }

    // Pasa clones omitidos a identificadas (los conservados ya están en ellas).
    private void AddClones(List<DataGridViewRow> rows)
    {
        var chosen = rows.Where(r => r.Tag is CloneInfo { Omitted: true }).ToList();
        if (chosen.Count == 0 || working) return;
        foreach (var row in chosen)
        {
            var info = (CloneInfo)row.Tag!;
            forced.Add(info.Identity);
            cloneGrid.Rows.Remove(row);
            cloneSkipped = Math.Max(0, cloneSkipped - 1);
            items.Add(info.Identity);
            AddRow(info.Identity);
        }
        UpdateAddButton(); UpdateDoubtLabel(); UpdateTabs();
        tabs.Selected = 0;
    }

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
        Redraw.EnableDoubleBuffer(g); // sin parpadeo de los iconos al añadir filas
        // Las columnas se reparten el ancho de la ventana según su peso (se ven bien también maximizada) y se pueden ajustar a mano.
        g.AllowUserToResizeColumns = true;
        const DataGridViewAutoSizeColumnMode fill = DataGridViewAutoSizeColumnMode.Fill;
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "file", HeaderText = "Archivo", AutoSizeMode = fill, FillWeight = 15, MinimumWidth = 80, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Automatic });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "game", HeaderText = "Juego", AutoSizeMode = fill, FillWeight = 28, MinimumWidth = 90, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Automatic });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "method", HeaderText = "Identificación", AutoSizeMode = fill, FillWeight = 22, MinimumWidth = 90, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Automatic });
        g.Columns.Add(new DataGridViewComboBoxColumn { Name = "folder", HeaderText = "Destino", AutoSizeMode = fill, FillWeight = 16, MinimumWidth = 90, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox, SortMode = DataGridViewColumnSortMode.Automatic });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "state", HeaderText = "Estado", AutoSizeMode = fill, FillWeight = 19, MinimumWidth = 90, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Automatic });
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
        g.CellBeginEdit += (_, e) => { if (e.RowIndex >= 0 && g.Columns[e.ColumnIndex].Name == "folder") FillFolderOptions(g.Rows[e.RowIndex]); };
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
        var viewFilesItem = new ToolStripMenuItem("Ver ficheros…");
        menu.Items.Add(assignItem); menu.Items.Add(viewFilesItem); menu.Items.Add(reidentifyItem); menu.Items.Add(removeItem);
        viewFilesItem.Click += async (_, _) => await ShowFilesAsync(g.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault());
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
            viewFilesItem.Enabled = selected.Count == 1 && selected[0].Tag is RomIdentity; // el contenido se ve de una ROM cada vez
            removeItem.Text = selected.Count == 1 ? "Quitar de la lista" : $"Quitar {selected.Count} de la lista";
            // Con una sola ROM el texto lleva su nombre de archivo (abreviado si es largo), para ver cuál está seleccionada.
            string selectedName = selected.Count == 1 ? selected[0].Cells["file"].Value as string ?? "" : "";
            if (selectedName.Length > 40) selectedName = selectedName[..39] + "…";
            assignItem.Text = selected.Count == 1 && selectedName.Length > 0 ? $"Asignar {selectedName} a carpeta" : selected.Count == 1 ? "Asignar a carpeta" : $"Asignar {selected.Count} a carpeta";
            // El submenú (unos 200 sistemas) se monta al desplegarlo, no al abrir el menú: así el menú contextual aparece al instante.
            assignItem.DropDownItems.Clear();
            assignItem.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
        };
        // Submenú de carpetas de «host» para las filas que devuelva rowsOf (las seleccionadas, o todas las de la lista).
        void FillFolderMenu(ToolStripMenuItem host, Func<List<DataGridViewRow>> rowsOf)
        {
            var targets = rowsOf();
            if (targets.Count == 0) return;
            host.DropDownItems.Clear();
            var dropDown = host.DropDown;
            dropDown.SuspendLayout(); // sin recolocar el submenú tras cada elemento añadido
            // Todas las carpetas, por bloques: las candidatas de las ROMs elegidas, las que ya existen en la colección y el resto de sistemas de ES-DE.
            var candidates = targets.Select(r => r.Tag).OfType<RomIdentity>().SelectMany(i => i.Candidates).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var existing = existingFolders.Where(f => !candidates.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
            var rest = EsDeStructure.Systems.Where(s => !candidates.Contains(s, StringComparer.OrdinalIgnoreCase) && !existing.Contains(s, StringComparer.OrdinalIgnoreCase))
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            void AddBlock(IEnumerable<string> folders, bool bold)
            {
                var list = folders.ToList();
                if (list.Count == 0) return;
                if (host.DropDownItems.Count > 0) host.DropDownItems.Add(new ToolStripSeparator());
                var boldFont = bold ? new Font(menu.Font, FontStyle.Bold) : null; // una sola fuente para todo el bloque; los demás usan la del menú sin asignarla
                var block = new List<ToolStripItem>(list.Count);
                foreach (var folder in list)
                {
                    var item = new ToolStripMenuItem(folder);
                    if (boldFont is not null) item.Font = boldFont;
                    item.Click += (_, _) => AssignFolder(g, targets, folder);
                    block.Add(item);
                }
                host.DropDownItems.AddRange(block.ToArray());
            }
            AddBlock(candidates, bold: true);
            AddBlock(existing, bold: false);
            AddBlock(rest, bold: false);
            if (host.DropDownItems.Count > 0) host.DropDownItems.Add(new ToolStripSeparator());
            host.DropDownItems.Add("Buscar carpeta…", null, (_, _) =>
            {
                using var picker = new FolderPickDialog(existingFolders, EsDeStructure.Systems, targets.Count);
                if (picker.ShowDialog(this) == DialogResult.OK && picker.Selected is { } chosen) AssignFolder(g, targets, chosen);
            });
            dropDown.ResumeLayout(true);
        }
        assignItem.DropDownOpening += (_, _) => FillFolderMenu(assignItem, () => g.SelectedRows.Cast<DataGridViewRow>().ToList());
        // «Destino para todas»: solo con el botón derecho sobre la columna «Destino»; asigna la carpeta elegida a todas las ROMs de la lista, no solo a las seleccionadas.
        var allItem = new ToolStripMenuItem("Destino para todas");
        ((ToolStripDropDownMenu)allItem.DropDown).ShowImageMargin = false;
        menu.Items.Insert(0, allItem);
        menu.Items.Insert(1, new ToolStripSeparator());
        allItem.DropDownOpening += (_, _) => FillFolderMenu(allItem, () => g.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is RomIdentity).ToList());
        var separatorAfterAll = menu.Items[1];
        int rightClickedColumn = -1;
        g.CellMouseDown += (_, e) => { if (e.Button == MouseButtons.Right) rightClickedColumn = e.ColumnIndex; };
        menu.Opening += (_, _) =>
        {
            bool onDestination = rightClickedColumn >= 0 && rightClickedColumn == g.Columns["folder"]!.Index && g.Rows.Count > 0;
            allItem.Visible = separatorAfterAll.Visible = onDestination;
            if (onDestination)
            {
                allItem.Text = $"Destino para todas ({g.Rows.Count:N0})";
                allItem.DropDownItems.Clear();
                allItem.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
            }
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

    // «Ver ficheros»: contenido del comprimido de la ROM elegida frente a los DAT (se lee solo la cabecera del zip o 7z, sin descomprimir).
    private async Task ShowFilesAsync(DataGridViewRow? row)
    {
        if (row?.Tag is not RomIdentity identity || !File.Exists(identity.Path)) return;
        UseWaitCursor = true;
        RomFilesReport report;
        try { report = await RomFilesReport.InspectAsync(identity.Path); }
        finally { UseWaitCursor = false; }
        if (IsDisposed) return;
        using var dialog = new RomFilesDialog(identity.Path, report, FormatBytes);
        dialog.ShowDialog(this);
    }

    private static string FormatBytes(long n) { string[] u = ["B", "KB", "MB", "GB"]; double x = n; int i = 0; while (x >= 1024 && i < 3) { x /= 1024; i++; } return i == 0 ? $"{n} B" : $"{x:0.#} {u[i]}"; }

    // Ctrl+A selecciona todas las filas de la lista de la pestaña activa, aunque el foco esté en el desplegable de carpeta de una fila (que se llevaría la
    // tecla). Con todas seleccionadas, «Asignar a carpeta» del menú contextual las pasa de golpe a la carpeta elegida.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.A) && !working)
        {
            var target = tabs.Selected switch { 0 => grid, 1 => doubtGrid, 2 => dupGrid, _ => cloneGrid };
            try { target.EndEdit(); } catch (InvalidOperationException) { }
            target.Focus();
            target.SelectAll();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
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
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        runCts = run; runName = "la re-identificación de las ROMs"; browseButton.Text = "Detener";
        SetBeating(true, scan: true);
        progress.Style = ProgressBarStyle.Marquee; progress.Visible = true;
        status.Text = $"Re-identificando {olds.Count} archivo(s)…";
        try
        {
            if (!RomDatabase.IndexReady && RomDatabase.InstalledCount() > 0) await PrepareIndexAsync("");
            progress.Style = ProgressBarStyle.Marquee; progress.Visible = true;
            var results = new RomIdentity?[olds.Count];
            int degree = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
            await Parallel.ForEachAsync(Enumerable.Range(0, olds.Count), new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = run.Token },
                async (i, ct) => { pauseGate.Wait(ct); results[i] = await RomIdentifier.IdentifyAsync(olds[i].Path, ct); });
            if (IsDisposed) return;
            int resolved = 0, toDuplicates = 0, toClones = 0;
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
                // Una ROM re-identificada puede acabar en Identificadas, o en Duplicadas (ya existe en la colección) o en Clones (su padre ya está): se cuenta dónde queda.
                if (grid.Rows.Cast<DataGridViewRow>().Any(r => ReferenceEquals(r.Tag, fresh))) resolved++;
                else if (dupGrid.Rows.Cast<DataGridViewRow>().Any(r => r.Tag is DupInfo d && ReferenceEquals(d.Identity, fresh))) toDuplicates++;
                else if (cloneGrid.Rows.Cast<DataGridViewRow>().Any(r => r.Tag is CloneInfo c && ReferenceEquals(c.Identity, fresh))) toClones++;
            }
            UpdateDoubtLabel(); UpdateTabs();
            int undecided = olds.Count - resolved - toDuplicates - toClones;
            // Fragmentos separados por « · »: cada uno se traduce por su cuenta.
            status.Text = $"Re-identificadas {olds.Count}: {resolved} identificadas" + (toDuplicates > 0 ? $" · {toDuplicates} duplicadas (ya están en la colección: pestaña Duplicadas)" : "")
                + (toClones > 0 ? $" · {toClones} clones (pestaña Clones)" : "") + $" · {undecided} sin decidir";
        }
        catch (OperationCanceledException) { if (!IsDisposed) status.Text = "Re-identificación cancelada."; }
        finally { runCts = null; working = false; SetBeating(false); if (!IsDisposed) { browseButton.Text = "Examinar…"; progress.Visible = false; progress.Style = ProgressBarStyle.Blocks; UpdateAddButton(); UpdateRemoveButton(); } }
    }

    // Asigna la carpeta de destino a las filas indicadas (de una sola lista). En la lista de dudas, cada fila pasa a la de identificadas.
    private void AssignFolder(DataGridView g, List<DataGridViewRow> rows, string folder)
    {
        // Si había una celda en edición (p. ej. el clic derecho cayó sobre un desplegable), se sale de ella antes de cambiar valores y mover filas.
        try { g.EndEdit(); } catch (InvalidOperationException) { }
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

    private void UpdateRemoveButton()
    {
        clearButton.Enabled = !working;
        UpdateDoubtLabel();
        removeButton.Enabled = !working && (grid.SelectedRows.Count > 0 || doubtGrid.SelectedRows.Count > 0);
    }

    private void RemoveSelected()
    {
        RemoveRows(grid, grid.SelectedRows.Cast<DataGridViewRow>().ToList());
        RemoveRows(doubtGrid, doubtGrid.SelectedRows.Cast<DataGridViewRow>().ToList());
    }

    private void ClearLists()
    {
        if (working || (grid.Rows.Count == 0 && doubtGrid.Rows.Count == 0)) return;
        if (!QuietMessage.Confirm(this, "Vaciar listas", "¿Vaciar las dos listas? No se borra ni se copia ningún archivo.", "Vaciar")) return;
        grid.Rows.Clear(); doubtGrid.Rows.Clear(); dupGrid.Rows.Clear(); cloneGrid.Rows.Clear(); keptClones.Clear(); forced.Clear(); autoAdd.Clear(); autoAdded = 0; UpdateDupUi();
        items.Clear(); known.Clear(); packCounts.Clear();
        CleanTemp();
        UpdateAddButton(); UpdateDoubtLabel(); UpdateRemoveButton();
        status.Text = "Listas vacías. Arrastra más ROMs cuando quieras.";
    }

    // Contadores de las pestañas (y la indicación de la lista activa).
    private void UpdateDoubtLabel() => UpdateTabs();

    private async void OpenDatManager()
    {
        // Los DAT se cargan al empezar a identificar: cambiarlos con el proceso en marcha no tendría efecto (o lo falsearía).
        if (working)
        {
            QuietMessage.Show(this, "Bases de datos", "No se pueden cambiar las bases de datos mientras hay una operación en marcha. Espera a que termine o detenla.");
            return;
        }
        using (var dialog = new DatManagerDialog(trace, root))
            dialog.ShowDialog(this);
        UpdateDatSummary();
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
                // Carga suelta (sin ser un paso de un proceso): el avance y el «OK!» van en la línea de las bases de datos, no en la de estado.
                string loadingText = $"{prefix}Base de datos de hashes: {RomDatabase.IndexPhase}… ({RomDatabase.IndexProgress:P0})";
                if (announceOk) SetDbStatus(Loc.T(loadingText)); else status.Text = loadingText;
                await Task.WhenAny(loading, Task.Delay(100));
            }
            await loading;
            if (announceOk) SetDbStatus(Loc.T("Base de datos de hashes OK!"));
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
            QuietMessage.Confirm(this, "Bases de datos", "Todavía no hay bases de datos descargadas, y sin ellas solo se puede identificar por extensión (los .zip, .7z o .bin no se reconocerían).\r\n\r\n¿Quieres descargarlas ahora?", "Abrir descargas", "Continuar"))
            OpenDatManager();

        // Feedback inmediato: recorrer carpetas grandes puede tardar, y se hace fuera del hilo de la interfaz.
        working = true; addButton.Enabled = false; UpdateRemoveButton();
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        runCts = run; runName = "la identificación de las ROMs"; browseButton.Text = "Detener";
        SetBeating(true, scan: true); // al analizar, el chip con la lupa
        progress.Style = ProgressBarStyle.Marquee; progress.Visible = true;
        status.ForeColor = SystemColors.ControlText;
        status.Text = "Paso 1 de 3: Buscando archivos…";
        List<string> files = [];
        try
        {
            int notRoms;
            try { (files, notRoms) = await Task.Run(() => CollectFiles(paths), run.Token); }
            catch (OperationCanceledException) { status.Text = "⚠ Cancelado antes de terminar: la lista está incompleta."; status.ForeColor = Color.FromArgb(190, 90, 0); return; }
            files = files.Where(f => known.Add(Path.GetFullPath(f))).ToList();
            if (files.Count == 0)
            {
                status.Text = notRoms > 0 ? $"Se han ignorado {notRoms} archivo(s) que no son ROMs." : "No hay archivos nuevos que añadir.";
                return;
            }
            lastIgnored = notRoms;

            // Clones de arcade: el filtro se aplica al colocar cada ROM, cuando ya se sabe su carpeta de destino (ver HandleClone): un padre solo vale si está en
            // el lote o en esa misma carpeta de la colección (no en otra, como fbneo cuando el clon va a mame).
            cloneSkipped = 0; cloneCtx = null;
            bool noCloneInfo = false;
            if (StorageSettings.Load().SkipClones)
            {
                var clones = await Task.Run(RomDatabase.CloneParents, run.Token);
                if (clones.Count == 0) noCloneInfo = true;
                else
                {
                    var batch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var f in files) batch.TryAdd(Path.GetFileNameWithoutExtension(f), Path.GetFileName(f));
                    // Lo que ya está en la lista de identificadas (tandas anteriores), por carpeta de destino y nombre: un padre de una tanda anterior también cuenta.
                    var listed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (DataGridViewRow row in grid.Rows)
                        if (row.Tag is RomIdentity id && row.Cells["folder"].Value is string f)
                            listed.TryAdd(f + "|" + Path.GetFileNameWithoutExtension(id.Path), Path.GetFileName(id.Path));
                    cloneCtx = (clones, batch, listed);
                }
            }

            // Paso 2: cargar las bases de datos en memoria (solo tarda la primera vez o tras cambiarlas).
            if (RomDatabase.IndexReady) status.Text = "Paso 2 de 3: Base de datos de hashes OK!";
            else await PrepareIndexAsync("Paso 2 de 3: ");

            // El paso 3 trabaja en paralelo (varios archivos a la vez; es sobre todo espera de disco, así que se usan más hilos que núcleos/2). Un temporizador de la interfaz refleja el avance sin saturarla.
            int degree = Math.Clamp(Environment.ProcessorCount, 4, 12);
            var token = run.Token;
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
                // Paso 3: examinar (extraer packs) e identificar, todo de una vez por archivo. Las filas se añaden en el orden original según terminan.
                var results = new List<RomIdentity>?[files.Count];
                int finished = 0, nextFile = 0;
                marquee = files.Count == 1; // con un solo archivo (posible pack grande) no hay porcentaje que dar
                flushRows = () =>
                {
                    // Presupuesto de tiempo (no de filas): cada fila cuesta lo suyo y así la ventana nunca se queda sin atender mensajes.
                    int added = 0;
                    var budget = System.Diagnostics.Stopwatch.StartNew();
                    // Cada tanda se pinta una sola vez (no fila a fila): así los iconos de la columna «Destino» no parpadean.
                    Redraw.Batch(grid, () => Redraw.Batch(doubtGrid, () =>
                    {
                        while (nextFile < files.Count && budget.ElapsedMilliseconds < 40 && Volatile.Read(ref results[nextFile]) is { } list)
                        {
                            foreach (var id in list) { items.Add(id); AddRow(id); added++; }
                            nextFile++;
                        }
                    }));
                    return added;
                };
                progressText = $"Paso 3 de 3: Examinando e identificando 0/{files.Count}…";
                await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = token }, async (i, ct) =>
                {
                    pauseGate.Wait(ct);
                    var parts = ArchiveExpander.Expand(files[i], tempRoot, ct);
                    string? origin = null;
                    if (!(parts.Count == 1 && parts[0] == files[i]))
                    {
                        origin = Path.GetFullPath(files[i]);
                        lock (packCounts) packCounts[origin] = packCounts.GetValueOrDefault(origin) + parts.Count;
                    }
                    var ids = new RomIdentity[parts.Count];
                    if (parts.Count == 1) ids[0] = await RomIdentifier.IdentifyAsync(parts[0], ct);
                    else await Parallel.ForEachAsync(Enumerable.Range(0, parts.Count), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (k, c) => ids[k] = await RomIdentifier.IdentifyAsync(parts[k], c));
                    foreach (var id in ids) id.Origin = origin;
                    Volatile.Write(ref results[i], ids.ToList());
                    int d = Interlocked.Increment(ref finished);
                    progressText = $"Paso 3 de 3: Examinando e identificando {d}/{files.Count} ({d * 100 / files.Count} %): {Path.GetFileName(files[i])}";
                    progressValue = d * 1000 / files.Count;
                });
                marquee = false;
                tick.Stop();
                // Las filas que falten por añadir, por tandas y cediendo el control a la ventana entre una y otra (si no, se queda bloqueada unos segundos).
                status.Text = "Añadiendo las filas a las listas…";
                while (flushRows() > 0) await Task.Yield();
                ReconcileClones();
            }
            catch (OperationCanceledException)
            {
                // Lo ya identificado se conserva; los archivos que no llegaron a tener fila se pueden volver a arrastrar.
                while (flushRows?.Invoke() > 0) await Task.Yield();
                var added = items.Select(i => Path.GetFullPath(i.Origin ?? i.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var f in files) if (!added.Contains(Path.GetFullPath(f))) known.Remove(Path.GetFullPath(f));
                status.Text = $"⚠ Cancelado antes de terminar: la lista es parcial ({items.Count} archivo(s) identificado(s))."; status.ForeColor = Color.FromArgb(190, 90, 0);
                return;
            }
            finally { tick.Stop(); }
            int doubts = doubtGrid.Rows.Count;
            // El tamaño de cada archivo se lee fuera del hilo de la interfaz (miles de accesos a disco, a veces a una tarjeta lenta).
            var itemPaths = items.Select(i => i.Path).ToList();
            long itemBytes = await Task.Run(() => { long total = 0; foreach (var itemPath in itemPaths) { try { total += new FileInfo(itemPath).Length; } catch { } } return total; });
            status.Text = $"{items.Count} archivo(s) · {ScreenScraperDialog.FormatBytes(itemBytes)} (tamaño original)." + (lastIgnored > 0 ? $" {lastIgnored} ignorado(s) por no ser ROMs." : "") + (doubts > 0 ? $" {doubts} sin decidir: elige su carpeta." : "")
                + (cloneSkipped > 0 ? $" {cloneSkipped} clon(es) omitido(s)." : "")
                + (cloneKept > 0 ? $" {cloneKept} clon(es) conservado(s) sin su padre: ver la pestaña Clones." : "")
                + (noCloneInfo ? " Los DAT activos no traen información de clones: no se ha omitido ninguno." : "");

        }
        finally { cloneCtx = null; runCts = null; working = false; SetBeating(false); if (!IsDisposed) { browseButton.Text = "Examinar…"; progress.Visible = false; progress.Style = ProgressBarStyle.Blocks; UpdateAddButton(); FlushAutoAdd(); } }
    }

    // Carpeta que se propone para la ROM, o null si hay dudas.
    private string? ProposeFolder(RomIdentity identity, out string note)
    {
        note = "";
        var existing = identity.Candidates.Where(c => existingFolders.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        bool hash = !identity.PartialMatch && (identity.Method.StartsWith("Hash", StringComparison.Ordinal) || identity.Method.StartsWith("Nombre", StringComparison.Ordinal) || identity.Method.StartsWith("CRC", StringComparison.Ordinal));
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

    // Filtro de clones con la carpeta de destino ya conocida. Un clon se omite si su padre está en el lote o en esa misma carpeta de la colección; si el padre
    // no está en ninguno de los dos sitios, el clon se conserva (se añade) y se anota. Devuelve true si se omite.
    private bool HandleClone(RomIdentity identity, string folder)
    {
        if (cloneCtx is not { } ctx || !RomIdentifier.ArcadeFolders.Contains(folder)) return false;
        string file = Path.GetFileName(identity.Path), stem = Path.GetFileNameWithoutExtension(identity.Path);
        if (!ctx.Clones.TryGetValue(stem, out var parent)) return false;
        // El padre real es el último de la cadena (un «padre» de un clon también puede figurar como clon).
        for (int hops = 0; hops < 5 && ctx.Clones.TryGetValue(parent, out var up) && !up.Equals(parent, StringComparison.OrdinalIgnoreCase); hops++) parent = up;
        string? situation = null;
        if (ctx.Batch.TryGetValue(parent, out var inBatch)) situation = $"Está en este lote: {inBatch}";
        else if (ctx.Listed.TryGetValue(folder + "|" + parent, out var inList)) situation = $"Está en la lista de identificadas: {inList}";
        else if (GetFolderIndex(folder).Stems.TryGetValue(parent, out var there))
            situation = $"Ya está en la colección: {Path.GetRelativePath(Path.Combine(root, "Roms"), there)}";
        if (situation is null)
        {
            cloneKept++;
            int added = cloneGrid.Rows.Add(file, parent, "No está ni en el lote ni en la colección", "Conservado (se añade)");
            cloneGrid.Rows[added].Tag = new CloneInfo(identity, folder, false);
            keptClones.Add((identity, folder, parent, cloneGrid.Rows[added]));
            UpdateTabs();
            return false;
        }
        cloneSkipped++;
        known.Remove(Path.GetFullPath(identity.Origin ?? identity.Path));
        items.Remove(identity);
        int omittedRow = cloneGrid.Rows.Add(file, parent, situation, "Omitido");
        cloneGrid.Rows[omittedRow].Tag = new CloneInfo(identity, folder, true);
        UpdateTabs();
        return true;
    }

    // Revisión de los clones al terminar de escanear una tanda: un clon conservado porque su padre no estaba puede tenerlo ya (el padre llegó después, en esta
    // tanda o en una posterior). Si es así, se quita de las identificadas y pasa a «Omitido» en la pestaña de clones.
    private void ReconcileClones()
    {
        if (cloneCtx is null) return;
        if (keptClones.Count > 0)
        {
            var listed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var rowOf = new Dictionary<RomIdentity, DataGridViewRow>();
            foreach (DataGridViewRow row in grid.Rows)
                if (row.Tag is RomIdentity id && row.Cells["folder"].Value is string f)
                {
                    listed.TryAdd(f + "|" + Path.GetFileNameWithoutExtension(id.Path), Path.GetFileName(id.Path));
                    rowOf[id] = row;
                }
            var toRemove = new List<DataGridViewRow>();
            foreach (var kept in keptClones.ToList())
            {
                if (!rowOf.TryGetValue(kept.Identity, out var row)) { keptClones.Remove(kept); continue; } // ya la quitó el usuario de la lista
                string? situation = null;
                if (listed.TryGetValue(kept.Folder + "|" + kept.Parent, out var inList)) situation = $"Está en la lista de identificadas: {inList}";
                else if (GetFolderIndex(kept.Folder).Stems.TryGetValue(kept.Parent, out var there))
                    situation = $"Ya está en la colección: {Path.GetRelativePath(Path.Combine(root, "Roms"), there)}";
                if (situation is null) continue;
                toRemove.Add(row);
                items.Remove(kept.Identity);
                known.Remove(Path.GetFullPath(kept.Identity.Origin ?? kept.Identity.Path));
                kept.CloneRow.Cells[2].Value = situation; kept.CloneRow.Cells[3].Value = "Omitido";
                kept.CloneRow.Tag = new CloneInfo(kept.Identity, kept.Folder, true);
                keptClones.Remove(kept); cloneSkipped++;
            }
            Redraw.Batch(grid, () => { foreach (var row in toRemove) grid.Rows.Remove(row); });
        }
        cloneKept = keptClones.Count;
        UpdateAddButton(); UpdateTabs();
    }

    private void AddRowCore(RomIdentity identity)
    {
        string? folder = ProposeFolder(identity, out string note);
        if (cloneCtx is not null && folder is not null && !forced.Contains(identity) && HandleClone(identity, folder)) return;
        var target = folder is null ? doubtGrid : grid;
        int index = target.Rows.Add();
        var row = target.Rows[index];
        var combo = (DataGridViewComboBoxCell)row.Cells["folder"];
        // El desplegable solo lleva de partida las candidatas y la carpeta elegida; la lista completa se carga al editar la celda (ver FillFolderOptions).
        combo.Items.AddRange(identity.Candidates.Concat(folder is null ? [] : [folder]).Distinct(StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray());
        row.Cells["file"].Value = Path.GetFileName(identity.Path);
        row.Cells["file"].ToolTipText = identity.Origin is null ? identity.Path : $"{identity.Path}\r\n(de {identity.Origin})";
        row.Cells["game"].Value = identity.PartialGame ?? identity.Game;
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
        row.Cells["game"].Value = identity.PartialGame ?? identity.Game;
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
            if (!forced.Contains(identity) && TryDuplicate(row, folder)) return;
            bool created = !existingFolders.Contains(folder, StringComparer.OrdinalIgnoreCase);
            // Formato con el que se guardará (según lo que admita el sistema) y nombre que tendrá el archivo de destino.
            var (format, note) = RomStorage.Plan(identity, CurrentStorage, SystemFormats.AllowedExtensions(root, folder), ArcadeZipOnly(folder));
            string stem = DatStem(identity, folder) ?? Path.GetFileNameWithoutExtension(identity.Path);
            string targetName = format switch { StorageFormat.Zip => stem + ".zip", StorageFormat.SevenZip => stem + ".7z", _ => Path.GetFileName(identity.Path) };
            string destination = Path.Combine(root, "Roms", folder, targetName);
            string how = " · " + RomStorage.Describe(format, CurrentStorage.Level) + (note.Length > 0 ? $" ({note})" : "");
            row.Cells["state"].Value = File.Exists(destination) ? "Ya existe en la carpeta" : (created ? "Lista (se creará la carpeta)" : "Lista") + how;
            row.DefaultCellStyle.BackColor = Color.Empty; // sin color propio: vale el fondo alterno de la tabla
        }
        UpdateAddButton();
    }

    // Solo las carpetas de MAME (mame, mame-advmame…): las de FBNeo y demás arcade admiten 7z sin problema.
    private static bool ArcadeZipOnly(string folder) => folder.StartsWith("mame", StringComparison.OrdinalIgnoreCase) && StorageSettings.Load().ArcadeZipOnly;

    private void UpdateAddButton()
    {
        clearButton.Enabled = !working;
        UpdateDoubtLabel();
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
        var request = ready.Select(r => new TemporaryCategorizer.Item(((RomIdentity)r.Tag!).Path, (string)r.Cells["folder"].Value!, Path.GetFileName(((RomIdentity)r.Tag!).Path), ((RomIdentity)r.Tag!).Method)).ToList();
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

    private Bitmap[]? chipFrames, chipScanFrames, chipActive;
    private PictureBox? chipBox;
    private System.Windows.Forms.Timer? chipTimer;
    private int chipFrame;

    // scan = true: el chip con la lupa (analizar: identificar y re-identificar); si no, el «+» que late (copiar a la colección).
    private void SetBeating(bool on, bool scan = false)
    {
        if (chipTimer is null || chipBox is null || chipFrames is null || chipScanFrames is null || IsDisposed) return;
        chipActive = on && scan ? chipScanFrames : chipFrames;
        chipTimer.Interval = on && scan ? 40 : 75;
        chipFrame = 0; chipBox.Image = chipActive[0];
        if (on) chipTimer.Start(); else chipTimer.Stop();
    }

    private async Task AddAsync()
    {
        var ready = grid.Rows.Cast<DataGridViewRow>().Where(r => r.Cells["state"].Value is string s && s.StartsWith("Lista", StringComparison.Ordinal)).ToList();
        if (ready.Count == 0) return;
        bool move = moveFiles.Checked;
        working = true; addButton.Enabled = false; curateButton.Enabled = false; UpdateRemoveButton();
        // Mientras copia o mueve, «Examinar» pasa a ser «Detener»; la cancelación es propia de esta operación.
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        runCts = run; runName = move ? "el movimiento de las ROMs a la colección" : "la copia de las ROMs a la colección"; browseButton.Text = "Detener";
        status.ForeColor = SystemColors.ControlText;
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
        var arcadeSetsUsed = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase); // carpeta de arcade → romset (DAT) con el que se identificó
        string progressText = ""; int progressValue = 0;
        using var tick = new System.Windows.Forms.Timer { Interval = 120 };
        tick.Tick += (_, _) => { if (IsDisposed) { tick.Stop(); return; } status.Text = progressText; progress.Value = Math.Clamp(progressValue, 0, 1000); };
        tick.Start();
        string verb = move ? "Moviendo" : "Copiando";
        progressText = $"Paso 1 de 1: {verb} 0/{jobs.Count}…";
        try
        {
            await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = run.Token }, async (job, ct) =>
            {
                pauseGate.Wait(ct);
                var (row, identity, folder) = job;
                Ui(() => row.Cells["state"].Value = "Copiando…");
                try
                {
                    await Task.Run(() =>
                    {
                        // Se deja el systeminfo.txt del sistema si falta y se decide el formato real según lo que admita (su lista de extensiones).
                        SystemFormats.EnsureSystemInfo(root, folder);
                        var (format, _) = RomStorage.Plan(identity, options, SystemFormats.AllowedExtensions(root, folder), ArcadeZipOnly(folder));
                        // ROM pasada a mano desde duplicadas: el archivo que existía se aparta, se guarda la nueva y entonces se borra el anterior (si falla, se devuelve).
                        string? held = identity.ReplaceExisting is { } toReplace ? RomStorage.HoldExisting(root, folder, toReplace) : null;
                        List<string> created;
                        identity.TargetStem = DatStem(identity, folder); // arcade: se guarda con el nombre del juego en el DAT
                        try { created = RomStorage.Store(identity, Path.Combine(root, "Roms", folder), format, options.Level, move, ct); }
                        catch { if (held is not null) RomStorage.RestoreHeld(held, identity.ReplaceExisting!); throw; }
                        if (held is not null) RomStorage.ReleaseHeld(held);
                        lock (addedLock) AddedPaths.AddRange(created.Select(Path.GetFullPath));
                        Interlocked.Increment(ref added);
                        if (RomIdentifier.ArcadeFolders.Contains(folder) && ArcadeSets.DatOf(identity.Method) is { } datUsed && ArcadeCategories.Applies(datUsed)) arcadeSetsUsed[folder] = datUsed;
                        trace.Add("rom_added", $"system={folder}; file={Path.GetFileName(identity.Path)}; method={identity.Method}; move={move}" + (identity.TargetStem is { } renamedTo ? $"; renamed_to={renamedTo}" : ""));
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
        finally { tick.Stop(); runCts = null; working = false; SetBeating(false); if (!IsDisposed) { browseButton.Text = "Examinar…"; progress.Visible = false; UpdateAddButton(); UpdateRemoveButton(); } }
        if (!arcadeSetsUsed.IsEmpty) ArcadeSets.SetMany(arcadeSetsUsed); // el romset de cada carpeta de arcade queda guardado para categorizar con su catver.ini
        if (added > 0) CollectionChanged = true; // aunque se haya cerrado el formulario, las ROMs ya añadidas deben pasar a los gamelists
        if (IsDisposed) return;
        if (run.IsCancellationRequested)
        {
            status.Text = $"⚠ Cancelado antes de terminar: {added} de {jobs.Count} ROM(s) añadidas a la colección." + (failed > 0 ? $" {failed} con error." : "") + " Al cerrar se actualizarán los gamelists.";
            status.ForeColor = Color.FromArgb(190, 90, 0);
            return;
        }
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
