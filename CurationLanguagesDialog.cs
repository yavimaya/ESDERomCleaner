namespace ESDERomCleaner;

// Elige los idiomas que entiende el usuario: las ROMs que no estén en ninguno de ellos son candidatas en el curado.
internal sealed class CurationLanguagesDialog : Form
{
    private readonly CheckedListBox list = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly CheckBox onlyTextGenres = new()
    {
        Text = "Solo en géneros que dependen del texto (rol, aventura, novela visual, estrategia, quiz, educativos…)",
        AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(3, 6, 3, 3)
    };
    private readonly CheckBox useFilename = new()
    {
        Text = "Si ScreenScraper no tiene datos, usar el idioma o la región del nombre del archivo",
        AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(3, 3, 3, 3)
    };

    public CurationLanguageSettings Result { get; private set; }

    public CurationLanguagesDialog(CurationLanguageSettings current, string coverage)
    {
        Result = current;
        Text = "Idiomas que entiendes";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(480, 470);
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        foreach (var (code, name) in LanguageCatalog.Common)
            list.Items.Add($"{name} ({code})", current.Understood.Contains(code, StringComparer.OrdinalIgnoreCase));
        onlyTextGenres.Checked = current.OnlyTextGenres;
        useFilename.Checked = current.UseFilename;

        var intro = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Las ROMs cuyos idiomas conocidos no incluyan ninguno de los marcados saldrán como candidatas en «Idiomas no entendibles». " +
                   "Las ROMs de las que no se sabe el idioma no se señalan nunca."
        };
        var coverageLabel = new Label { Dock = DockStyle.Fill, Text = coverage, ForeColor = SystemColors.GrayText };
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        options.Controls.Add(onlyTextGenres);
        options.Controls.Add(useFilename);

        var ok = new Button { Text = "Guardar", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(100, 30) };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(100, 30) };
        ok.Click += (_, _) =>
        {
            var understood = new List<string>();
            for (int i = 0; i < LanguageCatalog.Common.Length; i++)
                if (list.GetItemChecked(i)) understood.Add(LanguageCatalog.Common[i].Code);
            Result = new CurationLanguageSettings { Understood = understood, OnlyTextGenres = onlyTextGenres.Checked, UseFilename = useFilename.Checked };
        };
        var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, WrapContents = false, Margin = Padding.Empty };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        var buttonBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
        buttonBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttonBar.Controls.Add(buttons, 0, 0);
        AcceptButton = ok;
        CancelButton = cancel;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12, 10, 12, 6) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(intro, 0, 0);
        layout.Controls.Add(list, 0, 1);
        layout.Controls.Add(options, 0, 2);
        layout.Controls.Add(coverageLabel, 0, 3);
        layout.Controls.Add(buttonBar, 0, 4);
        Controls.Add(layout);
    }
}
