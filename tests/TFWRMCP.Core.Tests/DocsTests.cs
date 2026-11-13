using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace TFWRMCP.Core.Tests;

public class DocsToolTests
{
    [Fact]
    public async Task List_docs_returns_the_pages_linked_from_the_games_home_page_once_each()
    {
        var game = new FakeGame();
        game.Docs["docs/home.md"] =
            "## General\n<columns>\n[Credits](docs/credits.md)\n[Debug](docs/scripting/debug.md)\n</columns>\n" +
            "## Functions\n<columns>\n[harvest()](functions/harvest)\n[Debug again](docs/scripting/debug.md)\n</columns>";
        using var s = new TestServer(game);

        var (isError, text) = await s.Call("list_docs");

        Assert.False(isError);
        var pages = JObject.Parse(text)["pages"]!.Select(p => ((string)p["page"]!, (string)p["title"]!)).ToArray();
        Assert.Equal(new[] { ("docs/credits.md", "Credits"), ("docs/scripting/debug.md", "Debug"), ("functions/harvest", "harvest()") }, pages);
    }

    [Fact]
    public async Task Read_doc_returns_the_page_text_the_game_renders()
    {
        var game = new FakeGame();
        game.Docs["docs/credits.md"] = "# Credits\nMade by someone.";
        using var s = new TestServer(game);

        var (isError, text) = await s.Call("read_doc", new JObject { ["page"] = "docs/credits.md" });

        Assert.False(isError);
        var result = JObject.Parse(text);
        Assert.Equal("docs/credits.md", (string)result["page"]!);
        Assert.Equal("# Credits\nMade by someone.", (string)result["content"]!);
    }

    [Fact]
    public async Task Read_doc_of_an_unknown_page_is_an_error()
    {
        using var s = new TestServer(new FakeGame());

        var (isError, text) = await s.Call("read_doc", new JObject { ["page"] = "docs/nope.md" });

        Assert.True(isError);
        Assert.Contains("docs/nope.md", text);
    }

    [Fact]
    public async Task Read_doc_requires_a_page()
    {
        using var s = new TestServer(new FakeGame());

        var (isError, text) = await s.Call("read_doc");

        Assert.True(isError);
        Assert.Contains("'page' is required", text);
    }
}

public class DocMarkupTests
{
    const string Home =
        "<unlock=grass>[{{@grass}}](unlocks/grass)\n</unlock><unlock=cactus>[{{@cactus}}](docs/unlocks/cactus.md)\n</unlock>" +
        "<dlc>**Mining**\n[Dig](functions/dig)\n</dlc>[Credits](docs/credits.md)";

    static string Render(string markdown, bool miningDlc = true) =>
        DocMarkup.Render(markdown, name => name == "grass", key => key.ToUpperInvariant(), miningDlc,
            section => section.StartsWith("itemblock") ? "hay: 5" : null);

    static string Render(bool miningDlc) => Render(Home, miningDlc);

    [Fact]
    public void Sections_for_locked_Unlocks_are_dropped_like_the_games_docs_window_does()
    {
        var text = Render(miningDlc: true);

        Assert.Contains("[GRASS](unlocks/grass)", text);
        Assert.DoesNotContain("cactus", text);
        Assert.Contains("[Credits](docs/credits.md)", text);
    }

    [Fact]
    public void Dlc_sections_are_kept_only_when_the_DLC_is_installed()
    {
        Assert.Contains("[Dig](functions/dig)", Render(miningDlc: true));
        Assert.DoesNotContain("Dig", Render(miningDlc: false));
    }

    [Fact]
    public void Code_examples_become_fenced_code_without_their_animation_settings()
    {
        var text = Render("Try:\r\n{{codeexample \r\n{\"show_code\": true}\r\n#SETUP\r\ndo_a_flip()\r\n#CODE\r\nfor i in range(5):\r\n    harvest()\r\n}}\r\nDone.");

        Assert.Equal("Try:\n```\nfor i in range(5):\n    harvest()\n```\nDone.", text);
    }

    [Fact]
    public void Other_sections_are_resolved_by_the_game_or_kept()
    {
        Assert.Equal("Cost: hay: 5 / {{output}}", Render("Cost: {{itemblock cost unlock plant}} / {{output}}"));
    }

    [Fact]
    public void Syntax_highlighted_code_from_tooltips_becomes_plain_backticks()
    {
        var text = Render("Call <font=\"FiraCode-Regular SDF\"><noparse></noparse><color=#E6C87CFF><noparse>plant</noparse></color><noparse>()</noparse></font> now.");

        Assert.Equal("Call `plant()` now.", text);
    }
}
