using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TFWRMCP;

public static partial class Tools
{
    const string PageIdsDescription =
        "There are two kinds of page id: unlocks/<name>, functions/<name>, items/<name>, objects/<name> and " +
        "grounds/<name> are generated from the game's texts (unlocks/<name> shows what the Unlock does and the cost " +
        "of its next level); docs/....md are the tutorial pages, whose file names need not match an Unlock (the " +
        "tutorial for loops is docs/scripting/while.md, and docs/unlocks/loops.md does not exist). Take page ids from " +
        "list_docs (include_locked lists the locked ones too) rather than building them.";

    static async Task<object?> Unlock(IGame game, string name)
    {
        var attempt = await game.TryUnlock(name, UnlockCheck.WhyNot);
        var facts = attempt.Facts ?? throw new ToolException($"No Unlock named '{name}'. Unlock names are listed in get_state.");
        if (attempt.Refusal != null) throw new ToolException(attempt.Refusal);
        if (!attempt.Bought)
            throw new ToolException($"The game refused to unlock '{facts.Name}' although every check passed; its state may have just changed.");

        return new { unlock = facts.Name, level = attempt.Level };
    }

    static async Task<object?> UnlockMany(IGame game, JArray names, CancellationToken cancel)
    {
        var results = new List<object?>();
        var failed = false;
        foreach (var name in names.Select(n => (string)n!))
        {
            if (failed)
            {
                results.Add(new { unlock = name, skipped = true });
                continue;
            }

            cancel.ThrowIfCancellationRequested();
            try
            {
                results.Add(await Unlock(game, name));
            }
            catch (ToolException e)
            {
                failed = true;
                results.Add(new { unlock = name, error = e.Message });
            }
        }
        return new { results };
    }

    static JArray? OptionalStrings(JObject args, string name)
    {
        var value = args[name];
        if (value == null || value.Type == JTokenType.Null) return null;
        if (value is not JArray { Count: > 0 } array || array.Any(i => i.Type != JTokenType.String))
            throw new ToolException($"'{name}' must be a non-empty array of strings.");
        return array;
    }

    static int? OptionalInt(JObject args, string name)
    {
        var value = args[name];
        if (value == null || value.Type == JTokenType.Null) return null;
        return value.Type == JTokenType.Integer && (long)value is >= int.MinValue and <= int.MaxValue
            ? (int)value
            : throw new ToolException($"'{name}' must be an integer.");
    }

    static JObject UnlockSchema()
    {
        var schema = Schema(("name", "string", "Unlock name as listed by get_state, e.g. \"plant\".", false));
        ((JObject)schema["properties"]!)["names"] = new JObject
        {
            ["type"] = "array",
            ["items"] = new JObject { ["type"] = "string" },
            ["description"] = "Unlock names to buy one level each, in order; stops at the first failure.",
        };
        return schema;
    }

    static IEnumerable<Tool> UnlockAndDocsTools(IGame game) => new[]
    {
        new Tool(
            "unlock",
            "Buys or upgrades an Unlock exactly like the research menu: costs and prerequisites are checked by the game " +
            "(and its hat is unlocked too); unlike this Tool, a Script's unlock() does not check the parent Unlock. " +
            "Pass exactly one of name or names. With names the Unlocks are bought in order, one level each, stopping at " +
            "the first failure without undoing earlier purchases; results lists each as {unlock, level} (bought, level " +
            "after the purchase), {unlock, error} (failed) or {unlock, skipped} (not tried). On failure the error says " +
            "why: unknown name, missing prerequisite, already at maximum level, unmet extra requirement, not enough " +
            "items, or a Leaderboard Run or simulate() in progress.",
            UnlockSchema(),
            (args, cancel) =>
            {
                var name = OptionalString(args, "name");
                var names = OptionalStrings(args, "names");
                if ((name == null) == (names == null)) throw new ToolException("Pass exactly one of 'name' or 'names'.");
                return name != null ? Unlock(game, name) : UnlockMany(game, names!, cancel);
            },
            Capabilities.Unlock),
        new Tool(
            "plan_unlock",
            "Plans the purchases needed to bring an Unlock to a level, without buying anything: steps lists every " +
            "level still to buy in an order that works from the current state (parent Unlocks, levels of extra " +
            "required Unlocks, then the target itself) as {unlock, level (after the purchase), cost}; already met " +
            "requirements are not listed, and steps is empty when the target is reached. total_cost sums the steps; " +
            "missing lists the items the inventory lacks for it. Costs are the game's, also for levels not yet reachable.",
            Schema(("name", "string", "Unlock name as listed by get_state.", true),
                ("level", "integer", "Target Unlock level. Defaults to the current level + 1.", false)),
            async (args, _) => UnlockPlan.Build(await game.GetUnlockTree(), RequiredString(args, "name"), OptionalInt(args, "level")),
            Capabilities.Unlock, Capabilities.State),
        new Tool(
            "list_docs",
            "Lists the documentation pages in the game's docs window (what its home page links to, in the game's " +
            "language) as {page, title}. Pages for content you have not unlocked yet are left out unless include_locked " +
            "is true; they then carry locked: true, and read_doc answers them with the game's placeholder. " + PageIdsDescription,
            Schema(("include_locked", "boolean", "Also list pages for content not unlocked yet. Defaults to false.", false)),
            async (args, _) =>
            {
                var pages = await game.ListDocs(OptionalBool(args, "include_locked"));
                return new
                {
                    pages = pages.Select(p => p.Locked
                        ? new JObject { ["page"] = p.Page, ["title"] = p.Title, ["locked"] = true }
                        : new JObject { ["page"] = p.Page, ["title"] = p.Title }),
                };
            },
            Capabilities.Docs),
        new Tool(
            "read_doc",
            "Reads a documentation page in the game's language, as the docs window shows it. Pass a page id from " +
            "list_docs, e.g. \"docs/scripting/for.md\" or \"functions/harvest\". Locked content returns the game's " +
            "\"you need to unlock X\" page. " + PageIdsDescription,
            Schema(("page", "string", "Page id from list_docs.", true)),
            async (args, _) =>
            {
                var page = RequiredString(args, "page");
                return new { page, content = await game.ReadDoc(page) };
            },
            Capabilities.Docs),
        new Tool(
            "list_builtins",
            "Lists which functions, keywords and members (Entities, Items, Grounds, Hats) a Script can use right now " +
            "(available), and for every other name the Unlocks that grant it with the level needed (locked), e.g. " +
            "\"for\" needs Unlock expand at level 2. Operators (%, and, or, not) are one keyword, operators; syntax " +
            "for variables, lists, dicts and sets is listed as variables, lists, dicts and sets. Names are spelled as in a Script.",
            Schema(),
            async (_, _) =>
            {
                var builtins = await game.ListBuiltins();
                var locked = new JObject();
                foreach (var (name, grants) in builtins.Locked)
                    locked[name] = new JArray(grants.Select(g => new JObject { ["unlock"] = g.Unlock, ["level"] = g.Level }));
                return new
                {
                    available = new
                    {
                        functions = builtins.Functions, keywords = builtins.Keywords, entities = builtins.Entities,
                        items = builtins.Items, grounds = builtins.Grounds, hats = builtins.Hats,
                    },
                    locked,
                };
            },
            Capabilities.Builtins),
    };
}
