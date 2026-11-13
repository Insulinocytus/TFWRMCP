using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TFWRMCP;

public static partial class Tools
{
    const string HomePage = "docs/home.md";

    static async Task<object?> Unlock(IGame game, string name)
    {
        var attempt = await game.TryUnlock(name, UnlockCheck.WhyNot);
        var facts = attempt.Facts ?? throw new ToolException($"No Unlock named '{name}'. Unlock names are listed in get_state.");
        if (attempt.Refusal != null) throw new ToolException(attempt.Refusal);
        if (!attempt.Bought)
            throw new ToolException($"The game refused to unlock '{facts.Name}' although every check passed; its state may have just changed.");

        return new { unlock = facts.Name, level = attempt.Level };
    }

    static IEnumerable<Tool> UnlockAndDocsTools(IGame game) => new[]
    {
        new Tool(
            "unlock",
            "Buys or upgrades an Unlock exactly like the research menu: costs and prerequisites are checked by the game " +
            "(and its hat is unlocked too). On failure the error says why: unknown name, missing prerequisite, already at " +
            "maximum level, unmet extra requirement, not enough items, or a Leaderboard Run or simulate() in progress.",
            Schema(("name", "string", "Unlock name as listed by get_state, e.g. \"plant\".", true)),
            (args, _) => Unlock(game, RequiredString(args, "name")),
            Capabilities.Unlock),
        new Tool(
            "list_docs",
            "Lists the documentation pages in the game's docs window (what its home page links to, in the game's " +
            "language). Pages for content you have not unlocked yet are not listed, but read_doc still answers them " +
            "with the game's placeholder.",
            Schema(),
            async (_, _) =>
            {
                var home = await game.ReadDoc(HomePage);
                return new { pages = DocMarkup.Links(home).Select(l => new { page = l.Page, title = l.Title }) };
            },
            Capabilities.Docs),
        new Tool(
            "read_doc",
            "Reads a documentation page in the game's language, as the docs window shows it. Pass a page id from " +
            "list_docs, e.g. \"docs/scripting/for.md\" or \"functions/harvest\". Locked content returns the game's " +
            "\"you need to unlock X\" page.",
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
