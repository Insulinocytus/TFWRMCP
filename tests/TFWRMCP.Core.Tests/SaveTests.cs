using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace TFWRMCP.Core.Tests;

public class TitleMenuTests
{
    [Fact]
    public async Task Any_tool_closes_an_open_title_menu_before_running()
    {
        var game = new FakeGame { Scene = GameScene.TitleMenu };
        using var s = new TestServer(game);

        var (isError, _) = await s.Call("get_state");

        Assert.False(isError);
        Assert.Equal(new[] { "close_menu" }, game.Events);
    }

    [Fact]
    public async Task A_title_menu_the_Mod_cannot_close_is_reported_as_a_failed_capability_not_an_internal_error()
    {
        var game = new FakeGame { Scene = GameScene.TitleMenu };
        game.Unavailable[Capabilities.TitleMenu] = "Menu.Play not found";
        using var s = new TestServer(game);

        var (isError, text) = await s.Call("get_state");

        Assert.True(isError);
        Assert.Contains("title_menu", text);
        Assert.Contains("Menu.Play not found", text);
        Assert.DoesNotContain("Internal error", text);
        Assert.Empty(game.Events);
    }

    [Fact]
    public async Task A_playable_game_is_left_alone()
    {
        var game = new FakeGame();
        using var s = new TestServer(game);

        await s.Call("get_state");

        Assert.Empty(game.Events);
    }

    [Fact]
    public async Task A_tool_called_during_a_scene_reload_waits_for_the_new_scene_then_closes_its_title_menu()
    {
        var game = new FakeGame { Scene = GameScene.Loading, ReloadPolls = 3 };
        using var s = new TestServer(game);

        var (isError, _) = await s.Call("get_state");

        Assert.False(isError);
        Assert.Equal(new[] { "close_menu" }, game.Events);
    }
}

public class SaveToolTests
{
    [Fact]
    public async Task List_saves_includes_the_current_Save()
    {
        var game = new FakeGame();
        game.Saves.Add("Other");
        using var s = new TestServer(game);

        var (isError, text) = await s.Call("list_saves");

        Assert.False(isError);
        var result = JObject.Parse(text);
        Assert.Equal("Save0", (string)result["current"]!);
        Assert.Equal(new[] { "Save0", "Other" }, result["saves"]!.ToObject<string[]>());
    }

    [Fact]
    public async Task Create_save_creates_the_named_Save_and_loads_it_before_returning()
    {
        var game = new FakeGame();
        using var s = new TestServer(game);

        var (isError, text) = await s.Call("create_save", new JObject { ["name"] = "test" });

        Assert.False(isError);
        Assert.Equal("test", (string)JObject.Parse(text)["save"]!);
        Assert.Equal(new[] { "create:test", "save:Save0", "load:test", "close_menu" }, game.Events);
        Assert.Equal(GameScene.Playing, game.Scene);
        Assert.Equal("test", game.CurrentSave);
    }

    [Fact]
    public async Task Create_save_without_a_name_picks_the_games_default()
    {
        var game = new FakeGame();
        using var s = new TestServer(game);

        var (isError, text) = await s.Call("create_save");

        Assert.False(isError);
        Assert.Equal("Save1", (string)JObject.Parse(text)["save"]!);
        Assert.Equal("Save1", game.CurrentSave);
    }

    [Fact]
    public async Task Create_save_rejects_an_invalid_name_and_says_why()
    {
        var game = new FakeGame();
        using var s = new TestServer(game);

        var (isError, text) = await s.Call("create_save", new JObject { ["name"] = "a/b" });

        Assert.True(isError);
        Assert.Contains("Invalid Save name 'a/b'", text);
        Assert.Contains("must not be empty", text);
        Assert.Empty(game.Events);
    }

    [Fact]
    public async Task Create_save_rejects_an_existing_name_ignoring_case()
    {
        var game = new FakeGame();
        using var s = new TestServer(game);

        var (isError, text) = await s.Call("create_save", new JObject { ["name"] = "SAVE0" });

        Assert.True(isError);
        Assert.Contains("already exists", text);
        Assert.Empty(game.Events);
    }

    [Fact]
    public async Task Create_save_rejects_a_non_string_name()
    {
        using var s = new TestServer(new FakeGame());

        var (isError, text) = await s.Call("create_save", new JObject { ["name"] = 5 });

        Assert.True(isError);
        Assert.Contains("name", text);
    }

    [Fact]
    public async Task Load_save_persists_the_current_Save_first_and_returns_only_once_the_new_Save_is_playable()
    {
        var game = new FakeGame();
        game.Saves.Add("Other");
        using var s = new TestServer(game);

        var (isError, text) = await s.Call("load_save", new JObject { ["name"] = "Other" });

        Assert.False(isError);
        Assert.Equal("Other", (string)JObject.Parse(text)["save"]!);
        Assert.Equal(new[] { "save:Save0", "load:Other", "close_menu" }, game.Events);
        Assert.Equal(GameScene.Playing, game.Scene);

        var (stateError, _) = await s.Call("get_state");
        Assert.False(stateError);
    }

    [Fact]
    public async Task Load_save_unknown_name_lists_the_existing_Saves()
    {
        var game = new FakeGame();
        using var s = new TestServer(game);

        var (isError, text) = await s.Call("load_save", new JObject { ["name"] = "nope" });

        Assert.True(isError);
        Assert.Contains("nope", text);
        Assert.Contains("Save0", text);
        Assert.Empty(game.Events);
    }

    [Fact]
    public async Task Load_save_that_cannot_persist_the_current_Save_reports_it_and_does_not_switch()
    {
        var game = new FakeGame { SaveFails = true };
        game.Saves.Add("Other");
        using var s = new TestServer(game);

        var (isError, text) = await s.Call("load_save", new JObject { ["name"] = "Other" });

        Assert.True(isError);
        Assert.Contains("failed", text);
        Assert.Equal("Save0", game.CurrentSave);
    }

    [Fact]
    public async Task Save_game_persists_the_current_Save()
    {
        var game = new FakeGame();
        using var s = new TestServer(game);

        var (isError, _) = await s.Call("save_game");

        Assert.False(isError);
        Assert.Equal(new[] { "save:Save0" }, game.Events);
    }

    [Fact]
    public async Task Save_tools_report_a_failed_capability_by_name()
    {
        var game = new FakeGame();
        game.Unavailable[Capabilities.Saves] = "Saver.Save not found";
        using var s = new TestServer(game);

        foreach (var tool in new[] { "list_saves", "create_save", "load_save", "save_game" })
        {
            var (isError, text) = await s.Call(tool);
            Assert.True(isError);
            Assert.Contains(Capabilities.Saves, text);
        }
    }
}
