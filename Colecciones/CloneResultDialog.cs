using System.Text.RegularExpressions;

namespace ESDERomCleaner;

// Resultado de un clonado: cabecera oscura con el diskette, cuatro tarjetas (carpetas, archivos, datos copiados y extras borrados del destino) sacadas de la tabla
// de resumen de Robocopy, y las rutas de origen y destino con la duración. Sustituye al antiguo cuadro de texto plano.
internal sealed class CloneResultDialog : LocalizedForm
{
    private static readonly Color Ok = Color.FromArgb(34, 130, 70), Warn = Color.FromArgb(214, 100, 0), Bad = Color.FromArgb(170, 40, 40);

    // Una fila de la tabla de Robocopy: total, copiados, omitidos, desiguales, fallidos y extras. Ojo: en las carpetas «copiados» incluye las que solo reciben de nuevo
    // sus fechas (con /MIR y un destino idéntico suele ser el total), así que no se usa; solo importan los archivos, los bytes y los extras.
    private sealed record Row(long Total, long Copied, long Skipped, long Mismatch, long Failed, long Extras);

    // Las tres primeras filas con seis números son carpetas, archivos y bytes (con /BYTES los bytes salen como números enteros). Funciona en cualquier idioma de Windows.
    private static Row?[] ParseSummary(IEnumerable<string> lines)
    {
        var rows = new List<Row>();
        var pattern = new Regex(@"^\s*[^\d:]+:\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*$");
        foreach (string line in lines)
        {
            var m = pattern.Match(line);
            if (m.Success) rows.Add(new Row(long.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value), long.Parse(m.Groups[3].Value), long.Parse(m.Groups[4].Value), long.Parse(m.Groups[5].Value), long.Parse(m.Groups[6].Value)));
        }
        return rows.Count >= 3 ? [rows[^3], rows[^2], rows[^1]] : [null, null, null];
    }

    public CloneResultDialog(IEnumerable<string> robocopyLines, int exitCode, string source, string destination, TimeSpan elapsed)
    {
        Text = "Resultado de la clonación";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        BackColor = RaUi.Body;
        const int width = 660, headerHeight = 68, footerHeight = 52, innerWidth = width - 48;
        bool success = exitCode < 8;
        var summary = ParseSummary(robocopyLines);
        var dirs = summary[0]; var files = summary[1]; var bytes = summary[2];
        long failed = (files?.Failed ?? 0) + (dirs?.Failed ?? 0);

        bool upToDate = success && files is { Copied: 0 } && (files.Extras + (dirs?.Extras ?? 0)) == 0;
        string subtitle = !success ? $"La clonación terminó con errores (código {exitCode})." : upToDate ? "El destino ya estaba al día: no había nada que copiar." : "La clonación ha terminado correctamente.";
        var banner = new RaHeader("Resultado de la clonación", subtitle, headerHeight, 48, logo: false);
        banner.Icon.Image = RaUi.FloppyFrames().Idle;

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(24, 16, 24, 8), BackColor = RaUi.Body };
        var tiles = new Panel { Size = new Size(innerWidth, 96), Margin = new Padding(0, 0, 0, 12) };
        int tileWidth = (innerWidth - 3 * 10) / 4;
        void Tile(int index, string number, string name, string detail, Color color)
        {
            var tile = RaUi.Card(new Rectangle(index * (tileWidth + 10), 0, tileWidth, 96));
            tile.Controls.Add(new Label { Text = number, Tag = "noloc", Location = new Point(0, 8), Size = new Size(tileWidth, 38), TextAlign = ContentAlignment.MiddleCenter, Font = new Font(Font.FontFamily, 18F, FontStyle.Bold), ForeColor = color });
            tile.Controls.Add(new Label { Text = name, Location = new Point(0, 50), Size = new Size(tileWidth, 20), TextAlign = ContentAlignment.MiddleCenter, Font = new Font(Font, FontStyle.Bold) });
            tile.Controls.Add(new Label { Text = detail, Tag = "noloc", Location = new Point(0, 70), Size = new Size(tileWidth, 20), TextAlign = ContentAlignment.MiddleCenter, ForeColor = RaUi.Muted });
            tiles.Controls.Add(tile);
        }
        string Of(Row? r) => r is null ? "—" : $"{r.Copied:N0}";
        string Total(Row? r) => r is null ? "" : $"de {r.Total:N0}";
        Tile(0, Of(files), "Archivos copiados", Total(files), success ? Ok : Warn);
        Tile(1, bytes is null ? "—" : ScreenScraperDialog.FormatBytes(bytes.Copied), "Datos copiados", bytes is null ? "" : $"de {ScreenScraperDialog.FormatBytes(bytes.Total)}", success ? Ok : Warn);
        Tile(2, files is null ? "—" : $"{files.Skipped:N0}", "Archivos al día", "sin cambios", RaUi.Muted);
        Tile(3, files is null && dirs is null ? "—" : $"{(files?.Extras ?? 0) + (dirs?.Extras ?? 0):N0}", "Extras borrados", "del destino", RaUi.Muted);
        body.Controls.Add(tiles);

        // Origen, destino y duración.
        var info = new Panel { Width = innerWidth, BackColor = Color.White, Padding = new Padding(14, 10, 14, 10), Margin = new Padding(0, 0, 0, 10) };
        var lines = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Color.Transparent };
        void Line(string caption, string value, Color? color = null)
        {
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 3) };
            row.Controls.Add(new Label { Text = caption, AutoSize = false, Size = new Size(90, 20), ForeColor = RaUi.Muted });
            row.Controls.Add(new Label { Text = value, Tag = "noloc", AutoSize = false, Size = new Size(innerWidth - 28 - 90, 20), AutoEllipsis = true, ForeColor = color ?? SystemColors.ControlText });
            lines.Controls.Add(row);
        }
        Line("Origen:", source);
        Line("Destino:", destination);
        if (dirs is not null) Line("Carpetas:", $"{dirs.Total:N0}");
        Line("Duración:", $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}" + (bytes is { Copied: > 0 } && elapsed.TotalSeconds >= 1 ? $" · {ScreenScraperDialog.FormatBytes((long)(bytes.Copied / elapsed.TotalSeconds))}/s" : ""));
        if (failed > 0) Line("Fallidos:", $"{failed:N0}", Bad);
        info.Controls.Add(lines);
        info.Height = lines.PreferredSize.Height + 20;
        body.Controls.Add(info);

        var footer = RaUi.Footer(footerHeight);
        var accept = RaUi.MakeButton("Aceptar", true, 110);
        accept.DialogResult = DialogResult.OK; accept.Location = new Point(width - 20 - accept.Width, 10);
        footer.Controls.Add(accept);
        AcceptButton = accept; CancelButton = accept;
        Controls.AddRange([body, banner, footer]);
        ClientSize = new Size(width, headerHeight + body.GetPreferredSize(new Size(width, 0)).Height + footerHeight);
    }
}
