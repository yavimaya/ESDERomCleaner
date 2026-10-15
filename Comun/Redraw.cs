using System.Runtime.InteropServices;

namespace ESDERomCleaner;

// Utilidades para evitar parpadeos al añadir muchas filas a una tabla.
internal static class Redraw
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int WM_SETREDRAW = 0x000B;

    // Suspende el repintado del control mientras se ejecuta la acción y lo repinta una sola vez al terminar.
    public static void Batch(Control control, Action action)
    {
        if (!control.IsHandleCreated) { action(); return; }
        SendMessage(control.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try { action(); }
        finally { SendMessage(control.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero); control.Invalidate(true); }
    }

    // DoubleBuffered no es público en DataGridView, Label…: se activa por reflexión.
    public static void EnableDoubleBuffer(Control control) =>
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(control, true);
}
