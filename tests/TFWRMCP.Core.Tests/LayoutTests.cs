using System.Linq;
using Xunit;

namespace TFWRMCP.Core.Tests;

public class LayoutTests
{
    // y grows upwards, like the game's UI: Top is the larger y. A 100 high resource bar is off the bottom, so the middle is at 550.
    static readonly Box Screen = new(left: 0, top: 1000, right: 1600, bottom: 100);

    static Unit Window(string name, float width = 300, float height = 200, bool running = false) =>
        new(name, width, height, running);

    static Placement Place(string name, params Unit[] units) => Layout.Arrange(Screen, units).Single(p => p.Name == name);

    [Fact]
    public void A_running_window_goes_20_from_the_screen_left_and_is_centered_vertically()
    {
        var placed = Place("main", Window("main", height: 200, running: true));

        Assert.Equal(20, placed.Left);
        Assert.Equal(650, placed.Top);
    }

    [Fact]
    public void A_window_that_is_not_running_goes_20_from_the_screen_right_and_is_centered_vertically()
    {
        var placed = Place("idle", Window("idle", width: 300, height: 200));

        Assert.Equal(1600 - 20 - 300, placed.Left);
        Assert.Equal(650, placed.Top);
    }

    [Fact]
    public void Units_stack_down_each_column_by_name_touching_and_each_column_is_centered_as_a_whole()
    {
        var units = new[]
        {
            Window("b", height: 100),
            Window("a", height: 200),
            Window("y", height: 50, running: true),
            Window("X", height: 70, running: true),
            Window("c", height: 10),
        };

        var placed = Layout.Arrange(Screen, units).ToDictionary(p => p.Name, p => p.Top);

        // Right column: 200 + 100 + 10 = 310 high, so its top is 155 above the middle.
        Assert.Equal(705, placed["a"]);
        Assert.Equal(705 - 200, placed["b"]);
        Assert.Equal(705 - 200 - 100, placed["c"]);
        // Left column: 70 + 50 = 120 high.
        Assert.Equal(610, placed["X"]);
        Assert.Equal(610 - 70, placed["y"]);
    }

    [Fact]
    public void A_column_too_tall_for_the_screen_starts_20_below_the_screen_top()
    {
        var placed = Layout.Arrange(Screen, new[] { Window("a", height: 500), Window("b", height: 500) }).ToDictionary(p => p.Name, p => p.Top);

        Assert.Equal(980, placed["a"]);
        Assert.Equal(980 - 500, placed["b"]);
    }

    static Unit Group(params Unit[] topFirst) => Layout.Group(topFirst);

    [Fact]
    public void A_dock_group_takes_the_height_of_all_its_windows_in_its_column()
    {
        var units = new[]
        {
            Group(Window("a", height: 100), Window("zz", height: 80)),
            Window("b", height: 50),
        };

        var placed = Layout.Arrange(Screen, units).ToDictionary(p => p.Name, p => p.Top);

        // 180 + 50 = 230 high.
        Assert.Equal(665, placed["a"]);
        Assert.Equal(665 - 100 - 80, placed["b"]);
        Assert.DoesNotContain("zz", placed.Keys);
    }

    [Fact]
    public void A_dock_group_is_sorted_by_its_top_window_not_by_the_names_below_it()
    {
        var units = new[] { Window("b", height: 50), Group(Window("m", height: 10), Window("a", height: 10)) };

        var placed = Layout.Arrange(Screen, units).ToDictionary(p => p.Name, p => p.Top);

        Assert.True(placed["b"] > placed["m"]);
    }

    [Fact]
    public void A_dock_group_is_as_wide_as_its_widest_window_and_runs_when_any_of_its_windows_does()
    {
        var group = Group(Window("top", width: 200), Window("under", width: 320, running: true));

        Assert.Equal(320, group.Width);
        Assert.Equal(20, Layout.Arrange(Screen, new[] { group }).Single().Left);
    }

    [Fact]
    public void The_room_between_the_columns_leaves_20_on_each_side_of_the_widest_window_of_each_column()
    {
        var units = new[] { Window("run", width: 250, running: true), Window("a", width: 300), Window("b", width: 180) };

        var room = Layout.Between(Screen, units);

        Assert.Equal(20 + 250 + 20, room.Left);
        Assert.Equal(1600 - 20 - 300 - 20, room.Right);
        Assert.Equal(1000, room.Top);
        Assert.Equal(100, room.Bottom);
    }

    [Fact]
    public void Without_a_column_on_one_side_the_room_reaches_that_screen_edge()
    {
        var room = Layout.Between(Screen, new[] { Window("idle", width: 300) });

        Assert.Equal(0, room.Left);
        Assert.Equal(1600 - 20 - 300 - 20, room.Right);
    }

    [Fact]
    public void A_tween_starts_at_its_start_value_ends_at_its_end_value_and_eases_out_exponentially_in_between()
    {
        Assert.Equal(10, Tween.EaseOutExpo(10, 110, start: 5, duration: 0.5, now: 5));
        Assert.Equal(110, Tween.EaseOutExpo(10, 110, start: 5, duration: 0.5, now: 5.5));
        Assert.Equal(10 + 100 * 0.96875, Tween.EaseOutExpo(10, 110, start: 5, duration: 0.5, now: 5.25), 3);
        Assert.Equal(60, Tween.EaseOutExpo(10, 110, start: 5, duration: 0.5, now: 5.05), 3);
    }

    [Fact]
    public void A_tween_holds_its_ends_outside_its_time_span()
    {
        Assert.Equal(-4, Tween.EaseOutExpo(-4, 8, start: 1, duration: 1, now: 0));
        Assert.Equal(8, Tween.EaseOutExpo(-4, 8, start: 1, duration: 1, now: 99));
    }
}
