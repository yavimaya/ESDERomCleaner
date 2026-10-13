namespace ESDERomCleaner;

// Icono de la aplicación (el del ejecutable, el mismo de la ventana principal), que usan todos los formularios.
internal static class AppIcon
{
    private static readonly Lazy<Icon?> Instance = new(() =>
    {
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { return null; }
    });

    public static Icon? Value => Instance.Value;
}
