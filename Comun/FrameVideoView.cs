using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using LibVLCSharp.Shared;

namespace ESDERomCleaner;

// Vídeo de LibVLC dibujado por la propia aplicación en un control normal, en lugar de en la ventana nativa del reproductor (VideoView).
// LibVLC entrega cada fotograma a una memoria (formato RV32) y este control lo pinta ajustado a su tamaño. Así el vídeo es un control más
// de la ventana y Windows puede hacer la miniatura de la barra de tareas con todo el formulario, cosa que no conseguía con la ventana nativa.
internal sealed class FrameVideoView : Control
{
    private readonly object sync = new();
    private IntPtr buffer;
    private Bitmap? frame;
    private bool hasFrame, invalidatePending;
    // Las funciones de retorno deben seguir vivas mientras LibVLC las use.
    private MediaPlayer.LibVLCVideoFormatCb? formatCallback;
    private MediaPlayer.LibVLCVideoCleanupCb? cleanupCallback;
    private MediaPlayer.LibVLCVideoLockCb? lockCallback;
    private MediaPlayer.LibVLCVideoUnlockCb? unlockCallback;
    private MediaPlayer.LibVLCVideoDisplayCb? displayCallback;

    public FrameVideoView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    // Debe llamarse una vez, antes de reproducir nada con ese reproductor.
    public void Attach(MediaPlayer player)
    {
        formatCallback = OnFormat; cleanupCallback = OnCleanup; lockCallback = OnLock; unlockCallback = OnUnlock; displayCallback = OnDisplay;
        player.SetVideoFormatCallbacks(formatCallback, cleanupCallback);
        player.SetVideoCallbacks(lockCallback, unlockCallback, displayCallback);
    }

    // Olvida el último fotograma (para no ver el del vídeo anterior al empezar uno nuevo).
    public void ClearFrame()
    {
        lock (sync) hasFrame = false;
        Invalidate();
    }

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        Marshal.Copy(Encoding.ASCII.GetBytes("RV32"), 0, chroma, 4);
        pitches = width * 4;
        lines = height;
        lock (sync)
        {
            Release();
            buffer = Marshal.AllocHGlobal((int)(pitches * lines));
            frame = new Bitmap((int)width, (int)height, (int)pitches, PixelFormat.Format32bppRgb, buffer);
            hasFrame = false;
        }
        return 1;
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        lock (sync) { hasFrame = false; Release(); }
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, buffer);
        return IntPtr.Zero;
    }

    private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes) { }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        lock (sync) hasFrame = frame is not null;
        if (invalidatePending || !IsHandleCreated || IsDisposed) return;
        invalidatePending = true;
        try { BeginInvoke(new Action(() => { invalidatePending = false; Invalidate(); })); }
        catch (InvalidOperationException) { invalidatePending = false; }
    }

    private void Release()
    {
        frame?.Dispose();
        frame = null;
        if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        lock (sync)
        {
            if (!hasFrame || frame is null || ClientSize.Width < 1 || ClientSize.Height < 1) return;
            double scale = Math.Min((double)ClientSize.Width / frame.Width, (double)ClientSize.Height / frame.Height);
            int w = Math.Max(1, (int)(frame.Width * scale)), h = Math.Max(1, (int)(frame.Height * scale));
            e.Graphics.InterpolationMode = InterpolationMode.Bilinear;
            e.Graphics.DrawImage(frame, (ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) lock (sync) { hasFrame = false; Release(); }
        base.Dispose(disposing);
    }
}
