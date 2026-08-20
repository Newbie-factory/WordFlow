using WordFlow.Infrastructure.Windows;

namespace WordFlow.Infrastructure.Tests.Windows;

public sealed class WindowPlacementServiceTests
{
    [Fact]
    public void Default_uses_primary_physical_work_area_and_its_DPI()
    {
        var monitors = new[]
        {
            Monitor("stable-primary", "DISPLAY1", 0, 0, 2560, 1400, 144, true),
            Monitor("stable-left", "DISPLAY2", -1920, -200, 1920, 1040, 96, false),
        };

        var placement = WindowPlacementService.Resolve(null, monitors, 480, 340);

        Assert.Equal("stable-primary", placement.Monitor.DurableId);
        Assert.Equal(1816, placement.LeftPx);
        Assert.Equal(866, placement.TopPx);
        Assert.Equal(720, placement.WidthPx);
        Assert.Equal(510, placement.HeightPx);
    }

    [Fact]
    public void Mixed_DPI_negative_coordinates_remain_in_one_physical_coordinate_space()
    {
        var monitors = new[]
        {
            Monitor("primary", "DISPLAY1", 0, 0, 2560, 1400, 144, true),
            Monitor("left", "DISPLAY2", -1920, -180, 1920, 1040, 96, false),
        };
        var saved = new WindowPlacement(2, "left", "DISPLAY2", 110, 70);

        var placement = WindowPlacementService.Resolve(saved, monitors, 480, 340);

        Assert.Equal(-1810, placement.LeftPx);
        Assert.Equal(-110, placement.TopPx);
        Assert.Equal(480, placement.WidthPx);
        Assert.Equal(340, placement.HeightPx);
    }

    [Fact]
    public void Durable_identity_survives_device_renumbering_and_device_name_is_fallback()
    {
        var renamed = Monitor("edid-ABC", "DISPLAY7", -1600, 0, 1600, 900, 120, false);
        var primary = Monitor("edid-PRIMARY", "DISPLAY1", 0, 0, 1920, 1040, 96, true);

        var durable = WindowPlacementService.Resolve(new(2, "edid-ABC", "DISPLAY2", 40, 50), [primary, renamed], 480, 340);
        var fallback = WindowPlacementService.Resolve(new(2, "missing", "DISPLAY7", 40, 50), [primary, renamed], 480, 340);

        Assert.Equal("edid-ABC", durable.Monitor.DurableId);
        Assert.Equal("edid-ABC", fallback.Monitor.DurableId);
    }

    [Fact]
    public void Removed_monitor_and_short_work_area_repair_fully_onscreen()
    {
        var primary = Monitor("primary", "DISPLAY1", 0, 40, 1366, 728, 96, true);
        var resolved = WindowPlacementService.Resolve(new(2, "gone", "DISPLAY9", 4000, 2000), [primary], 480, 900);

        Assert.Equal(40, resolved.TopPx);
        Assert.True(resolved.HeightPx <= primary.HeightPx);
        Assert.InRange(resolved.LeftPx, primary.LeftPx, primary.RightPx - resolved.WidthPx);
        Assert.InRange(resolved.BottomPx, primary.TopPx, primary.BottomPx);
    }

    [Fact]
    public void Suggested_DPI_rectangle_is_honored_then_clamped_to_new_monitor_work_area()
    {
        var target = Monitor("right", "DISPLAY3", 1920, 0, 3840, 2080, 192, false);
        var suggested = new PhysicalWindowRect(2200, 120, 960, 680);

        var resolved = WindowPlacementService.ResolveSuggested(suggested, target);

        Assert.Equal(suggested, resolved);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void Optional_fullscreen_suppression_restores_normal_topmost_state(bool enabled, bool fullscreen, bool expected) =>
        Assert.Equal(expected, WindowPlacementService.ShouldBeTopmost(enabled, fullscreen));

    [Fact]
    public void Fullscreen_classifier_excludes_app_shell_cloaked_system_invisible_and_maximized_windows()
    {
        var full = new PhysicalWindowRect(0, 0, 1920, 1080);
        var monitor = new PhysicalWindowRect(0, 0, 1920, 1080);
        Assert.True(WindowPlacementService.IsFullscreenCandidate(new(true, false, false, false, false, "GameWindow", full), monitor));
        Assert.False(WindowPlacementService.IsFullscreenCandidate(new(true, false, false, true, false, "GameWindow", full), monitor));
        Assert.False(WindowPlacementService.IsFullscreenCandidate(new(true, false, true, false, false, "GameWindow", full), monitor));
        Assert.False(WindowPlacementService.IsFullscreenCandidate(new(true, true, false, false, false, "GameWindow", full), monitor));
        Assert.False(WindowPlacementService.IsFullscreenCandidate(new(true, false, false, false, true, "Shell_TrayWnd", full), monitor));
        Assert.False(WindowPlacementService.IsFullscreenCandidate(new(false, false, false, false, false, "GameWindow", full), monitor));
    }

    [Fact]
    public void Atomic_save_failure_is_nonfatal_and_reported()
    {
        string directoryTarget = Path.Combine(Path.GetTempPath(), $"wordflow-placement-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryTarget);
        try
        {
            var service = new WindowPlacementService(directoryTarget);
            Assert.False(service.TrySave(new(2, "primary", "DISPLAY1", 0, 0), out var error));
            Assert.NotNull(error);
        }
        finally { Directory.Delete(directoryTarget); }
    }

    private static MonitorWorkArea Monitor(string stable, string device, int left, int top, int width, int height, uint dpi, bool primary) =>
        new(stable, device, left, top, width, height, dpi, dpi, primary);
}
