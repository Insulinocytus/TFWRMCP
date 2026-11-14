using System.Linq;
using Xunit;

namespace TFWRMCP.Core.Tests;

public class LayoutTests
{
    // y grows upwards, like the game's UI: Top is the larger y.
    static readonly Box Farm = new(left: 1000, top: 500, right: 1600, bottom: -100);

    static Unit Window(string name, float width = 300, float height = 200, bool running = false, bool focused = false) =>
        new(name, width, height, running, focused);

    static Placement Place(string name, params Unit[] units) => Layout.Arrange(Farm, units).Single(p => p.Name == name);

    [Fact]
    public void A_running_window_goes_to_the_left_column_with_its_right_edge_20_from_the_farm_and_its_top_on_the_farm_top()
    {
        var placed = Place("main", Window("main", width: 300, running: true));

        Assert.Equal(1000 - 20 - 300, placed.Left);
        Assert.Equal(500, placed.Top);
    }

    [Fact]
    public void A_window_that_is_neither_running_nor_focused_goes_to_the_right_column_with_its_left_edge_20_from_the_farm()
    {
        var placed = Place("idle", Window("idle"));

        Assert.Equal(1600 + 20, placed.Left);
        Assert.Equal(500, placed.Top);
    }

    [Fact]
    public void A_focused_window_that_is_not_running_goes_to_the_left_column()
    {
        var placed = Place("editing", Window("editing", width: 250, focused: true));

        Assert.Equal(1000 - 20 - 250, placed.Left);
    }

    [Fact]
    public void Units_stack_down_each_column_by_name_with_20_between_them_whatever_order_they_come_in()
    {
        var units = new[]
        {
            Window("b", height: 100),
            Window("a", height: 200),
            Window("y", height: 50, running: true),
            Window("X", height: 70, running: true),
            Window("c", height: 10),
        };

        var placed = Layout.Arrange(Farm, units).ToDictionary(p => p.Name, p => p.Top);

        Assert.Equal(500, placed["a"]);
        Assert.Equal(500 - 200 - 20, placed["b"]);
        Assert.Equal(500 - 200 - 20 - 100 - 20, placed["c"]);
        Assert.Equal(500, placed["X"]);
        Assert.Equal(500 - 70 - 20, placed["y"]);
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

        var placed = Layout.Arrange(Farm, units).ToDictionary(p => p.Name, p => p.Top);

        Assert.Equal(500, placed["a"]);
        Assert.Equal(500 - 100 - 80 - 20, placed["b"]);
        Assert.DoesNotContain("zz", placed.Keys);
    }

    [Fact]
    public void A_dock_group_is_sorted_by_its_top_window_not_by_the_names_below_it()
    {
        var units = new[] { Window("b", height: 50), Group(Window("m", height: 10), Window("a", height: 10)) };

        var placed = Layout.Arrange(Farm, units).ToDictionary(p => p.Name, p => p.Top);

        Assert.Equal(500, placed["b"]);
        Assert.Equal(500 - 50 - 20, placed["m"]);
    }

    [Fact]
    public void A_dock_group_is_as_wide_as_its_widest_window_and_is_open_when_any_of_its_windows_is()
    {
        var group = Group(Window("top", width: 200), Window("under", width: 320, focused: true));

        var placed = Layout.Arrange(Farm, new[] { group }).Single();

        Assert.Equal(1000 - 20 - 320, placed.Left);
    }

    [Fact]
    public void A_dock_group_of_idle_windows_goes_to_the_right_column()
    {
        var placed = Layout.Arrange(Farm, new[] { Group(Window("top"), Window("under")) }).Single();

        Assert.Equal(1600 + 20, placed.Left);
    }

    [Fact]
    public void Both_columns_move_outward_when_the_farm_grows()
    {
        var bigger = new Box(left: 800, top: 500, right: 1900, bottom: -400);
        var units = new[] { Window("run", width: 300, running: true), Window("idle") };

        var before = Layout.Arrange(Farm, units).ToDictionary(p => p.Name);
        var after = Layout.Arrange(bigger, units).ToDictionary(p => p.Name);

        Assert.Equal(before["run"].Left - 200, after["run"].Left);
        Assert.Equal(before["idle"].Left + 300, after["idle"].Left);
    }

    [Fact]
    public void Expanded_follows_running_or_focus()
    {
        Assert.False(Window("w").Expanded);
        Assert.True(Window("w", running: true).Expanded);
        Assert.True(Window("w", focused: true).Expanded);
        Assert.True(Layout.IsExpanded(running: true, focused: true));
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
