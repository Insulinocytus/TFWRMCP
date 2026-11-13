using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TFWRMCP;

namespace TFWRMCP.Core.Tests;

public sealed partial class FakeGame
{
    public Dictionary<string, UnlockFacts> UnlockFacts { get; } = new();
    public bool UnlockRefused { get; set; }
    public Dictionary<string, string> Docs { get; } = new();

    public Task<UnlockAttempt> TryUnlock(string name, Func<UnlockFacts, string?> whyNot)
    {
        if (!UnlockFacts.TryGetValue(name, out var facts)) return Task.FromResult(new UnlockAttempt());
        var refusal = whyNot(facts);
        if (refusal != null) return Task.FromResult(new UnlockAttempt { Facts = facts, Refusal = refusal });

        Events.Add("unlock:" + name);
        if (UnlockRefused) return Task.FromResult(new UnlockAttempt { Facts = facts, Level = facts.Level });
        facts.Level++;
        foreach (var (item, amount) in facts.Cost) facts.Inventory[item] -= amount;
        return Task.FromResult(new UnlockAttempt { Facts = facts, Bought = true, Level = facts.Level });
    }

    public Task<string> ReadDoc(string page) =>
        Docs.TryGetValue(page, out var text) ? Task.FromResult(text) : throw new ToolException($"No doc page '{page}'.");
}
