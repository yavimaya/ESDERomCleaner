namespace ESDERomCleaner;

// Barra de pestañas plana, dibujada a mano para casar con el resto de formularios (la pestaña activa en blanco con un filete dorado arriba).
// No contiene páginas: quien la usa muestra u oculta lo que corresponda al cambiar la pestaña seleccionada.
internal sealed class TabBar : Control
{
    private readonly List<(string Text, Color Color)> tabs = [];
    private int selected;
    private int hot = -1;
    private readonly List<Rectangle> bounds = [];
    private readonly HashSet<int> hidden = []; // pestañas ocultas: no se dibujan ni se pueden elegir
    private readonly Dictionary<int, Image> icons = new(); // iconos propios (16 px) de las pestañas que no usan un símbolo de texto

    public event Action? SelectedChanged;

    public TabBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = false;
        Height = 30; Cursor = Cursors.Hand;
        BackColor = RaUi.Body;
    }

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Selected
    {
        get => selected;
        set { if (value == selected || value < 0 || value >= tabs.Count) return; selected = value; Invalidate(); SelectedChanged?.Invoke(); }
    }

    public void Add(string text) { tabs.Add((text, RaUi.Dark)); Invalidate(); }

    // Muestra u oculta una pestaña. Si se oculta la seleccionada, pasa a la primera.
    public void SetVisible(int index, bool visible)
    {
        if (index < 0 || index >= tabs.Count) return;
        bool changed = visible ? hidden.Remove(index) : hidden.Add(index);
        if (!changed) return;
        if (!visible && selected == index) Selected = 0;
        Invalidate();
    }

    // Icono de 16 px dibujado antes del texto de la pestaña.
    public void SetIcon(int index, Image icon) { icons[index] = icon; Invalidate(); }

    // Texto de una pestaña y, opcionalmente, el color del texto (para resaltar las que tienen algo pendiente).
    public void SetTab(int index, string text, Color? color = null)
    {
        if (index < 0 || index >= tabs.Count) return;
        var value = (text, color ?? RaUi.Dark);
        if (tabs[index] == value) return;
        tabs[index] = value;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = bounds.FindIndex(b => b.Contains(e.Location));
        if (index != hot) { hot = index; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot != -1) { hot = -1; Invalidate(); } }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        int index = bounds.FindIndex(b => b.Contains(e.Location));
        if (index >= 0) Selected = index;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        bounds.Clear();
        int x = 0, bottom = Height - 1;
        using var line = new Pen(RaUi.Border);
        using var bold = new Font(Font, FontStyle.Bold);
        // Texto con GDI+ para que los símbolos (❓ ⛔) usen la fuente de reserva que corresponda.
        using var format = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        for (int i = 0; i < tabs.Count; i++)
        {
            if (hidden.Contains(i)) { bounds.Add(Rectangle.Empty); continue; }
            var font = i == selected ? bold : Font;
            float width = g.MeasureString(tabs[i].Text, bold, int.MaxValue, format).Width;
            int iconWidth = icons.ContainsKey(i) ? 22 : 0; // 16 px de icono y 6 de separación
            var rect = new Rectangle(x, 2, (int)width + 32 + iconWidth, Height - 2);
            bounds.Add(rect);
            bool on = i == selected;
            using (var fill = new SolidBrush(on ? Color.White : i == hot ? Color.FromArgb(244, 245, 248) : RaUi.FooterBack)) g.FillRectangle(fill, rect);
            g.DrawRectangle(line, rect.X, rect.Y, rect.Width, rect.Height);
            if (on)
            {
                using var gold = new SolidBrush(RaUi.Gold);
                g.FillRectangle(gold, rect.X, rect.Y, rect.Width + 1, 3);
                using var white = new SolidBrush(Color.White);
                g.FillRectangle(white, rect.X + 1, bottom, rect.Width - 1, 1); // la pestaña activa se funde con el contenido
            }
            if (icons.TryGetValue(i, out var icon))
                g.DrawImage(icon, new Rectangle(rect.X + 16, rect.Y + (rect.Height - 16) / 2 + 1, 16, 16));
            using (var text = new SolidBrush(tabs[i].Color)) g.DrawString(tabs[i].Text, font, text, new RectangleF(rect.X + iconWidth, rect.Y + 2, rect.Width - iconWidth, rect.Height - 2), format);
            x += rect.Width + 2;
        }
        // Línea de base a la derecha de las pestañas (bajo la activa queda abierta).
        if (selected >= 0 && selected < bounds.Count)
        {
            g.DrawLine(line, 0, bottom, bounds[selected].X, bottom);
            g.DrawLine(line, bounds[selected].Right, bottom, Width, bottom);
        }
    }
}
