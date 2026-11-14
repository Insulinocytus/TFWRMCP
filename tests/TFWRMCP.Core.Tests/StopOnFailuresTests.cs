using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace TFWRMCP.Core.Tests;

public class StopOnFailuresTests
{
    static FakeGame GameWithScript()
    {
        var game = new FakeGame { GameSeconds = 50 };
        game.Scripts["main"] = "x";
        return game;
    }

    static async Task<JObject> Ok(TestServer s, string tool, JObject? args = null)
    {
        var (isError, text) = await s.Call(tool, args);
        Assert.False(isError, text);
        return JObject.Parse(text);
    }

    static JObject Rule(double? ratio = null, int? minCalls = null, params string[] ignore)
    {
        var rule = new JObject();
        if (ratio != null) rule["ratio"] = ratio;
        if (minCalls != null) rule["min_calls"] = minCalls;
        if (ignore.Length > 0) rule["ignore"] = new JArray(ignore);
        return rule;
    }

    static Task<JObject> Start(TestServer s, JToken rule) =>
        Ok(s, "run_script", new JObject { ["name"] = "main", ["timeout_seconds"] = 0, ["stop_on_failures"] = rule });

    static Task<JObject> Latest(TestServer s) => Ok(s, "wait_run", new JObject { ["timeout_seconds"] = 0 });

    static void Calls(FakeGame game, string function, int count, bool failed)
    {
        for (var i = 0; i < count; i++) game.Emit(new ActionCalled(function, failed));
    }

    [Fact]
    public async Task A_failure_ratio_just_over_the_threshold_stops_the_Run_and_exactly_at_it_does_not()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);
        await Start(s, Rule(ratio: 0.5, minCalls: 10));

        Calls(game, "move", 5, failed: false);
        Calls(game, "move", 5, failed: true);
        var atThreshold = await Latest(s);
        Calls(game, "move", 1, failed: true);
        var over = await Latest(s);

        Assert.Equal("running", (string)atThreshold["status"]!);
        Assert.Equal("stopped_by_tool", (string)over["status"]!);
        Assert.Equal(new JObject { ["move"] = 6 }, over["failing"]);
        Assert.Equal(1, game.StopCalls);
    }

    [Fact]
    public async Task A_fractional_ratio_equal_to_the_threshold_does_not_stop_the_Run()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);
        await Start(s, Rule(ratio: 0.3, minCalls: 10));

        Calls(game, "move", 7, failed: false);
        Calls(game, "move", 3, failed: true);
        var atThreshold = await Latest(s);
        Calls(game, "move", 1, failed: true);

        Assert.Equal("running", (string)atThreshold["status"]!);
        Assert.Equal("stopped_by_tool", (string)(await Latest(s))["status"]!);
    }

    [Fact]
    public async Task An_empty_rule_uses_ratio_one_half_and_at_least_100_calls()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);
        await Start(s, new JObject());

        Calls(game, "plant", 99, failed: true);
        var below = await Latest(s);
        Calls(game, "plant", 1, failed: true);
        var reached = await Latest(s);

        Assert.Equal("running", (string)below["status"]!);
        Assert.Equal("stopped_by_tool", (string)reached["status"]!);
        Assert.Equal(new JObject { ["plant"] = 100 }, reached["failing"]);
    }

    [Theory]
    [InlineData(59, true)]
    [InlineData(61, false)]
    public async Task Only_failures_within_the_last_10_game_seconds_count(double later, bool stopped)
    {
        var game = GameWithScript();
        using var s = new TestServer(game);
        await Start(s, Rule(minCalls: 10));

        Calls(game, "move", 9, failed: true);
        game.GameSeconds = later;
        Calls(game, "move", 1, failed: true);

        Assert.Equal(stopped ? "stopped_by_tool" : "running", (string)(await Latest(s))["status"]!);
    }

    [Fact]
    public async Task Functions_are_judged_separately()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);
        await Start(s, Rule(minCalls: 10));

        Calls(game, "harvest", 500, failed: false);
        Calls(game, "move", 9, failed: true);
        var before = await Latest(s);
        Calls(game, "move", 1, failed: true);
        var after = await Latest(s);

        Assert.Equal("running", (string)before["status"]!);
        Assert.Equal("stopped_by_tool", (string)after["status"]!);
        Assert.Equal(new JObject { ["move"] = 10 }, after["failing"]);
    }

    [Fact]
    public async Task Ignored_functions_do_not_take_part()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);
        await Start(s, Rule(minCalls: 10, ignore: "move"));

        Calls(game, "move", 500, failed: true);
        var ignored = await Latest(s);
        Calls(game, "harvest", 10, failed: true);
        var other = await Latest(s);

        Assert.Equal("running", (string)ignored["status"]!);
        Assert.Equal(new JObject { ["harvest"] = 10 }, other["failing"]);
    }

    [Fact]
    public async Task Without_the_parameter_failures_never_stop_the_Run()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);
        await Ok(s, "run_script", new JObject { ["name"] = "main", ["timeout_seconds"] = 0 });

        Calls(game, "move", 1000, failed: true);

        Assert.Equal("running", (string)(await Latest(s))["status"]!);
    }

    [Fact]
    public async Task A_Run_started_by_the_human_is_not_stopped()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);
        await Start(s, Rule(minCalls: 10));
        game.Emit(new RunEnded(RunEnd.Terminated, null, 0));

        game.Emit(new RunStarted("main", new()));
        Calls(game, "move", 1000, failed: true);

        Assert.Equal("running", (string)(await Latest(s))["status"]!);
        Assert.Equal(0, game.StopCalls);
    }

    [Fact]
    public async Task The_rule_ends_with_the_Run_and_the_next_Run_does_not_inherit_it()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);
        await Start(s, Rule(minCalls: 10));
        game.Emit(new RunEnded(RunEnd.Terminated, null, 0));
        var ended = await Latest(s);

        await Ok(s, "run_script", new JObject { ["name"] = "main", ["timeout_seconds"] = 0 });
        Calls(game, "move", 1000, failed: true);

        Assert.Equal("finished", (string)ended["status"]!);
        Assert.Equal("running", (string)(await Latest(s))["status"]!);
    }

    [Fact]
    public async Task Only_a_Run_stopped_by_the_rule_has_failing()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);

        await Start(s, Rule(minCalls: 10));
        Calls(game, "move", 5, failed: true);
        var stopped = await Ok(s, "stop_run");
        await Start(s, Rule(minCalls: 10));
        game.Emit(new RunEnded(RunEnd.Terminated, null, 0));
        var finished = await Latest(s);

        Assert.Equal("stopped_by_tool", (string)stopped["status"]!);
        Assert.Null(stopped["failing"]);
        Assert.Null(finished["failing"]);
    }

    [Fact]
    public async Task The_rule_keeps_watching_after_the_starting_call_has_returned_and_failed_calls_still_count()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);
        var started = await Start(s, Rule(minCalls: 10));

        game.GameSeconds = 55;
        Calls(game, "move", 12, failed: true);
        Calls(game, "move", 3, failed: false);
        var run = await Latest(s);

        Assert.Equal("running", (string)started["status"]!);
        Assert.Equal("stopped_by_tool", (string)run["status"]!);
        Assert.Equal(new JObject { ["move"] = 10 }, run["failing"]);
        Assert.Equal(new JObject { ["move"] = 10 }, run["failed_calls"]);
    }

    [Fact]
    public async Task A_deadline_reached_first_stops_the_Run_without_failing()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);
        await Ok(s, "run_script", new JObject
        {
            ["name"] = "main", ["timeout_seconds"] = 0, ["stop_after_seconds"] = 10, ["stop_on_failures"] = Rule(minCalls: 10),
        });

        Calls(game, "move", 9, failed: true);
        game.GameSeconds = 60;
        var run = await Ok(s, "wait_run", new JObject { ["timeout_seconds"] = 5 });

        Assert.Equal("stopped_by_tool", (string)run["status"]!);
        Assert.Null(run["failing"]);
    }

    [Fact]
    public async Task Failures_first_end_a_wait_for_an_inventory_goal()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);

        var waiting = Ok(s, "run_script", new JObject
        {
            ["name"] = "main", ["timeout_seconds"] = 30, ["until_inventory"] = new JObject { ["hay"] = 1e9 },
            ["stop_when_met"] = true, ["stop_on_failures"] = Rule(minCalls: 10),
        });
        await game.WaitForRun();
        await Task.Delay(100);
        Calls(game, "move", 10, failed: true);
        var run = await waiting;

        Assert.Equal("stopped_by_tool", (string)run["status"]!);
        Assert.False((bool)run["met"]!);
        Assert.Equal(new JObject { ["move"] = 10 }, run["failing"]);
    }

    [Fact]
    public async Task A_met_inventory_goal_first_stops_the_Run_without_failing()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);

        var run = await Ok(s, "run_script", new JObject
        {
            ["name"] = "main", ["timeout_seconds"] = 5, ["until_inventory"] = new JObject { ["hay"] = 1 },
            ["stop_when_met"] = true, ["stop_on_failures"] = Rule(minCalls: 10),
        });
        Calls(game, "move", 100, failed: true);

        Assert.Equal("stopped_by_tool", (string)run["status"]!);
        Assert.True((bool)run["met"]!);
        Assert.Null(run["failing"]);
    }

    [Fact]
    public async Task Write_script_with_run_applies_the_rule()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);

        var written = await Ok(s, "write_script", new JObject
        {
            ["name"] = "main", ["code"] = "y", ["run"] = true, ["timeout_seconds"] = 0, ["stop_on_failures"] = Rule(minCalls: 10),
        });
        Calls(game, "move", 10, failed: true);

        Assert.Equal("running", (string)written["run"]!["status"]!);
        var run = await Latest(s);
        Assert.Equal("stopped_by_tool", (string)run["status"]!);
        Assert.Equal(new JObject { ["move"] = 10 }, run["failing"]);
    }

    [Fact]
    public async Task Wait_run_rejects_the_parameter()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);
        await Ok(s, "run_script", new JObject { ["name"] = "main", ["timeout_seconds"] = 0 });

        var (isError, text) = await s.Call("wait_run", new JObject { ["stop_on_failures"] = new JObject() });
        Calls(game, "move", 1000, failed: true);

        Assert.True(isError);
        Assert.Contains("stop_on_failures", text);
        Assert.Equal("running", (string)(await Latest(s))["status"]!);
    }

    [Theory]
    [InlineData("{\"ratio\": 0}")]
    [InlineData("{\"ratio\": -0.5}")]
    [InlineData("{\"ratio\": 1.5}")]
    [InlineData("{\"ratio\": \"0.5\"}")]
    [InlineData("{\"min_calls\": 0}")]
    [InlineData("{\"min_calls\": -3}")]
    [InlineData("{\"min_calls\": 2.5}")]
    [InlineData("{\"min_calls\": \"100\"}")]
    [InlineData("{\"ignore\": \"move\"}")]
    [InlineData("{\"ignore\": [1]}")]
    [InlineData("{\"windows\": 5}")]
    [InlineData("\"yes\"")]
    [InlineData("[]")]
    public async Task Invalid_rules_are_rejected_and_nothing_starts_or_is_written(string rule)
    {
        var game = GameWithScript();
        using var s = new TestServer(game);

        var (runError, runText) = await s.Call("run_script", new JObject
            { ["name"] = "main", ["stop_on_failures"] = JToken.Parse(rule) });
        var (writeError, writeText) = await s.Call("write_script", new JObject
            { ["name"] = "other", ["code"] = "y", ["run"] = true, ["stop_on_failures"] = JToken.Parse(rule) });

        Assert.True(runError);
        Assert.Contains("stop_on_failures", runText);
        Assert.True(writeError);
        Assert.Contains("stop_on_failures", writeText);
        Assert.False(game.RunActive);
        Assert.DoesNotContain("other", game.Scripts.Keys);
    }

    [Fact]
    public async Task The_rule_without_run_is_rejected_by_write_script_and_nothing_is_written()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);

        var (isError, text) = await s.Call("write_script", new JObject
            { ["name"] = "other", ["code"] = "y", ["stop_on_failures"] = new JObject() });

        Assert.True(isError);
        Assert.Contains("stop_on_failures", text);
        Assert.DoesNotContain("other", game.Scripts.Keys);
    }
}
