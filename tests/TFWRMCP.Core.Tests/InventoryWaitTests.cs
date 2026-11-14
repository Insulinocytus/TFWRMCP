using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace TFWRMCP.Core.Tests;

public class InventoryWaitTests
{
    static FakeGame GameWith()
    {
        var game = new FakeGame();
        game.Scripts["main"] = "x";
        return game;
    }

    static JObject Wait(double timeout, double hay, bool? stop = null)
    {
        var args = new JObject { ["timeout_seconds"] = timeout, ["until_inventory"] = new JObject { ["hay"] = hay } };
        if (stop != null) args["stop_when_met"] = stop;
        return args;
    }

    static async Task<JObject> Ok(TestServer s, string tool, JObject? args = null)
    {
        var (isError, text) = await s.Call(tool, args);
        Assert.False(isError, text);
        return JObject.Parse(text);
    }

    static async Task StartRunning(TestServer s) =>
        await Ok(s, "run_script", new JObject { ["name"] = "main", ["timeout_seconds"] = 0 });

    static async Task RaiseLater(FakeGame game, string item, double amount)
    {
        await Task.Delay(150);
        game.State.Inventory[item] = amount;
    }

    [Fact]
    public async Task Wait_run_returns_met_once_the_inventory_reaches_the_target_and_the_Run_keeps_going()
    {
        var game = GameWith();
        using var s = new TestServer(game);
        await StartRunning(s);

        var clock = Stopwatch.StartNew();
        var waiting = Ok(s, "wait_run", Wait(30, 20));
        _ = RaiseLater(game, "hay", 25);
        var run = await waiting;

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal("running", (string)run["status"]!);
        Assert.True((bool)run["met"]!);
        Assert.Equal(25, (double)run["inventory"]!["hay"]!);
        Assert.Equal(0, (double)run["inventory"]!["wood"]!);
        Assert.True(game.RunActive);
        Assert.Equal(0, game.StopCalls);
    }

    [Fact]
    public async Task Stop_when_met_stops_the_Run_like_stop_run()
    {
        var game = GameWith();
        using var s = new TestServer(game);
        await StartRunning(s);

        var waiting = Ok(s, "wait_run", Wait(30, 20, stop: true));
        _ = RaiseLater(game, "hay", 25);
        var run = await waiting;

        Assert.Equal("stopped_by_tool", (string)run["status"]!);
        Assert.True((bool)run["met"]!);
        Assert.Equal(1, game.StopCalls);
        Assert.False(game.RunActive);
    }

    [Fact]
    public async Task A_target_that_already_holds_returns_met_at_once_and_stop_when_met_still_stops()
    {
        var game = GameWith();
        using var s = new TestServer(game);
        await StartRunning(s);

        var met = await Ok(s, "wait_run", Wait(30, 10));
        Assert.Equal("running", (string)met["status"]!);
        Assert.True((bool)met["met"]!);
        Assert.Equal(0, game.StopCalls);

        var stopped = await Ok(s, "wait_run", Wait(30, 10, stop: true));
        Assert.Equal("stopped_by_tool", (string)stopped["status"]!);
        Assert.True((bool)stopped["met"]!);
        Assert.Equal(1, game.StopCalls);
    }

    [Fact]
    public async Task A_Run_that_ends_first_returns_met_false_without_waiting_for_the_timeout()
    {
        var game = GameWith();
        using var s = new TestServer(game);
        await StartRunning(s);

        var clock = Stopwatch.StartNew();
        var waiting = Ok(s, "wait_run", Wait(30, 1000, stop: true));
        _ = Task.Delay(150).ContinueWith(_ => game.Emit(new RunEnded(RunEnd.Terminated, null, 0)));
        var run = await waiting;

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal("finished", (string)run["status"]!);
        Assert.False((bool)run["met"]!);
        Assert.Equal(12.5, (double)run["inventory"]!["hay"]!);
        Assert.Equal(0, game.StopCalls);
    }

    [Fact]
    public async Task A_Run_that_already_ended_returns_met_false_at_once()
    {
        var game = GameWith();
        game.OnStartRun = _ => game.Emit(new RunEnded(RunEnd.Terminated, null, 0));
        using var s = new TestServer(game);

        var run = await Ok(s, "run_script", new JObject { ["name"] = "main", ["until_inventory"] = new JObject { ["hay"] = 1000 } });

        Assert.Equal("finished", (string)run["status"]!);
        Assert.False((bool)run["met"]!);
    }

    [Fact]
    public async Task A_timeout_returns_running_and_met_false_and_the_Run_continues()
    {
        var game = GameWith();
        using var s = new TestServer(game);
        await StartRunning(s);

        var run = await Ok(s, "wait_run", Wait(0.3, 1000, stop: true));

        Assert.Equal("running", (string)run["status"]!);
        Assert.False((bool)run["met"]!);
        Assert.True(game.RunActive);
        Assert.Equal(0, game.StopCalls);
    }

    [Fact]
    public async Task A_zero_timeout_checks_once_and_returns()
    {
        var game = GameWith();
        using var s = new TestServer(game);
        await StartRunning(s);

        var run = await Ok(s, "wait_run", Wait(0, 1000));

        Assert.Equal("running", (string)run["status"]!);
        Assert.False((bool)run["met"]!);
    }

    [Fact]
    public async Task Run_script_runs_until_the_target_and_stops_in_one_call()
    {
        var game = GameWith();
        game.State.Inventory["carrot"] = 0;
        game.OnStartRun = script =>
        {
            game.Emit(new RunOutput("go"));
            _ = RaiseLater(game, "carrot", 600);
        };
        using var s = new TestServer(game);

        var run = await Ok(s, "run_script", new JObject
        {
            ["name"] = "main",
            ["timeout_seconds"] = 30,
            ["until_inventory"] = new JObject { ["carrot"] = 600 },
            ["stop_when_met"] = true,
        });

        Assert.Equal("stopped_by_tool", (string)run["status"]!);
        Assert.True((bool)run["met"]!);
        Assert.Equal(600, (double)run["inventory"]!["carrot"]!);
        Assert.Equal(new[] { "go" }, run["output"]!.ToObject<string[]>());
        Assert.Equal(1, game.StopCalls);
    }

    [Fact]
    public async Task Without_until_inventory_the_result_has_no_inventory_or_met()
    {
        var game = GameWith();
        game.OnStartRun = _ => game.Emit(new RunEnded(RunEnd.Terminated, null, 0));
        using var s = new TestServer(game);

        var run = await Ok(s, "run_script", new JObject { ["name"] = "main" });
        var again = await Ok(s, "wait_run");

        Assert.Null(run["inventory"]);
        Assert.Null(run["met"]);
        Assert.Null(again["inventory"]);
        Assert.Null(again["met"]);
    }

    [Fact]
    public async Task A_syntax_error_still_returns_parse_error_and_starts_nothing()
    {
        var game = GameWith();
        game.SyntaxErrors["main"] = new SyntaxError { Message = "bad", Source = "(", StartIndex = 0 };
        using var s = new TestServer(game);

        var run = await Ok(s, "run_script", new JObject
        {
            ["name"] = "main", ["until_inventory"] = new JObject { ["hay"] = 1 }, ["stop_when_met"] = true,
        });

        Assert.Equal("parse_error", (string)run["status"]!);
        Assert.False(game.RunActive);
        Assert.Equal(0, game.StopCalls);
    }

    [Theory]
    [InlineData("{\"carot\": 5}", "carot")]
    [InlineData("{\"hay\": -1}", "hay")]
    [InlineData("{\"hay\": \"lots\"}", "hay")]
    [InlineData("{}", "until_inventory")]
    [InlineData("[\"hay\"]", "until_inventory")]
    public async Task Invalid_until_inventory_is_an_error_and_starts_no_Run(string untilInventory, string expected)
    {
        var game = GameWith();
        using var s = new TestServer(game);

        var (isError, text) = await s.Call("run_script",
            new JObject { ["name"] = "main", ["until_inventory"] = JToken.Parse(untilInventory) });

        Assert.True(isError);
        Assert.Contains(expected, text);
        Assert.False(game.RunActive);
    }

    [Fact]
    public async Task An_unknown_item_error_lists_the_known_items()
    {
        using var s = new TestServer(GameWith());

        var (isError, text) = await s.Call("run_script",
            new JObject { ["name"] = "main", ["until_inventory"] = new JObject { ["carot"] = 5 } });

        Assert.True(isError);
        Assert.Contains("hay", text);
        Assert.Contains("wood", text);
    }

    [Fact]
    public async Task Stop_when_met_needs_until_inventory_and_must_be_a_boolean()
    {
        var game = GameWith();
        using var s = new TestServer(game);

        var (alone, aloneText) = await s.Call("run_script", new JObject { ["name"] = "main", ["stop_when_met"] = true });
        var (notBool, _) = await s.Call("run_script", new JObject
        {
            ["name"] = "main", ["stop_when_met"] = "yes", ["until_inventory"] = new JObject { ["hay"] = 1 },
        });

        Assert.True(alone);
        Assert.Contains("until_inventory", aloneText);
        Assert.True(notBool);
        Assert.False(game.RunActive);
    }

    [Fact]
    public async Task Wait_run_without_a_Run_still_reports_no_Run()
    {
        using var s = new TestServer(GameWith());

        var (isError, text) = await s.Call("wait_run", Wait(1, 1));

        Assert.True(isError);
        Assert.Contains("No Run", text);
    }

    [Fact]
    public async Task The_tool_schemas_describe_the_new_arguments_and_timeout_zero()
    {
        using var s = new TestServer(GameWith());

        var tools = (await s.Rpc("tools/list"))["result"]!["tools"]!.ToDictionary(t => (string)t["name"]!);

        foreach (var name in new[] { "run_script", "wait_run" })
        {
            var properties = tools[name]["inputSchema"]!["properties"]!;
            Assert.Equal("object", (string)properties["until_inventory"]!["type"]!);
            Assert.Equal("boolean", (string)properties["stop_when_met"]!["type"]!);
            var timeout = (string)properties["timeout_seconds"]!["description"]!;
            Assert.Contains("default 20", timeout);
            Assert.Contains("request timeout", timeout);
            Assert.Contains("wait_run", timeout);
            Assert.Contains("0", timeout);
        }
        Assert.Null(tools["run_script"]["inputSchema"]!["properties"]!["wait"]);
    }
}
