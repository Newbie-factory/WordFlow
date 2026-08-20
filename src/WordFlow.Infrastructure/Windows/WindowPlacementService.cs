using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;

namespace WordFlow.Infrastructure.Windows;

public sealed record MonitorWorkArea(string DurableId, string DeviceName, int LeftPx, int TopPx, int WidthPx, int HeightPx,
    uint DpiX, uint DpiY, bool IsPrimary)
{
    public int RightPx => LeftPx + WidthPx;
    public int BottomPx => TopPx + HeightPx;
    public double ScaleX => DpiX / 96d;
    public double ScaleY => DpiY / 96d;
}

public sealed record WindowPlacement(int Version, string MonitorIdentity, string DeviceNameFallback, double OffsetXDip, double OffsetYDip);
public sealed record PhysicalWindowRect(int LeftPx, int TopPx, int WidthPx, int HeightPx)
{
    public int RightPx => LeftPx + WidthPx;
    public int BottomPx => TopPx + HeightPx;
}
public sealed record ResolvedWindowPlacement(MonitorWorkArea Monitor, int LeftPx, int TopPx, int WidthPx, int HeightPx)
{
    public int RightPx => LeftPx + WidthPx;
    public int BottomPx => TopPx + HeightPx;
    public PhysicalWindowRect Rect => new(LeftPx, TopPx, WidthPx, HeightPx);
}
public sealed record ForegroundWindowSnapshot(bool Visible, bool Maximized, bool Cloaked, bool IsApplication,
    bool IsSystem, string ClassName, PhysicalWindowRect Bounds);

public sealed class WindowPlacementService(string storagePath)
{
    private const int CurrentVersion = 2;
    private const double EdgeMarginDip = 16;
    private readonly string storagePath = string.IsNullOrWhiteSpace(storagePath)
        ? throw new ArgumentException("A placement storage path is required.", nameof(storagePath))
        : Path.GetFullPath(storagePath);

    public WindowPlacement? Load()
    {
        try
        {
            if (!File.Exists(storagePath)) return null;
            var value = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(storagePath));
            return value is { Version: CurrentVersion, MonitorIdentity.Length: > 0 }
                && double.IsFinite(value.OffsetXDip) && double.IsFinite(value.OffsetYDip) ? value : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or SecurityException) { return null; }
    }

    public bool TrySave(WindowPlacement placement, out Exception? error)
    {
        ArgumentNullException.ThrowIfNull(placement);
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(storagePath) ?? throw new IOException("The placement path has no parent directory.");
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{Path.GetFileName(storagePath)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(placement));
            File.Move(temporary, storagePath, overwrite: true);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            error = exception;
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public static ResolvedWindowPlacement Resolve(WindowPlacement? saved, IReadOnlyList<MonitorWorkArea> monitors, double widthDip, double heightDip)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0) throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        if (!double.IsFinite(widthDip) || widthDip <= 0) throw new ArgumentOutOfRangeException(nameof(widthDip));
        if (!double.IsFinite(heightDip) || heightDip <= 0) throw new ArgumentOutOfRangeException(nameof(heightDip));
        var primary = monitors.FirstOrDefault(x => x.IsPrimary) ?? monitors[0];
        var monitor = saved is null ? primary
            : monitors.FirstOrDefault(x => string.Equals(x.DurableId, saved.MonitorIdentity, StringComparison.Ordinal))
              ?? monitors.FirstOrDefault(x => string.Equals(x.DeviceName, saved.DeviceNameFallback, StringComparison.OrdinalIgnoreCase))
              ?? primary;
        int width = Math.Min(monitor.WidthPx, Math.Max(1, (int)Math.Round(widthDip * monitor.ScaleX)));
        int height = Math.Min(monitor.HeightPx, Math.Max(1, (int)Math.Round(heightDip * monitor.ScaleY)));
        int left;
        int top;
        if (saved is null || (monitor == primary && !monitors.Any(x => string.Equals(x.DurableId, saved.MonitorIdentity, StringComparison.Ordinal) || string.Equals(x.DeviceName, saved.DeviceNameFallback, StringComparison.OrdinalIgnoreCase))))
        {
            left = monitor.RightPx - width - (int)Math.Round(EdgeMarginDip * monitor.ScaleX);
            top = monitor.BottomPx - height - (int)Math.Round(EdgeMarginDip * monitor.ScaleY);
        }
        else
        {
            left = monitor.LeftPx + (int)Math.Round(saved.OffsetXDip * monitor.ScaleX);
            top = monitor.TopPx + (int)Math.Round(saved.OffsetYDip * monitor.ScaleY);
        }
        return Clamp(new(monitor, left, top, width, height));
    }

    public static WindowPlacement Capture(PhysicalWindowRect rect, IReadOnlyList<MonitorWorkArea> monitors)
    {
        if (monitors.Count == 0) throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        var monitor = monitors.OrderByDescending(x => Intersection(rect, x)).First();
        return new(CurrentVersion, monitor.DurableId, monitor.DeviceName,
            (rect.LeftPx - monitor.LeftPx) / monitor.ScaleX, (rect.TopPx - monitor.TopPx) / monitor.ScaleY);
    }

    public static PhysicalWindowRect ResolveSuggested(PhysicalWindowRect suggested, MonitorWorkArea monitor) =>
        Clamp(new(monitor, suggested.LeftPx, suggested.TopPx, Math.Min(suggested.WidthPx, monitor.WidthPx), Math.Min(suggested.HeightPx, monitor.HeightPx))).Rect;

    public static bool ShouldBeTopmost(bool suppressForFullscreen, bool isForegroundFullscreen) => !suppressForFullscreen || !isForegroundFullscreen;

    public static bool IsFullscreenCandidate(ForegroundWindowSnapshot candidate, PhysicalWindowRect monitorBounds) =>
        candidate.Visible && !candidate.Maximized && !candidate.Cloaked && !candidate.IsApplication && !candidate.IsSystem
        && !string.Equals(candidate.ClassName, "Progman", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(candidate.ClassName, "WorkerW", StringComparison.OrdinalIgnoreCase)
        && Math.Abs(candidate.Bounds.LeftPx - monitorBounds.LeftPx) <= 1
        && Math.Abs(candidate.Bounds.TopPx - monitorBounds.TopPx) <= 1
        && Math.Abs(candidate.Bounds.RightPx - monitorBounds.RightPx) <= 1
        && Math.Abs(candidate.Bounds.BottomPx - monitorBounds.BottomPx) <= 1;

    public static IReadOnlyList<MonitorWorkArea> CaptureCurrentMonitors() => NativeMethods.CaptureMonitors();
    public static PhysicalWindowRect GetWindowRectangle(nint hwnd) => NativeMethods.GetPhysicalWindowRect(hwnd);
    public static void SetWindowRectangle(nint hwnd, PhysicalWindowRect rect) => NativeMethods.SetPhysicalWindowRect(hwnd, rect);
    public static MonitorWorkArea MonitorFor(PhysicalWindowRect rect, IReadOnlyList<MonitorWorkArea> monitors) =>
        monitors.OrderByDescending(x => Intersection(rect, x)).FirstOrDefault() ?? monitors.First(x => x.IsPrimary);
    public static bool IsForegroundFullscreen(nint applicationWindow = default) => NativeMethods.IsForegroundFullscreen(applicationWindow);

    private static ResolvedWindowPlacement Clamp(ResolvedWindowPlacement value)
    {
        var m = value.Monitor;
        int width = Math.Min(value.WidthPx, m.WidthPx);
        int height = Math.Min(value.HeightPx, m.HeightPx);
        return value with
        {
            LeftPx = Math.Clamp(value.LeftPx, m.LeftPx, m.RightPx - width),
            TopPx = Math.Clamp(value.TopPx, m.TopPx, m.BottomPx - height),
            WidthPx = width,
            HeightPx = height,
        };
    }
    private static long Intersection(PhysicalWindowRect rect, MonitorWorkArea monitor)
    {
        long width = Math.Max(0, Math.Min(rect.RightPx, monitor.RightPx) - Math.Max(rect.LeftPx, monitor.LeftPx));
        long height = Math.Max(0, Math.Min(rect.BottomPx, monitor.BottomPx) - Math.Max(rect.TopPx, monitor.TopPx));
        return width * height;
    }

    private static class NativeMethods
    {
        private const uint MonitorDefaultToNearest = 2;
        private const int EffectiveDpi = 0;
        private const uint MonitorInfoPrimary = 1;
        private const uint DisplayDeviceInterfaceName = 1;
        private const int DwmwaCloaked = 14;
        private const uint SwpNoActivate = 0x0010;
        private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string device, uint number, ref DisplayDevice info, uint flags);
        [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] private static extern nint GetShellWindow();
        [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
        [DllImport("user32.dll")] private static extern bool IsZoomed(nint hwnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
        [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder name, int maximum);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

        public static IReadOnlyList<MonitorWorkArea> CaptureMonitors()
        {
            var result = new List<MonitorWorkArea>();
            EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
            {
                var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
                if (!GetMonitorInfo(monitor, ref info)) return true;
                uint dpiX = 96, dpiY = 96;
                try { if (GetDpiForMonitor(monitor, EffectiveDpi, out var x, out var y) == 0) { dpiX = x; dpiY = y; } }
                catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) { }
                string durable = DeviceIdentity(info.DeviceName);
                result.Add(new(durable, info.DeviceName, info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left,
                    info.Work.Bottom - info.Work.Top, dpiX, dpiY, (info.Flags & MonitorInfoPrimary) != 0));
                return true;
            }, 0);
            return result;
        }

        private static string DeviceIdentity(string device)
        {
            var info = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            return EnumDisplayDevices(device, 0, ref info, DisplayDeviceInterfaceName) && !string.IsNullOrWhiteSpace(info.DeviceId) ? info.DeviceId : device;
        }

        public static PhysicalWindowRect GetPhysicalWindowRect(nint hwnd)
        {
            if (!GetWindowRect(hwnd, out var rect)) throw new InvalidOperationException("Cannot read the card window rectangle.");
            return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
        public static void SetPhysicalWindowRect(nint hwnd, PhysicalWindowRect rect)
        {
            if (!SetWindowPos(hwnd, 0, rect.LeftPx, rect.TopPx, rect.WidthPx, rect.HeightPx, SwpNoActivate))
                throw new InvalidOperationException("Cannot apply the card window rectangle.");
        }
        public static bool IsForegroundFullscreen(nint applicationWindow)
        {
            var window = GetForegroundWindow();
            if (window == 0 || !GetWindowRect(window, out var bounds)) return false;
            var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(monitor, ref info)) return false;
            var name = new StringBuilder(128);
            GetClassName(window, name, name.Capacity);
            int cloaked = 0;
            try { _ = DwmGetWindowAttribute(window, DwmwaCloaked, out cloaked, sizeof(int)); } catch (DllNotFoundException) { }
            GetWindowThreadProcessId(window, out var processId);
            var snapshot = new ForegroundWindowSnapshot(IsWindowVisible(window), IsZoomed(window), cloaked != 0,
                window == applicationWindow || processId == Environment.ProcessId,
                window == GetShellWindow() || window == GetDesktopWindow() || name.ToString() is "Shell_TrayWnd" or "Progman" or "WorkerW",
                name.ToString(), new(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top));
            return IsFullscreenCandidate(snapshot, new(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top));
        }

        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfoEx
        {
            public int Size; public Rect Monitor; public Rect Work; public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }
    }
}
