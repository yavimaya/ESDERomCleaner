using System.Runtime.InteropServices;

namespace ESDERomCleaner;

// Un medio de la ROM enfocada: una de sus imágenes (miximage, carátulas, capturas…) o su vídeo.
internal sealed record MediaItem(string Title, string Path, bool IsVideo, bool IsDefault);

// Panel de imagen/vídeo del formulario principal: un clic pasa al siguiente medio de la ROM y un doble clic lo abre en una ventana grande.
// El vídeo lo pinta una ventana nativa de LibVLC que no avisa de los clics a WinForms, así que se detectan sondeando el ratón.
public sealed partial class RomCleanerForm
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private readonly System.Windows.Forms.Timer mediaClickTimer = new() { Interval = 25 };
    private List<MediaItem>? romMedia;
    private int mediaIndex = -1; // medio que se está viendo: -1 si todavía no se ha elegido ninguno
    private bool leftWasDown;
    private long lastPressTick, singleDueTick;

    private void WireMediaClicks()
    {
        mediaClickTimer.Tick += (_, _) => PollMediaClicks();
        FormClosed += (_, _) => mediaClickTimer.Dispose();
        mediaClickTimer.Start();
    }

    private void ResetRomMedia() { romMedia = null; mediaIndex = -1; lastPressTick = singleDueTick = 0; }

    // Mientras haya abierto otro formulario de la aplicación (diálogo, visor, aviso…), el vídeo del panel principal va sin sonido.
    private bool mainMuted;

    private void ApplyMainVideoVolume()
    {
        if (mediaPlayer is null) return;
        bool on = videoSound.Checked && !mainMuted;
        try { mediaPlayer.Mute = !on; mediaPlayer.Volume = on ? 100 : 0; } catch { /* el reproductor ya se ha cerrado */ }
    }

    private void PollMediaClicks()
    {
        bool othersOpen = Application.OpenForms.Count > 1;
        if (othersOpen != mainMuted) { mainMuted = othersOpen; ApplyMainVideoVolume(); }
        long now = Environment.TickCount64;
        if (singleDueTick != 0 && now >= singleDueTick) { singleDueTick = 0; _ = StepMediaAsync(1); }
        bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0;
        if (!down) { leftWasDown = false; return; }
        if (leftWasDown) return;
        leftWasDown = true;
        if (Form.ActiveForm != this || preview.Parent is not Control frame || roms.SelectedItem is null) return;
        if (!frame.RectangleToScreen(frame.ClientRectangle).Contains(Cursor.Position)) { lastPressTick = 0; return; }
        int interval = SystemInformation.DoubleClickTime;
        if (lastPressTick != 0 && now - lastPressTick <= interval) { lastPressTick = 0; singleDueTick = 0; _ = OpenMediaViewerAsync(); }
        else { lastPressTick = now; singleDueTick = now + interval; }
    }

    // Medios que tiene la ROM: primero su imagen por defecto, luego el resto de imágenes y al final el vídeo.
    private async Task<List<MediaItem>> LoadRomMediaAsync(RomRow row)
    {
        string rootPath = root.Text;
        return await Task.Run(() =>
        {
            var items = new List<MediaItem>();
            foreach (var (folder, title) in ImageSettings.Choices.OrderBy(c => c.Folder == ImageSettings.Folder ? 0 : 1))
            {
                string? path = FindImage(rootPath, row, folder);
                if (path is not null) items.Add(new MediaItem(title, path, false, folder == ImageSettings.Folder));
            }
            string? video = FindVideo(rootPath, row);
            if (video is not null) items.Add(new MediaItem("Vídeo", video, true, false));
            return items;
        });
    }

    private async Task<List<MediaItem>?> EnsureRomMediaAsync(RomRow row)
    {
        int version = previewVersion;
        var list = romMedia ?? await LoadRomMediaAsync(row);
        if (version != previewVersion) return null; // mientras se buscaba se cambió de ROM
        if (romMedia is null)
        {
            romMedia = list;
            mediaIndex = list.Count > 0 && list[0].IsDefault ? 0 : -1;
        }
        return romMedia;
    }

    // Índice del medio que se ve ahora (si está sonando el vídeo, el del vídeo).
    private int CurrentMediaIndex()
    {
        if (romMedia is null) return -1;
        return videoView.Visible ? romMedia.FindIndex(m => m.IsVideo) : mediaIndex;
    }

    private async Task StepMediaAsync(int step)
    {
        if (roms.SelectedItem is not RomItem item) return;
        var row = item.Row;
        var list = await EnsureRomMediaAsync(row);
        if (list is null || list.Count == 0) return;
        int current = CurrentMediaIndex();
        if (list.Count == 1 && current == 0) return;
        mediaIndex = ((current < 0 ? (step > 0 ? -1 : 0) : current) + step + list.Count) % list.Count;
        videoPreviewTimer.Stop(); // al navegar a mano, el vídeo no salta solo
        var target = list[mediaIndex];
        if (target.IsVideo)
        {
            pendingVideoRow = row; pendingVideoVersion = previewVersion;
            await StartFocusedVideoAsync();
        }
        else
        {
            StopVideoPlayback();
            await ShowImageItemAsync(row, target);
        }
    }

    private async Task ShowImageItemAsync(RomRow row, MediaItem item)
    {
        int version = previewVersion;
        Bitmap? bitmap;
        try { bitmap = await Task.Run(() => { using var image = Image.FromFile(item.Path); return new Bitmap(image); }); }
        catch { return; }
        if (version != previewVersion) { bitmap.Dispose(); return; }
        preview.Image?.Dispose();
        preview.Image = bitmap;
        miximageGroup.Text = $"Imagen ({item.Title}) de {row.GameName}";
    }

    private async Task OpenMediaViewerAsync()
    {
        if (roms.SelectedItem is not RomItem item) return;
        var row = item.Row;
        var list = await EnsureRomMediaAsync(row);
        if (list is null || list.Count == 0) return;
        int start = Math.Max(0, CurrentMediaIndex());
        StopVideoPlayback();
        try { await EnsureVideoPlayerAsync(); } catch { /* sin motor de vídeo solo se verán las imágenes */ }
        trace.Add("media_viewer_opened", $"rom={row.RomName}; media={list.Count}");
        using (var viewer = new MediaViewerDialog(list, start, libVlc, videoSound.Checked))
            viewer.ShowDialog(this);
        // Al cerrar se vuelve a la imagen por defecto y, si procede, el vídeo salta otra vez solo.
        mediaIndex = list[0].IsDefault ? 0 : -1;
        if (mediaIndex == 0) await ShowImageItemAsync(row, list[0]);
        if (showVideos.Checked && roms.SelectedItem is RomItem still && ReferenceEquals(still.Row, row)) ScheduleVideoPreview(row, previewVersion);
    }
}

// Imagen por defecto: de qué carpeta de downloaded_media sale la imagen que se muestra de cada ROM y la que se comprueba en los contadores («Sin imagen»).
internal static class ImageSettings
{
    // Tipos de imagen que lee ES-DE (los manuales y los vídeos no son imágenes).
    public static readonly (string Folder, string Title)[] Choices =
    [
        ("miximages", "Miximage"), ("covers", "Carátula frontal"), ("backcovers", "Carátula trasera"), ("3dboxes", "Caja 3D"),
        ("physicalmedia", "Soporte físico (cartucho, disco)"), ("screenshots", "Captura de pantalla"), ("titlescreens", "Pantalla de título"),
        ("marquees", "Logo / marquee"), ("fanart", "Fan art"), ("custom", "Imagen personalizada (custom)")
    ];

    public static string Folder { get; private set; } = "miximages";

    static ImageSettings()
    {
        string saved = AppSettings.Get("DefaultImage", "miximages");
        if (Choices.Any(c => c.Folder == saved)) Folder = saved;
    }

    public static void Set(string folder)
    {
        if (Choices.All(c => c.Folder != folder)) return;
        Folder = folder;
        AppSettings.Set("DefaultImage", folder);
    }
}

public sealed partial class RomCleanerForm
{
    private readonly ToolStripMenuItem imageMenu = new("Imagen por defecto");

    private void WireImageMenu()
    {
        void UpdateChecks() { foreach (ToolStripMenuItem item in imageMenu.DropDownItems) item.Checked = (string)item.Tag! == ImageSettings.Folder; }
        foreach (var (folder, title) in ImageSettings.Choices)
        {
            var item = new ToolStripMenuItem(title) { Tag = folder };
            item.Click += async (_, _) =>
            {
                if (folder == ImageSettings.Folder) return;
                ImageSettings.Set(folder);
                UpdateChecks();
                trace.Add("default_image_changed", folder);
                // La imagen mostrada y los contadores «Sin imagen» dependen de esta carpeta.
                ShowDetails();
                if (all.Count > 0) await RefreshGapsAsync();
            };
            imageMenu.DropDownItems.Add(item);
        }
        UpdateChecks();
    }
}
