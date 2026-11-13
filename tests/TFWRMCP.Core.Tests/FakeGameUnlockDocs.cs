using System.Collections.Generic;
using System.Threading.Tasks;
using TFWRMCP;

namespace TFWRMCP.Core.Tests;

public sealed partial class FakeGame
{
    public Dictionary<string, UnlockFacts> UnlockFacts { get; } = new();
    public bool UnlockRefused { get; set; }
    public Dictionary<string, string> Docs { get; } = new();

    public Task<UnlockFacts?> GetUnlockFacts(string name) =>
        Task.FromResult(UnlockFacts.TryGetValue(name, out var facts) ? facts : null);

    public Task<bool> BuyUnlock(string name)
    {
        Events.Add("unlock:" + name);
        if (UnlockRefused) return Task.FromResult(false);
        var facts = UnlockFacts[name];
        facts.Level++;
        foreach (var (item, amount) in facts.Cost) facts.Inventory[item] -= amount;
        return Task.FromResult(true);
    }

    public Task<string> ReadDoc(string page) =>
        Docs.TryGetValue(page, out var text) ? Task.FromResult(text) : throw new ToolException($"No doc page '{page}'.");
}
