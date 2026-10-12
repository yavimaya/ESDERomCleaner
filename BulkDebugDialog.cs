namespace ESDERomCleaner;

// Registro de depuración de la descarga masiva: todo lo que hace, ROM a ROM y carpeta a carpeta. Lo llena el proceso desde cualquier hilo.
internal sealed class BulkDebugLog
{
    private const int MaxLines = 400_000;
    private readonly List<string> lines = [];
    private int removed; // líneas descartadas del principio: los índices de lectura son absolutos

    public int Count { get { lock (lines) return lines.Count; } }

    public void Add(string line)
    {
        lock (lines)
        {
            if (lines.Count >= MaxLines) lines.RemoveRange(0, 50_000); removed += 50_000; // el registro es solo de consulta: se descartan las líneas más antiguas
            lines.Add(line);
        }
    }

    public List<string> Snapshot() { lock (lines) return [.. lines]; }

    // Líneas nuevas desde «next» que cumplen el filtro (sin distinguir mayúsculas; vacío = todas). El filtro es una expresión regular (p. ej. «guardados [1-9]»); si no es
    // una expresión válida, se busca como texto. Devuelve el texto y cuántas líneas coinciden.
    public string TakeFrom(ref int next, string filter, out int matched)
    {
        lock (lines)
        {
            matched = 0;
            int start = Math.Clamp(next - removed, 0, lines.Count); // «next» es absoluto; si se recortó el principio, se sigue desde lo que queda
            if (start == lines.Count) { next = removed + lines.Count; return ""; }
            var text = new System.Text.StringBuilder();
            System.Text.RegularExpressions.Regex? pattern = null;
            if (filter.Length > 0)
                try { pattern = new System.Text.RegularExpressions.Regex(filter, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
                catch (ArgumentException) { }
            for (int i = start; i < lines.Count; i++)
            {
                if (filter.Length > 0)
                {
                    bool match;
                    try { match = pattern is not null ? pattern.IsMatch(lines[i]) : lines[i].Contains(filter, StringComparison.OrdinalIgnoreCase); }
                    catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { match = false; }
                    if (!match) continue;
                }
                text.AppendLine(lines[i]);
                matched++;
            }
            next = removed + lines.Count;
            return text.ToString();
        }
    }

    public void Clear() { lock (lines) lines.Clear(); }
}

// Ventana que va mostrando en directo el registro de depuración de la descarga masiva (no modal: se puede dejar abierta mientras se descarga).
internal sealed class BulkDebugDialog : LocalizedForm
{
    private readonly BulkDebugLog log;
    private readonly TextBox view = new()
    {
        Multiline = true, ReadOnly = true, HideSelection = false, WordWrap = false, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Font = new Font("Consolas", 9F),
        BackColor = Color.FromArgb(24, 26, 32), ForeColor = Color.FromArgb(220, 224, 230), Tag = "noloc"
    };
    // «Live»: con la casilla marcada la vista sigue el final del registro; sin marcar se queda quieta (las líneas nuevas se acumulan y aparecen al volver a marcarla).
    private readonly CheckBox follow = new() { Text = "Live", Checked = true, AutoSize = true };
    private readonly TextBox filterBox = new() { Width = 220, PlaceholderText = "Filtrar (admite expresiones regulares)…" };
    private readonly Label counter = new() { AutoSize = true, Tag = "noloc", ForeColor = SystemColors.GrayText };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private int next, shown;
    private bool refilter;

    public BulkDebugDialog(BulkDebugLog log)
    {
        this.log = log;
        Text = "Registro de la descarga masiva";
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        ClientSize = new Size(980, 620);
        MinimumSize = new Size(640, 360);
        ShowInTaskbar = false;

        var copy = new Button { Text = "Copiar todo", AutoSize = true, MinimumSize = new Size(100, 28) };
        var save = new Button { Text = "Guardar…", AutoSize = true, MinimumSize = new Size(100, 28) };
        var clear = new Button { Text = "Vaciar vista", AutoSize = true, MinimumSize = new Size(100, 28) };
        var close = new Button { Text = "Cerrar", AutoSize = true, MinimumSize = new Size(90, 28) };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(8, 6, 8, 4) };
        follow.Margin = new Padding(8, 6, 12, 0); filterBox.Margin = new Padding(0, 4, 0, 0); counter.Margin = new Padding(14, 7, 0, 0);
        bar.Controls.AddRange([copy, save, clear, close, follow, filterBox, counter]);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.Controls.Add(view, 0, 0);
        layout.Controls.Add(bar, 0, 1);
        Controls.Add(layout);
        CancelButton = close;

        copy.Click += (_, _) => { try { Clipboard.SetText(string.Join(Environment.NewLine, log.Snapshot())); } catch (Exception ex) { QuietMessage.Show(this, "Registro", ex.Message); } };
        save.Click += (_, _) => SaveToFile();
        clear.Click += (_, _) => { view.Clear(); shown = 0; };
        close.Click += (_, _) => Close(); // ventana no modal: el botón la cierra de forma explícita
        filterBox.TextChanged += (_, _) => refilter = true; // se vuelve a montar la vista con el filtro en el siguiente refresco
        timer.Tick += (_, _) => Refresh_();
        FormClosed += (_, _) => timer.Dispose();
        Shown += (_, _) => { Refresh_(); timer.Start(); };
    }

    private void Refresh_()
    {
        string filter = filterBox.Text.Trim();
        bool rebuild = refilter;
        if (rebuild) { view.Clear(); next = 0; shown = 0; refilter = false; } // con un filtro nuevo se recorre otra vez todo el registro
        if (!follow.Checked && !rebuild) { counter.Text = Summary(filter); return; } // vista parada: no se añade nada hasta volver a activar «Live»
        string text = log.TakeFrom(ref next, filter, out int matched);
        shown += matched;
        counter.Text = Summary(filter);
        if (text.Length == 0) return;
        if (view.TextLength > 3_000_000) { view.Select(0, 1_000_000); view.SelectedText = ""; }
        view.AppendText(text);
        if (follow.Checked) { view.SelectionStart = view.TextLength; view.ScrollToCaret(); }
        else { view.SelectionStart = 0; view.ScrollToCaret(); } // con «Live» apagado, un filtro nuevo muestra el principio
    }

    private string Summary(string filter) => filter.Length == 0 ? $"{log.Count:N0} " + Loc.T("líneas") : $"{shown:N0} / {log.Count:N0} " + Loc.T("líneas");

    private void SaveToFile()
    {
        using var dialog = new SaveFileDialog { Filter = Loc.T("Texto") + "|*.txt", FileName = $"descarga-masiva-{DateTime.Now:yyyyMMdd-HHmmss}.txt", DefaultExt = "txt" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllLines(dialog.FileName, log.Snapshot(), new System.Text.UTF8Encoding(true)); }
        catch (Exception ex) { QuietMessage.Show(this, "Registro", ex.Message); }
    }
}
