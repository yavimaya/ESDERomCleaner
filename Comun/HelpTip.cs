namespace ESDERomCleaner;

// Ayuda emergente de varias líneas para un control. A diferencia del ToolTip nativo, se coloca ella misma dentro del área de trabajo del monitor donde está
// el control (no se sale por los bordes, la barra de tareas ni a otro monitor), prefiere ponerse encima si el control está en la mitad inferior de la pantalla,
// ajusta el ancho al monitor y a su escala (DPI), parte las líneas largas, no roba el foco y se oculta al salir el ratón, pulsar, mover o desactivar la ventana.
internal sealed class HelpTip
{
    private readonly Control owner;
    private readonly string text; // en español: se traduce al mostrarse, así sigue al idioma elegido
    private readonly System.Windows.Forms.Timer showTimer = new() { Interval = 400 };
    private readonly System.Windows.Forms.Timer hideTimer = new() { Interval = 25000 };
    private ToolStripDropDown? drop;

    private HelpTip(Control owner, string text)
    {
        this.owner = owner; this.text = text;
        showTimer.Tick += (_, _) => { showTimer.Stop(); Show(); };
        hideTimer.Tick += (_, _) => Hide();
        owner.MouseEnter += (_, _) => showTimer.Start(); // también con el botón desactivado: explica qué hará cuando se active
        owner.MouseLeave += (_, _) => Hide();
        owner.MouseDown += (_, _) => Hide();
        owner.EnabledChanged += (_, _) => Hide();
        owner.VisibleChanged += (_, _) => Hide();
        owner.Disposed += (_, _) => { Hide(); showTimer.Dispose(); hideTimer.Dispose(); };
        owner.HandleCreated += (_, _) =>
        {
            if (owner.FindForm() is not { } form) return;
            form.Deactivate += (_, _) => Hide();
            form.LocationChanged += (_, _) => Hide();
            form.SizeChanged += (_, _) => Hide();
            form.FormClosing += (_, _) => Hide();
        };
    }

    public static void Attach(Control owner, string text) => _ = new HelpTip(owner, text);

    private void Hide()
    {
        showTimer.Stop(); hideTimer.Stop();
        if (drop is null) return;
        try { drop.Close(); drop.Dispose(); } catch { }
        drop = null;
    }

    private void Show()
    {
        if (!owner.IsHandleCreated || !owner.Visible || owner.FindForm() is not { } form || Form.ActiveForm != form) return;
        Hide();
        var bounds = owner.RectangleToScreen(owner.ClientRectangle);
        var area = Screen.FromRectangle(bounds).WorkingArea;
        float scale = owner.DeviceDpi / 96f;
        int maxWidth = Math.Min((int)(460 * scale), area.Width - 24);
        var font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        string shown = Loc.T(text);
        const TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top;
        var measured = TextRenderer.MeasureText(shown, font, new Size(maxWidth, 0), flags);
        int pad = (int)(6 * scale);
        var size = new Size(Math.Min(measured.Width + 2 * pad, maxWidth + 2 * pad), measured.Height + 2 * pad);
        var label = new Label
        {
            Text = shown, Font = font, AutoSize = false, Size = size, Margin = Padding.Empty, UseMnemonic = false,
            BackColor = Color.FromArgb(255, 255, 225), ForeColor = Color.Black, Padding = new Padding(pad), BorderStyle = BorderStyle.FixedSingle
        };
        var host = new ToolStripControlHost(label) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = size };
        drop = new ToolStripDropDown { AutoClose = false, AutoSize = false, Padding = Padding.Empty, Margin = Padding.Empty, DropShadowEnabled = false, Size = size };
        drop.Items.Add(host);

        int gap = (int)(6 * scale);
        // Encima si el control está en la mitad inferior del monitor (así no tapa lo de debajo ni se sale por abajo); si no, debajo. Si no cabe, al otro lado.
        bool above = bounds.Top + bounds.Height / 2 > area.Top + area.Height / 2;
        int y = above ? bounds.Top - size.Height - gap : bounds.Bottom + gap;
        if (y < area.Top) y = bounds.Bottom + gap;
        if (y + size.Height > area.Bottom) y = bounds.Top - size.Height - gap;
        y = Math.Clamp(y, area.Top + 2, Math.Max(area.Top + 2, area.Bottom - size.Height - 2));
        int x = Math.Clamp(bounds.Left + (bounds.Width - size.Width) / 2, area.Left + 2, Math.Max(area.Left + 2, area.Right - size.Width - 2));
        drop.Show(new Point(x, y));
        hideTimer.Start();
    }
}
