using System.Collections.Generic;
using System.Linq;

namespace TFWRMCP;

/// <summary>What the game knows about one Unlock right now; enough to explain why buying it would fail.</summary>
public sealed class UnlockFacts
{
    public string Name { get; set; } = "";
    public int Level { get; set; }
    public int MaxLevel { get; set; }

    /// <summary>Unlock that must be owned first; null when there is none.</summary>
    public string? Parent { get; set; }
    public bool ParentUnlocked { get; set; }

    /// <summary>Extra condition "Unlock X at level N"; null name when there is none.</summary>
    public string? RequiredUnlock { get; set; }
    public int RequiredLevel { get; set; }
    public int RequiredUnlockLevel { get; set; }

    /// <summary>Cost of the next level (non-zero items only) and the current inventory.</summary>
    public Dictionary<string, double> Cost { get; set; } = new();
    public Dictionary<string, double> Inventory { get; set; } = new();

    /// <summary>True during a Leaderboard Run or simulate(), when the game refuses all purchases.</summary>
    public bool Simulating { get; set; }
}

/// <summary>What <see cref="IGame.TryUnlock"/> found and did, all within one game tick.</summary>
public sealed class UnlockAttempt
{
    /// <summary>Null when no such Unlock is listed.</summary>
    public UnlockFacts? Facts { get; set; }

    /// <summary>Why the precheck refused the purchase; null when it passed.</summary>
    public string? Refusal { get; set; }

    /// <summary>True when the precheck passed and the game bought the Unlock; false when the game refused anyway.</summary>
    public bool Bought { get; set; }

    /// <summary>The Unlock's level after the attempt.</summary>
    public int Level { get; set; }
}

public static class UnlockCheck
{
    /// <summary>Why the game would refuse the purchase, following its own check order; null when it would accept.</summary>
    public static string? WhyNot(UnlockFacts f)
    {
        if (f.Simulating)
            return "Unlocks cannot be bought while a Leaderboard Run or simulate() is running.";
        if (!string.IsNullOrEmpty(f.Parent) && !f.ParentUnlocked)
            return $"'{f.Name}' requires '{f.Parent}' to be unlocked first.";
        if (f.Level >= f.MaxLevel)
            return $"'{f.Name}' is already at its maximum level ({f.MaxLevel}).";
        if (!string.IsNullOrEmpty(f.RequiredUnlock) && f.RequiredUnlockLevel < f.RequiredLevel)
            return $"'{f.Name}' also requires '{f.RequiredUnlock}' at level {f.RequiredLevel} (currently {f.RequiredUnlockLevel}).";

        var missing = f.Cost
            .Where(c => f.Inventory.GetValueOrDefault(c.Key) < c.Value)
            .Select(c => $"{c.Key}: need {c.Value}, have {f.Inventory.GetValueOrDefault(c.Key)}")
            .ToList();
        return missing.Count == 0 ? null : $"Not enough items for '{f.Name}' level {f.Level + 1} ({string.Join("; ", missing)}).";
    }
}
