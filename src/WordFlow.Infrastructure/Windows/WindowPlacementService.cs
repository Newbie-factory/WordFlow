using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace WordFlow.Infrastructure.Windows;

public sealed record MonitorWorkArea(
    string MonitorId,
    double LeftDip,
    double TopDip,
    double WidthDip,
    double HeightDip,
    bool IsPrimary)
{
    public double RightDip => LeftDip + WidthDip;
    public double BottomDip => TopDip + HeightDip;
}

public sealed record WindowPlacement(string MonitorId, double LeftDip, double TopDip);

public sealed class WindowPlacementService(string storagePath)
{
    private const double EdgeMarginDip = 16;
    private const double MinimumVisibleDip = 48;
    private readonly string storagePath = string.IsNullOrWhiteSpace(storagePath)
        ? throw new ArgumentException("A placement storage path is required.", nameof(storagePath))
        : Path.GetFullPath(storagePath);

    public WindowPlacement? Load()
    {
        try
        {
            if (!File.Exists(storagePath)) return null;
            var placement = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(storagePath));
            return placement is { MonitorId.Length: > 0 }
                && double.IsFinite(placement.LeftDip)
                && double.IsFinite(placement.TopDip)
                ? placement
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(WindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var directory = Path.GetDirectoryName(storagePath)
            ?? throw new InvalidOperationException("The placement path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(storagePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(placement));
            File.Move(temporary, storagePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static WindowPlacement Resolve(
        WindowPlacement? saved,
        IReadOnlyList<MonitorWorkArea> monitors,
        double widthDip,
        double heightDip)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0) throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        if (!double.IsFinite(widthDip) || widthDip <= 0) throw new ArgumentOutOfRangeException(nameof(widthDip));
        if (!double.IsFinite(heightDip) || heightDip <= 0) throw new ArgumentOutOfRangeException(nameof(heightDip));

        var primary = monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
        var matchingMonitor = saved is null ? null : monitors.FirstOrDefault(
            monitor => string.Equals(monitor.MonitorId, saved.MonitorId, StringComparison.Ordinal));
        var target = saved is null
            ? primary
            : matchingMonitor
                ?? MostVisibleMonitor(saved.LeftDip, saved.TopDip, widthDip, heightDip, monitors)
                ?? primary;

        if (saved is null || !IsMeaningfullyVisible(saved.LeftDip, saved.TopDip, widthDip, heightDip, target))
        {
            if (saved is not null && monitors.Any(monitor => IsMeaningfullyVisible(saved.LeftDip, saved.TopDip, widthDip, heightDip, monitor)))
            {
                var visibleMonitor = MostVisibleMonitor(saved.LeftDip, saved.TopDip, widthDip, heightDip, monitors)!;
                return Clamp(saved with { MonitorId = visibleMonitor.MonitorId }, visibleMonitor, widthDip, heightDip);
            }
            return BottomRight(matchingMonitor ?? primary, widthDip, heightDip);
        }

        return Clamp(saved with { MonitorId = target.MonitorId }, target, widthDip, heightDip);
    }

    public static bool ShouldBeTopmost(bool suppressForFullscreen, bool isForegroundFullscreen) =>
        !suppressForFullscreen || !isForegroundFullscreen;

    public static IReadOnlyList<MonitorWorkArea> CaptureCurrentMonitors()
    {
        var monitors = new List<MonitorWorkArea>();
        foreach (var screen in Screen.AllScreens)
        {
            var dpi = NativeMethods.GetMonitorDpi(screen.DeviceName);
            var scale = dpi / 96d;
            var area = screen.WorkingArea;
            monitors.Add(new(screen.DeviceName, area.Left / scale, area.Top / scale,
                area.Width / scale, area.Height / scale, screen.Primary));
        }
        return monitors;
    }

    public static string MonitorFor(double leftDip, double topDip, double widthDip, double heightDip, IReadOnlyList<MonitorWorkArea> monitors) =>
        MostVisibleMonitor(leftDip, topDip, widthDip, heightDip, monitors)?.MonitorId
        ?? monitors.FirstOrDefault(monitor => monitor.IsPrimary)?.MonitorId
        ?? monitors[0].MonitorId;

    public static bool IsForegroundFullscreen() => NativeMethods.IsForegroundFullscreen();

    private static WindowPlacement BottomRight(MonitorWorkArea monitor, double width, double height) => new(
        monitor.MonitorId,
        Math.Max(monitor.LeftDip, monitor.RightDip - width - EdgeMarginDip),
        Math.Max(monitor.TopDip, monitor.BottomDip - height - EdgeMarginDip));

    private static WindowPlacement Clamp(WindowPlacement placement, MonitorWorkArea monitor, double width, double height)
    {
        double maxLeft = Math.Max(monitor.LeftDip, monitor.RightDip - Math.Min(width, monitor.WidthDip));
        double maxTop = Math.Max(monitor.TopDip, monitor.BottomDip - Math.Min(height, monitor.HeightDip));
        return placement with
        {
            LeftDip = Math.Clamp(placement.LeftDip, monitor.LeftDip, maxLeft),
            TopDip = Math.Clamp(placement.TopDip, monitor.TopDip, maxTop),
        };
    }

    private static bool IsMeaningfullyVisible(double left, double top, double width, double height, MonitorWorkArea monitor) =>
        IntersectionArea(left, top, width, height, monitor) >=
        Math.Min(MinimumVisibleDip, width) * Math.Min(MinimumVisibleDip, height);

    private static MonitorWorkArea? MostVisibleMonitor(
        double left,
        double top,
        double width,
        double height,
        IReadOnlyList<MonitorWorkArea> monitors) => monitors
            .Select(monitor => (Monitor: monitor, Area: IntersectionArea(left, top, width, height, monitor)))
            .Where(result => result.Area > 0)
            .OrderByDescending(result => result.Area)
            .Select(result => result.Monitor)
            .FirstOrDefault();

    private static double IntersectionArea(double left, double top, double width, double height, MonitorWorkArea monitor)
    {
        double intersectionWidth = Math.Max(0, Math.Min(left + width, monitor.RightDip) - Math.Max(left, monitor.LeftDip));
        double intersectionHeight = Math.Max(0, Math.Min(top + height, monitor.BottomDip) - Math.Max(top, monitor.TopDip));
        return intersectionWidth * intersectionHeight;
    }

    private static class NativeMethods
    {
        private const uint MonitorDefaultToNearest = 2;
        private const int EffectiveDpi = 0;

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(nint window, out Rect rect);

        [DllImport("user32.dll")]
        private static extern nint MonitorFromWindow(nint window, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint MonitorFromPoint(Point point, uint flags);

        public static uint GetMonitorDpi(string deviceName)
        {
            var screen = Screen.AllScreens.First(candidate => candidate.DeviceName == deviceName);
            var monitor = MonitorFromPoint(new(screen.Bounds.Left + 1, screen.Bounds.Top + 1), MonitorDefaultToNearest);
            try
            {
                return GetDpiForMonitor(monitor, EffectiveDpi, out var dpiX, out _) == 0 ? dpiX : 96;
            }
            catch (DllNotFoundException) { return 96; }
            catch (EntryPointNotFoundException) { return 96; }
        }

        public static bool IsForegroundFullscreen()
        {
            var window = GetForegroundWindow();
            if (window == 0 || !GetWindowRect(window, out var bounds)) return false;
            var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return false;
            return Math.Abs(bounds.Left - info.Monitor.Left) <= 1
                && Math.Abs(bounds.Top - info.Monitor.Top) <= 1
                && Math.Abs(bounds.Right - info.Monitor.Right) <= 1
                && Math.Abs(bounds.Bottom - info.Monitor.Bottom) <= 1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct Point(int x, int y)
        {
            public readonly int X = x;
            public readonly int Y = y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
        }
    }
}
