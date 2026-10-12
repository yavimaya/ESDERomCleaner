namespace ESDERomCleaner;

// Datos para importar un DAT propio: nombre, origen, a qué carpetas de ES-DE corresponde y si se identifica por el nombre del archivo.
internal sealed class ImportDatDialog : LocalizedForm
{
    private readonly TextBox nameBox = new();
    private readonly ComboBox kindBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckedListBox folders = new() { CheckOnClick = true, IntegralHeight = false };
    private readonly CheckBox byName = new() { AutoSize = true, Text = "Identificar por el nombre del archivo (MAME, FBNeo y similares) en lugar de por hash" };

    public string DatName => nameBox.Text.Trim();
    public string Kind => kindBox.SelectedIndex switch { 0 => "no-intro", 1 => "redump", 2 => "tosec", 3 => "mame", 4 => "fbneo", _ => "other" };
    public string[] Folders => folders.CheckedItems.Cast<string>().ToArray();
    public bool NameMatch => byName.Checked;

    public ImportDatDialog(string file, string suggestedName, bool looksArcade, IEnumerable<string> existingFolders)
    {
        Text = "Importar base de datos";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        ClientSize = new Size(520, 470);

        var fileLabel = new Label { Text = "Archivo: " + Path.GetFileName(file), Location = new Point(10, 10), Size = new Size(500, 20), AutoEllipsis = true };
        var nameLabel = new Label { Text = "Nombre:", Location = new Point(10, 40), Size = new Size(80, 22) };
        nameBox.Location = new Point(95, 37); nameBox.Size = new Size(415, 23); nameBox.Text = suggestedName;
        var kindLabel = new Label { Text = "Origen:", Location = new Point(10, 72), Size = new Size(80, 22) };
        kindBox.Items.AddRange(["No-Intro", "Redump", "TOSEC", "MAME", "FBNeo", "Otra (GoodTools, clrmamepro, emuladores…)"]);
        kindBox.Location = new Point(95, 69); kindBox.Size = new Size(415, 23); kindBox.SelectedIndex = 5;
        var foldersLabel = new Label { Text = "Carpetas de ES-DE a las que corresponde (marca las que quieras; las que ya tienes van primero):", Location = new Point(10, 104), Size = new Size(500, 36) };
        folders.Location = new Point(10, 142); folders.Size = new Size(500, 250);
        var existing = existingFolders.ToList();
        foreach (var name in existing.Concat(EsDeStructure.Systems.Where(s => !existing.Contains(s, StringComparer.OrdinalIgnoreCase))))
            folders.Items.Add(name);
        byName.Location = new Point(10, 400); byName.MaximumSize = new Size(500, 0); byName.Checked = looksArcade;

        var ok = new Button { Text = "Importar", DialogResult = DialogResult.None, Size = new Size(100, 28), Location = new Point(300, 432) };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Size = new Size(100, 28), Location = new Point(410, 432) };
        ok.Click += (_, _) =>
        {
            if (DatName.Length == 0) { QuietMessage.Show(this, "Importar base de datos", "Escribe un nombre para la base de datos."); return; }
            if (Folders.Length == 0) { QuietMessage.Show(this, "Importar base de datos", "Marca al menos una carpeta de ES-DE."); return; }
            DialogResult = DialogResult.OK;
        };
        AcceptButton = ok; CancelButton = cancel;
        Controls.AddRange([fileLabel, nameLabel, nameBox, kindLabel, kindBox, foldersLabel, folders, byName, ok, cancel]);
    }
}
