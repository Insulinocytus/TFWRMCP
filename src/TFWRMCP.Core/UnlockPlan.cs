using System.Collections.Generic;
using System.Linq;

namespace TFWRMCP;

/// <summary>One listed Unlock with the cost of every purchase it can still take.</summary>
public sealed class UnlockNode
{
    public UnlockState State { get; set; } = new();

    /// <summary>Cost of buying level i + 1 (non-zero items only), for i in 0 until MaxLevel.</summary>
    public List<Dictionary<string, double>> LevelCosts { get; } = new();
}

/// <summary>Every Unlock listed by the research menu (by name) and the inventory, read in the same game tick.</summary>
public sealed class UnlockTree
{
    public Dictionary<string, UnlockNode> Nodes { get; } = new();
    public Dictionary<string, double> Inventory { get; set; } = new();
}

public sealed class UnlockStep
{
    public string Unlock { get; set; } = "";

    /// <summary>Level owned after this purchase.</summary>
    public int Level { get; set; }

    public Dictionary<string, double> Cost { get; set; } = new();
}

public sealed class UnlockPlanResult
{
    public List<UnlockStep> Steps { get; } = new();
    public Dictionary<string, double> TotalCost { get; } = new();

    /// <summary>Items the inventory lacks for <see cref="TotalCost"/>.</summary>
    public Dictionary<string, double> Missing { get; } = new();
}

public static class UnlockPlan
{
    /// <summary>
    /// The purchases, in an order that is executable from the current levels, that bring <paramref name="name"/> to
    /// <paramref name="level"/> (current level + 1 when null), including parents and extra requirements.
    /// </summary>
    public static UnlockPlanResult Build(UnlockTree tree, string name, int? level)
    {
        var target = Find(tree, name);
        var plan = new UnlockPlanResult();
        var owned = tree.Nodes.Values.ToDictionary(n => n.State.Name, n => n.State.Level);
        Reach(tree, owned, plan, target, level ?? target.State.Level + 1);

        foreach (var cost in plan.Steps.Select(s => s.Cost))
            foreach (var (item, amount) in cost)
                plan.TotalCost[item] = plan.TotalCost.GetValueOrDefault(item) + amount;
        foreach (var (item, amount) in plan.TotalCost)
            if (tree.Inventory.GetValueOrDefault(item) < amount)
                plan.Missing[item] = amount - tree.Inventory.GetValueOrDefault(item);
        return plan;
    }

    static UnlockNode Find(UnlockTree tree, string name) =>
        tree.Nodes.GetValueOrDefault(name.Trim().ToLowerInvariant()) ??
        throw new ToolException($"No Unlock named '{name}'. Unlock names are listed in get_state.");

    static void Reach(UnlockTree tree, Dictionary<string, int> owned, UnlockPlanResult plan, UnlockNode node, int level)
    {
        var state = node.State;
        if (level > state.MaxLevel)
            throw new ToolException($"'{state.Name}' has maximum level {state.MaxLevel}, so level {level} cannot be reached.");
        if (owned[state.Name] >= level) return;

        // The game checks the parent and the extra requirement on every purchase, but they hold from the first one on.
        if (!string.IsNullOrEmpty(state.Parent)) Reach(tree, owned, plan, Find(tree, state.Parent), 1);
        if (state.AlsoRequires != null) Reach(tree, owned, plan, Find(tree, state.AlsoRequires.Name), state.AlsoRequires.Level);

        while (owned[state.Name] < level)
        {
            plan.Steps.Add(new UnlockStep
            {
                Unlock = state.Name,
                Level = owned[state.Name] + 1,
                Cost = node.LevelCosts[owned[state.Name]],
            });
            owned[state.Name]++;
        }
    }
}
