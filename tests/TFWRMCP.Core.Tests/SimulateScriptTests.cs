using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace TFWRMCP.Core.Tests;

public class SimulateScriptTests
{
    static FakeGame Game(int simulationLevel = 1)
    {
        var game = new FakeGame { GameSeconds = 1000 };
        game.Scripts["main"] = "x";
        game.State.Unlocks.Add(new UnlockState { Name = "simulation", Level = simulationLevel, MaxLevel = 1 });
        game.TemporaryInventory = new Dictionary<string, double> { ["hay"] = 0, ["wood"] = 0 };
        return game;
    }

    static async Task<JObject> Ok(TestServer s, string tool, JObject? args = null)
    {
        var (isError, text) = await s.Call(tool, args);
        Assert.False(isError, text);
        return JObject.Parse(text);
    }

    static JObject Sim(double seconds = 60, double timeout = 5) =>
        new() { ["name"] = "main", ["game_seconds"] = seconds, ["timeout_seconds"] = timeout };

    static JObject RunField(JObject state) => (JObject)state["run"]!;

    static JObject Fields(params string[] fields) => new() { ["fields"] = new JArray(fields) };

    void EndAt(FakeGame game, double seconds) =>
        game.Emit(new RunEnded(RunEnd.Terminated, new Dictionary<string, double>(game.TemporaryInventory), 5, seconds));

    [Fact]
    public async Task Without_the_simulation_Unlock_the_call_is_refused_and_nothing_starts()
    {
        var game = Game(simulationLevel: 0);
        using var s = new TestServer(game);

        var (isError, text) = await s.Call("simulate_script", Sim());

        Assert.True(isError);
        Assert.Contains("simulation", text);
        Assert.Empty(game.SimulatedRuns);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData("60")]
    public async Task Game_seconds_must_be_a_positive_number_and_nothing_starts_otherwise(object? value)
    {
        var game = Game();
        using var s = new TestServer(game);
        var args = new JObject { ["name"] = "main" };
        if (value != null) args["game_seconds"] = JToken.FromObject(value);

        var (isError, text) = await s.Call("simulate_script", args);

        Assert.True(isError);
        Assert.Contains("game_seconds", text);
        Assert.Empty(game.SimulatedRuns);
    }

    [Fact]
    public async Task A_Run_in_progress_refuses_the_call_and_is_left_untouched()
    {
        var game = Game();
        using var s = new TestServer(game);
        await Ok(s, "run_script", new JObject { ["name"] = "main", ["timeout_seconds"] = 0 });

        var (isError, text) = await s.Call("simulate_script", Sim());

        Assert.True(isError);
        Assert.Contains("stop_current", text);
        Assert.Empty(game.SimulatedRuns);
        Assert.True(game.RunActive);
        Assert.Equal(0, game.StopCalls);
    }

    [Fact]
    public async Task Stop_current_stops_the_Run_in_progress_and_then_simulates()
    {
        var game = Game();
        using var s = new TestServer(game);
        await Ok(s, "run_script", new JObject { ["name"] = "main", ["timeout_seconds"] = 0 });
        var args = Sim(timeout: 0);
        args["stop_current"] = true;

        var run = await Ok(s, "simulate_script", args);

        Assert.Equal(1, game.StopCalls);
        Assert.Equal(new[] { ("main", 60.0) }, game.SimulatedRuns.Select(s => (s.Name, s.Options.GameSeconds)));
        Assert.Equal("running", (string)run["status"]!);
        Assert.True((bool)run["simulated"]!);
    }

    [Fact]
    public async Task A_missing_Script_is_an_error_and_a_syntax_error_is_a_parse_error()
    {
        var game = Game();
        game.Scripts["bad"] = "(";
        game.SyntaxErrors["bad"] = new SyntaxError { Message = "bad", Source = "(", StartIndex = 0 };
        using var s = new TestServer(game);

        var (isError, text) = await s.Call("simulate_script", new JObject { ["name"] = "nope", ["game_seconds"] = 60 });
        var run = await Ok(s, "simulate_script", new JObject { ["name"] = "bad", ["game_seconds"] = 60 });

        Assert.True(isError);
        Assert.Contains("No Script named 'nope'", text);
        Assert.Equal("parse_error", (string)run["status"]!);
        Assert.Equal(1, (int)run["error"]!["line"]!);
        Assert.Empty(game.SimulatedRuns);
    }

    [Fact]
    public async Task Simulated_is_true_in_simulate_script_wait_run_and_get_state_and_absent_for_normal_Runs()
    {
        var game = Game();
        using var s = new TestServer(game);

        var started = await Ok(s, "simulate_script", Sim(timeout: 0));
        var state = await Ok(s, "get_state", Fields("run"));
        var waited = await Ok(s, "wait_run", new JObject { ["timeout_seconds"] = 0 });
        EndAt(game, 60);
        var ended = await Ok(s, "wait_run");
        var normal = await Ok(s, "run_script", new JObject { ["name"] = "main", ["timeout_seconds"] = 0 });

        Assert.True((bool)started["simulated"]!);
        Assert.True((bool)RunField(state)["simulated"]!);
        Assert.True((bool)waited["simulated"]!);
        Assert.True((bool)ended["simulated"]!);
        Assert.Null(normal["simulated"]);
    }

    [Fact]
    public async Task The_summary_comes_from_the_temporary_farm_not_the_real_one()
    {
        var game = Game();
        game.OnStartSimulatedRun = _ =>
        {
            game.State.Inventory["hay"] = 99999;
            game.GameSeconds = 5000;
            game.TemporaryInventory["hay"] = 300;
            game.TemporaryInventory["wood"] = 60;
            EndAt(game, 60);
        };
        using var s = new TestServer(game);

        var run = await Ok(s, "simulate_script", Sim());

        Assert.Equal("finished", (string)run["status"]!);
        Assert.Equal(60, (double)run["game_seconds"]!);
        Assert.Equal(new JObject { ["hay"] = 300, ["wood"] = 60 }, run["inventory_delta"]);
        Assert.Equal(new JObject { ["hay"] = 5, ["wood"] = 1 }, run["rates"]);
        Assert.Equal(5, (long)run["ticks"]!);
    }

    [Fact]
    public async Task A_running_simulated_Run_reports_the_temporary_farm_so_far()
    {
        var game = Game();
        using var s = new TestServer(game);

        await Ok(s, "simulate_script", Sim(timeout: 0));
        game.TemporaryInventory["hay"] = 30;
        game.TemporarySeconds = 10;
        var state = await Ok(s, "get_state", Fields("inventory", "run"));

        Assert.Equal(10, (double)RunField(state)["game_seconds"]!);
        Assert.Equal(new JObject { ["hay"] = 30 }, RunField(state)["inventory_delta"]);
        Assert.Equal(new JObject { ["hay"] = 3 }, RunField(state)["rates"]);
        Assert.Equal(12.5, (double)state["inventory"]!["hay"]!);
    }

    [Fact]
    public async Task When_the_timeout_passes_first_the_Run_is_running_and_wait_run_gets_the_result_later()
    {
        var game = Game();
        using var s = new TestServer(game);

        var early = await Ok(s, "simulate_script", Sim(timeout: 0.2));
        game.TemporaryInventory["hay"] = 120;
        EndAt(game, 60);
        var late = await Ok(s, "wait_run", new JObject { ["timeout_seconds"] = 5 });

        Assert.Equal("running", (string)early["status"]!);
        Assert.Equal("finished", (string)late["status"]!);
        Assert.Equal(new JObject { ["hay"] = 120 }, late["inventory_delta"]);
    }

    [Fact]
    public async Task Stop_run_stops_a_simulated_Run_and_the_human_stopping_it_is_stopped_by_user()
    {
        var game = Game();
        using var s = new TestServer(game);

        await Ok(s, "simulate_script", Sim(timeout: 0));
        game.TemporarySeconds = 7;
        var stopped = await Ok(s, "stop_run");
        await Ok(s, "simulate_script", Sim(timeout: 0));
        game.Emit(new RunEnded(RunEnd.Stopped, new Dictionary<string, double>(game.TemporaryInventory), 1, 3));
        var byUser = await Ok(s, "wait_run");

        Assert.Equal("stopped_by_tool", (string)stopped["status"]!);
        Assert.Equal(7, (double)stopped["game_seconds"]!);
        Assert.True((bool)stopped["simulated"]!);
        Assert.Equal("stopped_by_user", (string)byUser["status"]!);
    }

    [Fact]
    public async Task A_runtime_error_ends_the_simulated_Run_as_error_with_its_position()
    {
        var game = Game();
        game.OnStartSimulatedRun = _ => game.Emit(new RunErrored("boom", "stack", "main", "a\nbb\n", 3));
        using var s = new TestServer(game);

        var run = await Ok(s, "simulate_script", Sim());

        Assert.Equal("error", (string)run["status"]!);
        Assert.Equal("boom", (string)run["error"]!["message"]!);
        Assert.Equal(2, (int)run["error"]!["line"]!);
        Assert.Equal(1, game.StopCalls);
    }

    [Fact]
    public async Task Wait_run_refuses_an_inventory_goal_for_a_simulated_Run_only()
    {
        var game = Game();
        using var s = new TestServer(game);
        await Ok(s, "simulate_script", Sim(timeout: 0));

        var goal = new JObject { ["until_inventory"] = new JObject { ["hay"] = 1 }, ["timeout_seconds"] = 0 };
        var (isError, text) = await s.Call("wait_run", goal);
        EndAt(game, 60);
        var (stillError, _) = await s.Call("wait_run", goal);
        await Ok(s, "run_script", new JObject { ["name"] = "main", ["timeout_seconds"] = 0 });
        var (normalError, normalText) = await s.Call("wait_run", goal);

        Assert.True(isError);
        Assert.Contains("simulated", text);
        Assert.True(stillError);
        Assert.False(normalError, normalText);
    }

    [Fact]
    public async Task Output_of_the_earlier_Run_is_gone_when_the_simulated_Run_starts()
    {
        var game = Game();
        game.OnStartRun = _ =>
        {
            game.Emit(new RunOutput("old"));
            game.Emit(new RunEnded(RunEnd.Terminated, new Dictionary<string, double>(game.State.Inventory), 1));
        };
        game.OnStartSimulatedRun = _ =>
        {
            game.Emit(new RunOutput("new"));
            EndAt(game, 60);
        };
        using var s = new TestServer(game);

        await Ok(s, "run_script", new JObject { ["name"] = "main", ["timeout_seconds"] = 5 });
        var run = await Ok(s, "simulate_script", Sim());

        Assert.Equal(new[] { "new" }, run["output"]!.Select(o => (string)o!));
    }

    [Fact]
    public async Task Without_parameters_the_game_gets_the_current_Save_no_globals_a_random_seed_and_no_warmup()
    {
        var game = Game();
        using var s = new TestServer(game);

        await Ok(s, "simulate_script", Sim(timeout: 0));

        var options = game.SimulatedRuns.Single().Options;
        Assert.Equal(60, options.GameSeconds);
        Assert.Equal(0, options.WarmupSeconds);
        Assert.Equal(-1, options.Seed);
        Assert.Empty(options.Unlocks);
        Assert.Empty(options.Items);
        Assert.Empty(options.Globals);
    }

    [Fact]
    public async Task Overrides_globals_seed_and_warmup_reach_the_game_as_given()
    {
        var game = Game();
        using var s = new TestServer(game);
        var args = Sim(timeout: 0);
        args["unlocks"] = new JObject { ["expand"] = 3, ["grass"] = 0 };
        args["items"] = new JObject { ["hay"] = 0, ["wood"] = 2e7 };
        args["globals"] = new JObject { ["K"] = 4, ["ratio"] = 0.5, ["mode"] = "fast", ["on"] = true };
        args["seed"] = 7;
        args["warmup_seconds"] = 10.5;

        await Ok(s, "simulate_script", args);

        var options = game.SimulatedRuns.Single().Options;
        Assert.Equal(new Dictionary<string, int> { ["expand"] = 3, ["grass"] = 0 }, options.Unlocks);
        Assert.Equal(new Dictionary<string, double> { ["hay"] = 0, ["wood"] = 2e7 }, options.Items);
        Assert.Equal(new Dictionary<string, object> { ["K"] = 4.0, ["ratio"] = 0.5, ["mode"] = "fast", ["on"] = true }, options.Globals);
        Assert.Equal(7, options.Seed);
        Assert.Equal(10.5, options.WarmupSeconds);
        Assert.Equal(60, options.GameSeconds);
    }

    [Theory]
    [InlineData("unlocks", """{"no_such_unlock": 1}""", "no_such_unlock")]
    [InlineData("unlocks", """{"expand": 10}""", "expand")]
    [InlineData("unlocks", """{"expand": -1}""", "expand")]
    [InlineData("unlocks", """{"expand": 1.5}""", "expand")]
    [InlineData("unlocks", """{"expand": "2"}""", "expand")]
    [InlineData("unlocks", """[1]""", "unlocks")]
    [InlineData("items", """{"no_such_item": 1}""", "no_such_item")]
    [InlineData("items", """{"hay": -1}""", "hay")]
    [InlineData("items", """{"hay": "1"}""", "hay")]
    [InlineData("items", """[1]""", "items")]
    [InlineData("globals", """{"K": {"a": 1}}""", "K")]
    [InlineData("globals", """{"K": [1, 2]}""", "K")]
    [InlineData("globals", """{"K": null}""", "K")]
    [InlineData("globals", """[1]""", "globals")]
    [InlineData("seed", "1.5", "seed")]
    [InlineData("seed", "\"7\"", "seed")]
    [InlineData("warmup_seconds", "-1", "warmup_seconds")]
    [InlineData("warmup_seconds", "\"5\"", "warmup_seconds")]
    public async Task An_invalid_parameter_is_refused_and_nothing_starts_or_stops(string parameter, string json, string mention)
    {
        var game = Game();
        using var s = new TestServer(game);
        await Ok(s, "run_script", new JObject { ["name"] = "main", ["timeout_seconds"] = 0 });
        var args = Sim(timeout: 0);
        args["stop_current"] = true;
        args[parameter] = JToken.Parse(json);

        var (isError, text) = await s.Call("simulate_script", args);

        Assert.True(isError);
        Assert.Contains(mention, text);
        Assert.Empty(game.SimulatedRuns);
        Assert.Equal(0, game.StopCalls);
        Assert.True(game.RunActive);
    }

    [Fact]
    public async Task The_summary_starts_over_when_the_warmup_ends()
    {
        var game = Game();
        game.OnStartSimulatedRun = _ =>
        {
            game.Emit(new ActionCalled("harvest", true));
            game.Emit(new ActionCalled("harvest", true));
            game.Emit(new ActionCalled("plant", false));
            game.Emit(new WarmupEnded(new Dictionary<string, double> { ["hay"] = 50, ["wood"] = 7 }, 10.25));
            game.Emit(new ActionCalled("plant", true));
            game.Emit(new ActionCalled("plant", false));
            game.TemporaryInventory["hay"] = 80;
            game.TemporaryInventory["wood"] = 7;
            EndAt(game, 70.25);
        };
        using var s = new TestServer(game);

        var run = await Ok(s, "simulate_script", Sim(timeout: 5));

        Assert.Equal("finished", (string)run["status"]!);
        Assert.Equal(60, (double)run["game_seconds"]!);
        Assert.Equal(new JObject { ["hay"] = 30 }, run["inventory_delta"]);
        Assert.Equal(new JObject { ["hay"] = 0.5 }, run["rates"]);
        Assert.Equal(new JObject { ["plant"] = 1 }, run["failed_calls"]);
    }

    [Fact]
    public async Task A_running_simulated_Run_counts_from_the_end_of_the_warmup()
    {
        var game = Game();
        using var s = new TestServer(game);

        await Ok(s, "simulate_script", Sim(timeout: 0));
        game.Emit(new WarmupEnded(new Dictionary<string, double> { ["hay"] = 50 }, 10));
        game.TemporaryInventory["hay"] = 70;
        game.TemporarySeconds = 25;
        var state = await Ok(s, "get_state", Fields("run"));

        Assert.Equal(15, (double)RunField(state)["game_seconds"]!);
        Assert.Equal(new JObject { ["hay"] = 20 }, RunField(state)["inventory_delta"]);
    }

    [Fact]
    public async Task A_simulated_Run_without_failures_has_empty_failed_calls()
    {
        var game = Game();
        game.OnStartSimulatedRun = _ => EndAt(game, 60);
        using var s = new TestServer(game);

        var run = await Ok(s, "simulate_script", Sim());

        Assert.Empty((JObject)run["failed_calls"]!);
    }
}
