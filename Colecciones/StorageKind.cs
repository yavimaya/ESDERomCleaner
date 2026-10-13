using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ESDERomCleaner;

// Detecta si una ruta está en una tarjeta SD o en una unidad extraíble: Windows la declara «extraíble», o el bus del disco es SD/MMC,
// o el dispositivo marca soporte extraíble (muchos lectores de tarjetas se presentan como discos fijos USB pero sí lo declaran).
internal static class StorageKind
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const byte BusTypeSd = 12, BusTypeMmc = 13;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

    public static bool IsSdOrRemovable(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false; // las rutas de red no son una tarjeta
            if (new DriveInfo(root).DriveType == DriveType.Removable) return true;
            using var handle = CreateFile(@"\\.\" + root.TrimEnd('\\'), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) return false;
            var query = new byte[12]; // STORAGE_PROPERTY_QUERY: StorageDeviceProperty (0) + PropertyStandardQuery (0)
            var buffer = new byte[1024];
            if (!DeviceIoControl(handle, IoctlStorageQueryProperty, query, query.Length, buffer, buffer.Length, out int returned, IntPtr.Zero) || returned < 32) return false;
            bool removableMedia = buffer[10] != 0; // STORAGE_DEVICE_DESCRIPTOR.RemovableMedia
            byte busType = buffer[28];             // STORAGE_DEVICE_DESCRIPTOR.BusType
            return removableMedia || busType is BusTypeSd or BusTypeMmc;
        }
        catch { return false; }
    }
}
