using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace TFWRMCP.Core.Tests;

public class RunSummaryTests
{
    static FakeGame GameWithScript()
    {
        var game = new FakeGame();
        game.Scripts["main"] = "x";
        return game;
    }

    static async Task<JObject> Ok(TestServer s, string tool, JObject? args = null)
    {
        var (isError, text) = await s.Call(tool, args);
        Assert.False(isError, text);
        return JObject.Parse(text);
    }

    static JObject Run(double timeout = 5) => new() { ["name"] = "main", ["timeout_seconds"] = timeout };

    static Dictionary<string, double> Inventory(FakeGame game) => new(game.State.Inventory);

    [Fact]
    public async Task Inventory_delta_lists_only_items_that_changed_over_the_Run()
    {
        var game = GameWithScript();
        game.OnStartRun = _ =>
        {
            game.State.Inventory["hay"] = 20;
            game.State.Inventory["wood"] = 3;
            game.State.Inventory["wood"] = 0;
            game.Emit(new RunEnded(RunEnd.Terminated, Inventory(game), 10));
        };
        using var s = new TestServer(game);

        var run = await Ok(s, "run_script", Run());

        Assert.Equal(new JObject { ["hay"] = 7.5 }, run["inventory_delta"]);
    }

    [Fact]
    public async Task Inventory_delta_of_a_running_Run_follows_the_current_inventory()
    {
        var game = GameWithScript();
        game.OnStartRun = _ => game.State.Inventory["wood"] = 4;
        using var s = new TestServer(game);

        var run = await Ok(s, "run_script", Run(timeout: 0));
        var state = await Ok(s, "get_state", new JObject { ["fields"] = new JArray("run") });

        Assert.Equal("running", (string)run["status"]!);
        Assert.Equal(new JObject { ["wood"] = 4 }, run["inventory_delta"]);
        Assert.Equal(new JObject { ["wood"] = 4 }, state["run"]!["inventory_delta"]);
    }

    [Fact]
    public async Task Failed_calls_count_per_function()
    {
        var game = GameWithScript();
        game.OnStartRun = _ =>
        {
            for (var i = 0; i < 3; i++) game.Emit(new ActionFailed("plant"));
            game.Emit(new ActionFailed("harvest"));
            game.Emit(new RunEnded(RunEnd.Terminated, Inventory(game), 10));
        };
        using var s = new TestServer(game);

        var run = await Ok(s, "run_script", Run());

        Assert.Equal(new JObject { ["plant"] = 3, ["harvest"] = 1 }, run["failed_calls"]);
    }

    [Fact]
    public async Task A_Run_without_failures_or_changes_has_empty_maps()
    {
        var game = GameWithScript();
        game.OnStartRun = _ => game.Emit(new RunEnded(RunEnd.Terminated, Inventory(game), 0));
        using var s = new TestServer(game);

        var run = await Ok(s, "run_script", Run());

        Assert.Empty((JObject)run["inventory_delta"]!);
        Assert.Empty((JObject)run["failed_calls"]!);
    }

    [Fact]
    public async Task Ticks_and_seconds_stop_growing_when_the_Run_ends()
    {
        var game = GameWithScript();
        game.OnStartRun = _ => game.RunTicks = 40;
        using var s = new TestServer(game);

        var running = await Ok(s, "run_script", Run(timeout: 0));
        game.RunTicks = 90;
        await Task.Delay(20);
        var stillRunning = await Ok(s, "get_state", new JObject { ["fields"] = new JArray("run") });
        game.Emit(new RunEnded(RunEnd.Terminated, Inventory(game), 120));
        var ended = await Ok(s, "wait_run");
        game.RunTicks = 500;
        await Task.Delay(50);
        var later = await Ok(s, "wait_run");

        Assert.Equal(40, (long)running["ticks"]!);
        Assert.Equal(90, (long)stillRunning["run"]!["ticks"]!);
        Assert.Equal(120, (long)ended["ticks"]!);
        Assert.Equal(120, (long)later["ticks"]!);
        Assert.True((double)ended["seconds"]! > 0);
        Assert.Equal((double)ended["seconds"]!, (double)later["seconds"]!);
    }

    [Fact]
    public async Task A_stopped_Run_keeps_its_summary()
    {
        var game = GameWithScript();
        game.OnStartRun = _ =>
        {
            game.RunTicks = 7;
            game.State.Inventory["wood"] = 2;
        };
        using var s = new TestServer(game);

        await Ok(s, "run_script", Run(timeout: 0));
        var stopped = await Ok(s, "stop_run");

        Assert.Equal("stopped_by_tool", (string)stopped["status"]!);
        Assert.Equal(7, (long)stopped["ticks"]!);
        Assert.Equal(new JObject { ["wood"] = 2 }, stopped["inventory_delta"]);
    }

    [Fact]
    public async Task A_Run_started_by_the_human_gets_a_summary_too()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);

        game.Emit(new RunStarted("main", Inventory(game)));
        game.Emit(new ActionFailed("move"));
        var state = await Ok(s, "get_state", new JObject { ["fields"] = new JArray("run") });

        Assert.Equal(new JObject { ["move"] = 1 }, state["run"]!["failed_calls"]);
    }

    [Fact]
    public async Task Idle_and_parse_error_have_no_summary_and_a_reloaded_Save_forgets_it()
    {
        var game = GameWithScript();
        game.SyntaxErrors["main"] = new SyntaxError { Message = "bad", Source = "x", StartIndex = 0 };
        using var s = new TestServer(game);

        var idle = await Ok(s, "get_state", new JObject { ["fields"] = new JArray("run") });
        var parseError = await Ok(s, "run_script", Run());

        game.SyntaxErrors.Clear();
        await Ok(s, "run_script", Run(timeout: 0));
        await Ok(s, "load_save", new JObject { ["name"] = "Save0" });
        var reloaded = await Ok(s, "get_state", new JObject { ["fields"] = new JArray("run") });

        foreach (var run in new[] { idle["run"]!, parseError, reloaded["run"]! })
            foreach (var field in new[] { "inventory_delta", "failed_calls", "ticks", "seconds" })
                Assert.Null(run[field]);
    }

    [Fact]
    public async Task A_Save_the_human_switches_to_forgets_the_Run_and_its_summary()
    {
        var game = GameWithScript();
        using var s = new TestServer(game);
        await Ok(s, "run_script", Run(timeout: 0));
        game.Emit(new ActionFailed("plant"));

        game.Emit(new SaveLoading());
        var state = await Ok(s, "get_state", new JObject { ["fields"] = new JArray("run") });

        Assert.Equal("idle", (string)state["run"]!["status"]!);
        foreach (var field in new[] { "inventory_delta", "failed_calls", "ticks", "seconds" })
            Assert.Null(state["run"]![field]);
    }

    [Fact]
    public async Task Get_state_reads_the_inventory_once_so_it_agrees_with_inventory_delta()
    {
        var game = GameWithScript();
        game.OnStartRun = _ => game.State.Inventory["wood"] = 4;
        using var s = new TestServer(game);
        await Ok(s, "run_script", Run(timeout: 0));
        var before = game.Calls;

        var state = await Ok(s, "get_state", new JObject { ["fields"] = new JArray("inventory", "run") });

        Assert.Equal(before + 1, game.Calls);
        Assert.Equal(new JObject { ["wood"] = 4 }, state["run"]!["inventory_delta"]);
    }
}
