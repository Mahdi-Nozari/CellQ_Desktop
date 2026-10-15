using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CellQ.Desktop.Services;

internal sealed record WindowsPortDevice(string? PortName, string? FriendlyName);

/// <summary>Reads present Windows port metadata. Never opens a serial port or writes device settings.</summary>
internal static class WindowsPortDevices
{
    private static readonly Guid PortsClass = new("4D36E978-E325-11CE-BFC1-08002BE10318");

    internal static IReadOnlyList<WindowsPortDevice> Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var classGuid = PortsClass;
        IntPtr devices = SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, 2 /* DIGCF_PRESENT */);
        if (devices == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read Windows USB connection information.");
        try
        {
            var result = new List<WindowsPortDevice>();
            for (uint index = 0; ; index++)
            {
                token.ThrowIfCancellationRequested();
                var device = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>(), ClassGuid = Guid.Empty, DevInst = 0, Reserved = IntPtr.Zero };
                if (!SetupDiEnumDeviceInfo(devices, index, ref device))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259 /* ERROR_NO_MORE_ITEMS */) break;
                    throw new Win32Exception(error, "Could not list Windows USB connections.");
                }
                string? name = ReadText(devices, ref device, 12 /* SPDRP_FRIENDLYNAME */)
                    ?? ReadText(devices, ref device, 0 /* SPDRP_DEVICEDESC */);
                using var handle = SetupDiOpenDevRegKey(devices, ref device, 1 /* DICS_FLAG_GLOBAL */, 0, 1 /* DIREG_DEV */, 1 /* KEY_QUERY_VALUE */);
                if (handle.IsInvalid) continue; // No port metadata, or unplugged during enumeration.
                using var key = RegistryKey.FromHandle(handle);
                string? port = key.GetValue("PortName", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if (port is not null) result.Add(new(port, name));
            }
            return result;
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
    }

    private static string? ReadText(IntPtr devices, ref DeviceInfo device, uint property)
    {
        var bytes = new byte[512];
        if (!SetupDiGetDeviceRegistryProperty(devices, ref device, property, out uint type, bytes, (uint)bytes.Length, out uint needed))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 122 /* ERROR_INSUFFICIENT_BUFFER */ || needed is < 2 or > 65536) return null;
            bytes = new byte[needed];
            if (!SetupDiGetDeviceRegistryProperty(devices, ref device, property, out type, bytes, (uint)bytes.Length, out needed)) return null;
        }
        if (type != 1 /* REG_SZ */ || needed > bytes.Length || needed % 2 != 0) return null;
        string text = Encoding.Unicode.GetString(bytes, 0, (int)needed);
        int end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr devices, uint index, ref DeviceInfo device);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr devices, ref DeviceInfo device, uint property, out uint type, [Out] byte[] buffer, uint length, out uint needed);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern SafeRegistryHandle SetupDiOpenDevRegKey(IntPtr devices, ref DeviceInfo device, uint scope, uint profile, uint keyType, uint access);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
}
