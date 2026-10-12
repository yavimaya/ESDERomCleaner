namespace ESDERomCleaner;

// Formulario simple para elegir cómo se guardan las ROMs al añadirlas: formato y nivel de compresión.
internal sealed class StorageOptionsDialog : LocalizedForm
{
    private readonly ComboBox formatBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox levelBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label hint = new() { AutoSize = false, ForeColor = SystemColors.GrayText, TextAlign = ContentAlignment.MiddleLeft };

    public StorageOptions Result => new((StorageFormat)Math.Max(0, formatBox.SelectedIndex), (StorageLevel)Math.Max(0, levelBox.SelectedIndex));

    public StorageOptionsDialog(StorageOptions current)
    {
        Text = "Cómo guardar las ROMs";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        ClientSize = new Size(440, 243);

        var formatLabel = new Label { Text = "Formato:", Location = new Point(12, 16), Size = new Size(90, 22) };
        formatBox.Location = new Point(110, 13); formatBox.Size = new Size(318, 23);
        formatBox.Items.AddRange(["Como llegan (sin tocarlas)", "Sin comprimir (extraer)", "ZIP", "7z"]);
        var levelLabel = new Label { Text = "Compresión:", Location = new Point(12, 52), Size = new Size(90, 22) };
        levelBox.Location = new Point(110, 49); levelBox.Size = new Size(318, 23);
        levelBox.Items.AddRange(["Sin compresión (almacenar)", "Rápida", "Normal", "Máxima"]);
        hint.Location = new Point(12, 86); hint.Size = new Size(416, 106);

        var ok = new Button { Text = "Aceptar", Size = new Size(90, 28), Location = new Point(244, 201), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancelar", Size = new Size(90, 28), Location = new Point(338, 201), DialogResult = DialogResult.Cancel };
        AcceptButton = ok; CancelButton = cancel;
        Controls.AddRange([formatLabel, formatBox, levelLabel, levelBox, hint, ok, cancel]);

        formatBox.SelectedIndex = (int)current.Format; levelBox.SelectedIndex = (int)current.Level;
        formatBox.SelectedIndexChanged += (_, _) => Refresh_();
        FitHint();
        Refresh_();
    }

    // El nivel solo cuenta con zip y 7z; el texto explica cada formato.
    private void Refresh_()
    {
        var format = (StorageFormat)formatBox.SelectedIndex;
        levelBox.Enabled = format is StorageFormat.Zip or StorageFormat.SevenZip;
        hint.Text = HintText(format);
    }

    private static string HintText(StorageFormat format) => (format switch
    {
        StorageFormat.Keep => "• La ROM se copia tal cual llega (si venía en zip, queda en zip).",
        StorageFormat.Raw => "• Lo que venga comprimido se extrae a la carpeta del sistema.\r\n• Si un comprimido tiene varios archivos de ROM o subcarpetas, no se extrae: se guarda tal como llega, para no mezclar ni perder archivos.",
        StorageFormat.Zip => "• Se crea un zip por ROM (si venía en 7z o rar, se recomprime).",
        _ => "• Se crea un 7z por ROM con el 7-Zip incorporado en la aplicación."
    }) + "\r\n• Si el sistema no admite el formato (según su systeminfo.txt), se usa el más parecido que sí admita.";

    // Ajusta la altura del formulario al texto más largo de todos los formatos y centra el texto verticalmente en ese hueco.
    private void FitHint()
    {
        int width = hint.Width;
        int tallest = Enum.GetValues<StorageFormat>()
            .Max(f => TextRenderer.MeasureText(Loc.T(HintText(f)), Font, new Size(width, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height);
        hint.Height = tallest + 8;
        foreach (Control c in Controls) if (c is Button) c.Top = hint.Bottom + 8;
        ClientSize = new Size(ClientSize.Width, hint.Bottom + 8 + 28 + 12);
    }
}
