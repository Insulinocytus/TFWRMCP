using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace TFWRMCP.Core.Tests;

public class ScriptToolTests
{
    static JObject Args(string name, string? code = null)
    {
        var args = new JObject { ["name"] = name };
        if (code != null) args["code"] = code;
        return args;
    }

    [Fact]
    public async Task A_written_Script_can_be_listed_and_read_back()
    {
        var game = new FakeGame();
        using var s = new TestServer(game);

        var (writeError, writeText) = await s.Call("write_script", Args("main", "print(1)\n"));
        var (listError, listText) = await s.Call("list_scripts");
        var (readError, readText) = await s.Call("read_script", Args("main"));

        Assert.False(writeError);
        Assert.True((bool)JObject.Parse(writeText)["created"]!);
        Assert.False(listError);
        Assert.Equal(new[] { "main" }, JObject.Parse(listText)["scripts"]!.ToObject<string[]>());
        Assert.False(readError);
        Assert.Equal("print(1)\n", (string)JObject.Parse(readText)["code"]!);
        Assert.Equal("print(1)\n", game.Scripts["main"]);
    }

    [Fact]
    public async Task Writing_an_existing_Script_overwrites_it()
    {
        var game = new FakeGame();
        game.Scripts["main"] = "old";
        using var s = new TestServer(game);

        var (isError, text) = await s.Call("write_script", Args("main", "new"));

        Assert.False(isError);
        Assert.False((bool)JObject.Parse(text)["created"]!);
        Assert.Equal("new", game.Scripts["main"]);
    }

    [Fact]
    public async Task An_empty_Script_is_a_valid_write()
    {
        var game = new FakeGame();
        using var s = new TestServer(game);

        var (isError, _) = await s.Call("write_script", Args("blank", ""));

        Assert.False(isError);
        Assert.Equal("", game.Scripts["blank"]);
    }

    [Fact]
    public async Task The_games_name_rejection_reaches_the_Agent_and_nothing_is_written()
    {
        var game = new FakeGame();
        game.Scripts["Main"] = "x";
        using var s = new TestServer(game);

        var (invalid, invalidText) = await s.Call("write_script", Args("a/b", "x"));
        var (clash, clashText) = await s.Call("write_script", Args("main", "x"));

        Assert.True(invalid);
        Assert.Contains("Invalid Script name", invalidText);
        Assert.True(clash);
        Assert.Contains("already open", clashText);
        Assert.Equal(new[] { "Main" }, game.Scripts.Keys);
        Assert.Equal("x", game.Scripts["Main"]);
    }

    [Fact]
    public async Task Write_script_requires_string_name_and_code()
    {
        using var s = new TestServer(new FakeGame());

        var (noCode, noCodeText) = await s.Call("write_script", Args("main"));
        var (badName, _) = await s.Call("write_script", new JObject { ["name"] = 5, ["code"] = "" });

        Assert.True(noCode);
        Assert.Contains("code", noCodeText);
        Assert.True(badName);
    }

    [Fact]
    public async Task Delete_script_removes_the_Script()
    {
        var game = new FakeGame();
        game.Scripts["main"] = "x";
        using var s = new TestServer(game);

        var (isError, _) = await s.Call("delete_script", Args("main"));

        Assert.False(isError);
        Assert.Empty(game.Scripts);
    }

    [Fact]
    public async Task Reading_or_deleting_an_unknown_Script_is_an_error()
    {
        using var s = new TestServer(new FakeGame());

        var (readError, readText) = await s.Call("read_script", Args("nope"));
        var (deleteError, deleteText) = await s.Call("delete_script", Args("nope"));

        Assert.True(readError);
        Assert.Contains("nope", readText);
        Assert.True(deleteError);
        Assert.Contains("nope", deleteText);
    }

    [Fact]
    public async Task Script_tools_report_a_failed_capability_by_name()
    {
        var game = new FakeGame();
        game.Unavailable[Capabilities.Scripts] = "Saver.SaveCode not found";
        using var s = new TestServer(game);

        foreach (var tool in new[] { "list_scripts", "read_script", "write_script", "delete_script" })
        {
            var (isError, text) = await s.Call(tool);
            Assert.True(isError);
            Assert.Contains(Capabilities.Scripts, text);
        }
    }

    [Fact]
    public async Task Run_tools_report_a_failed_capability_by_name()
    {
        var game = new FakeGame();
        game.Unavailable[Capabilities.Run] = "Simulation.StopProgramExecution not found";
        using var s = new TestServer(game);

        foreach (var tool in new[] { "run_script", "wait_run", "stop_run" })
        {
            var (isError, text) = await s.Call(tool);
            Assert.True(isError);
            Assert.Contains(Capabilities.Run, text);
        }
    }
}
