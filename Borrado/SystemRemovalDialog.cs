namespace ESDERomCleaner;

// Confirmación de «Eliminar sistema completo»: lista las carpetas que se borran con sus recuentos y exige escribir el nombre del sistema.
// El botón de borrar solo se activa cuando el texto coincide; el predeterminado (Intro) es Cancelar.
internal sealed class SystemRemovalDialog : LocalizedForm
{
    private const string MediaLabel = @"ES-DE\downloaded_media";
    private Func<bool> keepMediaFunc = () => true;
    // true si la casilla «Borrar» de la fila de medios está desmarcada (valor por defecto).
    public bool KeepMedia => keepMediaFunc();

    public SystemRemovalDialog(SystemRemovalPlan plan, Func<long, string> formatSize, int romCount, Func<bool, Task>? runDeletion = null)
    {
        Text = "Eliminar sistema completo";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        var danger = Color.FromArgb(150, 30, 30);
        int rows = plan.Folders.Count;
        ClientSize = new Size(640, 372 + rows * 26);

        const int dy = 16; // cabecera más alta: el subtítulo ocupa dos líneas en algunos idiomas
        var header = new Panel { Location = new Point(0, 0), Size = new Size(640, 72 + dy), BackColor = danger };
        header.Paint += (_, ev) => RaUi.PaintHeaderTexture(ev.Graphics, header.ClientRectangle, header.BackColor); // el mismo fondo texturizado que el borrado de ROMs
        // Papelera de grises, quieta mientras se pide la confirmación y animada mientras se borra (la misma que el borrado de ROMs).
        var trashIcon = new PictureBox { Location = new Point(20, 12), Size = new Size(64, 64), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
        header.Controls.Add(trashIcon);
        var titleLabel = new ShadowLabel
        {
            Text = "Eliminación permanente de un sistema", Location = new Point(96, 10), Size = new Size(524, 28), AutoSize = false,
            Font = new Font(Font.FontFamily, 14f, FontStyle.Bold), ForeColor = Color.White, BackColor = Color.Transparent
        };
        var subtitleLabel = new ShadowLabel
        {
            Text = "Se borran de tu colección y del almacenamiento. No pasan por la Papelera y no se pueden recuperar.",
            Location = new Point(96, 40), Size = new Size(524, 24 + dy), AutoSize = false, ForeColor = Color.FromArgb(255, 226, 226), BackColor = Color.Transparent
        };
        header.Controls.Add(titleLabel);
        header.Controls.Add(subtitleLabel);
        Controls.Add(header);
        var drop = RaUi.TrashDropFrames();
        trashIcon.Image = drop[0];
        int dropFrame = 0;
        bool running = false;
        var dropTimer = new System.Windows.Forms.Timer { Interval = 55 };
        dropTimer.Tick += (_, _) => { dropFrame = (dropFrame + 1) % drop.Length; trashIcon.Image = drop[dropFrame]; };
        FormClosed += (_, _) => dropTimer.Dispose();
        FormClosing += (_, e) => { if (running) e.Cancel = true; };

        Controls.Add(new Label
        {
            Text = plan.System, Tag = "noloc", Location = new Point(20, 84 + dy), Size = new Size(600, 32), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 16f, FontStyle.Bold), ForeColor = danger
        });
        Controls.Add(new Label
        {
            Text = romCount > 0 ? $"{romCount:N0} ROMs identificadas en este sistema." : "Este sistema no tiene ROMs identificadas.",
            Location = new Point(20, 116 + dy), Size = new Size(600, 20), AutoSize = false, ForeColor = SystemColors.GrayText
        });

        int y = 146 + dy;
        // Rutas, contenido y tamaño en una tabla con el aspecto de las demás; la última fila es el total.
        var grid = new DataGridView
        {
            Location = new Point(20, y), Size = new Size(600, 22 + (plan.Folders.Count + 1) * 26 + 2), AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, ScrollBars = ScrollBars.None, TabStop = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
        };
        RaUi.StyleGrid(grid, 26, 22, selectionVisible: false);
        const DataGridViewAutoSizeColumnMode fill = DataGridViewAutoSizeColumnMode.Fill;
        // La casilla «Borrar» marca lo que se elimina. Roms y gamelists siempre; los medios se conservan por defecto (desmarcados) y se pueden marcar.
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Borrar", Width = 60, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Carpeta", AutoSizeMode = fill, FillWeight = 50, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = new DataGridViewCellStyle { Font = new Font("Consolas", 9f) } });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Contenido", AutoSizeMode = fill, FillWeight = 32, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Tamaño", AutoSizeMode = fill, FillWeight = 18, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } });
        var mediaRows = new List<DataGridViewRow>();
        foreach (var folder in plan.Folders)
        {
            bool isMedia = folder.Label == MediaLabel;
            var row = grid.Rows[grid.Rows.Add(!isMedia, folder.Path, Loc.T(folder.Detail), formatSize(folder.Bytes))];
            if (isMedia) mediaRows.Add(row); else row.Cells[0].ReadOnly = true;
        }
        var totalRow = grid.Rows[grid.Rows.Add(false, "", "", "")];
        totalRow.Cells[0] = new DataGridViewTextBoxCell { Value = "" }; // la fila de total no lleva casilla
        totalRow.DefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
        totalRow.DefaultCellStyle.BackColor = SystemColors.Control;
        grid.ClearSelection();
        Controls.Add(grid);
        y += grid.Height + 6;
        keepMediaFunc = () => !mediaRows.Any(r => r.Cells[0].Value is true);

        void UpdateTotal()
        {
            var counted = plan.Folders.Where(f => !(keepMediaFunc() && f.Label == MediaLabel)).ToList();
            totalRow.Cells[1].Value = Loc.T("Total");
            totalRow.Cells[2].Value = Loc.T($"{counted.Sum(f => f.Files):N0} archivos");
            totalRow.Cells[3].Value = formatSize(counted.Sum(f => f.Bytes));
        }
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += (_, e) => { if (e.ColumnIndex == 0 && e.RowIndex >= 0) UpdateTotal(); };
        UpdateTotal();
        bool arcadeSystem = plan.Folders.Any(f => f.OnlyFiles is not null && !f.DeleteSubfolders);
        Controls.Add(new Label
        {
            Text = "Los medios se conservan por defecto: márcalos para borrarlos. Se podrán limpiar después con «Limpiar huérfanos». "
                + (arcadeSystem ? "En arcade solo se borran las ROMs de la raíz; se conservan las carpetas (bios, cfg…) y systeminfo.txt." : "Se conserva systeminfo.txt."),
            Location = new Point(20, y), Size = new Size(600, 52), AutoSize = false, ForeColor = SystemColors.GrayText
        });
        y += 56;

        Controls.Add(new Label
        {
            Text = "También se quitan sus idiomas, logros y exclusiones del curado. No se toca ningún otro sistema ni las colecciones de ES-DE.",
            Location = new Point(20, y), Size = new Size(600, 38), AutoSize = false, ForeColor = SystemColors.GrayText
        });
        y += 46;

        Controls.Add(new Label { Text = "Escribe el nombre del sistema para confirmar:", Location = new Point(20, y), Size = new Size(600, 20), AutoSize = false });
        var box = new TextBox { Location = new Point(20, y + 24), Size = new Size(600, 26) };
        Controls.Add(box);

        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Location = new Point(404, y + 66), Size = new Size(100, 30) };
        var delete = new Button { Text = "Eliminar para siempre", DialogResult = DialogResult.OK, Location = new Point(510, y + 66), Size = new Size(110, 30), Enabled = false, ForeColor = danger };
        delete.AutoSize = false; delete.Size = new Size(150, 30); delete.Location = new Point(470, y + 66); cancel.Location = new Point(360, y + 66);
        Controls.Add(cancel);
        Controls.Add(delete);
        AcceptButton = cancel; CancelButton = cancel;
        ClientSize = new Size(640, y + 112);
        box.TextChanged += (_, _) => delete.Enabled = string.Equals(box.Text.Trim(), plan.System, StringComparison.OrdinalIgnoreCase);
        Shown += (_, _) => box.Focus();

        // Con runDeletion el formulario no se cierra al confirmar: se queda con la papelera animada mientras se borra y se cierra con OK al terminar.
        if (runDeletion is not null) delete.DialogResult = DialogResult.None;
        delete.Click += async (_, _) =>
        {
            if (runDeletion is null) return;
            running = true;
            foreach (var control in Controls.OfType<Control>().Where(c => c != header)) control.Enabled = false;
            titleLabel.Text = Loc.T("Eliminando…"); titleLabel.Tag = "noloc";
            subtitleLabel.Text = Loc.T("No cierres la aplicación hasta que termine."); subtitleLabel.Tag = "noloc";
            dropTimer.Start();
            await runDeletion(KeepMedia);
            dropTimer.Stop(); running = false;
            DialogResult = DialogResult.OK;
        };
    }

    // true si el usuario ha escrito el nombre y ha confirmado; con runDeletion, el borrado se hace dentro del propio formulario.
    public static bool Ask(IWin32Window owner, SystemRemovalPlan plan, Func<long, string> formatSize, int romCount, Func<bool, Task> runDeletion)
    {
        using var dialog = new SystemRemovalDialog(plan, formatSize, romCount, runDeletion);
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }
}
