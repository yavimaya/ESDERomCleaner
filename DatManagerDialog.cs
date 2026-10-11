namespace ESDERomCleaner;

// Gestión de las bases de datos (DAT) con las que se identifican las ROMs: elegir qué orígenes se usan, descargar las de
// No-Intro y Redump (libretro-database) e importar las propias (GoodTools, clrmamepro, MAME, FBNeo, emuladores…).
internal sealed class DatManagerDialog : Form
{
    private const int Parallel = 4;

    private readonly DataGridView grid = new();
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 1000 };
    private readonly Label info = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Dictionary<string, CheckBox> kindChecks = new();
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
        var useLabel = new Label { Text = "Usar:", Location = new Point(10, 52), Size = new Size(40, 22), TextAlign = ContentAlignment.MiddleLeft };
        int x = 52;
        foreach (var (kind, text, width) in new[] { ("no-intro", "No-Intro", 85), ("redump", "Redump", 78), ("tosec", "TOSEC", 70), ("mame", "MAME", 70), ("fbneo", "FBNeo", 72), ("other", "Otras (importadas)", 150) })
        {
            var check = new CheckBox { Text = text, Location = new Point(x, 52), Size = new Size(width, 22), Checked = settings.Kinds.Contains(kind) };
            check.CheckedChanged += (_, _) => KindChanged(kind, check.Checked);
            kindChecks[kind] = check;
            Controls.Add(check);
            x += width;
        }

        grid.Location = new Point(10, 80); grid.Size = new Size(620, 296);
        grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false;
        grid.AllowUserToResizeColumns = false; grid.RowHeadersVisible = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false; grid.BackgroundColor = SystemColors.Window; grid.BorderStyle = BorderStyle.FixedSingle;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing; grid.ColumnHeadersHeight = 22;
        grid.RowTemplate.Height = 20;
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Origen", Width = 80, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Sistema", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Estado", Width = 210, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.SelectionChanged += (_, _) => UpdateButtons();
        progress.Location = new Point(10, 384); progress.Size = new Size(620, 18);
        info.Location = new Point(10, 406); info.Size = new Size(620, 22);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(6, 8, 6, 2) };
        buttons.Controls.Add(close);
        buttons.Controls.Add(removeButton);
        buttons.Controls.Add(importButton);
        buttons.Controls.Add(updateAll);
        buttons.Controls.Add(downloadMissing);
        Controls.Add(header); Controls.Add(useLabel); Controls.Add(grid); Controls.Add(progress); Controls.Add(info); Controls.Add(buttons);

        downloadMissing.Click += async (_, _) => await DownloadAsync(onlyMissing: true);
        updateAll.Click += async (_, _) => await DownloadAsync(onlyMissing: false);
        importButton.Click += (_, _) => ImportFiles();
        removeButton.Click += (_, _) => RemoveSelected();
        timer.Tick += (_, _) => RefreshProgress();
        FormClosing += (_, _) => { cts?.Cancel(); timer.Stop(); };
        Rebuild();
    }

    private void KindChanged(string kind, bool on)
    {
        if (loading) return;
        if (on && !settings.Kinds.Contains(kind)) settings.Kinds.Add(kind);
        if (!on) settings.Kinds.Remove(kind);
        settings.Save();
        RomDatabase.Invalidate();
        RefreshStates();
    }

    // Vuelve a cargar la lista de bases de datos (tras importar o quitar una).
    private void Rebuild()
    {
        loading = true;
        shown = RomDatabase.Sources.ToList();
        grid.Rows.Clear();
        foreach (var source in shown) grid.Rows.Add(source.KindLabel, source.Name, "");
        loading = false;
        RefreshStates();
    }

    private void RefreshStates()
    {
        for (int i = 0; i < shown.Count; i++)
        {
            var source = shown[i];
            string path = RomDatabase.PathOf(source);
            string state = File.Exists(path)
                ? (source.Imported ? "Importada " : "Descargada ") + File.GetLastWriteTime(path).ToString("dd/MM/yyyy")
                : source.Imported ? "Archivo no encontrado" : "Pendiente";
            if (!settings.Kinds.Contains(source.Kind)) state += " · desactivada";
            grid.Rows[i].Cells[2].Value = state;
            grid.Rows[i].DefaultCellStyle.ForeColor = settings.Kinds.Contains(source.Kind) ? SystemColors.WindowText : SystemColors.GrayText;
        }
        int usable = shown.Count(s => settings.Kinds.Contains(s.Kind) && RomDatabase.IsInstalled(s));
        info.Text = $"{usable} bases de datos en uso de {shown.Count}.";
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var downloadable = shown.Where(s => !s.Imported && settings.Kinds.Contains(s.Kind)).ToList();
        downloadMissing.Enabled = !busy && downloadable.Any(s => !RomDatabase.IsInstalled(s));
        updateAll.Enabled = !busy && downloadable.Any(RomDatabase.IsInstalled);
        importButton.Enabled = !busy;
        int row = grid.CurrentRow?.Index ?? -1;
        removeButton.Enabled = !busy && row >= 0 && row < shown.Count && shown[row].Imported;
    }

    // ---- Importar y quitar

    private void ImportFiles()
    {
        using var open = new OpenFileDialog { Title = "Elige los DAT a importar", Filter = "DAT (*.dat;*.xml)|*.dat;*.xml|Todos los archivos|*.*", Multiselect = true };
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
                // Si el origen elegido está desactivado, se activa para que la base importada se use.
                if (!settings.Kinds.Contains(dialog.Kind)) { settings.Kinds.Add(dialog.Kind); settings.Save(); loading = true; kindChecks[dialog.Kind].Checked = true; loading = false; }
                trace.Add("dat_imported", $"name={dialog.DatName}; kind={dialog.Kind}; folders={string.Join("|", dialog.Folders)}; by_name={dialog.NameMatch}");
                imported++;
            }
            catch (Exception ex) { QuietMessage.Show(this, "No se pudo importar", $"{Path.GetFileName(file)}\r\n\r\n{ex.Message}"); }
        }
        if (imported > 0) Rebuild();
    }

    private void RemoveSelected()
    {
        int row = grid.CurrentRow?.Index ?? -1;
        if (row < 0 || row >= shown.Count || !shown[row].Imported) return;
        var source = shown[row];
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
        var list = shown.Where(s => !s.Imported && settings.Kinds.Contains(s.Kind) && (!onlyMissing || !RomDatabase.IsInstalled(s))).ToList();
        if (list.Count == 0) return;

        jobCount = list.Count;
        received = new long[jobCount]; totals = new long[jobCount]; lastSample = new long[jobCount];
        active = new bool[jobCount]; outcome = new int[jobCount]; errors = new string?[jobCount];
        rowOf = list.Select(s => shown.IndexOf(s)).ToArray();
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
        if (token.IsCancellationRequested) info.Text += "  ·  Descarga cancelada.";
    }
}
