namespace ESDERomCleaner;

// Con qué romset (DAT de MAME) se creó cada carpeta de arcade de la colección. Se guarda en settings.json al añadir ROMs y se usa para elegir el catver.ini
// correcto al categorizar; si no hay dato, se le pregunta al usuario entre los DAT de arcade que tiene seleccionados.
internal static class ArcadeSets
{
    private const string Key = "ArcadeSets";

    private static Dictionary<string, string> Load() =>
        new(AppSettings.Get(Key, new Dictionary<string, string>()), StringComparer.OrdinalIgnoreCase);

    public static string? Get(string folder) => Load().TryGetValue(folder, out var dat) ? dat : null;

    public static void Set(string folder, string dat) => SetMany([new KeyValuePair<string, string>(folder, dat)]);

    public static void SetMany(IEnumerable<KeyValuePair<string, string>> items)
    {
        var all = Load();
        bool changed = false;
        foreach (var (folder, dat) in items)
            if (!all.TryGetValue(folder, out var current) || !current.Equals(dat, StringComparison.OrdinalIgnoreCase)) { all[folder] = dat; changed = true; }
        if (changed) AppSettings.Set(Key, all);
    }

    // DAT con el que se identificó una ROM: el último tramo de «Nombre · MAME · MAME 2003-Plus».
    public static string? DatOf(string method) => method.Contains(" · ") ? method.Split(" · ")[^1].Trim() : null;

    // DAT de MAME seleccionados que tienen catver.ini conocido.
    public static List<string> Available() =>
        RomDatabase.Allowed(DatSettings.Load()).Where(s => s.Kind == "mame" && ArcadeCategories.Applies(s.Name)).Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

// Pregunta con qué romset se creó una carpeta de arcade. «Usar este romset» devuelve OK con el elegido; «Consultar a ScreenScraper» devuelve Cancel.
// «Más DAT…» abre el gestor de DAT (descargar o importar) y, al cerrarlo, vuelve a leer los DAT de arcade disponibles.
internal sealed class ArcadeSetPickerDialog : LocalizedForm
{
    private const int Visible = 5; // filas visibles de la lista (con desplazamiento si hay más)
    private readonly Panel list = new() { AutoScroll = true };
    private readonly Label note = new() { AutoSize = false, ForeColor = SystemColors.GrayText };
    private readonly Button ok = new() { Text = "Usar este romset", DialogResult = DialogResult.OK, AutoSize = false, Size = new Size(140, 32) };
    private readonly Func<List<string>> moreSets;
    public string Selected => list.Controls.OfType<RadioButton>().FirstOrDefault(r => r.Checked)?.Tag as string ?? "";

    public ArcadeSetPickerDialog(string folder, IReadOnlyList<string> sets, Func<List<string>> moreSets)
    {
        this.moreSets = moreSets;
        Text = "Categorizar";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        BackColor = RaUi.Body;
        ClientSize = new Size(560, 84 + 14 + Visible * 30 + 4 + 8 + 40 + 8 + 52);

        var banner = new RaHeader($"¿Con qué romset se creó «{folder}»?", "La categoría de cada juego sale de la lista de su versión de MAME.", 84, 48, logo: false);
        banner.Icon.Image = AskIcon();
        list.Location = new Point(24, 84 + 14); list.Size = new Size(512, Visible * 30 + 4);
        note.Location = new Point(24, list.Bottom + 8); note.Size = new Size(512, 40);
        var footer = RaUi.Footer();
        var more = new Button { Text = "Más DAT…", AutoSize = false, Size = new Size(110, 32) };
        var ask = new Button { Text = "Consultar a ScreenScraper", DialogResult = DialogResult.Cancel, AutoSize = false, Size = new Size(190, 32) };
        RaUi.StyleButton(ok, true); RaUi.StyleButton(ask, false); RaUi.StyleButton(more, false);
        ok.Location = new Point(560 - 24 - ok.Width, 10);
        ask.Location = new Point(ok.Left - 10 - ask.Width, 10);
        more.Location = new Point(24, 10);
        footer.Controls.AddRange([more, ask, ok]);
        Controls.AddRange([banner, list, note, footer]);
        AcceptButton = ok; CancelButton = ask;
        more.Click += (_, _) => Fill(moreSets(), Selected);
        Fill(sets, null);
    }

    private void Fill(IReadOnlyList<string> sets, string? keep)
    {
        list.Controls.Clear();
        int y = 2;
        foreach (string set in sets)
        {
            var radio = new RadioButton { Text = set, Tag = set, Location = new Point(6, y), Size = new Size(470, 26) };
            radio.Checked = keep is not null ? set.Equals(keep, StringComparison.OrdinalIgnoreCase) : y == 2;
            list.Controls.Add(radio); y += 30;
        }
        if (sets.Count > 0 && Selected.Length == 0) list.Controls.OfType<RadioButton>().First().Checked = true;
        ok.Enabled = sets.Count > 0;
        note.Text = sets.Count > 0
            ? "Solo salen los DAT de arcade que tienes seleccionados. Se recuerda para esta carpeta; al añadir ROMs también se guarda solo."
            : "No hay ningún DAT de arcade seleccionado. Usa «Más DAT…» para descargar o importar uno, o consulta a ScreenScraper.";
    }

    // El chip de la aplicación (el de la cabecera del formulario principal) con un signo de interrogación en píxeles en la esquina inferior derecha.
    private static Bitmap AskIcon()
    {
        var bmp = new Bitmap(48, 48);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            if (AppIcon.Value is { } icon) { try { using var chip = new Icon(icon, 48, 48); g.DrawImage(chip.ToBitmap(), 0, 0, 48, 48); } catch { } }
            string[] glyph = ["01110", "10001", "00001", "00110", "00100", "00000", "00100"];
            const int cell = 3, w = 5 * cell, h = 7 * cell, pad = 3;
            int x0 = 48 - w - 2 * pad - 1, y0 = 48 - h - 2 * pad - 1;
            using var back = new SolidBrush(Color.FromArgb(24, 26, 43));
            using var edge = new Pen(Color.FromArgb(219, 164, 34), 2);
            g.FillRectangle(back, x0, y0, w + 2 * pad, h + 2 * pad);
            g.DrawRectangle(edge, x0, y0, w + 2 * pad - 1, h + 2 * pad - 1);
            using var gold = new SolidBrush(Color.FromArgb(255, 205, 70));
            for (int r = 0; r < glyph.Length; r++)
                for (int c = 0; c < 5; c++)
                    if (glyph[r][c] == '1') g.FillRectangle(gold, x0 + pad + c * cell, y0 + pad + r * cell, cell, cell);
        }
        return bmp;
    }
}
