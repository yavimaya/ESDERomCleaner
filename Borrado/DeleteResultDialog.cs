namespace ESDERomCleaner;

// Resultado de un borrado: cabecera oscura con la papelera, cuatro tarjetas (ROMs, accesorios, media y entradas del gamelist), el espacio liberado, las copias de
// seguridad y, si los hay, los errores. Sustituye al antiguo cuadro de texto plano.
internal sealed class DeleteResultDialog : LocalizedForm
{
    private static readonly Color Ok = Color.FromArgb(34, 130, 70), Warn = Color.FromArgb(214, 100, 0), Bad = Color.FromArgb(170, 40, 40);

    public DeleteResultDialog(DeletionResult result, Func<long, string> formatSize)
    {
        Text = "Resultado de la eliminación";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        BackColor = RaUi.Body;
        const int width = 660, headerHeight = 68, footerHeight = 52, innerWidth = width - 48;
        bool clean = result.Errors.Count == 0;

        var banner = new RaHeader("Resultado de la eliminación", clean ? "La operación ha finalizado correctamente." : $"La operación terminó con {result.Errors.Count:N0} error(es).", headerHeight, 48, logo: false);
        banner.Icon.Image = RaUi.TrashIcon();

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(24, 16, 24, 8), BackColor = RaUi.Body };

        // Tarjetas: lo eliminado de lo previsto, con el espacio de cada una.
        var tiles = new Panel { Size = new Size(innerWidth, 96), Margin = new Padding(0, 0, 0, 12) };
        void Tile(int index, int done, int planned, string name, string size, bool showPlanned = true)
        {
            Color color = showPlanned && done < planned ? Warn : Ok;
            var tile = RaUi.Card(new Rectangle(index * ((innerWidth - 3 * 10) / 4 + 10), 0, (innerWidth - 3 * 10) / 4, 96));
            tile.Controls.Add(new Label { Text = showPlanned ? $"{done:N0} / {planned:N0}" : $"{done:N0}", Tag = "noloc", Location = new Point(0, 8), Size = new Size(tile.Width, 38), TextAlign = ContentAlignment.MiddleCenter, Font = new Font(Font.FontFamily, 18F, FontStyle.Bold), ForeColor = color });
            tile.Controls.Add(new Label { Text = name, Location = new Point(0, 50), Size = new Size(tile.Width, 20), TextAlign = ContentAlignment.MiddleCenter, Font = new Font(Font, FontStyle.Bold) });
            tile.Controls.Add(new Label { Text = size, Tag = "noloc", Location = new Point(0, 70), Size = new Size(tile.Width, 20), TextAlign = ContentAlignment.MiddleCenter, ForeColor = RaUi.Muted });
            tiles.Controls.Add(tile);
        }
        Tile(0, result.DeletedRomCount, result.RomCountPlanned, "ROMs", formatSize(result.DeletedRomBytes));
        Tile(1, result.DeletedAccessoryCount, result.AccessoryCountPlanned, "Accesorios", formatSize(result.DeletedAccessoryBytes));
        Tile(2, result.DeletedMediaCount, result.MediaCountPlanned, "Media de ES-DE", formatSize(result.DeletedMediaBytes));
        Tile(3, result.GameEntriesRemoved, 0, "Entradas <game>", "", showPlanned: false);
        body.Controls.Add(tiles);

        body.Controls.Add(new Label
        {
            Text = $"Espacio total liberado: {formatSize(result.TotalDeletedBytes)}", AutoSize = false, Size = new Size(innerWidth, 28), TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, 12F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 10)
        });

        // Copias de seguridad de los gamelist.xml.
        var backups = new Panel { Width = innerWidth, BackColor = Color.White, Padding = new Padding(14, 10, 14, 10), Margin = new Padding(0, 0, 0, 10) };
        var backupLines = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Color.Transparent };
        backupLines.Controls.Add(new Label { Text = "COPIAS DE SEGURIDAD DE GAMELIST:", AutoSize = true, Font = new Font(Font, FontStyle.Bold), ForeColor = RaUi.Muted, Margin = new Padding(0, 0, 0, 4) });
        if (result.Backups.Count == 0) backupLines.Controls.Add(new Label { Text = "No se crearon copias de seguridad.", AutoSize = true });
        else
        {
            backupLines.Controls.Add(new Label { Text = $"Copias de seguridad creadas: {result.Backups.Count:N0}", AutoSize = true, Margin = new Padding(0, 0, 0, 2) });
            foreach (string backup in result.Backups.Take(4))
                backupLines.Controls.Add(new Label { Text = backup, Tag = "noloc", AutoSize = false, Size = new Size(innerWidth - 28, 18), AutoEllipsis = true, ForeColor = RaUi.Muted });
            if (result.Backups.Count > 4) backupLines.Controls.Add(new Label { Text = $"… +{result.Backups.Count - 4:N0}", Tag = "noloc", AutoSize = true, ForeColor = RaUi.Muted });
        }
        backups.Controls.Add(backupLines);
        backups.Height = backupLines.PreferredSize.Height + 20;
        body.Controls.Add(backups);

        // Errores (solo si los hay): recuadro rojo claro con la lista desplazable.
        if (!clean)
        {
            var errors = new Panel { Width = innerWidth, BackColor = Color.FromArgb(253, 236, 236), Padding = new Padding(14, 10, 14, 10), Margin = new Padding(0, 0, 0, 10) };
            var list = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = errors.BackColor, ForeColor = Bad, Tag = "noloc",
                Dock = DockStyle.Fill, Text = string.Join("\r\n", result.Errors.Select(e => "• " + e))
            };
            errors.Controls.Add(list);
            errors.Controls.Add(new Label { Text = "DETALLE DE ERRORES:", Dock = DockStyle.Top, Height = 22, Font = new Font(Font, FontStyle.Bold), ForeColor = Bad });
            errors.Height = 30 + Math.Min(140, 20 * Math.Min(result.Errors.Count + 1, 7)) + 12;
            body.Controls.Add(errors);
        }

        body.Controls.Add(new Label { Text = "A continuación se actualizarán solo los sistemas afectados.", AutoSize = true, ForeColor = RaUi.Muted, Margin = new Padding(0, 2, 0, 0) });

        var footer = RaUi.Footer(footerHeight);
        var accept = RaUi.MakeButton("Aceptar", true, 110);
        accept.DialogResult = DialogResult.OK; accept.Location = new Point(width - 20 - accept.Width, 10);
        footer.Controls.Add(accept);
        AcceptButton = accept; CancelButton = accept;
        Controls.AddRange([body, banner, footer]);
        ClientSize = new Size(width, headerHeight + body.GetPreferredSize(new Size(width, 0)).Height + footerHeight);
    }
}
