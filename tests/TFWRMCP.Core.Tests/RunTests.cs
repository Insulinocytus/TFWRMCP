using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace TFWRMCP.Core.Tests;

public class RunToolTests
{
    static JObject Run(string name, double? timeout = null)
    {
        var args = new JObject { ["name"] = name };
        if (timeout != null) args["timeout_seconds"] = timeout;
        return args;
    }

    static JObject Wait(double timeout) => new() { ["timeout_seconds"] = timeout };

    static FakeGame GameWith(string name = "main", string code = "x")
    {
        var game = new FakeGame();
        game.Scripts[name] = code;
        return game;
    }

    static async Task<JObject> Ok(TestServer s, string tool, JObject? args = null)
    {
        var (isError, text) = await s.Call(tool, args);
        Assert.False(isError, text);
        return JObject.Parse(text);
    }

    static string[] Output(JObject run) => run["output"]!.ToObject<string[]>()!;

    [Fact]
    public async Task A_Script_that_ends_returns_finished_with_its_whole_output()
    {
        var game = GameWith();
        game.OnStartRun = _ =>
        {
            game.Emit(new RunOutput("a"));
            game.Emit(new RunOutput("b"));
            game.Emit(new RunEnded(RunEnd.Terminated));
        };
        using var s = new TestServer(game);

        var run = await Ok(s, "run_script", Run("main"));

        Assert.Equal("finished", (string)run["status"]!);
        Assert.Equal("main", (string)run["script"]!);
        Assert.Equal(new[] { "a", "b" }, Output(run));
        Assert.Null(run["error"]);
    }

    [Fact]
    public async Task Running_an_unknown_Script_is_an_error()
    {
        using var s = new TestServer(new FakeGame());

        var (isError, text) = await s.Call("run_script", Run("nope"));

        Assert.True(isError);
        Assert.Contains("nope", text);
    }

    [Fact]
    public async Task A_syntax_error_returns_parse_error_with_one_based_line_and_column()
    {
        var game = GameWith();
        game.SyntaxErrors["main"] = new SyntaxError { Message = "bad token", Source = "a = 1\nb = (\nc", StartIndex = 10 };
        using var s = new TestServer(game);

        var run = await Ok(s, "run_script", Run("main"));

        Assert.Equal("parse_error", (string)run["status"]!);
        Assert.Equal("bad token", (string)run["error"]!["message"]!);
        Assert.Equal(2, (int)run["error"]!["line"]!);
        Assert.Equal(5, (int)run["error"]!["column"]!);
        Assert.False(game.RunActive);

        var state = await Ok(s, "get_state");
        Assert.Equal("parse_error", (string)state["run"]!["status"]!);
    }

    [Fact]
    public async Task A_syntax_error_on_the_first_line_is_line_1_column_1()
    {
        var game = GameWith();
        game.SyntaxErrors["main"] = new SyntaxError { Message = "m", Source = "(\nx", StartIndex = 0 };
        using var s = new TestServer(game);

        var run = await Ok(s, "run_script", Run("main"));

        Assert.Equal((1, 1), ((int)run["error"]!["line"]!, (int)run["error"]!["column"]!));
    }

    [Fact]
    public async Task A_runtime_error_returns_error_with_message_stack_and_line_and_the_Run_is_over()
    {
        var game = GameWith();
        game.OnStartRun = _ =>
        {
            game.Emit(new RunOutput("before"));
            game.Emit(new RunErrored("boom", "f <- main\nx = 1/0", "lib", "line one\nline two", 10));
        };
        using var s = new TestServer(game);

        var run = await Ok(s, "run_script", Run("main", 5));

        Assert.Equal("error", (string)run["status"]!);
        var error = run["error"]!;
        Assert.Equal("boom", (string)error["message"]!);
        Assert.Equal("f <- main\nx = 1/0", (string)error["stack"]!);
        Assert.Equal("lib", (string)error["file"]!);
        Assert.Equal((2, 2), ((int)error["line"]!, (int)error["column"]!));
        Assert.Equal(new[] { "before" }, Output(run));
        Assert.False(game.RunActive);
        Assert.Equal(1, game.StopCalls);
    }

    [Fact]
    public async Task A_runtime_error_without_a_known_source_still_reports_message_and_stack()
    {
        var game = GameWith();
        game.OnStartRun = _ =>
        {
            game.Emit(new RunErrored("boom", "main", null, null, 0));
        };
        using var s = new TestServer(game);

        var run = await Ok(s, "run_script", Run("main", 5));

        Assert.Equal("error", (string)run["status"]!);
        Assert.Equal("boom", (string)run["error"]!["message"]!);
        Assert.Null(run["error"]!["line"]);
    }

    [Fact]
    public async Task A_Script_still_running_at_the_timeout_returns_running_and_keeps_going_in_the_game()
    {
        var game = GameWith();
        game.OnStartRun = _ => game.Emit(new RunOutput("tick"));
        using var s = new TestServer(game);

        var run = await Ok(s, "run_script", Run("main", 0.1));

        Assert.Equal("running", (string)run["status"]!);
        Assert.Equal(new[] { "tick" }, Output(run));
        Assert.True(game.RunActive);
        Assert.Equal(0, game.StopCalls);
    }

    [Fact]
    public async Task Wait_run_waits_again_and_returns_the_final_result()
    {
        var game = GameWith();
        using var s = new TestServer(game);
        await Ok(s, "run_script", Run("main", 0.05));

        var waiting = s.Call("wait_run", Wait(10));
        game.Emit(new RunOutput("late"));
        game.Emit(new RunEnded(RunEnd.Terminated));
        var (isError, text) = await waiting;

        Assert.False(isError);
        var run = JObject.Parse(text);
        Assert.Equal("finished", (string)run["status"]!);
        Assert.Equal(new[] { "late" }, Output(run));
    }

    [Fact]
    public async Task Wait_run_that_times_out_still_reports_running()
    {
        var game = GameWith();
        using var s = new TestServer(game);
        await Ok(s, "run_script", Run("main", 0.05));

        var run = await Ok(s, "wait_run", Wait(0.1));

        Assert.Equal("running", (string)run["status"]!);
    }

    [Fact]
    public async Task Wait_run_after_the_Run_ended_returns_that_Runs_result()
    {
        var game = GameWith();
        game.OnStartRun = _ =>
        {
            game.Emit(new RunOutput("x"));
            game.Emit(new RunEnded(RunEnd.Terminated));
        };
        using var s = new TestServer(game);
        await Ok(s, "run_script", Run("main"));

        var run = await Ok(s, "wait_run");

        Assert.Equal("finished", (string)run["status"]!);
        Assert.Equal(new[] { "x" }, Output(run));
    }

    [Fact]
    public async Task Wait_run_before_any_Run_is_an_error()
    {
        using var s = new TestServer(new FakeGame());

        var (isError, text) = await s.Call("wait_run");

        Assert.True(isError);
        Assert.Contains("No Run", text);
    }

    [Fact]
    public async Task Stop_run_stops_the_Run_and_reports_stopped_by_tool()
    {
        var game = GameWith();
        using var s = new TestServer(game);
        await Ok(s, "run_script", Run("main", 0.05));

        var run = await Ok(s, "stop_run");

        Assert.Equal("stopped_by_tool", (string)run["status"]!);
        Assert.Equal(1, game.StopCalls);
        Assert.False(game.RunActive);
    }

    [Fact]
    public async Task A_Run_stopped_by_the_human_is_stopped_by_user()
    {
        var game = GameWith();
        using var s = new TestServer(game);
        await Ok(s, "run_script", Run("main", 0.05));

        game.Emit(new RunEnded(RunEnd.Stopped));
        var run = await Ok(s, "wait_run");

        Assert.Equal("stopped_by_user", (string)run["status"]!);
        Assert.Equal(0, game.StopCalls);
    }

    [Fact]
    public async Task Stop_run_without_a_Run_in_progress_just_reports_the_last_status()
    {
        var game = GameWith();
        using var s = new TestServer(game);

        var idle = await Ok(s, "stop_run");
        Assert.Equal("idle", (string)idle["status"]!);

        game.OnStartRun = _ => game.Emit(new RunEnded(RunEnd.Terminated));
        await Ok(s, "run_script", Run("main"));
        var finished = await Ok(s, "stop_run");

        Assert.Equal("finished", (string)finished["status"]!);
        Assert.Equal(0, game.StopCalls);
    }

    [Fact]
    public async Task Running_while_a_Run_is_in_progress_is_an_error_and_leaves_the_Run_alone()
    {
        var game = GameWith();
        game.Scripts["other"] = "y";
        using var s = new TestServer(game);
        await Ok(s, "run_script", Run("main", 0.05));

        var (isError, text) = await s.Call("run_script", Run("other", 0.05));

        Assert.True(isError);
        Assert.Contains("already in progress", text);
        Assert.Equal(0, game.StopCalls);
        Assert.True(game.RunActive);
        var state = await Ok(s, "get_state");
        Assert.Equal("running", (string)state["run"]!["status"]!);
        Assert.Equal("main", (string)state["run"]!["script"]!);
    }

    [Fact]
    public async Task A_new_Run_starts_with_a_clean_output_buffer()
    {
        var game = GameWith();
        game.OnStartRun = _ =>
        {
            game.Emit(new RunOutput("old"));
            game.Emit(new RunEnded(RunEnd.Terminated));
        };
        using var s = new TestServer(game);
        await Ok(s, "run_script", Run("main"));
        game.OnStartRun = _ =>
        {
            game.Emit(new RunOutput("new"));
            game.Emit(new RunEnded(RunEnd.Terminated));
        };

        var run = await Ok(s, "run_script", Run("main"));

        Assert.Equal(new[] { "new" }, Output(run));
    }

    [Fact]
    public async Task Up_to_1000_output_entries_are_kept_newest_last_and_the_dropped_count_is_reported()
    {
        var game = GameWith();
        game.OnStartRun = _ =>
        {
            for (var i = 1; i <= 1500; i++) game.Emit(new RunOutput("line " + i));
            game.Emit(new RunEnded(RunEnd.Terminated));
        };
        using var s = new TestServer(game);

        var run = await Ok(s, "run_script", Run("main"));

        var output = Output(run);
        Assert.Equal(1000, output.Length);
        Assert.Equal("line 501", output[0]);
        Assert.Equal("line 1500", output[^1]);
        Assert.Equal(500, (int)run["output_dropped"]!);
    }

    [Fact]
    public async Task Output_of_100_entries_is_not_cut_to_the_games_99()
    {
        var game = GameWith();
        game.OnStartRun = _ =>
        {
            for (var i = 0; i < 100; i++) game.Emit(new RunOutput(i.ToString()));
            game.Emit(new RunEnded(RunEnd.Terminated));
        };
        using var s = new TestServer(game);

        var run = await Ok(s, "run_script", Run("main"));

        Assert.Equal(100, Output(run).Length);
        Assert.Null(run["output_dropped"]);
    }

    [Fact]
    public async Task Get_state_reports_idle_then_the_latest_Runs_status_without_its_output()
    {
        var game = GameWith();
        using var s = new TestServer(game);

        var idle = await Ok(s, "get_state");
        Assert.Equal("idle", (string)idle["run"]!["status"]!);

        game.OnStartRun = _ =>
        {
            game.Emit(new RunOutput("x"));
            game.Emit(new RunEnded(RunEnd.Terminated));
        };
        await Ok(s, "run_script", Run("main"));
        var after = await Ok(s, "get_state");

        Assert.Equal("finished", (string)after["run"]!["status"]!);
        Assert.Equal("main", (string)after["run"]!["script"]!);
        Assert.Null(after["run"]!["output"]);
    }

    [Fact]
    public async Task A_Run_started_by_the_human_is_tracked_too()
    {
        var game = GameWith();
        using var s = new TestServer(game);

        game.Emit(new RunStarted("main", new Dictionary<string, double>()));
        var state = await Ok(s, "get_state");
        var (isError, text) = await s.Call("run_script", Run("main"));

        Assert.Equal("running", (string)state["run"]!["status"]!);
        Assert.True(isError);
        Assert.Contains("already in progress", text);
    }

    [Fact]
    public async Task Invalid_timeouts_are_rejected()
    {
        var game = GameWith();
        using var s = new TestServer(game);

        var (negative, _) = await s.Call("run_script", Run("main", -1));
        var (text, _) = await s.Call("wait_run", new JObject { ["timeout_seconds"] = "soon" });

        Assert.True(negative);
        Assert.True(text);
        Assert.False(game.RunActive);
    }

    [Fact]
    public async Task Switching_Save_stops_a_Run_in_progress_by_tool_and_starts_from_a_clean_status()
    {
        var game = GameWith();
        game.Saves.Add("Other");
        using var s = new TestServer(game);
        await Ok(s, "run_script", Run("main", 0.05));

        await Ok(s, "load_save", new JObject { ["name"] = "Other" });
        var state = await Ok(s, "get_state");

        Assert.Equal(1, game.StopCalls);
        Assert.Equal("idle", (string)state["run"]!["status"]!);
    }
}

public class LeaderboardRunTests
{
    static async Task<JObject> RunLeaderboard(FakeGame game, params RunEvent[] events)
    {
        game.Scripts["main"] = "x";
        game.OnStartRun = _ =>
        {
            foreach (var e in events) game.Emit(e);
        };
        using var s = new TestServer(game);
        var (isError, text) = await s.Call("run_script", new JObject { ["name"] = "main" });
        Assert.False(isError, text);
        return JObject.Parse(text);
    }

    [Fact]
    public async Task A_finished_Leaderboard_Run_attaches_time_goal_and_rank()
    {
        var run = await RunLeaderboard(new FakeGame(),
            new LeaderboardFinished(true, 12.5, 7),
            new RunEnded(RunEnd.Terminated));

        Assert.Equal("finished", (string)run["status"]!);
        var leaderboard = run["leaderboard"]!;
        Assert.True((bool)leaderboard["reached_goal"]!);
        Assert.Equal(12.5, (double)leaderboard["average_seconds"]!);
        Assert.Equal(7, (int)leaderboard["rank"]!);
    }

    [Fact]
    public async Task Without_a_rank_the_result_still_arrives_with_a_null_rank()
    {
        var run = await RunLeaderboard(new FakeGame(),
            new LeaderboardFinished(true, 3, null),
            new RunEnded(RunEnd.Terminated));

        Assert.Equal("finished", (string)run["status"]!);
        Assert.Equal(JTokenType.Null, run["leaderboard"]!["rank"]!.Type);
        Assert.Equal(3, (double)run["leaderboard"]!["average_seconds"]!);
    }

    [Fact]
    public async Task A_Leaderboard_Run_that_missed_its_goal_is_finished_with_reached_goal_false()
    {
        var run = await RunLeaderboard(new FakeGame(),
            new LeaderboardFinished(false, 99, null),
            new RunEnded(RunEnd.Terminated));

        Assert.Equal("finished", (string)run["status"]!);
        Assert.False((bool)run["leaderboard"]!["reached_goal"]!);
    }

    [Fact]
    public async Task A_cancelled_Leaderboard_Run_reports_who_stopped_it()
    {
        var run = await RunLeaderboard(new FakeGame(),
            new LeaderboardFinished(false, 1, null),
            new RunEnded(RunEnd.Stopped));

        Assert.Equal("stopped_by_user", (string)run["status"]!);
        Assert.False((bool)run["leaderboard"]!["reached_goal"]!);
    }

    [Fact]
    public async Task A_Run_without_a_Leaderboard_Run_has_no_leaderboard_result()
    {
        var run = await RunLeaderboard(new FakeGame(), new RunEnded(RunEnd.Terminated));

        Assert.Null(run["leaderboard"]);
    }

    [Fact]
    public async Task The_Run_stays_running_until_the_Leaderboard_Run_has_ended()
    {
        var game = new FakeGame();
        game.Scripts["main"] = "x";
        using var s = new TestServer(game);
        var (_, started) = await s.Call("run_script", new JObject { ["name"] = "main", ["timeout_seconds"] = 0.05 });

        var status = (string)JObject.Parse(started)["status"]!;
        game.Emit(new LeaderboardFinished(true, 5, 1));
        var (_, mid) = await s.Call("wait_run", new JObject { ["timeout_seconds"] = 0.05 });

        Assert.Equal("running", status);
        Assert.Equal("running", (string)JObject.Parse(mid)["status"]!);
        Assert.Equal(1, (int)JObject.Parse(mid)["leaderboard"]!["rank"]!);
    }
}
