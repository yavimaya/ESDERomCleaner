using System.Net;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ESDERomCleaner;

public sealed partial class RomCleanerForm : LocalizedForm
{
    private static readonly string[] VideoExtensions = [".mp4", ".m4v", ".wmv", ".avi", ".mov", ".mpg", ".mpeg", ".webm", ".mkv", ".ogv"];
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<string[]>> VideoFileIndex = new(StringComparer.OrdinalIgnoreCase);

    private void ScheduleVideoPreview(RomRow row, int version)
    {
        if (!showVideos.Checked) return;
        pendingVideoRow = row;
        pendingVideoVersion = version;
        videoPreviewTimer.Stop();
        videoPreviewTimer.Interval = 4000;
        videoPreviewTimer.Start();
    }

    private async Task StartFocusedVideoAsync()
    {
        videoPreviewTimer.Stop();
        RomRow? row = pendingVideoRow;
        int version = pendingVideoVersion;
        if (!showVideos.Checked || row is null || version != previewVersion) return;

        try
        {
            string? path = await Task.Run(() => FindVideo(root.Text, row));
            if (version != previewVersion || !showVideos.Checked || !File.Exists(path)) return;
            await EnsureVideoPlayerAsync();
            if (version != previewVersion || !showVideos.Checked) return; // mientras cargaba el motor se cambió de ROM
            currentVideoMedia?.Dispose();
            currentVideoMedia = new Media(libVlc!, new Uri(Path.GetFullPath(path!)));
            mediaPlayer!.Volume = videoSound.Checked ? 100 : 0;
            videoView.ClearFrame();
            videoView.Visible = true;
            videoView.BringToFront();
            preview.Visible = false;
            miximageGroup.Text = row.GameName;
            if (!mediaPlayer.Play(currentVideoMedia)) throw new InvalidOperationException("LibVLC no pudo iniciar la reproducción de este vídeo.");
            VideoVolume.Apply(mediaPlayer, () => videoSound.Checked && !mainMuted, text => trace.Add("video_audio", text));
            trace.Add("video_started", $"system={row.System}; rom={row.RomName}; path={path}");
            videoMonitorTimer.Start();
        }
        catch (Exception ex)
        {
            StopVideoPlayback();
            miximageGroup.Text = $"Vídeo no disponible — {row.GameName}";
            trace.Add("video_start_error", $"rom={row.RomName}; error={ex}");
            status.Text = "No se pudo reproducir el vídeo de la ROM enfocada.";
        }
    }

    private Task? libVlcWarmup;

    // Cargar LibVLC (sus bibliotecas y complementos) tarda varios segundos la primera vez; se hace en segundo plano nada más abrir la ventana,
    // para que el primer vídeo no deje la aplicación bloqueada.
    private void WarmUpVideoEngine()
    {
        if (libVlcWarmup is not null) return;
        libVlcWarmup = Task.Run(() =>
        {
            try
            {
                Core.Initialize();
                // Decodificación por software y salida GDI: la ruta Direct3D11 de LibVLC cerraba la aplicación con una excepción en dxgi.dll.
                libVlc ??= new LibVLC("--no-video-title-show", "--no-osd", "--avcodec-hw=none", "--vout=wingdi");
            }
            catch (Exception ex) { trace.Add("libvlc_init_error", ex.ToString()); }
        });
    }

    private async Task EnsureVideoPlayerAsync()
    {
        if (mediaPlayer is not null) return;
        WarmUpVideoEngine();
        await libVlcWarmup!;
        if (mediaPlayer is not null) return;
        if (libVlc is null) throw new InvalidOperationException("No se pudo iniciar el motor de vídeo (LibVLC).");
        mediaPlayer = new MediaPlayer(libVlc);
        // VLC no debe gestionar el ratón ni el teclado en su ventana: un doble clic suyo pasa a pantalla completa y bloquea la aplicación.
        mediaPlayer.EnableMouseInput = false;
        mediaPlayer.EnableKeyInput = false;
        mediaPlayer.Scale = 0;
        mediaPlayer.AspectRatio = null;
        videoView.Attach(mediaPlayer); // el vídeo se pinta en un control normal, no en la ventana nativa de LibVLC
        mediaPlayer.EndReached += (_, _) =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => HandleVideoEnded())); } catch (InvalidOperationException) { }
        };
    }

    private void HandleVideoEnded()
    {
        if (IsDisposed || !videoView.Visible) return;
        videoMonitorTimer.Stop();
        currentVideoMedia?.Dispose();
        currentVideoMedia = null;
        videoView.Visible = false;
        preview.Visible = true;
        preview.BringToFront();
        // Tras el vídeo se vuelve a la imagen por defecto de la ROM.
        if (romMedia is { Count: > 0 } mediaList && pendingVideoRow is { } endedRow)
        {
            mediaIndex = mediaList[0].IsDefault ? 0 : -1;
            if (mediaIndex == 0) _ = ShowImageItemAsync(endedRow, mediaList[0]);
        }
        if (showVideos.Checked && pendingVideoRow is not null)
            ScheduleVideoPreview(pendingVideoRow, pendingVideoVersion);
        else miximageGroup.Text = "Imagen de la ROM enfocada";
    }

    private void CheckVideoPlayback()
    {
        if (mediaPlayer is null || mediaPlayer.IsPlaying) return;
        videoMonitorTimer.Stop();
        if (mediaPlayer.State is VLCState.Ended or VLCState.Error) HandleVideoEnded();
    }

    private void DisposeVideoPlayer()
    {
        StopVideoPlayback();
        videoPreviewTimer.Dispose();
        videoMonitorTimer.Dispose();
        currentVideoMedia?.Dispose();
        currentVideoMedia = null;
        mediaPlayer?.Dispose();
        mediaPlayer = null;
        libVlc?.Dispose();
        libVlc = null;
    }

    private void StopVideoPlayback()
    {
        videoPreviewTimer.Stop();
        videoMonitorTimer.Stop();
        try { if (mediaPlayer?.IsPlaying == true) mediaPlayer.Stop(); } catch { }
        currentVideoMedia?.Dispose();
        currentVideoMedia = null;
        videoView.Visible = false;
        preview.Visible = true;
        preview.BringToFront();
    }

    private static string? FindVideo(string rootPath, RomRow row)
    {
        string directory = Path.Combine(rootPath, "ES-DE", "downloaded_media", row.System, "videos");
        if (!Directory.Exists(directory)) return null;
        var stems = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.Path))
        {
            try { stems.Add(Directory.Exists(row.Path) ? new DirectoryInfo(row.Path).Name : Path.GetFileNameWithoutExtension(row.Path)); } catch { }
        }
        if (!string.IsNullOrWhiteSpace(row.GamelistPath)) stems.Add(Path.GetFileNameWithoutExtension(row.GamelistPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!string.IsNullOrWhiteSpace(row.RomName)) stems.Add(Path.GetFileNameWithoutExtension(row.RomName));
        if (!string.IsNullOrWhiteSpace(row.GameName)) stems.Add(row.GameName);
        foreach (string stem in stems.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (string extension in VideoExtensions)
            {
                string direct = Path.Combine(directory, stem + extension);
                if (File.Exists(direct)) return direct;
            }

        string[] files;
        try
        {
            files = VideoFileIndex.GetOrAdd(directory, key => new Lazy<string[]>(() =>
                Directory.EnumerateFiles(key, "*", SearchOption.AllDirectories)
                    .Where(file => VideoExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
)
                    .ToArray())).Value;
        }
        catch { return null; }

        foreach (string stem in stems)
        {
            string normalized = NormalizeMediaStem(stem);
            var matches = files.Where(file => NormalizeMediaStem(Path.GetFileNameWithoutExtension(file)) == normalized).ToArray();
            var rootMatches = matches.Where(file => Path.GetDirectoryName(file)?.Equals(directory, StringComparison.OrdinalIgnoreCase) == true).ToArray();
            if (rootMatches.Length > 0) matches = rootMatches;
            foreach (string extension in VideoExtensions)
            {
                var preferred = matches.Where(file => Path.GetExtension(file).Equals(extension, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (preferred.Length > 0) { matches = preferred; break; }
            }
            if (matches.Length == 1) return matches[0];
        }
        return null;
    }
}
