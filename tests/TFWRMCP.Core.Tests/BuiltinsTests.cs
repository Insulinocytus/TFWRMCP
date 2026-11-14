using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace TFWRMCP.Core.Tests;

public class BuiltinsToolTests
{
    static BuiltinList Sample()
    {
        var builtins = new BuiltinList();
        builtins.Functions.AddRange(new[] { "harvest", "plant" });
        builtins.Keywords.Add("pass");
        builtins.Entities.Add("Grass");
        builtins.Items.Add("Hay");
        builtins.Grounds.Add("Grassland");
        builtins.Hats.Add("Straw_Hat");
        builtins.Locked["for"] = new List<UnlockGrant> { new() { Unlock = "expand", Level = 2 } };
        builtins.Locked["measure"] = new List<UnlockGrant>
        {
            new() { Unlock = "cactus", Level = 1 },
            new() { Unlock = "mazes", Level = 1 },
        };
        builtins.Locked["Carrot"] = new List<UnlockGrant> { new() { Unlock = "carrots", Level = 1 } };
        return builtins;
    }

    [Fact]
    public async Task List_builtins_returns_available_names_by_kind_and_locked_names_with_their_Unlocks()
    {
        var game = new FakeGame { Builtins = Sample() };
        using var s = new TestServer(game);

        var (isError, text) = await s.Call("list_builtins");

        Assert.False(isError);
        var result = JObject.Parse(text);
        var available = (JObject)result["available"]!;
        Assert.Equal(new[] { "harvest", "plant" }, available["functions"]!.Select(t => (string)t!));
        Assert.Equal(new[] { "pass" }, available["keywords"]!.Select(t => (string)t!));
        Assert.Equal(new[] { "Grass" }, available["entities"]!.Select(t => (string)t!));
        Assert.Equal(new[] { "Hay" }, available["items"]!.Select(t => (string)t!));
        Assert.Equal(new[] { "Grassland" }, available["grounds"]!.Select(t => (string)t!));
        Assert.Equal(new[] { "Straw_Hat" }, available["hats"]!.Select(t => (string)t!));

        var locked = (JObject)result["locked"]!;
        Assert.Equal(new JArray(new JObject { ["unlock"] = "expand", ["level"] = 2 }), locked["for"]);
        Assert.Equal(new[] { "cactus", "mazes" }, locked["measure"]!.Select(g => (string)g["unlock"]!));
        Assert.True(locked.ContainsKey("Carrot"), "names keep the spelling used in Scripts");
    }

    [Fact]
    public async Task List_builtins_is_unavailable_when_the_game_cannot_provide_it()
    {
        var game = new FakeGame();
        game.Unavailable[Capabilities.Builtins] = "BuiltinFunctions.get_Functions not found";
        using var s = new TestServer(game);

        var (isError, text) = await s.Call("list_builtins");

        Assert.True(isError);
        Assert.Contains("BuiltinFunctions.get_Functions not found", text);
    }
}
