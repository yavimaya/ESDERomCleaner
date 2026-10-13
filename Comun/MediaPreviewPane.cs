using LibVLCSharp.Shared;

namespace ESDERomCleaner;

// Vista previa de un medio local: imagen, vídeo en bucle (LibVLC) o un mensaje. Cada panel tiene su propio
// reproductor, así que pueden sonar o callar por separado para comparar.
internal sealed class MediaPreviewPane : Panel
{
    internal static readonly Lazy<LibVLC> SharedLibVlc = new(() =>
    {
        Core.Initialize();
        // Decodificación por software y salida GDI: los vídeos de las previsualizaciones son pequeños y así no se crea ningún
        // dispositivo Direct3D11, que en este equipo provocaba cierres de la aplicación (excepción en dxgi.dll).
        return new LibVLC("--no-video-title-show", "--no-osd", "--avcodec-hw=none", "--vout=wingdi");
    });

    private readonly PictureBox picture = new() { SizeMode = PictureBoxSizeMode.Zoom };
    private readonly FrameVideoView video = new() { Visible = false };
    private readonly Label message = new() { TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.ControlText, Visible = false };
    private MediaPlayer? player;
    private Media? media;
    private bool sound;
    private string? pendingVideo;
    // Evita arrancar y parar el reproductor en cada fila al recorrer la lista con el teclado.
    private readonly System.Windows.Forms.Timer startTimer = new() { Interval = 350 };

    // Doble clic sobre la imagen o el mensaje de la vista previa (la ventana nativa del vídeo no reenvía clics).
    public event EventHandler? ContentDoubleClick;

    public MediaPreviewPane()
    {
        picture.DoubleClick += (_, _) => ContentDoubleClick?.Invoke(this, EventArgs.Empty);
        message.DoubleClick += (_, _) => ContentDoubleClick?.Invoke(this, EventArgs.Empty);
        video.DoubleClick += (_, _) => ContentDoubleClick?.Invoke(this, EventArgs.Empty);
        startTimer.Tick += (_, _) => { startTimer.Stop(); if (pendingVideo is string path) { pendingVideo = null; StartVideoNow(path); } };
        BackColor = picture.BackColor = message.BackColor = video.BackColor = SystemColors.Control; // mismo fondo que el panel de imagen y vídeo de la ventana principal
        Controls.Add(video);
        Controls.Add(message);
        Controls.Add(picture);
        FitContent();
    }

    // El contenido ocupa siempre un recuadro 4:3 centrado dentro del panel; si el panel tiene otra proporción, sobran bandas
    // del color de fondo, pero la imagen o el vídeo nunca se deforman ni se recortan.
    private void FitContent()
    {
        var area = ClientSize;
        if (area.Width <= 0 || area.Height <= 0) return;
        int width = area.Width, height = width * 3 / 4;
        if (height > area.Height) { height = area.Height; width = height * 4 / 3; }
        var bounds = new Rectangle((area.Width - width) / 2, (area.Height - height) / 2, width, height);
        // El contenido queda dentro del borde que delimita el panel de cada imagen o vídeo, con el mismo margen que en la ventana principal
        // (proporcional al tamaño, de 1 a 5 píxeles).
        int inset = Math.Clamp(Math.Min(bounds.Width, bounds.Height) / 80, 1, 5);
        var inner = Rectangle.Inflate(bounds, -inset, -inset);
        foreach (Control child in new Control[] { picture, video, message })
            if (child.Bounds != inner) child.Bounds = inner;
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        FitContent();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Sound
    {
        get => sound;
        set
        {
            sound = value;
            if (player is not null) { player.Mute = !value; player.Volume = value ? 100 : 0; }
        }
    }

    public void ShowFile(string path)
    {
        string extension = Path.GetExtension(path).TrimStart('.');
        switch (SsMediaInfo.Kind(extension))
        {
            case "image": ShowImage(path); break;
            case "video": PlayVideo(path); break;
            case "pdf": ShowMessage("Manual en PDF.\r\nÁbrelo con su botón para verlo."); break;
            default: ShowMessage("Sin vista previa para este formato."); break;
        }
    }

    public void ShowImage(string path)
    {
        StopVideo();
        try
        {
            using var stream = File.OpenRead(path);
            using var image = Image.FromStream(stream);
            SetImage(new Bitmap(image));
            picture.Visible = true;
            message.Visible = false;
        }
        catch { ShowMessage("No se puede previsualizar esta imagen."); }
    }

    public void PlayVideo(string path)
    {
        StopVideo();
        SetImage(null);
        picture.Visible = false;
        message.Text = "Cargando vídeo…";
        message.Visible = true;
        message.BringToFront();
        pendingVideo = path;
        startTimer.Stop();
        startTimer.Start();
    }

    private void StartVideoNow(string path)
    {
        try
        {
            if (player is null)
            {
                player = new MediaPlayer(SharedLibVlc.Value) { Scale = 0 };
                player.AspectRatio = null;
                video.Attach(player); // el vídeo se pinta en un control normal (como en la ventana principal)
            }
            video.ClearFrame();
            media = new Media(SharedLibVlc.Value, new Uri(Path.GetFullPath(path)));
            media.AddOption(":input-repeat=65535");
            player.Mute = !sound;
            player.Volume = sound ? 100 : 0;
            picture.Visible = false;
            message.Visible = false;
            video.Visible = true;
            video.BringToFront();
            if (!player.Play(media)) throw new InvalidOperationException();
            // LibVLC puede restablecer el volumen al arrancar: se vuelve a aplicar.
            player.Mute = !sound;
        }
        catch { ShowMessage("No se pudo reproducir el vídeo."); }
    }

    public void ShowMessage(string text)
    {
        StopVideo();
        SetImage(null);
        message.Text = text;
        picture.Visible = text.Length == 0;
        message.Visible = text.Length > 0;
        if (message.Visible) message.BringToFront();
    }

    public void Clear() => ShowMessage("");

    public void StopVideo()
    {
        startTimer.Stop();
        pendingVideo = null;
        // Se para siempre, no solo si "está reproduciendo": en Opening/Buffering la salida de vídeo ya existe y ocultar la
        // ventana con ella activa es lo que tumbaba la aplicación.
        try
        {
            if (player is not null && player.State is not (VLCState.Stopped or VLCState.NothingSpecial or VLCState.Ended or VLCState.Error))
                player.Stop();
        }
        catch { }
        media?.Dispose();
        media = null;
        video.Visible = false;
    }

    private void SetImage(Image? image)
    {
        var old = picture.Image;
        picture.Image = image;
        old?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopVideo();
            startTimer.Dispose();
            player?.Dispose();
            player = null;
            SetImage(null);
        }
        base.Dispose(disposing);
    }
}

// Lee resolución y duración de un vídeo local con LibVLC, sin reproducirlo. Se cachea por archivo y fecha.
internal static class VideoProbe
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>> Cache = new(StringComparer.OrdinalIgnoreCase);

    // Devuelve, por ejemplo, "640×480 · 0:32", o "" si no se puede leer.
    public static Task<string> DescribeAsync(string path)
    {
        try { return Cache.GetOrAdd($"{Path.GetFullPath(path)}|{File.GetLastWriteTimeUtc(path).Ticks}", _ => ProbeAsync(path)); }
        catch { return Task.FromResult(""); }
    }

    private static async Task<string> ProbeAsync(string path)
    {
        try
        {
            using var media = new Media(MediaPreviewPane.SharedLibVlc.Value, new Uri(Path.GetFullPath(path)));
            var status = await media.Parse(MediaParseOptions.ParseLocal, 10000);
            if (status != MediaParsedStatus.Done) return "";
            var parts = new List<string>();
            foreach (var track in media.Tracks)
                if (track.TrackType == TrackType.Video && track.Data.Video.Width > 0)
                {
                    parts.Add($"{track.Data.Video.Width}×{track.Data.Video.Height}");
                    break;
                }
            if (media.Duration > 0) parts.Add(TimeSpan.FromMilliseconds(media.Duration).ToString(media.Duration >= 3600000 ? @"h\:mm\:ss" : @"m\:ss"));
            return string.Join(" · ", parts);
        }
        catch { return ""; }
    }
}
