using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace TFWRMCP.Core.Tests;

public class LayoutTests
{
    // y grows upwards, like the game's UI: Top is the larger y. A 100 high resource bar is off the bottom.
    static readonly Box Screen = new(left: 0, top: 1000, right: 1600, bottom: 100);

    // The column hangs from (right 1600, top 900), say the bottom of the game's buttons.
    static Dictionary<string, Placement> Arrange(params Unit[] units) =>
        Layout.Arrange(right: 1600, top: 900, units).ToDictionary(p => p.Name);

    static Unit Window(string name, float width = 300, float height = 200, bool running = false) =>
        new(name, width, height, running);

    [Fact]
    public void A_window_hangs_20_left_of_the_right_edge_and_20_below_the_top()
    {
        var placed = Arrange(Window("idle", width: 300))["idle"];

        Assert.Equal(1600 - 20 - 300, placed.Left);
        Assert.Equal(880, placed.Top);
    }

    [Fact]
    public void Running_windows_come_first_then_the_rest_each_by_name_and_touching()
    {
        var placed = Arrange(
            Window("b", height: 100),
            Window("a", height: 200),
            Window("y", height: 50, running: true),
            Window("X", height: 70, running: true),
            Window("c", height: 10));

        Assert.Equal(880, placed["X"].Top);
        Assert.Equal(880 - 70, placed["y"].Top);
        Assert.Equal(880 - 70 - 50, placed["a"].Top);
        Assert.Equal(880 - 70 - 50 - 200, placed["b"].Top);
        Assert.Equal(880 - 70 - 50 - 200 - 100, placed["c"].Top);
    }

    [Fact]
    public void Windows_of_different_widths_share_the_right_edge()
    {
        var placed = Arrange(Window("a", width: 300), Window("b", width: 180));

        Assert.Equal(1600 - 20 - 300, placed["a"].Left);
        Assert.Equal(1600 - 20 - 180, placed["b"].Left);
    }

    static Unit Group(params Unit[] topFirst) => Layout.Group(topFirst);

    [Fact]
    public void A_dock_group_takes_the_height_of_all_its_windows()
    {
        var placed = Arrange(Group(Window("a", height: 100), Window("zz", height: 80)), Window("b", height: 50));

        Assert.Equal(880, placed["a"].Top);
        Assert.Equal(880 - 100 - 80, placed["b"].Top);
        Assert.DoesNotContain("zz", placed.Keys);
    }

    [Fact]
    public void A_dock_group_is_sorted_by_its_top_window_not_by_the_names_below_it()
    {
        var placed = Arrange(Window("b", height: 50), Group(Window("m", height: 10), Window("a", height: 10)));

        Assert.True(placed["b"].Top > placed["m"].Top);
    }

    [Fact]
    public void A_dock_group_is_as_wide_as_its_widest_window_and_runs_when_any_of_its_windows_does()
    {
        var group = Group(Window("top", width: 200), Window("under", width: 320, running: true));
        var placed = Arrange(Window("a"), group);

        Assert.Equal(320, group.Width);
        Assert.True(placed["top"].Top > placed["a"].Top);
    }

    [Fact]
    public void The_room_leaves_the_widest_window_and_20_on_each_side_off_both_sides_of_the_screen()
    {
        var room = Layout.Room(Screen, new[] { Window("run", width: 250, running: true), Window("a", width: 300) });

        Assert.Equal(20 + 300 + 20, room.Left);
        Assert.Equal(1600 - 20 - 300 - 20, room.Right);
        Assert.Equal(1000, room.Top);
        Assert.Equal(100, room.Bottom);
    }

    [Fact]
    public void Without_windows_the_room_is_the_whole_screen()
    {
        var room = Layout.Room(Screen, new Unit[0]);

        Assert.Equal(0, room.Left);
        Assert.Equal(1600, room.Right);
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
