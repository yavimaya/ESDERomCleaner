namespace ESDERomCleaner;

// Lista de las ROMs seleccionadas, ordenada por sistema y con el mismo aspecto que la lista de logros: filas con fondos alternos, icono de cada
// sistema y casilla para quitar de la selección las que no se quieran. Las filas no desaparecen al desmarcarlas (se pueden volver a marcar, también con
// doble clic); los cambios se aplican al momento y el formulario solo se puede cerrar.
internal sealed class SelectedRomsDialog : LocalizedForm
{
    private readonly DataGridView grid = new();
    private readonly Label summary = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Tag = "noloc" };
    private readonly Func<long, string> formatSize;
    private readonly Action<RomRow, bool> onToggle;
    private Dictionary<string, long>? mediaSizes;   // tamaño de los medios de cada ROM; null mientras se calcula
    private readonly List<RomRow> rows;
    private readonly string root;
    private readonly CancellationTokenSource cts = new();

    public SelectedRomsDialog(string root, IReadOnlyList<RomRow> selectedRows, Func<long, string> formatSize, Action<RomRow, bool> onToggle, string? subtitle = null)
    {
        this.root = root; this.formatSize = formatSize; this.onToggle = onToggle;
        rows = selectedRows.OrderBy(r => r.System, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Display, StringComparer.OrdinalIgnoreCase).ToList();
        Text = "ROMs seleccionadas";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false; MinimizeBox = false;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        BackColor = RaUi.Body;
        ClientSize = new Size(980, 600); MinimumSize = new Size(720, 380);

        // Es el mismo formulario desde la ventana principal y desde la confirmación del borrado (que solo cambia el subtítulo): cabecera con la papelera.
        var banner = new RaHeader("ROMs seleccionadas", subtitle ?? "Desmarca las ROMs que quieras quitar de la selección; el cambio se aplica al momento.", 68, 48, logo: false);
        banner.Icon.Image = RaUi.TrashIcon();

        grid.Dock = DockStyle.Fill; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = false;
        grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None; grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; grid.GridColor = Color.FromArgb(232, 234, 239);
        grid.RowTemplate.Height = 28; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing; grid.ColumnHeadersHeight = 30;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(52, 56, 68); grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = grid.ColumnHeadersDefaultCellStyle.BackColor; grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.White;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
        // Fondos alternos y sin resaltar la fila enfocada (la selección conserva el color de su fila).
        grid.DefaultCellStyle.BackColor = Color.White; grid.DefaultCellStyle.SelectionBackColor = Color.White; grid.DefaultCellStyle.SelectionForeColor = SystemColors.ControlText;
        grid.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(243, 245, 249); grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = Color.FromArgb(243, 245, 249);
        grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = SystemColors.ControlText;

        var center = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter, Padding = new Padding(0) };
        var right = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight };
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "mark", HeaderText = "", Width = 40, DefaultCellStyle = center });
        grid.Columns.Add(new DataGridViewImageColumn { Name = "icon", HeaderText = "", Width = 40, ImageLayout = DataGridViewImageCellLayout.Zoom, ReadOnly = true, DefaultCellStyle = new DataGridViewCellStyle { Padding = new Padding(3) } });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "system", HeaderText = "Sistema", Width = 150, ReadOnly = true, DefaultCellStyle = new DataGridViewCellStyle { Font = new Font(Font, FontStyle.Bold) } });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Nombre", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 100, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "category", HeaderText = "Categoría", Width = 190, ReadOnly = true, DefaultCellStyle = new DataGridViewCellStyle { ForeColor = Color.FromArgb(70, 76, 90) } });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "size", HeaderText = "Tamaño", Width = 90, ReadOnly = true, DefaultCellStyle = right });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "media", HeaderText = "Medios", Width = 90, ReadOnly = true, DefaultCellStyle = right });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "file", HeaderText = "Archivo", Width = 230, ReadOnly = true, DefaultCellStyle = new DataGridViewCellStyle { ForeColor = RaUi.Muted } });
        foreach (var row in rows)
        {
            int i = grid.Rows.Add(true, PixelSystemIcons.Get(row.System, 32), row.System, row.Display,
                string.IsNullOrWhiteSpace(row.Genre) ? "Sin categoría" : row.Genre, formatSize(row.Size), "…", row.RomName);
            grid.Rows[i].Tag = row;
        }
        // La casilla se confirma al pulsarla (sin esperar a salir de la celda) y cada cambio se aplica a la selección de la ventana principal.
        // Doble clic en cualquier otra celda de la fila marca o desmarca la ROM.
        grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex == grid.Columns["mark"]!.Index) return;
            var cell = grid.Rows[e.RowIndex].Cells["mark"];
            cell.Value = cell.Value is not true;
        };
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != grid.Columns["mark"]!.Index || grid.Rows[e.RowIndex].Tag is not RomRow row) return;
            this.onToggle(row, grid.Rows[e.RowIndex].Cells["mark"].Value is true);
            UpdateSummary();
        };

        var footer = RaUi.Footer();
        var close = RaUi.MakeButton("Cerrar", true); close.DialogResult = DialogResult.Cancel;
        footer.Controls.AddRange([summary, close]);
        // El botón va pegado a la derecha del pie y el resumen ocupa el resto; se recolocan al cambiar el ancho (el pie aún no tiene su ancho final aquí).
        void PlaceFooter() { close.Location = new Point(footer.Width - close.Width - 20, 10); summary.SetBounds(20, 0, Math.Max(100, close.Left - 30), footer.Height); }
        footer.Resize += (_, _) => PlaceFooter();
        Shown += (_, _) => PlaceFooter();
        CancelButton = close; AcceptButton = close;
        Controls.Add(grid); Controls.Add(banner); Controls.Add(footer);
        UpdateSummary();
        // El tamaño de los medios de cada ROM se calcula en segundo plano (hay que recorrer las carpetas de medios) y se rellena al terminar.
        Shown += async (_, _) => await LoadMediaSizesAsync();
        FormClosing += (_, _) => cts.Cancel();
    }

    private async Task LoadMediaSizesAsync()
    {
        Dictionary<string, long>? sizes = null;
        try { sizes = await Task.Run(() => DeletionService.MediaBytesByRom(root, rows, cts.Token), cts.Token); }
        catch (OperationCanceledException) { return; }
        catch { /* sin acceso a los medios: se muestra «—» */ }
        if (IsDisposed) return;
        mediaSizes = sizes ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        UpdateSummary();
        foreach (DataGridViewRow gridRow in grid.Rows)
            if (gridRow.Tag is RomRow row)
                gridRow.Cells["media"].Value = sizes is not null && sizes.TryGetValue(Path.GetFullPath(row.Path), out long bytes) && bytes > 0 ? formatSize(bytes) : "—";
    }

    private void UpdateSummary()
    {
        var marked = grid.Rows.Cast<DataGridViewRow>().Where(r => r.Cells["mark"].Value is true).Select(r => r.Tag).OfType<RomRow>().ToList();
        long romBytes = marked.Sum(r => r.Size);
        long? mediaBytes = mediaSizes is null ? null : marked.Sum(r => mediaSizes.GetValueOrDefault(Path.GetFullPath(r.Path)));
        // Los medios se suman cuando termina su cálculo; hasta entonces se muestra «…».
        summary.Text = Loc.T("{0} de {1} ROMs marcadas · ROMs {2} · Medios {3} · Total {4}", marked.Count.ToString("N0"), rows.Count.ToString("N0"),
            formatSize(romBytes), mediaBytes is { } m ? formatSize(m) : "…", mediaBytes is { } m2 ? formatSize(romBytes + m2) : "…");
    }
}
