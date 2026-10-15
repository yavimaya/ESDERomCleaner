namespace ESDERomCleaner;

// Gestión de las bases de datos (DAT) con las que se identifican las ROMs: elegir qué orígenes se usan, descargar las de
// No-Intro y Redump (libretro-database) e importar las propias (GoodTools, clrmamepro, MAME, FBNeo, emuladores…).
internal sealed class DatManagerDialog : LocalizedForm
{
    private const int Parallel = 4;

    private readonly DataGridView grid = new();
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 1000 };
    private readonly Label info = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    private static readonly (string Kind, string Label)[] Kinds = [("no-intro", "No-Intro"), ("redump", "Redump"), ("tosec", "TOSEC"), ("mame", "MAME"), ("fbneo", "FBNeo"), ("other", "Otras (importadas)")];
    private readonly HashSet<string> collapsed = [];
    private readonly Dictionary<string, int> rowIndex = new(); // DatSource.Key → fila de la tabla
    private readonly ContextMenuStrip rowMenu = new() { ShowImageMargin = false };
    private readonly Button downloadMissing = new() { Text = "Descargar las que faltan", AutoSize = true, MinimumSize = new Size(150, 28) };
    private readonly Button updateAll = new() { Text = "Actualizar todas", AutoSize = true, MinimumSize = new Size(110, 28) };
    private readonly Button importButton = new() { Text = "Importar DAT…", AutoSize = true, MinimumSize = new Size(110, 28) };
    private readonly Button removeButton = new() { Text = "Quitar importada", AutoSize = true, MinimumSize = new Size(110, 28), Enabled = false };
    private readonly Button close = new() { Text = "Cerrar", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 28) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private readonly TraceLog trace;
    private readonly string? root;
    private readonly DatSettings settings = DatSettings.Load();
    private List<DatSource> shown = [];
    private CancellationTokenSource? cts;
    private bool busy;
    private bool loading;

    // Estado de la descarga en curso (se escribe desde los hilos y se lee desde el temporizador de la interfaz).
    private long[] received = [];
    private long[] totals = [];
    private long[] lastSample = [];
    private bool[] active = [];
    private int[] outcome = []; // 0 pendiente, 1 hecha, 2 error
    private string?[] errors = [];
    private int[] rowOf = [];
    private int jobCount;
    private long previousTotalBytes;
    private DateTime previousTick;

    public DatManagerDialog(TraceLog trace, string? root = null)
    {
        this.trace = trace;
        this.root = root;
        Text = "Bases de datos de ROMs";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        ClientSize = new Size(640, 490);
        CancelButton = close;

        var header = new Label
        {
            Text = "Las ROMs se identifican con los DAT de los orígenes marcados. No-Intro, Redump, TOSEC, MAME y FBNeo se descargan del repositorio libretro-database de GitHub; cualquier otro DAT (GoodTools, clrmamepro, emuladores…) se importa.",
            Location = new Point(10, 8), Size = new Size(620, 36), AutoSize = false
        };
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false;
        grid.AllowUserToResizeColumns = false; grid.RowHeadersVisible = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = true;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing; grid.ColumnHeadersHeight = 22;
        RaUi.StyleGrid(grid, 22, 24);
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "check", HeaderText = "", Width = 30, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Origen / Sistema", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "state", HeaderText = "Estado", Width = 210, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.CellPainting += PaintCheck;
        grid.CellMouseClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 0 && e.Button == MouseButtons.Left && !busy) ToggleRows([e.RowIndex]); };
        grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex == 0 || busy) return;
            if (grid.Rows[e.RowIndex].Tag is string kind) ToggleCollapsed(kind); else ToggleRows([e.RowIndex]);
        };
        grid.CellMouseDown += (_, e) =>
        {
            // Clic derecho sobre una fila que no estaba seleccionada: pasa a ser la selección.
            if (e.Button != MouseButtons.Right || e.RowIndex < 0 || grid.Rows[e.RowIndex].Selected) return;
            grid.ClearSelection(); grid.Rows[e.RowIndex].Selected = true;
        };
        var enableItem = new ToolStripMenuItem("Activar seleccionadas");
        var disableItem = new ToolStripMenuItem("Desactivar seleccionadas");
        rowMenu.Items.AddRange([enableItem, disableItem]);
        enableItem.Click += (_, _) => SetSelected(true);
        disableItem.Click += (_, _) => SetSelected(false);
        rowMenu.Opening += (_, e) => { if (busy || grid.SelectedRows.Count == 0) e.Cancel = true; };
        grid.ContextMenuStrip = rowMenu;
        grid.SelectionChanged += (_, _) => UpdateButtons();
        progress.Location = new Point(10, 388); progress.Size = new Size(620, 10); // fina, como en los demás formularios
        info.Location = new Point(10, 406); info.Size = new Size(620, 22);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 52, Padding = new Padding(8, 10, 8, 2), BackColor = RaUi.FooterBack };
        buttons.Paint += (_, e) => { using var pen = new Pen(RaUi.Border); e.Graphics.DrawLine(pen, 0, 0, buttons.Width, 0); };
        foreach (var button in new[] { close, removeButton, importButton, updateAll, downloadMissing }) { button.MinimumSize = new Size(button.MinimumSize.Width, 32); RaUi.StyleButton(button, button == downloadMissing); }
        buttons.Controls.Add(close);
        buttons.Controls.Add(removeButton);
        buttons.Controls.Add(importButton);
        buttons.Controls.Add(updateAll);
        buttons.Controls.Add(downloadMissing);
        var gridFrame = RaUi.Frame(grid, new Rectangle(10, 52, 620, 324));
        Controls.Add(header); Controls.Add(gridFrame); Controls.Add(progress); Controls.Add(info); Controls.Add(buttons);
        // Cabecera oscura como en los demás formularios; el texto explicativo pasa a ser su subtítulo.
        var banner = new RaHeader("Bases de datos de ROMs", "Los DAT identifican tus ROMs: se descargan de libretro-database o se importan.", 68, 48, logo: false);
        banner.Icon.Image = RaUi.DatabaseIcon();
        header.Location = new Point(10, 10); header.Size = new Size(620, 36); header.ForeColor = RaUi.Muted;
        RaUi.WrapWithHeader(this, banner);

        downloadMissing.Click += async (_, _) => await DownloadAsync(onlyMissing: true);
        updateAll.Click += async (_, _) => await DownloadAsync(onlyMissing: false);
        importButton.Click += (_, _) => ImportFiles();
        removeButton.Click += (_, _) => RemoveSelected();
        timer.Tick += (_, _) => RefreshProgress();
        FormClosing += (_, e) => { if (busy && !CancelConfirm.Ask(this, "la descarga de bases de datos")) { e.Cancel = true; return; } cts?.Cancel(); timer.Stop(); };
        Rebuild();
    }

    private bool Enabled(DatSource source) => settings.IsEnabled(source);

    // Vuelve a cargar la lista de bases de datos (tras importar o quitar una): una fila de grupo por clase y debajo sus DAT.
    private void Rebuild()
    {
        loading = true;
        shown = RomDatabase.Sources.ToList();
        grid.Rows.Clear(); rowIndex.Clear();
        foreach (var (kind, _) in Kinds)
        {
            var sources = shown.Where(s => s.Kind == kind).ToList();
            if (sources.Count == 0) continue;
            int g = grid.Rows.Add("", "", "");
            grid.Rows[g].Tag = kind;
            grid.Rows[g].DefaultCellStyle.Font = new Font(grid.Font, FontStyle.Bold);
            grid.Rows[g].DefaultCellStyle.BackColor = Color.FromArgb(238, 240, 244);
            foreach (var source in sources)
            {
                int i = grid.Rows.Add("", "      " + source.Name, "");
                grid.Rows[i].Tag = source;
                grid.Rows[i].Visible = !collapsed.Contains(kind);
                rowIndex[source.Key] = i;
            }
        }
        loading = false;
        RefreshStates();
    }

    private void RefreshStates()
    {
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.Tag is DatSource source)
            {
                string path = RomDatabase.PathOf(source);
                string state = File.Exists(path)
                    ? (source.Imported ? "Importada " : "Descargada ") + File.GetLastWriteTime(path).ToString("dd/MM/yyyy")
                    : source.Imported ? "Archivo no encontrado" : "Pendiente";
                if (!Enabled(source)) state += " · desactivada";
                row.Cells[2].Value = state;
                row.DefaultCellStyle.ForeColor = Enabled(source) ? SystemColors.WindowText : SystemColors.GrayText;
            }
            else if (row.Tag is string kind)
            {
                var all = shown.Where(s => s.Kind == kind).ToList();
                int on = all.Count(Enabled);
                string label = Kinds.First(k => k.Kind == kind).Label;
                row.Cells[1].Value = $"{(collapsed.Contains(kind) ? "▸" : "▾")} {Loc.T(label)} ({Loc.T("{0} de {1} en uso", on, all.Count)})";
            }
        }
        int usable = shown.Count(s => Enabled(s) && RomDatabase.IsInstalled(s));
        info.Text = $"{usable} bases de datos en uso de {shown.Count}.";
        grid.Invalidate();
        UpdateButtons();
    }

    // Casilla de cada fila: marcada, vacía o a medias (en las filas de clase cuando solo algunos de sus DAT están activos).
    private void PaintCheck(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
        var unchecked_ = System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal;
        var checked_ = System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal;
        System.Windows.Forms.VisualStyles.CheckBoxState? state = null;
        var row = grid.Rows[e.RowIndex];
        if (row.Tag is DatSource source) state = Enabled(source) ? checked_ : unchecked_;
        else if (row.Tag is string kind)
        {
            var all = shown.Where(s => s.Kind == kind).ToList();
            int on = all.Count(Enabled);
            state = on == 0 ? unchecked_ : on == all.Count ? checked_ : System.Windows.Forms.VisualStyles.CheckBoxState.MixedNormal;
        }
        if (state is null) return;
        e.Paint(e.CellBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border);
        var size = CheckBoxRenderer.GetGlyphSize(e.Graphics!, state.Value);
        CheckBoxRenderer.DrawCheckBox(e.Graphics!, new Point(e.CellBounds.X + (e.CellBounds.Width - size.Width) / 2, e.CellBounds.Y + (e.CellBounds.Height - size.Height) / 2), state.Value);
        e.Handled = true;
    }

    // DAT afectados por un conjunto de filas: los de las filas sueltas y todos los de las filas de clase.
    private List<DatSource> SourcesOf(IEnumerable<int> rows)
    {
        var result = new List<DatSource>();
        foreach (int r in rows)
        {
            if (grid.Rows[r].Tag is DatSource s) result.Add(s);
            else if (grid.Rows[r].Tag is string kind) result.AddRange(shown.Where(x => x.Kind == kind));
        }
        return result.Distinct().ToList();
    }

    // Clic en la casilla o doble clic: si todos los afectados están activos se desactivan; si no, se activan (una clase a medias pasa a completa).
    private void ToggleRows(IEnumerable<int> rows)
    {
        var sources = SourcesOf(rows);
        if (sources.Count == 0) return;
        ApplyEnabled(sources, !sources.All(Enabled));
    }

    private void SetSelected(bool enabled) => ApplyEnabled(SourcesOf(grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index)), enabled);

    private void ApplyEnabled(List<DatSource> sources, bool enabled)
    {
        if (loading || sources.Count == 0) return;
        foreach (var source in sources) settings.SetEnabled(source, enabled);
        settings.Save();
        RomDatabase.Invalidate();
        RefreshStates();
    }

    private void ToggleCollapsed(string kind)
    {
        if (!collapsed.Remove(kind)) collapsed.Add(kind);
        grid.CurrentCell = null;
        foreach (var source in shown.Where(s => s.Kind == kind)) grid.Rows[rowIndex[source.Key]].Visible = !collapsed.Contains(kind);
        RefreshStates();
    }

    private void UpdateButtons()
    {
        var downloadable = shown.Where(s => !s.Imported && Enabled(s)).ToList();
        downloadMissing.Enabled = !busy && downloadable.Any(s => !RomDatabase.IsInstalled(s));
        updateAll.Enabled = !busy && downloadable.Any(RomDatabase.IsInstalled);
        importButton.Enabled = !busy;
        removeButton.Enabled = !busy && grid.CurrentRow?.Tag is DatSource { Imported: true };
    }

    // ---- Importar y quitar

    private void ImportFiles()
    {
        using var open = new OpenFileDialog { Title = Loc.T("Elige los DAT a importar"), Filter = Loc.T("DAT (*.dat;*.xml)|*.dat;*.xml|Todos los archivos|*.*"), Multiselect = true };
        if (open.ShowDialog(this) != DialogResult.OK) return;
        string romRoot = root is null ? "" : Path.Combine(root, "Roms");
        var existing = Directory.Exists(romRoot)
            ? Directory.EnumerateDirectories(romRoot).Select(d => Path.GetFileName(d)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        int imported = 0;
        foreach (var file in open.FileNames)
        {
            try
            {
                var (name, arcade) = RomDatabase.Peek(file);
                using var dialog = new ImportDatDialog(file, name, arcade, existing);
                if (dialog.ShowDialog(this) != DialogResult.OK) continue;
                RomDatabase.Import(file, dialog.DatName, dialog.Kind, dialog.Folders, dialog.NameMatch);
                // Una base importada de nuevo se usa desde el principio, aunque antes se hubiera desactivado una con el mismo nombre.
                var again = RomDatabase.Sources.FirstOrDefault(x => x.Imported && x.Name == dialog.DatName && x.Kind == dialog.Kind);
                if (again is not null && !settings.IsEnabled(again)) { settings.SetEnabled(again, true); settings.Save(); RomDatabase.Invalidate(); }
                trace.Add("dat_imported", $"name={dialog.DatName}; kind={dialog.Kind}; folders={string.Join("|", dialog.Folders)}; by_name={dialog.NameMatch}");
                imported++;
            }
            catch (Exception ex) { QuietMessage.Show(this, "No se pudo importar", $"{Path.GetFileName(file)}\r\n\r\n{ex.Message}"); }
        }
        if (imported > 0) Rebuild();
    }

    private void RemoveSelected()
    {
        if (grid.CurrentRow?.Tag is not DatSource { Imported: true } source) return;
        if (!QuietMessage.Confirm(this, "Quitar base de datos", $"¿Quitar «{source.Name}» de la lista? Solo se borra la copia que guarda la aplicación, no tu archivo original.", "Quitar")) return;
        RomDatabase.RemoveImport(source);
        trace.Add("dat_removed", source.Name);
        Rebuild();
    }

    // ---- Descarga

    private static string Speed(double bytesPerSecond) =>
        bytesPerSecond >= 1024 * 1024 ? $"{bytesPerSecond / 1024 / 1024:0.0} MB/s" : $"{bytesPerSecond / 1024:0} KB/s";

    private static string Bytes(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024:0.0} MB" : $"{bytes / 1024.0:0} KB";

    // Refresca la barra, la velocidad global y las filas que se están descargando.
    private void RefreshProgress()
    {
        if (!busy || jobCount == 0) return;
        var now = DateTime.UtcNow;
        double seconds = Math.Max(0.1, (now - previousTick).TotalSeconds);
        long totalBytes = 0; int done = 0, running = 0; double fraction = 0;
        for (int i = 0; i < jobCount; i++)
        {
            long got = Interlocked.Read(ref received[i]);
            long? total = Interlocked.Read(ref totals[i]) is var t and > 0 ? t : null;
            totalBytes += got;
            if (outcome[i] != 0) { done++; fraction += 1; continue; }
            if (!active[i]) continue;
            running++;
            double part = total is > 0 ? Math.Min(1.0, (double)got / total.Value) : 0;
            fraction += part;
            double speed = (got - lastSample[i]) / seconds;
            lastSample[i] = got;
            grid.Rows[rowOf[i]].Cells[2].Value = total is > 0 ? $"Descargando {part:P0} · {Speed(speed)}" : $"Descargando {Bytes(got)} · {Speed(speed)}";
        }
        double overallSpeed = (totalBytes - previousTotalBytes) / seconds;
        previousTotalBytes = totalBytes; previousTick = now;
        progress.Value = Math.Clamp((int)(fraction / jobCount * 1000), 0, 1000);
        info.Text = $"{fraction / jobCount:P0} · {done}/{jobCount} terminadas · {running} en curso · {Speed(overallSpeed)} · {Bytes(totalBytes)} descargados";
    }

    private async Task DownloadAsync(bool onlyMissing)
    {
        var list = shown.Where(s => !s.Imported && Enabled(s) && (!onlyMissing || !RomDatabase.IsInstalled(s))).ToList();
        if (list.Count == 0) return;
        if (!await Requirements.CheckAsync(this, trace, "Descargar bases de datos", Need.Internet, "raw.githubusercontent.com")) return;

        jobCount = list.Count;
        received = new long[jobCount]; totals = new long[jobCount]; lastSample = new long[jobCount];
        active = new bool[jobCount]; outcome = new int[jobCount]; errors = new string?[jobCount];
        rowOf = list.Select(s => rowIndex[s.Key]).ToArray();
        previousTotalBytes = 0; previousTick = DateTime.UtcNow;
        foreach (int row in rowOf) grid.Rows[row].Cells[2].Value = "En cola";
        progress.Value = 0;

        busy = true; cts = new CancellationTokenSource();
        UpdateButtons();
        timer.Start();
        var gate = new SemaphoreSlim(Parallel);
        var token = cts.Token;
        var tasks = list.Select((source, i) => Task.Run(async () =>
        {
            try { await gate.WaitAsync(token); } catch (OperationCanceledException) { return; }
            try
            {
                active[i] = true;
                await RomDatabase.DownloadAsync(source, token, (got, total) =>
                {
                    Interlocked.Exchange(ref received[i], got);
                    if (total is long t) Interlocked.Exchange(ref totals[i], t);
                });
                outcome[i] = 1;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { errors[i] = ex.Message; outcome[i] = 2; }
            finally { active[i] = false; gate.Release(); }
        }, token)).ToList();

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
        finally { busy = false; timer.Stop(); }
        if (IsDisposed) return; // se cerró la ventana durante la descarga

        int ok = outcome.Count(o => o == 1), failed = outcome.Count(o => o == 2);
        trace.Add("dat_downloaded", $"ok={ok}; failed={failed}");
        progress.Value = 0;
        RefreshStates();
        for (int i = 0; i < jobCount; i++)
            if (outcome[i] == 2) grid.Rows[rowOf[i]].Cells[2].Value = "Error: " + errors[i];
        info.Text += failed > 0 ? $"  ·  {failed} con error." : "";
        if (token.IsCancellationRequested) info.Text += "  ·  ⚠ Descarga cancelada antes de terminar: faltan bases por descargar.";
    }
}
