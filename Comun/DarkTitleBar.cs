using System.Runtime.InteropServices;

namespace ESDERomCleaner;

// Barra de título oscura, del color de las cabeceras: en Windows 10 (a partir de la 2004) se activa el modo oscuro de la barra, y en Windows 11 además se le pone
// el color exacto de la cabecera, el texto claro y el borde del mismo color, para que la barra y la cabecera parezcan una sola pieza.
internal static class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20, BorderColor = 34, CaptionColor = 35, TextColor = 36;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static int Colorref(Color c) => c.R | (c.G << 8) | (c.B << 16);

    public static void Apply(IntPtr handle)
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref on, sizeof(int)); // Windows 10 2004+ y Windows 11
            int caption = Colorref(RaUi.Dark), border = caption, text = Colorref(Color.FromArgb(230, 232, 240));
            DwmSetWindowAttribute(handle, CaptionColor, ref caption, sizeof(int));   // solo Windows 11: si no existe, se ignora
            DwmSetWindowAttribute(handle, BorderColor, ref border, sizeof(int));
            DwmSetWindowAttribute(handle, TextColor, ref text, sizeof(int));
        }
        catch { }
    }
}
