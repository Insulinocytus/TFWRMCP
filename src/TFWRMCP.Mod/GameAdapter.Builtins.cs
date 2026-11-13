using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TFWRMCP.Mod;

public sealed partial class GameAdapter
{
    // and/or/not and every other operator are gated by the single Unlock "operators"; these syntax features are gated
    // by an Unlock of the same name but are not in Farm.allKeyWords.
    static readonly HashSet<string> OperatorWords = new() { "and", "or", "not" };
    static readonly string[] SyntaxUnlocks = { "operators", "variables", "lists", "dicts", "sets" };

    void ResolveBuiltins(Action<string> warn)
    {
        Resolve(Capabilities.Builtins, Field("Farm", "allKeyWords"), "Farm.allKeyWords", warn);
        ResolveMethods(Capabilities.Builtins, warn,
            ("MainSim", "IsUnlocked"), ("BuiltinFunctions", "get_Functions"), ("BuiltinFunctions", "get_Methods"),
            ("ResourceManager", "GetAllUnlocks"), ("ResourceManager", "GetAllEntitites"), ("ResourceManager", "GetAllItems"),
            ("ResourceManager", "GetAllGrounds"), ("ResourceManager", "GetAllHats"), ("CodeUtilities", "ToUpperSnake"));
    }

    // The same tests the code window's autocomplete uses (MainSim.IsUnlocked per name), over the tables Scripts resolve names in.
    public Task<BuiltinList> ListBuiltins() => MainThread.Run(() =>
    {
        var main = MainSim.Inst;
        if (main == null) throw new ToolException(SceneLoading);

        var grants = GrantsByName();
        var list = new BuiltinList();

        void Classify(List<string> available, string name, string unlockName)
        {
            if (main.IsUnlocked(unlockName)) available.Add(name);
            else if (grants.TryGetValue(unlockName.ToLowerInvariant(), out var by)) list.Locked[name] = by;
        }

        foreach (var name in BuiltinFunctions.Functions.Keys.Concat(BuiltinFunctions.Methods.Keys).OrderBy(n => n, StringComparer.Ordinal))
            Classify(list.Functions, name, name);
        foreach (var name in Farm.allKeyWords.Where(w => !OperatorWords.Contains(w)).Concat(SyntaxUnlocks).OrderBy(n => n, StringComparer.Ordinal))
            Classify(list.Keywords, name, name);
        foreach (var name in ResourceManager.GetAllEntitites().Select(e => e.objectName).OrderBy(n => n, StringComparer.Ordinal))
            Classify(list.Entities, CodeUtilities.ToUpperSnake(name), name);
        foreach (var name in ResourceManager.GetAllItems().Where(i => i.enabled).Select(i => i.itemName).OrderBy(n => n, StringComparer.Ordinal))
            Classify(list.Items, CodeUtilities.ToUpperSnake(name), name);
        foreach (var name in ResourceManager.GetAllGrounds().Select(g => g.objectName).OrderBy(n => n, StringComparer.Ordinal))
            Classify(list.Grounds, CodeUtilities.ToUpperSnake(name), name);
        foreach (var name in ResourceManager.GetAllHats().Where(h => !h.hidden).Select(h => h.hatName).OrderBy(n => n, StringComparer.Ordinal))
            Classify(list.Hats, CodeUtilities.ToUpperSnake(name), name);
        return list;
    });

    /// <summary>
    /// Lowercase name to the Unlocks listing it (UnlockSO.unlocks, "2for" meaning level 2; the Unlock's own name and hat
    /// count too), as Farm.UnlockOrUpgrade/UnlockAllIn grant them. Shortcut: grants chained through another Unlock's
    /// list are not followed.
    /// </summary>
    static Dictionary<string, List<UnlockGrant>> GrantsByName()
    {
        var grants = new Dictionary<string, List<UnlockGrant>>();

        void Grant(string name, UnlockSO unlock, int level)
        {
            var key = name.ToLowerInvariant();
            if (!grants.TryGetValue(key, out var list)) grants[key] = list = new List<UnlockGrant>();
            var existing = list.Find(g => g.Unlock == unlock.unlockName);
            if (existing == null) list.Add(new UnlockGrant { Unlock = unlock.unlockName, Level = level });
            else existing.Level = Math.Min(existing.Level, level);
        }

        foreach (var unlock in ListedUnlocks())
        {
            Grant(unlock.unlockName, unlock, 1);
            if (!string.IsNullOrEmpty(unlock.unlockedHat)) Grant(unlock.unlockedHat, unlock, 1);
            foreach (var entry in unlock.unlocks)
            {
                var leveled = entry.Length > 1 && char.IsDigit(entry[0]);
                Grant(leveled ? entry.Substring(1) : entry, unlock, leveled ? entry[0] - '0' : 1);
            }
        }
        return grants;
    }
}
