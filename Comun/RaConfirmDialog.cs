namespace ESDERomCleaner;

// Pregunta de confirmación con el formato de los formularios nuevos: cabecera oscura con icono, título y subtítulo, cuerpo claro con el texto y pie con botones
// («OK» principal en dorado y «Cancelar»).
internal sealed class RaConfirmDialog : LocalizedForm
{
    public static bool Ask(IWin32Window owner, string title, string subtitle, string body, Bitmap icon, string okText, bool subtitleLocalized = false)
    {
        using var dialog = new RaConfirmDialog(title, subtitle, body, icon, okText, subtitleLocalized);
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }

    private RaConfirmDialog(string title, string subtitle, string body, Bitmap icon, string okText, bool subtitleLocalized)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        BackColor = RaUi.Body;
        const int width = 580, headerHeight = 68, footerHeight = 52;

        var banner = new RaHeader(title, subtitle, headerHeight, 48, logo: false);
        banner.Icon.Image = icon;
        if (subtitleLocalized) banner.Subtitle.Tag = "noloc";
        var text = new Label
        {
            Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 8), BackColor = RaUi.Body, TextAlign = ContentAlignment.TopLeft, Text = body
        };
        var footer = RaUi.Footer(footerHeight);
        var ok = RaUi.MakeButton(okText, true, 130);
        ok.DialogResult = DialogResult.OK; ok.Location = new Point(width - 20 - 100 - 8 - 130, 10);
        var cancel = RaUi.MakeButton("Cancelar", false, 100);
        cancel.DialogResult = DialogResult.Cancel; cancel.Location = new Point(width - 20 - 100, 10);
        footer.Controls.AddRange([ok, cancel]);
        AcceptButton = ok; CancelButton = cancel;
        // La altura se ajusta al texto ya traducido y con sus saltos de línea por ancho.
        int textHeight = TextRenderer.MeasureText(Loc.T(body), Font, new Size(width - 48, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        ClientSize = new Size(width, headerHeight + 28 + textHeight + 16 + footerHeight);
        Controls.Add(text); Controls.Add(footer); Controls.Add(banner);
    }
}
