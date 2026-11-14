using Xunit;

namespace TFWRMCP.Core.Tests;

public class CameraTargetTests
{
    // y grows upwards, like the game's UI. Center (1300, 200), 300 from the center to each farm edge.
    static readonly Box Farm = new(left: 1000, top: 500, right: 1600, bottom: -100);

    static CameraView Fit(Box[]? windows = null, float bar = 0, float min = 0.1f, float max = 10) =>
        CameraTarget.Fit(Farm, windows ?? new Box[0], viewportWidth: 1920, viewportHeight: 1080, barHeight: bar, minZoom: min, maxZoom: max);

    [Fact]
    public void The_target_center_is_the_farm_center_whatever_the_windows_are()
    {
        var view = Fit(new[] { new Box(100, 450, 800, 300) });

        Assert.Equal(1300, view.X);
        Assert.Equal(200, view.Y);
    }

    [Fact]
    public void The_zoom_puts_the_farthest_window_edge_exactly_on_the_screen_edge()
    {
        // The window's left edge is 1200 from the center, so the symmetric range is 2400 wide: 1920 / 2400.
        var view = Fit(new[] { new Box(100, 450, 800, 300) });

        Assert.Equal(0.8f, view.Zoom, 4);
    }

    [Fact]
    public void The_resource_bar_height_is_taken_off_the_vertical_room()
    {
        // Farm alone: 600 range; 1080 - 120 high leaves 960 / 600, tighter than 1920 / 600.
        Assert.Equal(1.6f, Fit(bar: 120).Zoom, 4);
        Assert.Equal(1.8f, Fit().Zoom, 4);
    }

    [Fact]
    public void A_window_far_above_the_farm_limits_the_zoom_by_height()
    {
        // Its top edge is 800 above the center: a 1600 range in 1080 - 80.
        var view = Fit(new[] { new Box(1100, 1000, 1500, 900) }, bar: 80);

        Assert.Equal(1000f / 1600f, view.Zoom, 4);
    }

    [Fact]
    public void The_zoom_is_held_between_its_limits()
    {
        Assert.Equal(1.2f, Fit(max: 1.2f).Zoom, 4);
        Assert.Equal(1f, Fit(new[] { new Box(100, 450, 800, 300) }, min: 1f).Zoom, 4);
    }
}
