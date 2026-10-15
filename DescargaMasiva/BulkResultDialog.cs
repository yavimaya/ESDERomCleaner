namespace ESDERomCleaner;

// Resumen final de una descarga masiva: contadores y, por cada tipo de medio, el espacio que ha ganado o perdido la colección.
// Mismo aspecto que los formularios nuevos: cabecera oscura, contenido sobre fondo claro y pie con el botón. Si no se ha guardado ningún medio,
// no hay tabla y el formulario queda reducido a los contadores.
internal sealed class BulkResultDialog : LocalizedForm
{
    public BulkResultDialog(string headline, string counters, BulkSummary summary)
    {
        Text = "Resumen de la descarga masiva";
        ShowInTaskbar = false; ControlBox = true;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        BackColor = RaUi.Body;

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
            BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.None
        };
        RaUi.StyleGrid(grid, 26, 28);
        // «Tipo de medio» ocupa lo que sobra; Guardados y Sustituidos van centrados; Antes, Ahora y Diferencia, a la derecha (el signo y el color de la diferencia ya
        // dicen si la colección ocupa más o menos, por eso no hay una columna con la palabra).
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Tipo de medio", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 160 });
        foreach (var (header, colWidth, align) in new[]
        {
            ("Guardados", 95, DataGridViewContentAlignment.MiddleCenter), ("Sustituidos", 105, DataGridViewContentAlignment.MiddleCenter),
            ("Antes", 95, DataGridViewContentAlignment.MiddleRight), ("Ahora", 95, DataGridViewContentAlignment.MiddleRight), ("Diferencia", 100, DataGridViewContentAlignment.MiddleRight)
        })
        {
            var column = new DataGridViewTextBoxColumn { HeaderText = header, Width = colWidth, DefaultCellStyle = new DataGridViewCellStyle { Alignment = align } };
            column.HeaderCell.Style.Alignment = align;
            grid.Columns.Add(column);
        }

        long totalBefore = 0, totalAfter = 0;
        int totalFiles = 0, totalReplaced = 0;
        foreach (var folder in EsDeMediaInstaller.Folders)
        {
            if (!summary.ByFolder.TryGetValue(folder.Folder, out var s)) continue;
            AddRow(grid, $"{folder.Description} ({folder.Folder})", s.Files, s.Replaced, s.BytesBefore, s.BytesAfter, bold: false);
            totalBefore += s.BytesBefore; totalAfter += s.BytesAfter; totalFiles += s.Files; totalReplaced += s.Replaced;
        }
        bool nothingSaved = grid.Rows.Count == 0;
        if (!nothingSaved) AddRow(grid, "TOTAL", totalFiles, totalReplaced, totalBefore, totalAfter, bold: true);
        grid.ClearSelection();

        int width = nothingSaved ? 640 : 820;
        var banner = new RaHeader("Resumen de la descarga masiva", nothingSaved ? headline + " " + "No se guardó ningún medio." : headline, 68, 48, logo: false);
        banner.Icon.Image = RaUi.ChartIcon();
        var counts = new Label { Text = counters.Replace(" · ", "   ·   "), AutoSize = false, Location = new Point(20, 80), Size = new Size(width - 40, nothingSaved ? 60 : 24) };
        var ok = new Button { Text = "Cerrar", DialogResult = DialogResult.OK, MinimumSize = new Size(100, 32), AutoSize = true };
        RaUi.StyleButton(ok, true);
        AcceptButton = ok; CancelButton = ok;
        var footer = RaUi.Footer();
        // El pie se estira al acoplarse: el botón se recoloca contra el borde derecho cada vez que cambia su ancho (un Anchor mal calculado lo sacaba de la vista).
        footer.Resize += (_, _) => ok.Location = new Point(footer.Width - 20 - ok.Width, 10);
        ok.Location = new Point(width - 20 - ok.Width, 10);
        footer.Controls.Add(ok);

        if (nothingSaved)
        {
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(width, 68 + 12 + 60 + 12 + footer.Height);
            Controls.AddRange([counts, footer, banner]);
        }
        else
        {
            MinimizeBox = false;
            ClientSize = new Size(width, 520);
            MinimumSize = new Size(640, 420);
            counts.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            var note = new Label
            {
                Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(20, 6, 20, 0), TextAlign = ContentAlignment.TopLeft,
                Text = "«Antes» es lo que ocupaban los archivos que se han sustituido y «Ahora» lo que ocupan los nuevos. " +
                       "En verde, la colección ocupa menos; en naranja, ocupa más. Los medios añadidos donde no había nada cuentan como aumento."
            };
            var frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = RaUi.Border };
            frame.Controls.Add(grid);
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 110, 20, 8) };
            body.Controls.Add(frame);
            Controls.AddRange([body, counts, note, footer, banner]);
            counts.BringToFront();
            // La línea de contadores ocupa lo que mide (una línea o dos según el ancho) y la tabla empieza justo debajo, sin hueco.
            void FitCounts()
            {
                int textHeight = TextRenderer.MeasureText(counts.Text, counts.Font, new Size(Math.Max(100, counts.Width), 0), TextFormatFlags.WordBreak).Height;
                counts.Height = textHeight + 8;
                body.Padding = new Padding(20, 80 + counts.Height + 4, 20, 8);
            }
            FitCounts();
            Resize += (_, _) => FitCounts();
        }
    }

    private void AddRow(DataGridView grid, string name, int files, int replaced, long before, long after, bool bold)
    {
        long delta = after - before;
        int index = grid.Rows.Add(name, files.ToString("N0"), replaced.ToString("N0"),
            before > 0 ? ScreenScraperDialog.FormatBytes(before) : "—", ScreenScraperDialog.FormatBytes(after), FormatDelta(delta));
        var row = grid.Rows[index];
        row.Cells[5].Style.ForeColor = delta < 0 ? Color.DarkGreen : delta > 0 ? Color.DarkOrange : SystemColors.ControlText;
        if (bold) row.DefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
    }

    // Con signo explícito: «−1,2 GB» es espacio ahorrado y «+300 MB» espacio añadido.
    internal static string FormatDelta(long delta) =>
        delta == 0 ? "0 B" : (delta < 0 ? "−" : "+") + ScreenScraperDialog.FormatBytes(Math.Abs(delta));
}
