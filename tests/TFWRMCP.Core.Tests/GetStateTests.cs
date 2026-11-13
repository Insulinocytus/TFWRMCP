using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace TFWRMCP.Core.Tests;

public class GetStateTests
{
    [Fact]
    public async Task Returns_the_full_snapshot()
    {
        using var s = new TestServer(new FakeGame());
        var (isError, text) = await s.Call("get_state");

        Assert.False(isError);
        var state = JObject.Parse(text);
        Assert.Equal("Save0", (string)state["save"]!);
        Assert.Equal(12.5, (double)state["inventory"]!["hay"]!);
        Assert.Equal(0, (double)state["inventory"]!["wood"]!);

        var unlocks = state["unlocks"]!;
        Assert.Equal(1, (int)unlocks[0]!["level"]!);
        Assert.Equal(JTokenType.Null, unlocks[0]!["next_cost"]!.Type);
        Assert.Equal("expand", (string)unlocks[1]!["name"]!);
        Assert.Equal(9, (int)unlocks[1]!["max_level"]!);
        Assert.Equal(20, (double)unlocks[1]!["next_cost"]!["wood"]!);

        Assert.Equal(2, (int)state["world_size"]!["x"]!);
        Assert.Equal(2, (int)state["world_size"]!["y"]!);

        var drone = Assert.Single(state["drones"]!);
        Assert.Equal((0, 1, 0, 0), ((int)drone["id"]!, (int)drone["x"]!, (int)drone["y"]!, (int)drone["depth"]!));

        var tiles = state["tiles"]!;
        Assert.Equal(2, tiles.Count());
        Assert.Equal("grass", (string)tiles[0]![0]!["entity"]!);
        Assert.Equal("grassland", (string)tiles[0]![0]!["ground"]!);
        Assert.Null(tiles[0]![0]!["depth"]);
        Assert.Null(tiles[0]![1]!["entity"]);
        Assert.Equal("soil", (string)tiles[0]![1]!["ground"]!);
        Assert.Equal("tree", (string)tiles[1]![0]!["entity"]!);
        Assert.Equal("dirt", (string)tiles[1]![1]!["ground"]!);
        Assert.Equal(2, (int)tiles[1]![1]!["depth"]!);

        Assert.Equal("idle", (string)state["run"]!["status"]!);
    }

    [Fact]
    public async Task Game_errors_are_reported_as_tool_errors()
    {
        var game = new ThrowingGame();
        using var s = new TestServer(game);
        var (isError, text) = await s.Call("get_state");

        Assert.True(isError);
        Assert.Contains("scene is loading", text);
    }

    [Fact]
    public async Task Unavailable_capability_fails_only_the_dependent_tool_and_names_the_capability()
    {
        var game = new FakeGame();
        game.Unavailable[Capabilities.State] = "field MainSim.sim not found";
        var echo = new Tool("echo", "Echo.", new JObject { ["type"] = "object" }, _ => Task.FromResult<object?>("pong"));
        using var s = new TestServer(game, echo);

        var (stateError, stateText) = await s.Call("get_state");
        Assert.True(stateError);
        Assert.Contains(Capabilities.State, stateText);
        Assert.Contains("field MainSim.sim not found", stateText);
        Assert.Equal(0, game.Calls);

        var (echoError, echoText) = await s.Call("echo");
        Assert.False(echoError);
        Assert.Equal("pong", JToken.Parse(echoText).ToString());

        var tools = (await s.Rpc("tools/list"))["result"]!["tools"]!;
        Assert.Contains(tools, t => (string)t["name"]! == "get_state");
    }

    [Fact]
    public async Task Concurrent_clients_are_served_one_tool_call_at_a_time()
    {
        var game = new FakeGame { GetStateDelay = TimeSpan.FromMilliseconds(30) };
        using var s = new TestServer(game);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => s.Call("get_state")));

        Assert.All(results, r => Assert.False(r.IsError));
        Assert.Equal(8, game.Calls);
        Assert.Equal(1, game.MaxConcurrentCalls);
    }

    sealed class ThrowingGame : IGame
    {
        public string? UnavailableReason(string capability) => null;
        public Task<GameState> GetState() => throw new ToolException("The game scene is loading.");
        public Task<GameScene> GetScene() => Task.FromResult(GameScene.Playing);
        public Task CloseTitleMenu() => throw new NotSupportedException();
        public Task<SaveList> ListSaves() => throw new NotSupportedException();
        public Task<bool> IsValidSaveName(string name) => throw new NotSupportedException();
        public Task<string> GenerateSaveName() => throw new NotSupportedException();
        public Task CreateSave(string name) => throw new NotSupportedException();
        public Task LoadSave(string name) => throw new NotSupportedException();
        public Task SaveGame() => throw new NotSupportedException();
        public Task<List<string>> ListScripts() => throw new NotSupportedException();
        public Task<string> ReadScript(string name) => throw new NotSupportedException();
        public Task<bool> WriteScript(string name, string code) => throw new NotSupportedException();
        public Task DeleteScript(string name) => throw new NotSupportedException();
        public Task<SyntaxError?> StartRun(string name) => throw new NotSupportedException();
        public Task StopRun() => throw new NotSupportedException();
        public event Action<RunEvent> RunEvents { add { } remove { } }
    }
}
