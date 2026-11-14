using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace TFWRMCP.Core.Tests;

public class WriteAndRunTests
{
    static async Task<JObject> Ok(TestServer s, string tool, JObject args)
    {
        var (isError, text) = await s.Call(tool, args);
        Assert.False(isError, text);
        return JObject.Parse(text);
    }

    static async Task<(FakeGame Game, TestServer Server)> Running()
    {
        var game = new FakeGame();
        game.Scripts["main"] = "old";
        var s = new TestServer(game);
        await Ok(s, "run_script", new JObject { ["name"] = "main", ["timeout_seconds"] = 0 });
        return (game, s);
    }

    [Fact]
    public async Task Stop_current_and_run_stop_the_Run_overwrite_the_Script_and_run_it_in_one_call()
    {
        var (game, s) = await Running();
        using var _ = s;

        var result = await Ok(s, "write_script", new JObject
        {
            ["name"] = "main", ["code"] = "new", ["stop_current"] = true, ["run"] = true, ["timeout_seconds"] = 0,
        });

        Assert.Equal("stopped_by_tool", (string)result["stopped"]!["status"]!);
        Assert.False((bool)result["created"]!);
        Assert.Equal("new", game.Scripts["main"]);
        Assert.Equal("running", (string)result["run"]!["status"]!);
        Assert.True(game.RunActive);
    }

    [Fact]
    public async Task Run_while_a_Run_is_in_progress_without_stop_current_is_an_error_and_changes_nothing()
    {
        var (game, s) = await Running();
        using var _ = s;

        var (isError, text) = await s.Call("write_script", new JObject { ["name"] = "main", ["code"] = "new", ["run"] = true });

        Assert.True(isError);
        Assert.Contains("stop_current", text);
        Assert.Equal("old", game.Scripts["main"]);
        Assert.True(game.RunActive);
        Assert.Equal(0, game.StopCalls);
    }

    [Fact]
    public async Task Run_with_a_syntax_error_returns_parse_error_and_the_code_is_written()
    {
        var game = new FakeGame();
        game.SyntaxErrors["main"] = new SyntaxError { Message = "bad", Source = "(", StartIndex = 0 };
        using var s = new TestServer(game);

        var result = await Ok(s, "write_script", new JObject { ["name"] = "main", ["code"] = "(", ["run"] = true });

        Assert.Equal("parse_error", (string)result["run"]!["status"]!);
        Assert.Equal("(", game.Scripts["main"]);
        Assert.Null(result["stopped"]);
    }
}
