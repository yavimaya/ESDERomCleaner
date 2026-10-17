namespace ESDERomCleaner;

// Botón pequeño con una flecha (▲ o ▼) dibujada con líneas, para ampliar o restaurar un panel desde su título. Sin texto: la ayuda emergente dice qué hace.
internal sealed class ChevronButton : Control
{
    private bool hover;

    // false: flecha hacia arriba (ampliar); true: hacia abajo (restaurar).
    private bool down;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Down { get => down; set { down = value; Invalidate(); } }

    public ChevronButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        SetStyle(ControlStyles.Selectable, false);
        Size = new Size(18, 16);
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        bool active = hover || down;
        using (var back = new SolidBrush(active ? Color.FromArgb(255, 244, 207) : SystemColors.Control)) g.FillRectangle(back, ClientRectangle);
        using (var edge = new Pen(active ? Color.FromArgb(219, 164, 34) : SystemColors.ControlDark)) g.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
        float cx = Width / 2f, cy = Height / 2f;
        PointF[] points = down ? [new(cx - 4, cy - 2), new(cx, cy + 2), new(cx + 4, cy - 2)] : [new(cx - 4, cy + 2), new(cx, cy - 2), new(cx + 4, cy + 2)];
        using var pen = new Pen(Color.FromArgb(52, 56, 68), 1.8f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round, StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        g.DrawLines(pen, points);
    }
}
