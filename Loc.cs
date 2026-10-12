using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace ESDERomCleaner;

// Idioma de la interfaz (español o inglés), con cambio «al vuelo». El código fuente sigue escrito en español: los textos en español son las claves
// y esta clase los traduce al inglés al mostrarlos. Se traduce con la tabla LocTable (textos exactos y plantillas con {0}, {1}…), frase a frase.
//  - Los controles (etiquetas, botones, menús, títulos…) se enganchan con Attach: guardan su texto original en español y se retraducen al cambiar de idioma.
//  - Los diálogos y mensajes pasan por T(...) (QuietMessage, MessageBox).
//  - Un control, elemento de menú o lista con Tag = "noloc" no se traduce (p. ej. los nombres «Español» y «English» del propio menú de idioma).
internal static class Loc
{
    private sealed class Template
    {
        public Regex Pattern = null!;
        public string English = "";
        public string Prefix = "";
        public string Suffix = "";
        public int Literal;
    }

    private sealed class Original { public string? Text; public string? Tip; }

    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal);
    private static readonly List<Template> Templates = [];
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<object, Original> Originals = new();
    private static readonly ConditionalWeakTable<object, object> Attached = new();
    private static readonly List<(WeakReference<ToolTip> Tip, WeakReference<Control> Control, string Spanish)> Tips = [];
    private static bool suppress;

    private static string LanguageFile => Path.Combine(AppPaths.Data, "language.txt");

    public static string Language { get; private set; } = "es";
    public static bool English => Language == "en";
    public static event Action? LanguageChanged;

    static Loc()
    {
        foreach (var (es, en) in LocTable.Pairs)
        {
            // Las claves se guardan sin espacios sobrantes: al traducir, los de los extremos del texto se conservan aparte.
            string key = es.Trim(' ', '\t', '\r', '\n'), value = en.Trim(' ', '\t', '\r', '\n');
            if (key.Length == 0) continue;
            if (Regex.IsMatch(key, @"\{\d+\}")) AddTemplate(key, value);
            else Exact[key] = value;
        }
        Templates.Sort((a, b) => b.Literal.CompareTo(a.Literal));
        // Sin elección guardada se usa el idioma del sistema operativo (español) y, si no es español, inglés.
        try
        {
            string saved = File.ReadAllText(LanguageFile).Trim();
            if (saved is "en" or "es" or "system") Setting = saved;
        }
        catch { /* sin archivo: idioma del sistema */ }
        Language = Resolve(Setting);
        try { Extra = new Dictionary<string, string>(GenreNames.LoadCache(), StringComparer.OrdinalIgnoreCase); } catch { }
    }

    private static void AddTemplate(string es, string en)
    {
        // «Se han añadido {0} ROM(s) de {1} sistemas» → expresión regular con un grupo por hueco.
        var parts = Regex.Split(es, @"(\{\d+\})");
        var pattern = new System.Text.StringBuilder("^");
        int literal = 0;
        foreach (var part in parts)
        {
            var hole = Regex.Match(part, @"^\{(\d+)\}$");
            if (hole.Success) pattern.Append($"(?<g{hole.Groups[1].Value}>.*?)");
            else { pattern.Append(Regex.Escape(part)); literal += part.Length; }
        }
        pattern.Append('$');
        string prefix = parts[0], suffix = parts[^1].StartsWith('{') ? "" : parts[^1];
        Templates.Add(new Template
        {
            Pattern = new Regex(pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)),
            English = en, Prefix = prefix, Suffix = suffix, Literal = literal
        });
    }

    // Traducciones descargadas (nombres de género de ScreenScraper), aparte de las incorporadas en LocTable.
    private static Dictionary<string, string> Extra = new(StringComparer.OrdinalIgnoreCase);

    public static void SetExtra(IReadOnlyDictionary<string, string> map)
    {
        Extra = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        Cache.Clear();
        DataCache.Clear();
        foreach (Form form in Application.OpenForms.Cast<Form>().ToList())
            if (form.IsHandleCreated) form.BeginInvoke(() => Refresh(form));
    }

    // Elección del usuario: «system» (idioma del sistema operativo), «es» o «en».
    public static string Setting { get; private set; } = "system";

    private static string Resolve(string setting) => setting is "en" or "es" ? setting
        : System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? "es" : "en";

    public static void SetLanguage(string code)
    {
        if (code is not ("en" or "es" or "system") || code == Setting) return;
        Setting = code;
        try { Directory.CreateDirectory(AppPaths.Data); File.WriteAllText(LanguageFile, code); } catch { }
        string effective = Resolve(code);
        if (effective == Language) return;
        Language = effective;
        Cache.Clear();
        DataCache.Clear();
        foreach (Form form in Application.OpenForms.Cast<Form>().ToList()) Refresh(form);
        RefreshTips();
        LanguageChanged?.Invoke();
    }

    // ---- Traducción de textos

    public static string T(string spanish)
    {
        if (!English || string.IsNullOrEmpty(spanish)) return spanish;
        return Cache.GetOrAdd(spanish, Translate);
    }

    public static string T(string spanish, params object?[] args) => string.Format(T(spanish), args);

    // Igual que T, pero para textos que son datos (descripciones de juegos, nombres, valores de tablas y listas): traduce lo que reconoce y no anota lo que no.
    private static readonly ConcurrentDictionary<string, string> DataCache = new(StringComparer.Ordinal);

    public static string TData(string spanish)
    {
        if (!English || string.IsNullOrEmpty(spanish)) return spanish;
        return DataCache.GetOrAdd(spanish, s => TranslateCore(s, 0));
    }

    private static string Translate(string s)
    {
        string result = TranslateCore(s, 0);
        if (result == s && LooksSpanish(s)) LogMissing(s);
        return result;
    }

    private static bool LooksSpanish(string s) => Regex.IsMatch(s, @"[áéíóúñ¿¡]|\b(el|la|los|las|de|del|que|para|con|una|un|se|no|hay|sin|por|ya|es)\b", RegexOptions.IgnoreCase) && s.Length > 3;

    private static void LogMissing(string s)
    {
        // Los textos que se muestran en inglés sin estar traducidos se anotan (cada uno una vez) para poder completar la traducción; el archivo se limita a 256 KB.
        try
        {
            string path = Path.Combine(AppPaths.Data, "untranslated.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) return;
            File.AppendAllText(path, s.Replace("\r", "\\r").Replace("\n", "\\n") + Environment.NewLine);
        }
        catch { }
    }

    private static string TranslateCore(string s, int depth)
    {
        if (s.Length == 0 || depth > 6) return s;
        if (Exact.TryGetValue(s, out var exact)) return exact;
        if (Extra.TryGetValue(s, out var extra)) return extra;

        // Espacios al principio y al final (los textos se concatenan con ellos).
        string trimmed = s.Trim(' ', '\t');
        if (trimmed.Length != s.Length)
        {
            if (trimmed.Length == 0) return s;
            int lead = s.IndexOf(trimmed[0]);
            string leading = s[..lead], trailing = s[(lead + trimmed.Length)..];
            string inner = TranslateCore(trimmed, depth + 1);
            return leading + inner + trailing;
        }

        var template = MatchTemplate(s, depth);
        if (template is not null) return template;

        // Varias líneas: se traduce cada una.
        if (s.Contains('\n'))
        {
            var lines = s.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                bool cr = lines[i].EndsWith('\r');
                string line = cr ? lines[i][..^1] : lines[i];
                lines[i] = TranslateCore(line, depth + 1) + (cr ? "\r" : "");
            }
            return string.Join("\n", lines);
        }

        // Varias frases o fragmentos en una línea («a · b», «a; b», «a. b», «Género / Subgénero»).
        var pieces = Regex.Split(s, @"(\s*·\s*|;\s+|(?<=[\.\!\?])\s+| / | — |(?<=\))\s+(?=\())");
        if (pieces.Length > 1)
        {
            for (int i = 0; i < pieces.Length; i += 2) pieces[i] = TranslateCore(pieces[i], depth + 1);
            return string.Concat(pieces);
        }
        return s;
    }

    private static string? MatchTemplate(string s, int depth)
    {
        foreach (var t in Templates)
        {
            if (t.Prefix.Length > 0 && !s.StartsWith(t.Prefix, StringComparison.Ordinal)) continue;
            if (t.Suffix.Length > 0 && !s.EndsWith(t.Suffix, StringComparison.Ordinal)) continue;
            Match m;
            try { m = t.Pattern.Match(s); } catch (RegexMatchTimeoutException) { continue; }
            if (!m.Success) continue;
            // Los valores de los huecos pueden ser texto traducible (p. ej. «normal», «rápida»).
            return Regex.Replace(t.English, @"\{(\d+)\}", h =>
            {
                var g = m.Groups["g" + h.Groups[1].Value];
                if (!g.Success) return h.Value;
                string value = g.Value;
                return value.Any(char.IsLetter) ? TranslateCore(value, depth + 1) : value;
            });
        }
        return null;
    }

    // ---- Controles

    private static bool IsNoLoc(object o) => o switch
    {
        Control c => c.Tag as string == "noloc",
        ToolStripItem i => i.Tag as string == "noloc",
        _ => false
    };

    // Controles cuyo texto es de la interfaz (no lo que escribe el usuario).
    private static bool IsTranslatable(Control c) =>
        !IsNoLoc(c) && (c is Label or ButtonBase or GroupBox or Form or TabPage || (c is TextBoxBase tb && tb.ReadOnly));

    public static void Attach(Control root)
    {
        if (root is null || Attached.TryGetValue(root, out _)) return;
        Attached.Add(root, root);
        if (IsTranslatable(root))
        {
            Originals.GetOrCreateValue(root).Text = root.Text;
            Apply(root);
            root.TextChanged += OnTextChanged;
        }
        foreach (Control child in root.Controls) Attach(child);
        root.ControlAdded += (_, e) => Attach(e.Control!);
        if (root.ContextMenuStrip is { } menu) Attach(menu);
        if (root is ToolStrip strip) AttachStrip(strip);
        if (root is TextBox box && !IsNoLoc(root) && box.PlaceholderText.Length > 0)
        {
            // Texto de ayuda de los cuadros de texto (el que se ve cuando están vacíos).
            Originals.GetOrCreateValue(root).Tip = box.PlaceholderText;
            box.PlaceholderText = T(box.PlaceholderText);
        }
        if (root is DataGridView grid) HookGrid(grid);
        if (root is ListControl list && !IsNoLoc(root)) HookList(list);
        if (root is ListView view) foreach (ColumnHeader h in view.Columns) { Originals.GetOrCreateValue(h).Text = h.Text; h.Text = T(h.Text); }
    }

    private static void OnTextChanged(object? sender, EventArgs e)
    {
        if (suppress || sender is not Control c) return;
        Originals.GetOrCreateValue(c).Text = c.Text;   // el texto nuevo lo ha puesto el programa, en español
        if (English) Apply(c);
    }

    private static void Apply(Control c)
    {
        if (!Originals.TryGetValue(c, out var o) || o.Text is null) return;
        string target = c is TextBoxBase ? TData(o.Text) : T(o.Text); // los cuadros de texto de solo lectura llevan datos (descripciones…): se traducen sin anotar lo que falte
        if (c.Text == target) return;
        suppress = true;
        try { c.Text = target; } finally { suppress = false; }
    }

    private static void AttachStrip(ToolStrip strip)
    {
        foreach (ToolStripItem item in strip.Items) AttachItem(item);
        strip.ItemAdded += (_, e) => { if (e.Item is { } item) AttachItem(item); };
    }

    private static void AttachItem(ToolStripItem item)
    {
        if (Attached.TryGetValue(item, out _)) return;
        Attached.Add(item, item);
        if (!IsNoLoc(item))
        {
            var o = Originals.GetOrCreateValue(item);
            o.Text = item.Text;
            o.Tip = !string.IsNullOrEmpty(item.ToolTipText) && item.ToolTipText != item.Text ? item.ToolTipText : null;
            ApplyItem(item);
            item.TextChanged += (_, _) =>
            {
                if (suppress) return;
                Originals.GetOrCreateValue(item).Text = item.Text;
                if (English) ApplyItem(item);
            };
        }
        if (item is ToolStripDropDownItem drop) AttachStrip(drop.DropDown);
    }

    private static void ApplyItem(ToolStripItem item)
    {
        if (!Originals.TryGetValue(item, out var o)) return;
        suppress = true;
        try
        {
            if (o.Text is not null && item.Text != T(o.Text)) item.Text = T(o.Text);
            if (o.Tip is not null) item.ToolTipText = T(o.Tip);
        }
        finally { suppress = false; }
    }

    // Tablas: las cabeceras de columna se traducen una vez y el contenido de las celdas al dibujarlas (sin cambiar los datos).
    private static void HookGrid(DataGridView grid)
    {
        void Capture(DataGridViewColumn col)
        {
            Originals.GetOrCreateValue(col).Text = col.HeaderText;
            col.HeaderText = T(col.HeaderText);
        }
        foreach (DataGridViewColumn col in grid.Columns) Capture(col);
        grid.ColumnAdded += (_, e) => Capture(e.Column!);
        grid.CellFormatting += (_, e) =>
        {
            if (!English || IsNoLoc(grid) || e.Value is not string s || s.Length == 0) return;
            string t = TData(s);
            if (!ReferenceEquals(t, s) && t != s) { e.Value = t; e.FormattingApplied = true; }
        };
    }

    // Desplegables y listas: el texto de cada elemento se traduce al dibujarlo.
    private static void HookList(ListControl list)
    {
        list.FormattingEnabled = true;
        list.Format += (_, e) =>
        {
            if (!English || e.Value is null) return;
            string s = e.Value.ToString() ?? "";
            string t = TData(s);
            if (t != s) e.Value = t;
        };
    }

    // Vuelve a poner los textos de un formulario y de sus controles en el idioma actual.
    private static void Refresh(Control root)
    {
        if (Originals.TryGetValue(root, out var o) && o.Text is not null) Apply(root);
        foreach (Control child in root.Controls) Refresh(child);
        if (root.ContextMenuStrip is { } menu) Refresh(menu);
        if (root is ToolStrip strip) foreach (ToolStripItem item in strip.Items) RefreshItem(item);
        if (root is TextBox box && Originals.TryGetValue(root, out var po) && po.Tip is not null) box.PlaceholderText = T(po.Tip);
        if (root is DataGridView grid)
        {
            foreach (DataGridViewColumn col in grid.Columns)
                if (Originals.TryGetValue(col, out var co) && co.Text is not null) col.HeaderText = T(co.Text);
            grid.Invalidate();
        }
        if (root is ListControl list && Attached.TryGetValue(root, out _) && !IsNoLoc(root))
        {
            list.FormattingEnabled = false; list.FormattingEnabled = true; // fuerza a volver a formatear los elementos
            root.Invalidate();
        }
        if (root is ListView view)
            foreach (ColumnHeader h in view.Columns)
                if (Originals.TryGetValue(h, out var ho) && ho.Text is not null) h.Text = T(ho.Text);
    }

    private static void RefreshItem(ToolStripItem item)
    {
        ApplyItem(item);
        if (item is ToolStripDropDownItem drop) foreach (ToolStripItem child in drop.DropDownItems) RefreshItem(child);
    }

    // ---- Tooltips (componente ToolTip): se registran para poder retraducirlos

    public static void SetToolTip(ToolTip tip, Control control, string spanish)
    {
        Tips.Add((new WeakReference<ToolTip>(tip), new WeakReference<Control>(control), spanish));
        tip.SetToolTip(control, T(spanish));
    }

    private static void RefreshTips()
    {
        foreach (var (tip, control, spanish) in Tips.ToList())
            if (tip.TryGetTarget(out var t) && control.TryGetTarget(out var c) && !c.IsDisposed) t.SetToolTip(c, T(spanish));
    }
}

// Base de los formularios de la aplicación: al cargarse, se engancha a Loc para traducir sus textos y los que cambien después.
public class LocalizedForm : Form
{
    protected override void OnLoad(EventArgs e)
    {
        Loc.Attach(this);
        base.OnLoad(e);
    }
}

// Sustituye a System.Windows.Forms.MessageBox dentro de la aplicación: traduce el texto y el título antes de mostrarlos.
internal static class MessageBox
{
    public static DialogResult Show(IWin32Window? owner, string text, string caption, MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.None, MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
        => System.Windows.Forms.MessageBox.Show(owner, Loc.T(text), Loc.T(caption), buttons, icon, defaultButton);

    public static DialogResult Show(string text, string caption)
        => System.Windows.Forms.MessageBox.Show(Loc.T(text), Loc.T(caption));
}
