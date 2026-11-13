using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace TFWRMCP.Core.Tests;

public class InputLockTests
{
    static Tool Probe(FakeGame game) => new("probe", "Reports the lock.", new JObject { ["type"] = "object" },
        (_, _) => Task.FromResult<object?>(new { locked_by = game.LockedBy }));

    static Tool Failing() => new("failing", "Always fails.", new JObject { ["type"] = "object" },
        (_, _) => throw new ToolException("nope"));

    static Tool Crashing() => new("crashing", "Always crashes.", new JObject { ["type"] = "object" },
        (_, _) => throw new InvalidOperationException("kaput"));

    static async Task<JObject> Ok(TestServer s, string tool, JObject? args = null)
    {
        var (isError, text) = await s.Call(tool, args);
        Assert.False(isError, text);
        return JObject.Parse(text);
    }

    static FakeGame GameWithNeverEndingScript()
    {
        var game = new FakeGame();
        game.Scripts["main"] = "while True: pass";
        return game;
    }

    [Fact]
    public async Task The_game_is_locked_for_the_Tool_that_is_running_and_free_again_when_it_returns()
    {
        var game = new FakeGame();
        using var s = new TestServer(game, Probe(game));

        var result = await Ok(s, "probe");

        Assert.Equal("probe", (string)result["locked_by"]!);
        Assert.Null(game.LockedBy);
        Assert.Equal(new[] { "lock:probe", "unlock" }, game.LockEvents);
    }

    [Theory]
    [InlineData("failing")]
    [InlineData("crashing")]
    public async Task A_failing_Tool_releases_the_lock(string tool)
    {
        var game = new FakeGame();
        using var s = new TestServer(game, Failing(), Crashing());

        var (isError, _) = await s.Call(tool);

        Assert.True(isError);
        Assert.Null(game.LockedBy);
        Assert.Equal(new[] { "lock:" + tool, "unlock" }, game.LockEvents);
    }

    [Fact]
    public async Task Every_call_takes_and_releases_the_lock_on_its_own()
    {
        var game = new FakeGame();
        using var s = new TestServer(game);

        await Ok(s, "get_state");
        await Ok(s, "list_saves");

        Assert.Equal(new[] { "lock:get_state", "unlock", "lock:list_saves", "unlock" }, game.LockEvents);
    }

    [Fact]
    public async Task Calls_that_never_reach_the_game_do_not_lock_it()
    {
        var game = new FakeGame();
        game.Unavailable[Capabilities.State] = "field MainSim.sim not found";
        using var s = new TestServer(game);

        var (unavailable, _) = await s.Call("get_state");
        var unknown = await s.Rpc("tools/call", new JObject { ["name"] = "nope" });
        await s.Rpc("tools/list");

        Assert.True(unavailable);
        Assert.NotNull(unknown["error"]);
        Assert.Empty(game.LockEvents);
    }

    [Fact]
    public async Task The_lock_is_held_for_a_whole_wait_but_released_when_the_wait_times_out_with_the_Run_still_going()
    {
        var game = GameWithNeverEndingScript();
        string? lockedDuringRun = null;
        game.OnStartRun = _ => lockedDuringRun = game.LockedBy;
        using var s = new TestServer(game);

        var run = await Ok(s, "run_script", new JObject { ["name"] = "main", ["timeout_seconds"] = 0.1 });

        Assert.Equal("running", (string)run["status"]!);
        Assert.Equal("run_script", lockedDuringRun);
        Assert.Null(game.LockedBy);
        Assert.True(game.RunActive);
    }

    [Fact]
    public async Task F10_during_a_Tool_returns_Interrupted_by_the_user_and_stops_the_Run_as_stopped_by_user()
    {
        var game = GameWithNeverEndingScript();
        using var s = new TestServer(game);

        var call = s.Call("run_script", new JObject { ["name"] = "main", ["timeout_seconds"] = 60 });
        await game.WaitForRun();
        game.PressF10();
        var (isError, text) = await call;

        Assert.True(isError);
        Assert.Contains("interrupted by the user", text, StringComparison.OrdinalIgnoreCase);
        Assert.False(game.RunActive);
        Assert.Null(game.LockedBy);
        var state = await Ok(s, "get_state");
        Assert.Equal("stopped_by_user", (string)state["run"]!["status"]!);
    }

    [Fact]
    public async Task F10_during_any_Tool_stops_a_Run_that_an_earlier_call_left_running()
    {
        var game = GameWithNeverEndingScript();
        using var s = new TestServer(game);
        await Ok(s, "run_script", new JObject { ["name"] = "main", ["timeout_seconds"] = 0.05 });
        Assert.True(game.RunActive);
        game.GetStateBlocker = new TaskCompletionSource<bool>();

        var call = s.Call("get_state");
        await game.WaitForLock("get_state");
        game.PressF10();
        var (isError, text) = await call;

        Assert.True(isError);
        Assert.Contains("interrupted by the user", text, StringComparison.OrdinalIgnoreCase);
        Assert.False(game.RunActive);
        game.GetStateBlocker.SetResult(true);
        var state = await Ok(s, "get_state");
        Assert.Equal("stopped_by_user", (string)state["run"]!["status"]!);
    }

    [Fact]
    public async Task The_next_Tool_call_works_and_locks_again_after_an_interruption()
    {
        var game = new FakeGame { GetStateBlocker = new TaskCompletionSource<bool>() };
        using var s = new TestServer(game);
        var interrupted = s.Call("get_state");
        await game.WaitForLock("get_state");
        game.PressF10();
        await interrupted;
        game.GetStateBlocker.SetResult(true);
        game.LockEvents.Clear();

        var state = await Ok(s, "get_state");

        Assert.Equal("Save0", (string)state["save"]!);
        Assert.Equal(new[] { "lock:get_state", "unlock" }, game.LockEvents);
    }

    [Fact]
    public async Task F10_while_the_Tool_waits_for_the_game_stops_it_before_it_starts_a_Run()
    {
        var game = GameWithNeverEndingScript();
        game.Scene = GameScene.Loading;
        game.LoadingPolls = int.MaxValue;
        using var s = new TestServer(game);

        var call = s.Call("run_script", new JObject { ["name"] = "main", ["timeout_seconds"] = 60 });
        await game.WaitForLock("run_script");
        game.PressF10();
        var (isError, _) = await call;
        game.Scene = GameScene.Playing;
        await Task.Delay(300);

        Assert.True(isError);
        Assert.False(game.RunActive);
    }

    [Fact]
    public async Task F10_during_a_reload_wait_stops_the_Tool_polling_the_game()
    {
        var game = new FakeGame { ReloadPolls = int.MaxValue };
        game.Saves.Add("other");
        using var s = new TestServer(game);

        var call = s.Call("load_save", new JObject { ["name"] = "other" });
        await game.WaitForLock("load_save");
        game.PressF10();
        await call;
        var polls = game.ScenePolls;
        await Task.Delay(300);

        Assert.Equal(polls, game.ScenePolls);
    }

    [Fact]
    public async Task The_next_Tool_waits_until_the_abandoned_one_has_finished()
    {
        var game = new FakeGame();
        var release = new TaskCompletionSource<bool>();
        var slow = new Tool("slow", "Ignores everything.", new JObject { ["type"] = "object" },
            async (_, _) =>
            {
                await release.Task;
                return "done";
            });
        using var s = new TestServer(game, slow);

        var interrupted = s.Call("slow");
        await game.WaitForLock("slow");
        game.PressF10();
        await interrupted;
        var next = s.Call("get_state");
        var early = await Task.WhenAny(next, Task.Delay(300)) == next;
        var callsWhileAbandonedWorkRuns = game.Calls;
        release.SetResult(true);
        var (isError, _) = await next;

        Assert.False(early);
        Assert.Equal(0, callsWhileAbandonedWorkRuns);
        Assert.False(isError);
    }
}
