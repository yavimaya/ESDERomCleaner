namespace ESDERomCleaner;

// Resumen final de una descarga masiva: contadores y, por cada tipo de medio, el espacio que ha ganado o perdido la colección.
internal sealed class BulkResultDialog : LocalizedForm
{
    public BulkResultDialog(string headline, string counters, BulkSummary summary)
    {
        Text = "Resumen de la descarga masiva";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(820, 520);
        MinimumSize = new Size(640, 420);
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        var title = new Label { Dock = DockStyle.Fill, Text = headline, Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        var counts = new Label { Dock = DockStyle.Fill, Text = counters.Replace(" · ", "   ·   "), AutoSize = false };

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
            BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Tipo de medio", Width = 190 });
        foreach (string header in new[] { "Guardados", "Sustituidos", "Antes", "Ahora", "Diferencia" })
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = header, Width = header == "Diferencia" ? 130 : 95, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

        long totalBefore = 0, totalAfter = 0;
        int totalFiles = 0, totalReplaced = 0;
        foreach (var folder in EsDeMediaInstaller.Folders)
        {
            if (!summary.ByFolder.TryGetValue(folder.Folder, out var s)) continue;
            AddRow(grid, $"{folder.Description} ({folder.Folder})", s.Files, s.Replaced, s.BytesBefore, s.BytesAfter, bold: false);
            totalBefore += s.BytesBefore; totalAfter += s.BytesAfter; totalFiles += s.Files; totalReplaced += s.Replaced;
        }
        // Si no se ha guardado nada no hay tabla que enseñar: el resumen queda reducido a los contadores.
        bool nothingSaved = grid.Rows.Count == 0;
        if (!nothingSaved) AddRow(grid, "TOTAL", totalFiles, totalReplaced, totalBefore, totalAfter, bold: true);
        grid.ClearSelection();

        var note = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopLeft,
            Text = "«Antes» es lo que ocupaban los archivos que se han sustituido y «Ahora» lo que ocupan los nuevos. " +
                   "En verde, la colección ocupa menos; en naranja, ocupa más. Los medios añadidos donde no había nada cuentan como aumento."
        };

        var ok = new Button { Text = "Aceptar", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(100, 30) };
        AcceptButton = ok;
        CancelButton = ok;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(ok);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12, 8, 12, 8) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        if (nothingSaved)
        {
            title.Text = headline + " " + Loc.T("No se guardó ningún medio.");
            layout.RowStyles[2].SizeType = SizeType.Absolute; layout.RowStyles[2].Height = 0;
            layout.RowStyles[3].Height = 0;
            grid.Visible = false; note.Visible = false;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
            ClientSize = new Size(820, 8 + 28 + 64 + 40 + 8);
            MinimumSize = Size;
        }
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(counts, 0, 1);
        layout.Controls.Add(grid, 0, 2);
        layout.Controls.Add(note, 0, 3);
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);
    }

    private void AddRow(DataGridView grid, string name, int files, int replaced, long before, long after, bool bold)
    {
        long delta = after - before;
        int index = grid.Rows.Add(name, files.ToString("N0"), replaced.ToString("N0"),
            before > 0 ? ScreenScraperDialog.FormatBytes(before) : "—", ScreenScraperDialog.FormatBytes(after), FormatDelta(delta), "");
        var row = grid.Rows[index];
        row.Cells[5].Style.ForeColor = delta < 0 ? Color.DarkGreen : delta > 0 ? Color.DarkOrange : SystemColors.ControlText;
        row.Cells[6].Value = delta < 0 ? "ahorro" : delta > 0 ? "aumento" : "";
        row.Cells[6].Style.ForeColor = row.Cells[5].Style.ForeColor;
        if (bold) row.DefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
    }

    // Con signo explícito: «−1,2 GB» es espacio ahorrado y «+300 MB» espacio añadido.
    internal static string FormatDelta(long delta) =>
        delta == 0 ? "0 B" : (delta < 0 ? "−" : "+") + ScreenScraperDialog.FormatBytes(Math.Abs(delta));
}
