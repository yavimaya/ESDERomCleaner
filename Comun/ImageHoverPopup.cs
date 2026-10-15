namespace ESDERomCleaner;

// Ventanita sin foco con una imagen; se cierra al apartar el ratón y no capta el ratón (el puntero sigue sobre la tabla).
internal sealed class ImageHoverPopup : Form
{
    private const int MaxWidth = 480, MaxHeight = 360;
    private Image? image;

    // La imagen se ajusta (conservando la proporción) a la caja indicada; se muestra centrada en el rectángulo de pantalla dado.
    public ImageHoverPopup(Image image, Size box)
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; TopMost = true;
        BackColor = Color.Black; Padding = Padding.Empty;
        double fit = Math.Min((double)box.Width / image.Width, (double)box.Height / image.Height);
        this.image = image;
        Controls.Add(new PictureBox { Dock = DockStyle.Fill, Image = image, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, Enabled = false });
        ClientSize = new Size(Math.Max(1, (int)(image.Width * fit)), Math.Max(1, (int)(image.Height * fit)));
    }

    public void ShowCentered(IWin32Window owner, Rectangle area)
    {
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        Show(owner);
    }

    public ImageHoverPopup(Image image)
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; TopMost = true;
        BackColor = Color.Black; Padding = Padding.Empty;
        double scale = Math.Min(1.0, Math.Min((double)MaxWidth / image.Width, (double)MaxHeight / image.Height));
        // Las imágenes pequeñas se amplían hasta un mínimo razonable.
        if (image.Width < 240 && image.Height < 180) scale = Math.Min(240.0 / image.Width, 180.0 / image.Height);
        var size = new Size(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
        this.image = image;
        Controls.Add(new PictureBox { Dock = DockStyle.Fill, Image = image, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, Enabled = false });
        ClientSize = size;
    }

    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0084) { m.Result = (IntPtr)(-1); return; } // WM_NCHITTEST: HTTRANSPARENT
        base.WndProc(ref m);
    }

    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080; return cp; } } // NOACTIVATE | TOOLWINDOW

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { image?.Dispose(); image = null; }
    }

    public void ShowNear(IWin32Window owner, Point cursor)
    {
        var area = Screen.FromPoint(cursor).WorkingArea;
        int x = Math.Min(cursor.X + 16, area.Right - Width), y = cursor.Y + 18;
        if (y + Height > area.Bottom) y = Math.Max(area.Top, cursor.Y - Height - 8);
        Location = new Point(Math.Max(area.Left, x), y);
        Show(owner);
    }
}
