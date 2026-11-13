using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace TFWRMCP.Core.Tests;

public class UnlockToolTests
{
    static async Task<(bool IsError, string Text)> Unlock(FakeGame game, string name)
    {
        using var s = new TestServer(game);
        return await s.Call("unlock", new JObject { ["name"] = name });
    }

    static UnlockFacts Facts(string name, int level = 0, int max = 1) =>
        new() { Name = name, Level = level, MaxLevel = max, Cost = { ["hay"] = 10 }, Inventory = { ["hay"] = 50 } };

    [Fact]
    public async Task Buying_an_affordable_Unlock_goes_through_the_game_and_reports_the_new_level()
    {
        var game = new FakeGame();
        game.UnlockFacts["plant"] = Facts("plant", level: 1, max: 3);

        var (isError, text) = await Unlock(game, "plant");

        Assert.False(isError);
        var result = JObject.Parse(text);
        Assert.Equal("plant", (string)result["unlock"]!);
        Assert.Equal(2, (int)result["level"]!);
        Assert.Equal(new[] { "unlock:plant" }, game.Events);
    }

    [Fact]
    public async Task An_unknown_name_is_rejected_without_touching_the_game()
    {
        var game = new FakeGame();

        var (isError, text) = await Unlock(game, "nope");

        Assert.True(isError);
        Assert.Contains("No Unlock named 'nope'", text);
        Assert.Empty(game.Events);
    }

    [Fact]
    public async Task A_missing_prerequisite_names_the_parent()
    {
        var game = new FakeGame();
        game.UnlockFacts["carrots"] = Facts("carrots");
        game.UnlockFacts["carrots"].Parent = "plant";

        var (isError, text) = await Unlock(game, "carrots");

        Assert.True(isError);
        Assert.Contains("requires 'plant'", text);
        Assert.Empty(game.Events);
    }

    [Fact]
    public async Task A_maxed_Unlock_says_it_is_at_its_maximum_level()
    {
        var game = new FakeGame();
        game.UnlockFacts["expand"] = Facts("expand", level: 9, max: 9);

        var (isError, text) = await Unlock(game, "expand");

        Assert.True(isError);
        Assert.Contains("maximum level (9)", text);
        Assert.Empty(game.Events);
    }

    [Fact]
    public async Task An_unmet_additional_requirement_names_the_other_Unlock_and_level()
    {
        var game = new FakeGame();
        game.UnlockFacts["cactus"] = Facts("cactus");
        game.UnlockFacts["cactus"].RequiredUnlock = "speed";
        game.UnlockFacts["cactus"].RequiredLevel = 3;
        game.UnlockFacts["cactus"].RequiredUnlockLevel = 1;

        var (isError, text) = await Unlock(game, "cactus");

        Assert.True(isError);
        Assert.Contains("'speed' at level 3 (currently 1)", text);
        Assert.Empty(game.Events);
    }

    [Fact]
    public async Task An_unaffordable_Unlock_lists_the_missing_items_and_changes_nothing()
    {
        var game = new FakeGame();
        game.UnlockFacts["trees"] = Facts("trees");
        game.UnlockFacts["trees"].Cost = new Dictionary<string, double> { ["hay"] = 10, ["wood"] = 20 };
        game.UnlockFacts["trees"].Inventory = new Dictionary<string, double> { ["hay"] = 50, ["wood"] = 5 };

        var (isError, text) = await Unlock(game, "trees");

        Assert.True(isError);
        Assert.Contains("wood: need 20, have 5", text);
        Assert.DoesNotContain("hay", text);
        Assert.Empty(game.Events);
        Assert.Equal(0, game.UnlockFacts["trees"].Level);
        Assert.Equal(5, game.UnlockFacts["trees"].Inventory["wood"]);
    }

    [Fact]
    public async Task Unlocks_are_unavailable_during_a_Leaderboard_Run()
    {
        var game = new FakeGame();
        game.UnlockFacts["plant"] = Facts("plant");
        game.UnlockFacts["plant"].Simulating = true;

        var (isError, text) = await Unlock(game, "plant");

        Assert.True(isError);
        Assert.Contains("Leaderboard Run", text);
        Assert.Empty(game.Events);
    }

    [Fact]
    public async Task A_refusal_from_the_game_after_a_passing_precheck_is_still_an_error()
    {
        var game = new FakeGame { UnlockRefused = true };
        game.UnlockFacts["plant"] = Facts("plant");

        var (isError, text) = await Unlock(game, "plant");

        Assert.True(isError);
        Assert.Contains("refused", text);
    }
}
