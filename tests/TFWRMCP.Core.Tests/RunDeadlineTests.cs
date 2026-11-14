using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace TFWRMCP.Core.Tests;

public class RunDeadlineTests
{
    static FakeGame GameWithScript()
    {
        var game = new FakeGame { GameSeconds = 50 };
        game.Scripts["main"] = "x";
        return game;
    }

    static JObject RunFor(JToken stopAfter, double timeout = 0) =>
        new() { ["name"] = "main", ["stop_after_seconds"] = stopAfter, ["timeout_seconds"] = timeout };

    static async Task<JObject> Ok(TestServer s, string tool, JObject? args = null)
    {
        var (isError, text) = await s.Call(tool, args);
        Assert.False(isError, text);
        return JObject.Parse(text);
    }

    [Fact]
    public async Task The_Run_is_stopped_once_the_game_clock_has_advanced_that_far_after_the_call_returned()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);

        var started = await Ok(s, "run_script", RunFor(10));
        game.GameSeconds = 59.5;
        var early = await Ok(s, "wait_run", new JObject { ["timeout_seconds"] = 0.3 });
        game.GameSeconds = 60;
        var stopped = await Ok(s, "wait_run", new JObject { ["timeout_seconds"] = 5 });

        Assert.Equal("running", (string)started["status"]!);
        Assert.Equal("running", (string)early["status"]!);
        Assert.Equal("stopped_by_tool", (string)stopped["status"]!);
        Assert.Equal(10, (double)stopped["game_seconds"]!);
        Assert.Equal(1, game.StopCalls);
    }

    [Fact]
    public async Task An_inventory_goal_that_is_met_first_stops_the_Run_before_the_deadline()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);
        var args = RunFor(10, timeout: 5);
        args["until_inventory"] = new JObject { ["hay"] = 1 };
        args["stop_when_met"] = true;

        var run = await Ok(s, "run_script", args);

        Assert.Equal("stopped_by_tool", (string)run["status"]!);
        Assert.True((bool)run["met"]!);
        Assert.Equal(0, (double)run["game_seconds"]!);
    }

    [Fact]
    public async Task A_deadline_reached_first_ends_a_wait_for_an_inventory_goal()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);
        var args = RunFor(10, timeout: 30);
        args["until_inventory"] = new JObject { ["hay"] = 1e9 };
        args["stop_when_met"] = true;

        var waiting = Ok(s, "run_script", args);
        await game.WaitForRun();
        game.GameSeconds = 61;
        var run = await waiting;

        Assert.Equal("stopped_by_tool", (string)run["status"]!);
        Assert.False((bool)run["met"]!);
    }

    [Fact]
    public async Task A_Run_that_ends_first_drops_its_deadline_and_the_next_Run_does_not_inherit_it()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);

        await Ok(s, "run_script", RunFor(10));
        game.Emit(new RunEnded(RunEnd.Terminated, null, 0));
        await Ok(s, "run_script", new JObject { ["name"] = "main", ["timeout_seconds"] = 0 });
        game.GameSeconds = 1000;
        var run = await Ok(s, "wait_run", new JObject { ["timeout_seconds"] = 0.3 });

        Assert.Equal("running", (string)run["status"]!);
        Assert.Equal(0, game.StopCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData("10")]
    public async Task Stop_after_seconds_must_be_a_positive_number_and_nothing_starts_otherwise(object value)
    {
        var game = GameWithScript();
        using var s = new TestServer(game);

        var (isError, text) = await s.Call("run_script", RunFor(JToken.FromObject(value)));

        Assert.True(isError);
        Assert.Contains("stop_after_seconds", text);
        Assert.False(game.RunActive);
    }
}
