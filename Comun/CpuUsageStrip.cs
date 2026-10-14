namespace ESDERomCleaner;

// Barra inferior con el uso de CPU del proceso (sobre el total de la máquina), igual que la casilla de la barra de la descarga masiva.
// Con withQuota (formularios que consultan ScreenScraper) lleva además, a la izquierda, las peticiones de hoy, y la CPU pasa a la derecha.
internal sealed class CpuUsageStrip : StatusStrip
{
    private readonly ToolStripStatusLabel cpu = new()
    {
        AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, ToolTipText = "Uso de CPU del proceso (sobre el total de la máquina).",
        BorderSides = ToolStripStatusLabelBorderSides.Right, BorderStyle = Border3DStyle.Etched
    };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private TimeSpan lastCpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
    private long lastTick = Environment.TickCount64;

    private readonly ToolStripStatusLabel quota = new()
    {
        AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, ToolTipText = "Peticiones de hoy según ScreenScraper y límite diario de tu cuenta.",
        BorderSides = ToolStripStatusLabelBorderSides.Right, BorderStyle = Border3DStyle.Etched, Tag = "noloc"
    };
    private readonly bool withQuota;

    public CpuUsageStrip(bool withQuota = false)
    {
        this.withQuota = withQuota;
        Dock = DockStyle.Bottom;
        AutoSize = false;
        SizingGrip = false;
        ShowItemToolTips = true;
        if (withQuota)
        {
            Items.Add(quota);
            Items.Add(new ToolStripStatusLabel { Spring = true });
            cpu.BorderSides = ToolStripStatusLabelBorderSides.Left;
            UpdateQuota();
            ScreenScraperQuota.Changed += OnQuotaChanged;
        }
        Items.Add(cpu);
        cpu.Width = TextRenderer.MeasureText("CPU: 100 %", Font).Width + 18;
        timer.Tick += (_, _) => Update();
        timer.Start();
    }

    // Misma altura (22 px lógicos) que la barra de la descarga masiva en todas las ventanas, sea cual sea el autoescalado de la ventana.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Height = LogicalToDeviceUnits(22);
    }

    // Porcentaje de CPU del proceso desde la última lectura, sobre todos los núcleos.
    private void Update()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var total = process.TotalProcessorTime;
            long now = Environment.TickCount64;
            double elapsedMs = now - lastTick;
            if (elapsedMs <= 0) return;
            double percent = (total - lastCpu).TotalMilliseconds / (elapsedMs * Environment.ProcessorCount) * 100;
            lastCpu = total; lastTick = now;
            cpu.Text = $"CPU: {Math.Clamp(percent, 0, 100):0} %";
        }
        catch { cpu.Text = ""; }
    }

    private void OnQuotaChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(UpdateQuota); } catch (InvalidOperationException) { }
    }

    private void UpdateQuota() => quota.Text = Loc.T(ScreenScraperQuota.TodayText());

    protected override void Dispose(bool disposing)
    {
        if (disposing && withQuota) ScreenScraperQuota.Changed -= OnQuotaChanged;
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
