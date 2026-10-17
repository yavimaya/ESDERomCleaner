using System.Text.RegularExpressions;

namespace ESDERomCleaner;

// Regla de curado «Una por juego (regiones)» para ROMs de consola: los DAT de libretro no traen padres y clones, así que se agrupan por título base (el nombre sin las
// etiquetas entre paréntesis) y de cada grupo se conserva la mejor versión según el orden de regiones del usuario (Opciones › Orden de regiones del curado…).
// El resto salen como candidatas. Las versiones beta, prototipo, demo, sin licencia y pirata van siempre por detrás de una versión normal.
internal static class RegionPreferences
{
    public static readonly string[] Keys = ["Spain", "Europe", "World", "USA", "Japan", "Other"];
    private const string SettingsKey = "RegionOrder";

    public static string Label(string key) => key switch
    {
        "Spain" => "España", "Europe" => "Europa", "World" => "Mundo", "USA" => "USA", "Japan" => "Japón", _ => "Otras regiones"
    };

    // Orden guardado (primero la que se prefiere). Si falta alguna región o hay datos raros, se completa con el orden de partida.
    public static List<string> Load()
    {
        var saved = AppSettings.Get(SettingsKey, new List<string>());
        var order = saved.Where(k => Keys.Contains(k)).Distinct().ToList();
        order.AddRange(Keys.Where(k => !order.Contains(k)));
        return order;
    }

    public static void Save(IEnumerable<string> order) => AppSettings.Set(SettingsKey, order.ToList());

    private static readonly Dictionary<string, string> RegionOf = BuildRegionMap();

    private static Dictionary<string, string> BuildRegionMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Spain"] = "Spain", ["World"] = "World", ["Japan"] = "Japan", ["USA"] = "USA", ["Canada"] = "USA" };
        foreach (string r in new[] { "Europe", "France", "Germany", "Italy", "UK", "United Kingdom", "Netherlands", "Sweden", "Norway", "Denmark", "Finland", "Russia", "Portugal", "Poland", "Austria", "Switzerland", "Greece", "Ireland", "Belgium" })
            map[r] = "Europe";
        foreach (string r in new[] { "Korea", "China", "Taiwan", "Hong Kong", "Asia", "Brazil", "Australia", "New Zealand", "Latin America", "Mexico", "Argentina", "India", "Turkey", "Israel", "South Africa", "Unknown" })
            map[r] = "Other";
        return map;
    }

    private static readonly Regex Tags = new(@"\(([^()]*)\)", RegexOptions.Compiled);
    private static readonly Regex AnyTag = new(@"\s*[\(\[][^\)\]]*[\)\]]", RegexOptions.Compiled);
    private static readonly Regex IdentityTag = new(@"^(?:Disc|Disk|Side|Tape|CD|Part|Track|Cart)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LowQuality = new(@"^(?:Beta|Proto|Prototype|Demo|Sample|Unl|Pirate|Kiosk)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Regiones de las etiquetas de un nombre («(USA, Europe)» → USA y Europa). Una etiqueta cuenta como región solo si todos sus elementos lo son.
    private static List<string> RegionsIn(string name)
    {
        var found = new List<string>();
        foreach (Match m in Tags.Matches(name))
        {
            var parts = m.Groups[1].Value.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (parts.Count > 0 && parts.All(RegionOf.ContainsKey)) found.AddRange(parts.Select(p => RegionOf[p]));
        }
        return found;
    }

    // Revisión: «Rev A» → 1, «Rev 2» → 2, «v1.1» → 1,1; sin etiqueta 0.
    private static double Revision(string name)
    {
        double best = 0;
        foreach (Match m in Tags.Matches(name))
        {
            string tag = m.Groups[1].Value.Trim();
            var rev = Regex.Match(tag, @"^Rev\s+([A-Z]|\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase);
            if (rev.Success) best = Math.Max(best, rev.Groups[1].Value.Length == 1 && char.IsLetter(rev.Groups[1].Value[0]) ? char.ToUpperInvariant(rev.Groups[1].Value[0]) - 'A' + 1 : double.Parse(rev.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            var ver = Regex.Match(tag, @"^v(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            if (ver.Success) best = Math.Max(best, double.Parse(ver.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        }
        return best;
    }

    private static bool IsLowQuality(string name) => Tags.Matches(name).Any(m => m.Groups[1].Value.Split(',').Any(p => LowQuality.IsMatch(p.Trim())));

    // Título base del juego más las etiquetas que lo distinguen de verdad (disco, cara, cinta…): «Game (USA) (Disc 2)» → «game|disc 2».
    private static string GroupKey(string system, string stem)
    {
        string title = Regex.Replace(AnyTag.Replace(stem, ""), @"\s+", " ").Trim().ToLowerInvariant();
        var identity = Tags.Matches(stem).Select(m => m.Groups[1].Value.Trim()).Where(t => IdentityTag.IsMatch(t)).Select(t => t.ToLowerInvariant());
        return system.ToLowerInvariant() + "|" + title + "|" + string.Join("+", identity);
    }

    // ROMs que sobran: de cada grupo de versiones del mismo juego, todas menos la mejor. Clave RomKey → motivo (con la versión que se conserva).
    public static Dictionary<string, string> Candidates(IEnumerable<RomRow> roms)
    {
        var order = Load();
        int Rank(string name)
        {
            var regions = RegionsIn(name);
            return regions.Count == 0 ? order.IndexOf("Other") : regions.Min(r => order.IndexOf(r));
        }
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groups = roms.Where(r => r.Path.Length > 0 && !RomIdentifier.ArcadeFolders.Contains(r.System))
            .GroupBy(r => GroupKey(r.System, Path.GetFileNameWithoutExtension(r.RomName)));
        foreach (var group in groups)
        {
            if (group.Count() < 2) continue;
            var ranked = group.Select(r => (Row: r, Stem: Path.GetFileNameWithoutExtension(r.RomName))).Select(x => (x.Row, x.Stem, Low: IsLowQuality(x.Stem), Region: Rank(x.Stem), Rev: Revision(x.Stem)))
                .OrderBy(x => x.Low).ThenBy(x => x.Region).ThenByDescending(x => x.Rev).ThenBy(x => x.Stem, StringComparer.OrdinalIgnoreCase).ToList();
            var best = ranked[0];
            foreach (var other in ranked.Skip(1))
            {
                string why = other.Low && !best.Low ? "versión beta, prototipo o demo"
                    : other.Region != best.Region ? "región " + Label(RegionLabelKey(other.Stem, order))
                    : other.Rev < best.Rev ? "revisión anterior" : "otra versión del mismo juego";
                result[RomKey.Of(other.Row)] = $"{why} (se conserva «{best.Stem}»)";
            }
        }
        return result;
    }

    private static string RegionLabelKey(string stem, List<string> order)
    {
        var regions = RegionsIn(stem);
        return regions.Count == 0 ? "Other" : regions.OrderBy(r => order.IndexOf(r)).First();
    }
}

// Orden de las regiones preferidas: la primera de la lista es la que se conserva. Se abre desde Opciones › Orden de regiones del curado.
internal sealed class RegionOrderDialog : LocalizedForm
{
    private readonly ListBox list = new() { Dock = DockStyle.Fill, IntegralHeight = false, ItemHeight = 26, DrawMode = DrawMode.OwnerDrawFixed };
    private readonly List<string> order;
    public List<string> Result => order.ToList();

    public RegionOrderDialog(List<string> current)
    {
        order = current.ToList();
        Text = "Orden de regiones del curado";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        BackColor = RaUi.Body;
        ClientSize = new Size(460, 440);

        var banner = new RaHeader("Orden de regiones", "En la regla «Una por juego (regiones)» del curado se conserva la versión de la primera región de la lista; las demás salen como candidatas.", 84, 40, logo: false);
        var up = new Button { Text = "▲ Subir", AutoSize = false, Size = new Size(96, 32) };
        var down = new Button { Text = "▼ Bajar", AutoSize = false, Size = new Size(96, 32) };
        var note = new Label { Text = "Las versiones beta, prototipo o demo van siempre por detrás de una versión normal.", AutoSize = false, ForeColor = SystemColors.GrayText };
        var footer = RaUi.Footer();
        var save = new Button { Text = "Guardar", DialogResult = DialogResult.OK, AutoSize = false, Size = new Size(110, 32) };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, AutoSize = false, Size = new Size(110, 32) };
        RaUi.StyleButton(save, true); RaUi.StyleButton(cancel, false); RaUi.StyleButton(up, false); RaUi.StyleButton(down, false);
        save.Location = new Point(460 - 24 - save.Width, 10); cancel.Location = new Point(save.Left - 10 - cancel.Width, 10);
        footer.Controls.AddRange([cancel, save]);

        list.Location = new Point(24, 84 + 14); list.Size = new Size(412, 6 * 26 + 6);
        up.Location = new Point(24, list.Bottom + 10); down.Location = new Point(up.Right + 8, list.Bottom + 10);
        note.Location = new Point(24, up.Bottom + 10); note.Size = new Size(412, 40);
        Controls.AddRange([banner, list, up, down, note, footer]);
        AcceptButton = save; CancelButton = cancel;

        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            e.DrawBackground();
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using var back = new SolidBrush(selected ? Color.FromArgb(255, 236, 179) : e.Index % 2 == 0 ? Color.White : Color.FromArgb(243, 245, 249));
            e.Graphics.FillRectangle(back, e.Bounds);
            TextRenderer.DrawText(e.Graphics, $"{e.Index + 1}   {Loc.T(RegionPreferences.Label(order[e.Index]))}", e.Font, new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height), Color.Black,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        };
        Refill(0);
        void Move(int delta)
        {
            int i = list.SelectedIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= order.Count) return;
            (order[i], order[j]) = (order[j], order[i]);
            Refill(j);
        }
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(1);
    }

    private void Refill(int select)
    {
        list.Items.Clear();
        foreach (string key in order) list.Items.Add(key);
        list.SelectedIndex = Math.Clamp(select, 0, order.Count - 1);
    }
}
