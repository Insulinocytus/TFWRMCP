using System.Linq;
using Xunit;

namespace TFWRMCP.Core.Tests;

public class UnlockToastsTests
{
    [Fact]
    public void A_toast_is_fully_visible_for_5_seconds_then_fades_out_over_1_second()
    {
        var toasts = new UnlockToasts();
        toasts.Add("Unlocked Carrots", now: 100);

        Assert.Equal(1f, toasts.Visible(104.9).Single().Alpha);
        Assert.Equal(0.5f, toasts.Visible(105.5).Single().Alpha, 3);
        Assert.Empty(toasts.Visible(106));
    }

    [Fact]
    public void An_eleventh_toast_pushes_out_the_oldest()
    {
        var toasts = new UnlockToasts();
        for (var i = 1; i <= 11; i++) toasts.Add("t" + i, now: i * 0.1);

        var shown = toasts.Visible(1.2).Select(t => t.Text).ToList();

        Assert.Equal(10, shown.Count);
        Assert.Equal("t2", shown.First());
        Assert.Equal("t11", shown.Last());
    }

    [Theory]
    [InlineData("ZH", 1, "已解锁 胡萝卜")]
    [InlineData("ZH", 3, "胡萝卜 升至 3 级")]
    [InlineData("EN", 1, "Unlocked 胡萝卜")]
    [InlineData("EN", 3, "胡萝卜 reached level 3")]
    [InlineData("DE", 3, "胡萝卜 reached level 3")]
    public void The_text_follows_the_game_language_and_tells_first_purchase_from_a_later_level(string language, int level, string expected)
    {
        Assert.Equal(expected, UnlockToasts.Text(language, "胡萝卜", level));
    }
}
