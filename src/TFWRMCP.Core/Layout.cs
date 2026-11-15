using System;
using System.Collections.Generic;
using System.Linq;

namespace TFWRMCP;

/// <summary>A rectangle, y growing upwards.</summary>
public readonly struct Box
{
    public Box(float left, float top, float right, float bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public float Left { get; }
    public float Top { get; }
    public float Right { get; }
    public float Bottom { get; }
}

/// <summary>What gets arranged: a code window or a dock group (see <see cref="Layout.Group"/>).</summary>
public readonly struct Unit
{
    public Unit(string name, float width, float height, bool running)
    {
        Name = name;
        Width = width;
        Height = height;
        Running = running;
    }

    public string Name { get; }
    public float Width { get; }
    public float Height { get; }

    /// <summary>The window takes part in a Run; such units go to the top of the column.</summary>
    public bool Running { get; }
}

/// <summary>Where a <see cref="Unit"/>'s top-left corner goes.</summary>
public readonly struct Placement
{
    public Placement(string name, float left, float top)
    {
        Name = name;
        Left = left;
        Top = top;
    }

    public string Name { get; }
    public float Left { get; }
    public float Top { get; }
}

/// <summary>Decides where code windows go on the screen; pure data in, pure data out.</summary>
public static class Layout
{
    public const float Gap = 20;

    /// <summary>
    /// Docked windows, top one first, as one unit: named by the top window (which is what it sorts by and what moves), as wide
    /// as the widest member and as tall as all of them together, running when any member is.
    /// </summary>
    public static Unit Group(IReadOnlyList<Unit> topFirst) => new(
        topFirst[0].Name, topFirst.Max(u => u.Width), topFirst.Sum(u => u.Height), topFirst.Any(u => u.Running));

    /// <summary>
    /// Stacks the units in one column hanging <see cref="Gap"/> left of <paramref name="right"/> and <see cref="Gap"/> below
    /// <paramref name="top"/>, running ones first, each part sorted by name, with no space between units; the column runs
    /// past the bottom of the screen when it has to.
    /// </summary>
    public static List<Placement> Arrange(float right, float top, IEnumerable<Unit> units)
    {
        var placed = new List<Placement>();
        var y = top - Gap;
        foreach (var unit in units.OrderByDescending(u => u.Running).ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase))
        {
            placed.Add(new Placement(unit.Name, right - Gap - unit.Width, y));
            y -= unit.Height;
        }

        return placed;
    }

    /// <summary>
    /// The middle of the screen clear of the column <see cref="Arrange"/> makes, with the same width taken off the left so
    /// that what is centered in it stays at the screen's center.
    /// </summary>
    public static Box Room(Box screen, IEnumerable<Unit> units)
    {
        var margin = units.Select(u => u.Width + 2 * Gap).DefaultIfEmpty(0).Max();
        return new Box(screen.Left + margin, screen.Top, screen.Right - margin, screen.Bottom);
    }
}

/// <summary>Smooth moves; times are in seconds on any monotonic clock.</summary>
public static class Tween
{
    /// <summary>Value of a move from <paramref name="from"/> to <paramref name="to"/> that starts at <paramref name="start"/>; exact at both ends.</summary>
    public static float EaseOutExpo(float from, float to, double start, double duration, double now)
    {
        var t = (now - start) / duration;
        if (t <= 0) return from;
        if (t >= 1) return to;
        return from + (to - from) * (float)(1 - Math.Pow(2, -10 * t));
    }
}
