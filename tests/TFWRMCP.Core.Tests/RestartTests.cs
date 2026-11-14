using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace TFWRMCP.Core.Tests;

public class RestartTests
{
    [Fact]
    public async Task After_a_restart_on_another_port_only_the_new_port_answers()
    {
        using var s = new TestServer(new FakeGame());
        var old = s.Url;

        s.Restart();

        Assert.NotNull((await s.Rpc("tools/list"))["result"]);
        await Assert.ThrowsAsync<HttpRequestException>(() => s.Http.PostAsync(old, new StringContent("{}")));
    }

    [Fact]
    public async Task A_restart_on_a_taken_port_fails_and_leaves_the_server_stopped()
    {
        using var s = new TestServer(new FakeGame());
        var old = s.Url;
        var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        try
        {
            Assert.ThrowsAny<Exception>(() => s.Server.Restart(((IPEndPoint)taken.LocalEndpoint).Port));
        }
        finally
        {
            taken.Stop();
        }

        Assert.False(s.Server.IsListening);
        await Assert.ThrowsAsync<HttpRequestException>(() => s.Http.PostAsync(old, new StringContent("{}")));
    }

    [Fact]
    public async Task A_restart_during_a_Tool_drops_the_request_but_keeps_the_Run_going()
    {
        var game = new FakeGame();
        game.Scripts["main"] = "while True: pass";
        using var s = new TestServer(game);

        var call = s.Call("run_script", new JObject { ["name"] = "main", ["timeout_seconds"] = 60 });
        await game.WaitForRun();
        s.Restart();

        await Assert.ThrowsAsync<HttpRequestException>(() => call);
        Assert.True(game.RunActive);
    }

    [Fact]
    public async Task After_a_restart_the_next_Tool_still_waits_for_the_interrupted_one()
    {
        var game = new FakeGame();
        var started = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource<bool>();
        var slow = new Tool("slow", "Ignores everything.", new JObject { ["type"] = "object" },
            async (_, _) =>
            {
                started.SetResult(true);
                await release.Task;
                return "done";
            });
        using var s = new TestServer(game, slow);

        var interrupted = s.Call("slow");
        await started.Task;
        s.Restart();
        await Assert.ThrowsAsync<HttpRequestException>(() => interrupted);
        var next = s.Call("get_state");
        var early = await Task.WhenAny(next, Task.Delay(300)) == next;
        var callsWhileInterruptedWorkRuns = game.Calls;
        release.SetResult(true);
        var (isError, _) = await next;

        Assert.False(early);
        Assert.Equal(0, callsWhileInterruptedWorkRuns);
        Assert.False(isError);
    }

    [Fact]
    public async Task A_call_queued_behind_the_interrupted_Tool_is_dropped_by_the_restart_and_never_runs()
    {
        var game = new FakeGame();
        var started = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource<bool>();
        var slow = new Tool("slow", "Ignores everything.", new JObject { ["type"] = "object" },
            async (_, _) =>
            {
                started.SetResult(true);
                await release.Task;
                return "done";
            });
        using var s = new TestServer(game, slow);

        var interrupted = s.Call("slow");
        await started.Task;
        var queued = s.Call("get_state");
        await Task.Delay(200);
        s.Restart();
        release.SetResult(true);
        await Assert.ThrowsAnyAsync<Exception>(() => interrupted);
        await Assert.ThrowsAnyAsync<Exception>(() => queued);
        await Task.Delay(300);

        Assert.Equal(0, game.Calls);
    }
}
