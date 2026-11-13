using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TFWRMCP;

public sealed class DocLink
{
    public string Page { get; }
    public string Title { get; }

    public DocLink(string page, string title)
    {
        Page = page;
        Title = title;
    }
}

/// <summary>The text-level markup the game's docs window resolves before drawing a page (MarkdownText.Setup).</summary>
public static class DocMarkup
{
    static readonly Regex Dlc = new(@"<dlc>(.*?)</dlc>", RegexOptions.Singleline);
    static readonly Regex Template = new(@"\{\{@(.*?)\}\}");
    static readonly Regex UnlockSection = new(@"<unlock=([^>]*)>(.*?)</unlock>", RegexOptions.Singleline);
    static readonly Regex Link = new(@"(?<!!)\[([^\]\n]*)\]\(([^)\s]+)\)");
    static readonly Regex CustomSection = new(@"\{\{(?!@)([\s\S]*?)\}\}");
    // The game's tooltips carry syntax-highlighted code as rich text; plain backticks read better for an Agent.
    static readonly Regex HighlightedCode = new("<font=\"FiraCode-Regular SDF\">(.*?)</font>", RegexOptions.Singleline);
    static readonly Regex RichTextTag = new("</?(noparse|color(=[^>]*)?)>");

    /// <summary>
    /// Applies &lt;dlc&gt; sections, {{@key}} texts, &lt;unlock=X&gt; sections and code examples (as fenced code, without
    /// the animation settings) like the game; other {{...}} sections go to <paramref name="section"/> (null keeps them),
    /// other markup stays.
    /// </summary>
    public static string Render(string markdown, Func<string, bool> isUnlocked, Func<string, string> localize,
        bool miningDlcInstalled, Func<string, string?> section)
    {
        var text = markdown.Replace("\r\n", "\n");
        text = Dlc.Replace(text, m => miningDlcInstalled ? m.Groups[1].Value : "");
        text = Template.Replace(text, m => localize(m.Groups[1].Value));
        text = UnlockSection.Replace(text, m => isUnlocked(m.Groups[1].Value) ? m.Groups[2].Value : "");
        text = CustomSection.Replace(text, m =>
        {
            var body = m.Groups[1].Value;
            var marker = body.IndexOf("#CODE", StringComparison.Ordinal);
            if (body.StartsWith("codeexample") && marker >= 0)
                return "```\n" + body.Substring(marker + "#CODE".Length).Trim('\n').TrimEnd() + "\n```";
            return section(body) ?? m.Value;
        });
        return HighlightedCode.Replace(text, m => "`" + RichTextTag.Replace(m.Groups[1].Value, "") + "`");
    }

    /// <summary>Markdown links in order of appearance, once per target page.</summary>
    public static IReadOnlyList<DocLink> Links(string markdown) =>
        Link.Matches(markdown)
            .Select(m => new DocLink(m.Groups[2].Value, m.Groups[1].Value))
            .GroupBy(l => l.Page)
            .Select(g => g.First())
            .ToList();
}
