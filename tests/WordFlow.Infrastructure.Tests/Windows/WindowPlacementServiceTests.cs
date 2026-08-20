using WordFlow.Infrastructure.Windows;

namespace WordFlow.Infrastructure.Tests.Windows;

public sealed class WindowPlacementServiceTests
{
    [Fact]
    public void Default_placement_uses_primary_work_area_bottom_right_in_dips()
    {
        var monitors = new[]
        {
            new MonitorWorkArea("PRIMARY", 0, 0, 1536, 824, true),
            new MonitorWorkArea("SECONDARY", -1280, 0, 1280, 720, false),
        };

        var placement = WindowPlacementService.Resolve(null, monitors, widthDip: 480, heightDip: 340);

        Assert.Equal("PRIMARY", placement.MonitorId);
        Assert.Equal(1040, placement.LeftDip);
        Assert.Equal(468, placement.TopDip);
    }

    [Fact]
    public void Persisted_negative_coordinates_are_kept_on_the_matching_monitor()
    {
        var monitors = new[]
        {
            new MonitorWorkArea("PRIMARY", 0, 0, 1920, 1040, true),
            new MonitorWorkArea("LEFT", -1600, -120, 1600, 900, false),
        };
        var saved = new WindowPlacement("LEFT", -1510, -60);

        var placement = WindowPlacementService.Resolve(saved, monitors, 480, 340);

        Assert.Equal(saved, placement);
    }

    [Fact]
    public void Missing_monitor_and_offscreen_coordinates_are_repaired_to_a_visible_work_area()
    {
        var monitors = new[] { new MonitorWorkArea("PRIMARY", 0, 0, 1280, 680, true) };
        var saved = new WindowPlacement("REMOVED", 4200, 2100);

        var placement = WindowPlacementService.Resolve(saved, monitors, 480, 340);

        Assert.Equal("PRIMARY", placement.MonitorId);
        Assert.InRange(placement.LeftDip, 0, 800);
        Assert.InRange(placement.TopDip, 0, 340);
    }

    [Fact]
    public void Invalid_coordinates_on_an_existing_monitor_repair_on_that_monitor()
    {
        var monitors = new[]
        {
            new MonitorWorkArea("PRIMARY", 0, 0, 1920, 1040, true),
            new MonitorWorkArea("LEFT", -1600, -120, 1600, 900, false),
        };

        var placement = WindowPlacementService.Resolve(new("LEFT", -9000, 4000), monitors, 480, 340);

        Assert.Equal("LEFT", placement.MonitorId);
        Assert.InRange(placement.LeftDip, -1600, -480);
        Assert.InRange(placement.TopDip, -120, 440);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void Optional_fullscreen_suppression_restores_normal_topmost_state(bool enabled, bool fullscreen, bool expected) =>
        Assert.Equal(expected, WindowPlacementService.ShouldBeTopmost(enabled, fullscreen));
}
