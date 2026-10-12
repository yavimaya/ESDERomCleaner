namespace ESDERomCleaner;

// Formulario simple para elegir cómo se guardan las ROMs al añadirlas: formato y nivel de compresión.
internal sealed class StorageOptionsDialog : LocalizedForm
{
    private readonly ComboBox formatBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox levelBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label hint = new() { AutoSize = false, ForeColor = SystemColors.GrayText };

    public StorageOptions Result => new((StorageFormat)Math.Max(0, formatBox.SelectedIndex), (StorageLevel)Math.Max(0, levelBox.SelectedIndex));

    public StorageOptionsDialog(StorageOptions current)
    {
        Text = "Cómo guardar las ROMs";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        ClientSize = new Size(440, 190);

        var formatLabel = new Label { Text = "Formato:", Location = new Point(12, 16), Size = new Size(90, 22) };
        formatBox.Location = new Point(110, 13); formatBox.Size = new Size(318, 23);
        formatBox.Items.AddRange(["Como llegan (sin tocarlas)", "Sin comprimir (extraer)", "ZIP", "7z"]);
        var levelLabel = new Label { Text = "Compresión:", Location = new Point(12, 52), Size = new Size(90, 22) };
        levelBox.Location = new Point(110, 49); levelBox.Size = new Size(318, 23);
        levelBox.Items.AddRange(["Sin compresión (almacenar)", "Rápida", "Normal", "Máxima"]);
        hint.Location = new Point(12, 86); hint.Size = new Size(416, 52);

        var ok = new Button { Text = "Aceptar", Size = new Size(90, 28), Location = new Point(244, 148), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancelar", Size = new Size(90, 28), Location = new Point(338, 148), DialogResult = DialogResult.Cancel };
        AcceptButton = ok; CancelButton = cancel;
        Controls.AddRange([formatLabel, formatBox, levelLabel, levelBox, hint, ok, cancel]);

        formatBox.SelectedIndex = (int)current.Format; levelBox.SelectedIndex = (int)current.Level;
        formatBox.SelectedIndexChanged += (_, _) => Refresh_();
        Refresh_();
    }

    // El nivel solo cuenta con zip y 7z; el texto explica cada formato.
    private void Refresh_()
    {
        var format = (StorageFormat)formatBox.SelectedIndex;
        levelBox.Enabled = format is StorageFormat.Zip or StorageFormat.SevenZip;
        hint.Text = format switch
        {
            StorageFormat.Keep => "La ROM se copia tal cual llega (si venía en zip, queda en zip).",
            StorageFormat.Raw => "Lo que venga comprimido se extrae a la carpeta del sistema.",
            StorageFormat.Zip => "Se crea un zip por ROM (si venía en 7z o rar, se recomprime).",
            _ => "Se crea un 7z por ROM con el 7-Zip incorporado en la aplicación."
        };
        hint.Text += "\r\nSi el sistema no admite el formato (según su systeminfo.txt), se usa el más parecido que sí admita.";
    }
}
