using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using System.Runtime.InteropServices;

namespace ESDERomCleaner;

// Ventana grande para ver una imagen o un vídeo de la ROM. Flechas izquierda y derecha cambian de medio, espacio pausa el vídeo y Esc cierra.
internal sealed class MediaViewerDialog : LocalizedForm
{
    private readonly List<MediaItem> media;
    private readonly LibVLC? libVlc;
    private readonly bool sound;
    private readonly PictureBox picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private readonly VideoView video = new() { Dock = DockStyle.Fill, BackColor = Color.Black, Visible = false };
    private readonly Label caption = new() { Dock = DockStyle.Bottom, Height = 30, ForeColor = Color.Gainsboro, BackColor = Color.Black, TextAlign = ContentAlignment.MiddleCenter, Tag = "noloc" };
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private readonly System.Windows.Forms.Timer keyTimer = new() { Interval = 40 };
    private readonly HashSet<int> keysDown = [];
    private MediaPlayer? player;
    private Media? current;
    private int index;

    public MediaViewerDialog(List<MediaItem> media, int start, LibVLC? libVlc, bool sound)
    {
        this.media = media; this.libVlc = libVlc; this.sound = sound;
        Text = "Visor de medios";
        BackColor = Color.Black;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1100, 820);
        MinimumSize = new Size(480, 400);
        KeyPreview = true;
        ShowInTaskbar = false;
        Controls.Add(picture);
        Controls.Add(video);
        Controls.Add(caption);
        picture.Click += (_, _) => Step(1);
        // Con el vídeo, el foco puede estar en la ventana nativa de VLC y el formulario no recibe el teclado: se sondea el estado de las teclas.
        keyTimer.Tick += (_, _) => PollKeys();
        keyTimer.Start();
        Shown += (_, _) => { index = Math.Clamp(start, 0, media.Count - 1); ShowCurrent(); };
        FormClosed += (_, _) => { keyTimer.Dispose(); StopMedia(dispose: true); };
    }

    private bool Pressed(int key)
    {
        bool down = (GetAsyncKeyState(key) & 0x8000) != 0;
        if (!down) { keysDown.Remove(key); return false; }
        return keysDown.Add(key); // solo la pulsación, no la repetición mientras se mantiene
    }

    private void PollKeys()
    {
        if (Form.ActiveForm != this) return;
        if (Pressed(0x1B)) Close();
        else if (Pressed(0x27) | Pressed(0x28)) Step(1);
        else if (Pressed(0x25) | Pressed(0x26)) Step(-1);
        else if (Pressed(0x20) && player is not null && video.Visible) player.Pause();
    }

    private void Step(int step)
    {
        if (media.Count < 2) return;
        index = (index + step + media.Count) % media.Count;
        ShowCurrent();
    }

    private void ShowCurrent()
    {
        var item = media[index];
        // Con un solo medio no hay nada que cambiar: no se muestra ni el contador ni la pista de las flechas.
        string hint = media.Count == 1
            ? Loc.T(item.IsVideo ? "Espacio pausa el vídeo · Esc cierra" : "Esc cierra")
            : Loc.T("← → cambiar de medio · Espacio pausa el vídeo · Esc cierra");
        caption.Text = media.Count == 1 ? $"{Loc.T(item.Title)}   ·   {hint}" : $"{Loc.T(item.Title)}  ({index + 1}/{media.Count})   ·   {hint}";
        StopMedia(dispose: false);
        if (!item.IsVideo)
        {
            video.Visible = false;
            picture.Visible = true;
            try { using var image = Image.FromFile(item.Path); var old = picture.Image; picture.Image = new Bitmap(image); old?.Dispose(); }
            catch { picture.Image = null; }
            return;
        }
        picture.Image = null;
        picture.Visible = false; // la imagen está por encima del vídeo en el orden de capas: se oculta para que el vídeo se vea
        if (libVlc is null) { video.Visible = false; caption.Text = Loc.T("No se pudo iniciar el motor de vídeo (LibVLC)."); return; }
        try
        {
            if (player is null)
            {
                player = new MediaPlayer(libVlc);
                player.EnableMouseInput = false; // el doble clic de VLC pasaría a pantalla completa
                player.EnableKeyInput = false;
            }
            video.MediaPlayer = player;
            video.Visible = true;
            video.BringToFront();
            current = new Media(libVlc, new Uri(Path.GetFullPath(item.Path)));
            current.AddOption(":input-repeat=65535"); // el vídeo se repite mientras la ventana esté abierta
            player.Volume = sound ? 100 : 0;
            player.Play(current);
            VideoVolume.Apply(player, () => sound);
        }
        catch (Exception ex) { video.Visible = false; caption.Text = ex.Message; }
    }

    private void StopMedia(bool dispose)
    {
        try { if (player?.IsPlaying == true) player.Stop(); } catch { }
        current?.Dispose(); current = null;
        if (!dispose) return;
        video.MediaPlayer = null;
        player?.Dispose(); player = null;
        picture.Image?.Dispose(); picture.Image = null;
    }
}

// LibVLC ignora el volumen y el silencio fijados antes de que cree la salida de audio (unas décimas de segundo después de empezar). Además Windows recuerda
// por programa el volumen que se le puso, así que un «sin sonido» anterior dejaría mudo el siguiente vídeo. Se aplica varias veces durante los primeros segundos.
internal static class VideoVolume
{
    public static void Apply(MediaPlayer player, Func<bool> sound, Action<string>? report = null)
    {
        int count = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (_, _) =>
        {
            try
            {
                bool on = sound();
                player.Mute = !on; player.Volume = on ? 100 : 0;
                // Si no hay pista de audio elegida, se elige la primera real.
                if (on && player.AudioTrack < 0 && player.AudioTrackDescription.FirstOrDefault(t => t.Id >= 0) is { } track) player.SetAudioTrack(track.Id);
                if (count == 7) report?.Invoke($"audio: sonido={on}; mute={player.Mute}; volumen={player.Volume}; pista={player.AudioTrack}; estado={player.State}");
            }
            catch { /* el reproductor ya se ha cerrado */ }
            if (++count >= 8) { timer.Stop(); timer.Dispose(); }
        };
        timer.Start();
    }
}