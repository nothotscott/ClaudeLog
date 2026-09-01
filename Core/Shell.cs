using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeLog.Core;

/// <summary>Windows shell integration: Explorer, and the taskbar flash used when the limit resets.</summary>
public static class Shell
{
    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            Log.Warn($"open folder: not found {path}");
            return;
        }
        Start("explorer.exe", $"\"{path}\"");
    }

    /// <summary>Opens Explorer with the file already selected.</summary>
    public static void RevealFile(string path)
    {
        if (!File.Exists(path))
        {
            Log.Warn($"reveal: not found {path}");
            return;
        }
        Start("explorer.exe", $"/select,\"{path}\"");
    }

    /// <summary>Opens a file with whatever is associated with it — Notepad++, for these.</summary>
    public static void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            Log.Warn($"open file: not found {path}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"open {path} failed: {ex.Message}");
        }
    }

    private static void Start(string exe, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"{exe} {args} failed: {ex.Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public nint hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_ALL = 3;
    private const uint FLASHW_TIMERNOFG = 12;

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    /// <summary>Flashes the taskbar button until the window is brought to the front.</summary>
    public static void FlashTaskbar(nint hwnd)
    {
        if (hwnd == 0) return;
        try
        {
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hwnd,
                dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                uCount = uint.MaxValue,
                dwTimeout = 0,
            };
            FlashWindowEx(ref info);
        }
        catch (Exception ex)
        {
            Log.Warn($"flash failed: {ex.Message}");
        }
    }
}
