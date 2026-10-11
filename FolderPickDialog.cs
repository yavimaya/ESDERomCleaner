namespace ESDERomCleaner;

// Elegir una carpeta de sistema de ES-DE de entre todas: primero las que ya existen en la colección, luego el resto, con un filtro por texto.
internal sealed class FolderPickDialog : Form
{
    private readonly TextBox filter = new() { PlaceholderText = "Filtrar…" };
    private readonly ListBox list = new() { IntegralHeight = false };
    private readonly List<string> all;

    public string? Selected => list.SelectedItem as string;

    public FolderPickDialog(IEnumerable<string> existing, IEnumerable<string> others, int count)
    {
        Text = "Asignar a carpeta";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        ClientSize = new Size(360, 420);

        var existingList = existing.ToList();
        all = existingList.Concat(others.Where(o => !existingList.Contains(o, StringComparer.OrdinalIgnoreCase))).ToList();

        var label = new Label { Text = $"Carpeta de destino para {count} ROM(s) (las que ya tienes van primero):", Location = new Point(10, 8), Size = new Size(340, 36), AutoSize = false };
        filter.Location = new Point(10, 48); filter.Size = new Size(340, 23);
        list.Location = new Point(10, 76); list.Size = new Size(340, 296);
        var ok = new Button { Text = "Asignar", Size = new Size(90, 28), Location = new Point(160, 382), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancelar", Size = new Size(90, 28), Location = new Point(260, 382), DialogResult = DialogResult.Cancel };
        AcceptButton = ok; CancelButton = cancel;
        Controls.AddRange([label, filter, list, ok, cancel]);

        Fill();
        filter.TextChanged += (_, _) => Fill();
        list.DoubleClick += (_, _) => { if (list.SelectedItem is not null) DialogResult = DialogResult.OK; };
    }

    private void Fill()
    {
        string text = filter.Text.Trim();
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var name in all.Where(n => text.Length == 0 || n.Contains(text, StringComparison.OrdinalIgnoreCase))) list.Items.Add(name);
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        list.EndUpdate();
    }
}
