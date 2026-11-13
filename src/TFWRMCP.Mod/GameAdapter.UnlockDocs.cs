using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Core.Platform;
using Platforms;
using UnityEngine;

namespace TFWRMCP.Mod;

public sealed partial class GameAdapter
{
    void ResolveUnlockDocs(Action<string> warn)
    {
        const BindingFlags Public = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
        foreach (var (type, name) in new[]
                 {
                     (typeof(MainSim), "UnlockOrUpgrade"), (typeof(MainSim), "UnlockHat"), (typeof(MainSim), "IsSimulating"),
                     (typeof(MainSim), "GetUnlockCost"), (typeof(MainSim), "NumUnlocked"), (typeof(ResourceManager), "GetUnlock"),
                     (typeof(ResourceManager), "GetHat"),
                 })
            Resolve(Capabilities.Unlock, type.GetMethod(name, Public), $"{type.Name}.{name}", warn);

        foreach (var (type, name) in new[]
                 {
                     (typeof(Localizer), "Localize"), (typeof(Localizer), "LoadDoc"), (typeof(ResourceManager), "GetUnlockOf"),
                     (typeof(TooltipUtils), "UnlockTooltip"), (typeof(TooltipUtils), "ItemTooltip"),
                     (typeof(TooltipUtils), "FarmObjectTooltip"), (typeof(TooltipUtils), "GroundTooltip"),
                     (typeof(TooltipUtils), "GetKeywordDocsPage"), (typeof(CodeUtilities), "ToUpperSnake"),
                 })
            Resolve(Capabilities.Docs, type.GetMethod(name, Public), $"{type.Name}.{name}", warn);
    }

    public Task<UnlockFacts?> GetUnlockFacts(string name) => MainThread.Run(() => ReadUnlockFacts(name));

    static UnlockFacts? ReadUnlockFacts(string name)
    {
        var main = MainSim.Inst;
        if (main == null) throw new ToolException("The game scene is loading; try again shortly.");

        // Same list the research menu shows (and get_state reports).
        var unlock = ResourceManager.GetUnlock(name.Trim().ToLowerInvariant());
        if (unlock == null || !unlock.enabled || !PlatformManager.IsDlcInstalled(unlock.dlc)) return null;

        var extra = unlock.additionalUnlockRequirement;
        var hasExtra = extra != null && !string.IsNullOrEmpty(extra.unlockName);
        return new UnlockFacts
        {
            Name = unlock.unlockName,
            Level = main.NumUnlocked(unlock.unlockName),
            MaxLevel = unlock.maxUnlockLevel,
            Parent = unlock.parentUnlock,
            ParentUnlocked = string.IsNullOrEmpty(unlock.parentUnlock) || main.IsUnlocked(unlock.parentUnlock),
            RequiredUnlock = hasExtra ? extra!.unlockName : null,
            RequiredLevel = hasExtra ? extra!.level : 0,
            RequiredUnlockLevel = hasExtra ? main.NumUnlocked(extra!.unlockName) : 0,
            Cost = Items(main.GetUnlockCost(unlock), includeZero: false),
            Inventory = Items(main.GetInventory(), includeZero: true),
            Simulating = main.IsSimulating(),
        };
    }

    // The research menu's UnlockBox.ButtonClicked, minus the reset-to-max cheat branch.
    public Task<bool> BuyUnlock(string name) => MainThread.Run(() =>
    {
        var main = MainSim.Inst;
        var unlock = ResourceManager.GetUnlock(name);
        if (main == null || unlock == null || !main.UnlockOrUpgrade(unlock)) return false;

        if (!string.IsNullOrEmpty(unlock.unlockedHat)) main.UnlockHat(ResourceManager.GetHat(unlock.unlockedHat));
        FMODSoundManager.PlaySound(SoundEffectType.Unlock, Vector3.zero);
        return true;
    });

    public Task<string> ReadDoc(string page) => MainThread.Run(() => RenderDoc(page));

    static string RenderDoc(string doc)
    {
        var main = MainSim.Inst;
        if (main == null) throw new ToolException("The game scene is loading; try again shortly.");

        var text = DocSource(doc) ?? throw new ToolException(
            $"No doc page '{doc}'. Page ids are listed by list_docs (for example docs/home.md, functions/harvest, unlocks/plant).");
        return DocMarkup.Render(text, main.IsUnlocked, Localizer.Localize, PlatformManager.IsDlcInstalled(EDlcType.Mining), ItemBlockSection);
    }

    /// <summary>Mirrors DocsWindow.DisplayDoc (placeholder for locked pages, generated pages, files); null when the page does not exist.</summary>
    static string? DocSource(string doc)
    {
        var missing = MissingUnlock(doc);
        if (missing != null)
            return string.Format(Localizer.Localize("missing_unlock_page"), Localizer.Localize(missing.unlockName),
                TooltipUtils.UnlockTooltip(missing.unlockName).text);

        var slash = doc.IndexOf('/');
        var prefix = slash < 0 ? "" : doc.Substring(0, slash + 1);
        var rest = slash < 0 ? doc : doc.Substring(slash + 1);
        var text = new StringBuilder();
        switch (prefix)
        {
            case "functions/":
                var key = "code_tooltip_" + rest;
                var tooltip = Localizer.Localize(key);
                if (tooltip == key) return null;
                text.Append(tooltip);
                AddUnlockedIn(text, rest);
                return text.ToString();
            case "unlocks/":
                return TooltipUtils.UnlockTooltip(rest)?.text;
            case "items/":
                return Generated(TooltipUtils.ItemTooltip(rest), rest, text);
            case "objects/":
                return Generated(TooltipUtils.FarmObjectTooltip(rest), rest, text);
            case "grounds/":
                return Generated(TooltipUtils.GroundTooltip(rest), rest, text);
        }

        // Only game doc files; the page id must not escape the language's docs folder.
        if (!doc.StartsWith("docs/") || !doc.EndsWith(".md") || doc.Contains("..") || doc.Contains('\\') || doc.Contains(':')) return null;
        var file = Localizer.LoadDoc(doc);
        return file == "" ? null : file;
    }

    static string? Generated(TooltipInfo? tooltip, string name, StringBuilder text)
    {
        if (tooltip == null) return null;
        text.Append(tooltip.text);
        AddUnlockedIn(text, name);
        return text.ToString();
    }

    static UnlockSO? MissingUnlock(string doc)
    {
        if (doc == "docs/output.md") return null;
        var name = System.IO.Path.GetFileNameWithoutExtension(doc);
        var unlock = ResourceManager.GetUnlockOf(name) ?? ResourceManager.GetUnlockOf(name.ToLowerInvariant());
        return unlock == null || MainSim.Inst.IsUnlocked(name) ? null : unlock;
    }

    static void AddUnlockedIn(StringBuilder text, string word)
    {
        var page = TooltipUtils.GetKeywordDocsPage(word);
        if (page == "") return;
        var name = page.Split('/').Last();
        text.Append("\n\n").Append(string.Format(Localizer.Localize("unlocked_in"), CodeUtilities.ToUpperSnake(name.Substring(0, name.Length - 3)), page));
    }

    /// <summary>The docs window draws {{itemblock cost ...}} as an item grid; as text it is "item: amount" pairs.</summary>
    static string? ItemBlockSection(string section)
    {
        var words = section.Split(' ');
        if (words.Length != 4 || words[0] != "itemblock" || words[1] != "cost") return null;

        ItemBlock? cost = null;
        if (words[2] == "unlock")
        {
            var unlock = ResourceManager.GetUnlock(words[3]);
            if (unlock != null) cost = MainSim.Inst.GetUnlockCost(unlock);
        }
        else if (words[2] == "object")
        {
            var entity = ResourceManager.GetEntity(words[3]);
            if (entity != null) cost = entity.cost * (entity.yieldUpgradeName != "" ? Mathf.Max(1, 1 << Mathf.Max(0, MainSim.Inst.NumUnlocked(entity.yieldUpgradeName) - 1)) : 1);
        }
        var items = Items(cost, includeZero: false);
        return items.Count == 0 ? "(free)" : string.Join(", ", items.Select(i => $"{i.Key}: {i.Value}"));
    }
}
