using Xunit;

namespace TFWRMCP.Core.Tests;

public class CameraTargetTests
{
    // y grows upwards, like the game's UI. Center (1300, 200), 300 from the center to each farm edge.
    static readonly Box Farm = new(left: 1000, top: 500, right: 1600, bottom: -100);

    static CameraView Fit(Box[]? windows = null, float width = 1920, float height = 1080, float min = 0.1f, float max = 10) =>
        CameraTarget.Fit(Farm, windows ?? new Box[0], roomWidth: width, roomHeight: height, minZoom: min, maxZoom: max);

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
    public void The_farm_alone_fills_the_room_left_by_the_windows()
    {
        // A 600 square farm in a 1000 by 840 room: the height limits.
        Assert.Equal(1.4f, Fit(width: 1000, height: 840).Zoom, 4);
        Assert.Equal(1000f / 600f, Fit(width: 1000, height: 1080).Zoom, 4);
    }

    [Fact]
    public void A_window_far_above_the_farm_limits_the_zoom_by_height()
    {
        // Its top edge is 800 above the center: a 1600 range in a 920 high room.
        var view = Fit(new[] { new Box(1100, 1000, 1500, 900) }, height: 920);

        Assert.Equal(920f / 1600f, view.Zoom, 4);
    }

    [Fact]
    public void The_zoom_is_held_between_its_limits()
    {
        Assert.Equal(1.2f, Fit(max: 1.2f).Zoom, 4);
        Assert.Equal(1f, Fit(new[] { new Box(100, 450, 800, 300) }, min: 1f).Zoom, 4);
    }
}
