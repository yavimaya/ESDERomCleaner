namespace ESDERomCleaner;

internal sealed record MediaSaveRow(string Folder, string Media, string Detail, string ReplacedFiles);

// Confirmación del guardado de medios en la colección: una tabla en lugar de un texto corrido.
// Es un formulario propio y no un MessageBox, así que no reproduce ningún sonido del sistema.
internal sealed class MediaSaveConfirmDialog : LocalizedForm
{
    public MediaSaveConfirmDialog(string romName, IReadOnlyList<MediaSaveRow> rows, string backupRoot)
    {
        int replacing = rows.Count(r => r.ReplacedFiles.Length > 0);
        Text = "Guardar medios en la colección";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(820, 150 + Math.Min(rows.Count, 8) * 28 + (replacing > 0 ? 70 : 28));
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        var title = new Label
        {
            Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font, FontStyle.Bold),
            Text = $"Se guardarán {rows.Count} {(rows.Count == 1 ? "medio" : "medios")} para «{romName}»"
        };

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
            BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Carpeta", Width = 110 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Medio elegido", Width = 170 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Detalle", Width = 120 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Acción", Width = 90 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Sustituye a", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True } });
        foreach (var r in rows)
        {
            int index = grid.Rows.Add(r.Folder, r.Media, r.Detail, r.ReplacedFiles.Length > 0 ? "Sustituir" : "Nuevo", r.ReplacedFiles.Length > 0 ? r.ReplacedFiles : "—");
            var action = grid.Rows[index].Cells[3];
            action.Style.ForeColor = r.ReplacedFiles.Length > 0 ? Color.DarkOrange : Color.DarkGreen;
            action.Style.Font = new Font(Font, FontStyle.Bold);
        }
        grid.ClearSelection();

        var footer = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        if (replacing > 0)
            footer.Text = $"Los {replacing} {(replacing == 1 ? "medio sustituido se moverá" : "medios sustituidos se moverán")} a una copia de seguridad; no se borra nada.\r\n{backupRoot}";
        else footer.Text = "No se sustituye ningún archivo existente.";

        var ok = new Button { Text = "Scrapear", AutoSize = true, DialogResult = DialogResult.OK, MinimumSize = new Size(90, 28) };
        var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel, MinimumSize = new Size(90, 28) };
        AcceptButton = ok;
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12, 8, 12, 8) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, replacing > 0 ? 50 : 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(grid, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
    }
}

// Aviso con un único botón, sin icono: no suena nada al mostrarse (los MessageBox con icono reproducen un sonido del sistema).
internal static class QuietMessage
{
    // Pregunta con dos botones, sin icono de sistema ni sonido, con el formato del resto de formularios (cabecera oscura con el título).
    // Devuelve true si se pulsa el botón de aceptar.
    public static bool Confirm(IWin32Window? owner, string title, string text, string okText = "Aceptar", string cancelText = "Cancelar")
    {
        using var dialog = new QuietBox(Loc.T(title), Loc.T(text), Loc.T(okText), Loc.T(cancelText));
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }

    public static void Show(IWin32Window? owner, string title, string text)
    {
        using var dialog = new QuietBox(Loc.T(title), Loc.T(text), Loc.T("Aceptar"), null);
        dialog.ShowDialog(owner);
    }

    // Pide un texto corto (p. ej. un nombre). Devuelve null si se cancela o queda vacío.
    public static string? Prompt(IWin32Window? owner, string title, string label, string initial = "", string okText = "Aceptar")
    {
        title = Loc.T(title); label = Loc.T(label); okText = Loc.T(okText);
        using var dialog = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(460, 124),
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont
        };
        var prompt = new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft };
        var box = new TextBox { Text = initial, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
        var ok = new Button { Text = okText, DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(100, 28) };
        var cancel = new Button { Text = Loc.T("Cancelar"), DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(14, 8, 14, 6) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(prompt, 0, 0);
        layout.Controls.Add(box, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => { box.Focus(); box.SelectAll(); };
        if (AppIcon.Value is { } appIcon) dialog.Icon = appIcon;
        dialog.HandleCreated += (_, _) => DarkTitleBar.Apply(dialog.Handle);
        CollectionTitle.Attach(dialog);
        if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
        string text = box.Text.Trim();
        return text.Length == 0 ? null : text;
    }
}

// Cuadro de aviso o pregunta con el mismo aspecto que los formularios nuevos: cabecera oscura con el título, el texto en un cuerpo claro y los botones
// en un pie. Sin icono de sistema, así que no suena nada. Con cancelText = null solo lleva el botón de aceptar.
internal sealed class QuietBox : Form
{
    public QuietBox(string title, string text, string okText, string? cancelText)
    {
        Text = title;
        if (AppIcon.Value is { } appIcon) Icon = appIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        BackColor = RaUi.Body;

        var banner = new RaHeader(title, "", 52, 32, logo: false);
        banner.Icon.Image = RaUi.InfoIcon(2);
        banner.Title.Tag = "noloc";
        var message = new Label { Dock = DockStyle.Fill, AutoSize = false, Text = text, ForeColor = RaUi.Dark, Padding = new Padding(22, 18, 22, 8) };

        var footer = RaUi.Footer(52);
        var ok = RaUi.MakeButton(okText, cancelText is not null, Math.Max(100, TextRenderer.MeasureText(okText, Font).Width + 36));
        ok.DialogResult = DialogResult.OK;
        Button? cancel = null;
        if (cancelText is not null)
        {
            cancel = RaUi.MakeButton(cancelText, false, Math.Max(90, TextRenderer.MeasureText(cancelText, Font).Width + 36));
            cancel.DialogResult = DialogResult.Cancel;
        }

        // Ancho ajustado al texto (de 420 a 700) y alto al resultado.
        int width = Math.Clamp(TextRenderer.MeasureText(text, Font, new Size(656, 0), TextFormatFlags.WordBreak).Width + 44 + 8, 420, 700);
        var size = TextRenderer.MeasureText(text, Font, new Size(width - 44, 0), TextFormatFlags.WordBreak);
        ClientSize = new Size(width, Math.Clamp(52 + 18 + size.Height + 8 + 52, 150, 640));

        ok.Location = new Point(width - 24 - ok.Width, 10);
        footer.Controls.Add(ok);
        if (cancel is not null) { cancel.Location = new Point(ok.Left - 10 - cancel.Width, 10); footer.Controls.Add(cancel); }
        Controls.Add(message); Controls.Add(footer); Controls.Add(banner);
        AcceptButton = ok; CancelButton = (IButtonControl?)cancel ?? ok;
        CollectionTitle.Attach(this);
    }

    // Barra de título oscura, del color de las cabeceras, como en el resto de formularios (los que heredan de LocalizedForm ya la llevan).
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTitleBar.Apply(Handle);
    }
}
