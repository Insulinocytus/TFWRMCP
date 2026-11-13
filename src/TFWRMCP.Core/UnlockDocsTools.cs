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
    };
}
