namespace ESDERomCleaner;

// Aviso pequeño y sin bordes («Preparando…» con una barra animada) que se muestra centrado sobre la ventana mientras se prepara un formulario
// pesado, para que no parezca que no se ha pulsado nada. Se cierra con Dispose.
internal sealed class BusyNotice : Form
{
    private BusyNotice(string text)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(300, 74);
        BackColor = SystemColors.Window;
        Padding = new Padding(1);
        var label = new Label { Text = Loc.T(text), AutoSize = false, Dock = DockStyle.Top, Height = 38, TextAlign = ContentAlignment.BottomCenter };
        var bar = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Height = 8, Width = 240, Location = new Point(30, 50) };
        Controls.Add(bar);
        Controls.Add(label);
        Paint += (_, e) => e.Graphics.DrawRectangle(SystemPens.ControlDark, 0, 0, Width - 1, Height - 1);
    }

    // Muestra el aviso, construye el formulario (lo que tarda) y lo cierra solo cuando el formulario aparece (Shown).
    public static T Create<T>(Form owner, Func<T> create, string text = "Preparando la ventana…") where T : Form
    {
        var notice = Show(owner, text);
        try
        {
            var form = create();
            form.Shown += (_, _) => notice.Dispose();
            return form;
        }
        catch { notice.Dispose(); throw; }
    }

    // Lo muestra ya (y lo pinta antes de volver) centrado sobre «owner».
    public static BusyNotice Show(Form owner, string text)
    {
        var notice = new BusyNotice(text);
        var bounds = owner.Bounds;
        notice.Location = new Point(bounds.X + (bounds.Width - notice.Width) / 2, bounds.Y + (bounds.Height - notice.Height) / 2);
        ((Form)notice).Show(owner);
        notice.Refresh();
        return notice;
    }
}
