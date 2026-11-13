using System;
using System.Collections.Generic;
using System.Linq;

namespace TFWRMCP;

/// <summary>The bottom-left notices of bought Unlocks; times are in seconds on any monotonic clock.</summary>
public sealed class UnlockToasts
{
    const double Hold = 5;
    const double Fade = 1;
    public const int Max = 10;

    readonly List<(string Text, double Shown)> toasts = new();

    public void Add(string text, double now)
    {
        toasts.Add((text, now));
        if (toasts.Count > Max) toasts.RemoveAt(0);
    }

    /// <summary>Language is the game's language code (OptionHolder "language"); level is the new Unlock level.</summary>
    public static string Text(string language, string unlock, int level) => (language, level) switch
    {
        ("ZH", 1) => $"已解锁 {unlock}",
        ("ZH", _) => $"{unlock} 升至 {level} 级",
        (_, 1) => $"Unlocked {unlock}",
        _ => $"{unlock} reached level {level}",
    };

    /// <summary>Oldest first; expired toasts are dropped.</summary>
    public List<(string Text, float Alpha)> Visible(double now)
    {
        toasts.RemoveAll(t => now - t.Shown >= Hold + Fade);
        return toasts.Select(t => (t.Text, (float)Math.Min(1, (Hold + Fade - (now - t.Shown)) / Fade))).ToList();
    }
}
