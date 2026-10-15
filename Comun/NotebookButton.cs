namespace ESDERomCleaner;

internal sealed class NotebookButton : Button
{
    public NotebookButton()
    {
        FlatStyle = FlatStyle.Standard;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float scale = DeviceDpi / 96f;
        float width = 17 * scale, height = 19 * scale;
        float x = (ClientSize.Width - width) / 2f;
        float y = (ClientSize.Height - height) / 2f;
        using var outline = new Pen(ForeColor, Math.Max(1, 1.2f * scale));
        using var page = new SolidBrush(Enabled ? SystemColors.Window : SystemColors.Control);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.FillRectangle(page, x + 2 * scale, y, width - 2 * scale, height);
        e.Graphics.DrawRectangle(outline, x + 2 * scale, y, width - 2 * scale, height);
        e.Graphics.DrawLine(outline, x + 5 * scale, y, x + 5 * scale, y + height);
        for (int line = 1; line <= 3; line++)
        {
            float lineY = y + (4 + line * 3) * scale;
            e.Graphics.DrawLine(outline, x + 7 * scale, lineY, x + width - 2 * scale, lineY);
        }
        for (int mark = 0; mark < 4; mark++)
        {
            float markY = y + (2 + mark * 4) * scale;
            e.Graphics.DrawLine(outline, x, markY, x + 3 * scale, markY);
        }
    }
}

// Barra de menús que responde al primer clic aunque la ventana no estuviera activa (por defecto WinForms usa ese clic solo para activarla).
internal sealed class ClickThroughMenuStrip : MenuStrip
{
    private const int WM_MOUSEACTIVATE = 0x21, MA_ACTIVATE = 1, MA_ACTIVATEANDEAT = 2;

    // Estilo plano y claro: fondo blanco con línea fina debajo; el menú activo se resalta en dorado suave con subrayado dorado.
    public ClickThroughMenuStrip()
    {
        Renderer = new FlatMenuRenderer();
        BackColor = Color.White; ForeColor = RaUi.Dark;
        Padding = new Padding(8, 2, 8, 0);
        GripStyle = ToolStripGripStyle.Hidden;
    }

    // Los desplegables sin imágenes se generan sin la columna de imágenes (la de casillas solo si algún elemento es marcable).
    protected override void OnItemAdded(ToolStripItemEventArgs e)
    {
        base.OnItemAdded(e);
        if (e.Item is ToolStripMenuItem item) Hook(item);
    }

    private static void Hook(ToolStripMenuItem item)
    {
        item.DropDownOpening -= Trim; item.DropDownOpening += Trim;
    }

    private static void Trim(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem item || item.DropDown is not ToolStripDropDownMenu menu) return;
        var children = item.DropDownItems.OfType<ToolStripMenuItem>().ToList();
        menu.ShowImageMargin = children.Any(c => c.Image is not null);
        menu.ShowCheckMargin = children.Any(c => c.CheckOnClick || c.Checked);
        foreach (var child in children) Hook(child);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // Si el clic solo iba a activar la ventana (se «come»), se deja pasar también al menú.
        if (m.Msg == WM_MOUSEACTIVATE && m.Result == (IntPtr)MA_ACTIVATEANDEAT) m.Result = (IntPtr)MA_ACTIVATE;
    }
}

internal sealed class FlatMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color Tint = Color.FromArgb(251, 243, 220);

    private sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.White;
        public override Color ImageMarginGradientBegin => Color.White;
        public override Color ImageMarginGradientMiddle => Color.White;
        public override Color ImageMarginGradientEnd => Color.White;
        public override Color MenuBorder => RaUi.Border;
        public override Color MenuItemBorder => Tint;
        public override Color MenuItemSelected => Tint;
        public override Color MenuItemSelectedGradientBegin => Tint;
        public override Color MenuItemSelectedGradientEnd => Tint;
        public override Color MenuItemPressedGradientBegin => Tint;
        public override Color MenuItemPressedGradientMiddle => Tint;
        public override Color MenuItemPressedGradientEnd => Tint;
        public override Color SeparatorDark => RaUi.Border;
        public override Color SeparatorLight => Color.White;
    }

    public FlatMenuRenderer() : base(new Colors()) { RoundedEdges = false; }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Color.White);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(RaUi.Border);
        if (e.ToolStrip is MenuStrip) e.Graphics.DrawLine(pen, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
        else e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected && !e.Item.Pressed) return;
        var rect = new Rectangle(Point.Empty, e.Item.Size);
        using (var brush = new SolidBrush(Tint)) e.Graphics.FillRectangle(brush, rect);
        if (e.Item.Owner is MenuStrip)
        {
            using var gold = new SolidBrush(RaUi.Gold);
            e.Graphics.FillRectangle(gold, 0, rect.Height - 2, rect.Width, 2);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? RaUi.Dark : RaUi.Muted;
        base.OnRenderItemText(e);
    }
}
