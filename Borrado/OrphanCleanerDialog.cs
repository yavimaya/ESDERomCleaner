using System.Text;

namespace ESDERomCleaner;

// «Limpiar huérfanos»: busca medios, entradas de gamelist y carpetas de medios sin correspondencia con una ROM real y borra lo que se marque.
// Un gamelist que se queda sin juegos se elimina entero (lo hace OrphanService). Para borrar hay que escribir BORRAR.
internal sealed class OrphanCleanerDialog : LocalizedForm
{
    private readonly string root;
    private readonly TraceLog trace;
    private readonly bool backups;
    private readonly Func<long, string> formatSize;
    private readonly ComboBox scope = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly Button searchButton = new() { Text = "Buscar huérfanos", AutoSize = true, MinimumSize = new Size(140, 28) };
    private readonly DataGridView list = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, ScrollBars = ScrollBars.None, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
    };

    private IEnumerable<DataGridViewRow> Rows => list.Rows.Cast<DataGridViewRow>();
    private static bool IsChecked(DataGridViewRow row) => row.Cells[0].Value is true;
    // Barra animada mientras se busca o se borra (no se sabe de antemano cuánto falta); fuera de eso queda oculta.
    private readonly ProgressBar progressBar = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Visible = false, Margin = new Padding(0, 4, 0, 4) };
    private readonly Label statusLabel = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button deleteButton = new() { Text = "Eliminar marcados…", AutoSize = true, MinimumSize = new Size(170, 30), ForeColor = Color.Firebrick };
    private readonly Button closeButton = new() { Text = "Cerrar", AutoSize = true, MinimumSize = new Size(100, 30), DialogResult = DialogResult.Cancel };
    private CancellationTokenSource? cts;
    private OrphanScan? scan;
    private bool busy;

    // true si se borró algo: la ventana principal debe recalcular tamaños y contadores.
    public bool Changed { get; private set; }

    public OrphanCleanerDialog(string rootPath, TraceLog trace, bool gamelistBackups, Func<long, string> formatSize)
    {
        root = rootPath; this.trace = trace; backups = gamelistBackups; this.formatSize = formatSize;
        Text = "Limpiar huérfanos";
        // Alto fijo: 10 + 36 (selector) + 108 (tabla) + 24 + 24 (aviso y estado) + 40 (botones) + 8; solo se puede cambiar el ancho.
        ClientSize = new Size(660, 266); MinimumSize = new Size(560, 250 + (Height - ClientSize.Height)); MaximumSize = new Size(1600, Height);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        CancelButton = closeButton;

        scope.Items.Add(Loc.T("Todos los sistemas"));
        foreach (string name in OrphanService.SystemNames(rootPath)) scope.Items.Add(name);
        scope.SelectedIndex = 0;

        // Mismo aspecto que las demás tablas: cabecera oscura, filas alternas y el alto de cabecera y filas coordinado.
        RaUi.StyleGrid(list, 28, 22);
        const DataGridViewAutoSizeColumnMode fill = DataGridViewAutoSizeColumnMode.Fill;
        list.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "", Width = 34, SortMode = DataGridViewColumnSortMode.NotSortable });
        list.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Qué se borraría", AutoSizeMode = fill, FillWeight = 52, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        list.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Cantidad", AutoSizeMode = fill, FillWeight = 33, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        list.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Espacio", AutoSizeMode = fill, FillWeight = 15, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } });

        // Sin Dock y con Anchor ninguno, la celda de la tabla centra el conjunto horizontalmente.
        // El desplegable mide lo que su elemento más largo y, junto con el botón, se centra; la etiqueta se queda a la izquierda.
        int widest = scope.Items.Cast<string>().Max(item => TextRenderer.MeasureText(item, scope.Font).Width);
        scope.Width = widest + SystemInformation.VerticalScrollBarWidth + 14;
        var group = new FlowLayoutPanel { Anchor = AnchorStyles.None, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        // La etiqueta va pegada al desplegable y los tres se centran juntos.
        group.Controls.Add(new Label { Text = "Sistemas:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        group.Controls.Add(scope);
        group.Controls.Add(searchButton);
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Margin = Padding.Empty };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        top.Controls.Add(group, 0, 0);

        var hint = new Label { Text = "Doble clic en una fila para ver la lista. Un gamelist que se quede sin juegos se elimina entero.", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(deleteButton);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(12, 10, 12, 8) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22 + 3 * 28 + 2)); // cabecera + tres filas fijas: caben todas sin barra de desplazamiento
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 16)); // barra de progreso: el hueco se reserva siempre para que el formulario no cambie de alto
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.Controls.Add(top, 0, 0);
        layout.Controls.Add(list, 0, 1);
        layout.Controls.Add(hint, 0, 2);
        layout.Controls.Add(statusLabel, 0, 3);
        layout.Controls.Add(progressBar, 0, 4);
        layout.Controls.Add(buttons, 0, 5);
        Controls.Add(layout);

        searchButton.Click += async (_, _) => await SearchAsync();
        deleteButton.Click += async (_, _) => await DeleteAsync();
        // Doble clic (también sobre la casilla) o clic derecho › «Ver lista…» abren la lista de lo que hay en esa fila.
        list.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ShowList(list.Rows[e.RowIndex]); };
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        var view = new ToolStripMenuItem("Ver lista…");
        view.Click += (_, _) => ShowList(list.CurrentRow);
        menu.Items.Add(view);
        menu.Opening += (_, e) => { if (scan is null || list.CurrentRow is null || !list.CurrentRow.Selected) e.Cancel = true; };
        list.ContextMenuStrip = menu;
        list.CellMouseDown += (_, e) => { if (e.Button == MouseButtons.Right && e.RowIndex >= 0) { list.ClearSelection(); list.Rows[e.RowIndex].Selected = true; list.CurrentCell = list.Rows[e.RowIndex].Cells[1]; } };
        list.CurrentCellDirtyStateChanged += (_, _) => { if (list.IsCurrentCellDirty) list.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        // El botón mide lo mismo que el desplegable.
        searchButton.AutoSize = false;
        searchButton.MinimumSize = Size.Empty; // el MinimumSize de 28 px de alto impedía igualarlo al desplegable
        // Misma altura que el desplegable, también si cambia con la fuente o el DPI.
        void SyncHeight() { searchButton.Margin = scope.Margin; searchButton.Size = new Size(140, scope.Height); }
        scope.SizeChanged += (_, _) => SyncHeight();
        Load += (_, _) => SyncHeight();
        Shown += (_, _) => SyncHeight();
        list.CellValueChanged += (_, e) => { if (e.ColumnIndex == 0) UpdateButtons(); };
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; return; } cts?.Cancel(); };
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        progressBar.Visible = busy;
        searchButton.Enabled = !busy;
        scope.Enabled = !busy;
        deleteButton.Enabled = !busy && scan is not null && Rows.Any(IsChecked);
    }

    private async Task SearchAsync()
    {
        if (busy) return;
        busy = true; UpdateButtons();
        list.Rows.Clear(); scan = null;
        cts = new CancellationTokenSource();
        var token = cts.Token;
        var systems = scope.SelectedIndex <= 0 ? OrphanService.SystemNames(root) : [(string)scope.SelectedItem!];
        var progress = new Progress<string>(text => statusLabel.Text = Loc.T(text));
        Cursor = Cursors.WaitCursor;
        try
        {
            scan = await Task.Run(() => OrphanService.Scan(root, systems, progress, token), token);
            FillList(scan);
            statusLabel.Text = Loc.T(Rows.All(r => !IsChecked(r) && (string?)r.Cells[2].Value == "0") ? "No se encontró ningún huérfano." : "Búsqueda terminada.");
            trace.Add("orphans_scan_finished", $"systems={systems.Count}; media={scan.Media.Count}; entries={scan.EntryCount}; gamelists_whole={scan.WholeGamelists}; empty_media_folders={scan.EmptySystemMediaFolders.Count}");
        }
        catch (OperationCanceledException) { statusLabel.Text = Loc.T("Búsqueda cancelada."); }
        catch (Exception ex) { scan = null; statusLabel.Text = ex.Message; }
        finally { Cursor = Cursors.Default; busy = false; UpdateButtons(); }
    }

    private void FillList(OrphanScan result)
    {
        list.Rows.Clear();
        long mediaBytes = result.Media.Sum(f => f.Size), emptyBytes = result.EmptySystemMedia.Sum(f => f.Size);
        void Add(bool check, string name, string amount, string size, string tag) => list.Rows[list.Rows.Add(check, name, amount, size)].Tag = tag;
        Add(result.Media.Count > 0, Loc.T("Medios sin ROM (miximages, vídeos…)"), result.Media.Count == 0 ? "0" : Loc.T($"{result.Media.Count:N0} archivos"), formatSize(mediaBytes), "media");
        string entries = result.WholeGamelists > 0 ? Loc.T($"{result.EntryCount:N0} entradas · {result.WholeGamelists:N0} gamelists enteros") : Loc.T($"{result.EntryCount:N0} entradas");
        Add(result.Gamelists.Count > 0, Loc.T("Entradas <game> sin ROM"), result.Gamelists.Count == 0 ? "0" : entries, "", "entries");
        Add(false, Loc.T("Carpetas de medios de sistemas sin ROMs"), result.EmptySystemMediaFolders.Count == 0 ? "0" : Loc.T($"{result.EmptySystemMediaFolders.Count:N0} carpetas · {result.EmptySystemMedia.Count:N0} archivos"), formatSize(emptyBytes), "folders");
        list.ClearSelection();
        UpdateButtons();
    }

    private void ShowList(DataGridViewRow? item)
    {
        if (scan is null || item is null) return;
        var lines = new List<string>();
        string Rel(string p) => Path.GetRelativePath(root, p);
        switch (item.Tag as string)
        {
            case "media": lines.AddRange(scan.Media.Select(f => Rel(f.Path))); break;
            case "folders":
                foreach (string folder in scan.EmptySystemMediaFolders) lines.Add(Rel(folder));
                break;
            case "entries":
                foreach (var g in scan.Gamelists)
                {
                    lines.Add((g.WholeFile ? "[GAMELIST ENTERO] " : "") + Rel(g.Path) + $"  ({g.OrphanCount:N0}/{g.TotalGames:N0})");
                    lines.AddRange(g.OrphanKeys.Take(500).Select(k => "    " + k));
                }
                break;
        }
        using var form = new LocalizedForm { Text = "Lista de huérfanos", Size = new Size(760, 520), StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false };
        var box = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9f) };
        box.Text = string.Join("\r\n", lines.Take(20000)) + (lines.Count > 20000 ? $"\r\n… {lines.Count - 20000:N0}" : "");
        var close = new Button { Text = "Cerrar", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(100, 30) };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty, Padding = new Padding(8, 5, 8, 0) };
        bar.Controls.Add(close);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(box, 0, 0);
        layout.Controls.Add(bar, 0, 1);
        form.Controls.Add(layout);
        form.CancelButton = close; form.AcceptButton = close;
        form.ShowDialog(this);
    }

    // Confirmación con el mismo formulario que el borrado de ROMs (cabecera roja, tarjetas con las cifras, «BORRAR» y «Cancelar» por defecto).
    private bool ConfirmDelete(bool media, bool entries, bool folders)
    {
        if (scan is null) return false;
        long mediaBytes = media ? scan.Media.Sum(f => f.Size) : 0, folderBytes = folders ? scan.EmptySystemMedia.Sum(f => f.Size) : 0;
        var tiles = new (string Number, string Name, string Size)[]
        {
            ((media ? scan.Media.Count : 0).ToString("N0"), "Medios sin ROM", formatSize(mediaBytes)),
            ((entries ? scan.EntryCount : 0).ToString("N0"), "Entradas <game>", ""),
            ((entries ? scan.WholeGamelists : 0).ToString("N0"), "Gamelists enteros", ""),
            ((folders ? scan.EmptySystemMediaFolders.Count : 0).ToString("N0"), "Carpetas de medios", formatSize(folderBytes))
        };
        var data = new DeleteConfirmData(0, "", 0, "", 0, "", 0, formatSize(mediaBytes + folderBytes), backups);
        using var confirm = new DeleteConfirmDialog(data, tilesOverride: tiles);
        return confirm.ShowDialog(this) == DialogResult.OK;
    }

    private async Task DeleteAsync()
    {
        if (busy || scan is null) return;
        bool media = Rows.Any(r => IsChecked(r) && (string?)r.Tag == "media");
        bool entries = Rows.Any(r => IsChecked(r) && (string?)r.Tag == "entries");
        bool folders = Rows.Any(r => IsChecked(r) && (string?)r.Tag == "folders");
        if (!media && !entries && !folders) return;
        if (!ConfirmDelete(media, entries, folders)) { statusLabel.Text = Loc.T("Borrado cancelado; no se modificó ningún archivo."); return; }

        busy = true; UpdateButtons();
        trace.Add("orphans_delete_started", $"media={(media ? scan.Media.Count : 0)}; entries={(entries ? scan.EntryCount : 0)}; gamelists_whole={(entries ? scan.WholeGamelists : 0)}; empty_media_folders={(folders ? scan.EmptySystemMediaFolders.Count : 0)}");
        var progress = new Progress<string>(text => statusLabel.Text = text);
        var current = scan;
        OrphanResult result;
        Cursor = Cursors.WaitCursor;
        try { result = await Task.Run(() => OrphanService.Execute(current, media, entries, folders, backups, progress)); }
        catch (Exception ex)
        {
            trace.Add("orphans_delete_error", ex.ToString());
            Cursor = Cursors.Default; busy = false; UpdateButtons();
            QuietMessage.Show(this, "Limpiar huérfanos", ex.Message);
            return;
        }
        Cursor = Cursors.Default;
        trace.Add("orphans_delete_finished", $"media={result.MediaDeleted}; bytes={result.MediaBytes}; entries={result.EntriesRemoved}; gamelists={result.GamelistsDeleted}; folders={result.FoldersDeleted}; folder_files={result.FolderFiles}; errors={result.Errors.Count}");
        Changed = true;
        busy = false;
        var text = new StringBuilder(Loc.T($"Huérfanos eliminados: {result.MediaDeleted + result.FolderFiles:N0} archivos de medios ({formatSize(result.MediaBytes + result.FolderBytes)}), {result.EntriesRemoved:N0} entradas, {result.GamelistsDeleted:N0} gamelists enteros."));
        if (result.Errors.Count > 0) text.Append("\r\n\r\n").Append(string.Join("\r\n", result.Errors.Take(10)));
        QuietMessage.Show(this, "Limpiar huérfanos", text.ToString());
        await SearchAsync(); // vuelve a buscar: lo que quede se ve al momento
    }
}
