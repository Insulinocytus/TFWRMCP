using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace TFWRMCP.Core.Tests;

public class PlanUnlockTests
{
    static Dictionary<string, double> Items(string item, double amount) => new() { [item] = amount };

    static void Add(FakeGame game, string name, int level, int max, string? parent, UnlockRequirement? also,
        params Dictionary<string, double>[] costs)
    {
        var node = new UnlockNode
        {
            State = new UnlockState { Name = name, Level = level, MaxLevel = max, Parent = parent, AlsoRequires = also },
        };
        node.LevelCosts.AddRange(costs);
        game.Tree.Nodes[name] = node;
    }

    // top_hat needs hats, which needs loops, and expand at level 3; expand is owned at level 1.
    static FakeGame Tree(int loopsLevel = 0)
    {
        var game = new FakeGame();
        game.Tree.Inventory = new() { ["hay"] = 100, ["wood"] = 200 };
        Add(game, "loops", loopsLevel, 1, null, null, Items("hay", 5));
        Add(game, "hats", 0, 1, "loops", null, Items("hay", 10));
        Add(game, "expand", 1, 9, null, null, Items("wood", 20), Items("wood", 40), Items("wood", 80), Items("wood", 160));
        Add(game, "top_hat", 0, 1, "hats", new UnlockRequirement { Name = "expand", Level = 3 },
            new Dictionary<string, double> { ["hay"] = 100, ["wood"] = 30 });
        return game;
    }

    static async Task<(bool IsError, string Text)> Plan(FakeGame game, string name, int? level = null)
    {
        using var s = new TestServer(game);
        var args = new JObject { ["name"] = name };
        if (level != null) args["level"] = level;
        return await s.Call("plan_unlock", args);
    }

    static string[] Steps(JObject plan) =>
        plan["steps"]!.Select(s => $"{(string)s["unlock"]!}:{(int)s["level"]!}").ToArray();

    [Fact]
    public async Task The_plan_lists_the_whole_parent_chain_and_extra_requirement_levels_in_buyable_order()
    {
        var (isError, text) = await Plan(Tree(), "top_hat");

        Assert.False(isError);
        var plan = JObject.Parse(text);
        Assert.Equal(new[] { "loops:1", "hats:1", "expand:2", "expand:3", "top_hat:1" }, Steps(plan));
        Assert.Equal(40, (double)plan["steps"]![2]!["cost"]!["wood"]!);
        Assert.Equal(80, (double)plan["steps"]![3]!["cost"]!["wood"]!);
        Assert.Equal(115, (double)plan["total_cost"]!["hay"]!);
        Assert.Equal(150, (double)plan["total_cost"]!["wood"]!);
    }

    [Fact]
    public async Task Missing_lists_only_the_items_the_inventory_lacks()
    {
        var (_, text) = await Plan(Tree(), "top_hat");

        var missing = (JObject)JObject.Parse(text)["missing"]!;
        Assert.Equal(15, (double)missing["hay"]!);
        Assert.Null(missing["wood"]);
    }

    [Fact]
    public async Task Requirements_that_are_already_met_are_not_listed()
    {
        var (_, text) = await Plan(Tree(loopsLevel: 1), "top_hat");

        Assert.Equal(new[] { "hats:1", "expand:2", "expand:3", "top_hat:1" }, Steps(JObject.Parse(text)));
    }

    [Fact]
    public async Task The_target_level_defaults_to_the_next_one_and_a_higher_level_buys_every_level_in_between()
    {
        var (_, next) = await Plan(Tree(), "expand");
        var (_, fourth) = await Plan(Tree(), "expand", 4);

        Assert.Equal(new[] { "expand:2" }, Steps(JObject.Parse(next)));
        Assert.Equal(new[] { "expand:2", "expand:3", "expand:4" }, Steps(JObject.Parse(fourth)));
        Assert.Equal(280, (double)JObject.Parse(fourth)["total_cost"]!["wood"]!);
    }

    [Fact]
    public async Task A_target_that_is_already_reached_has_no_steps()
    {
        var (isError, text) = await Plan(Tree(), "expand", 1);

        Assert.False(isError);
        var plan = JObject.Parse(text);
        Assert.Empty(plan["steps"]!);
        Assert.Empty(plan["total_cost"]!);
        Assert.Empty(plan["missing"]!);
    }

    [Fact]
    public async Task An_unknown_name_is_an_error()
    {
        var (isError, text) = await Plan(Tree(), "nope");

        Assert.True(isError);
        Assert.Contains("No Unlock named 'nope'", text);
    }

    [Fact]
    public async Task A_level_above_the_maximum_is_an_error()
    {
        var (isError, text) = await Plan(Tree(), "expand", 10);

        Assert.True(isError);
        Assert.Contains("maximum level 9", text);
    }
}
