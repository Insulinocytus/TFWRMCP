using System;
using System.Collections.Generic;
using System.Linq;

namespace TFWRMCP;

/// <summary>A rectangle in the game's UI container units, y growing upwards.</summary>
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

/// <summary>What gets arranged: a code window now, a dock group later (named by whichever window sorts it).</summary>
public readonly struct Unit
{
    public Unit(string name, float width, float height, bool running, bool focused)
    {
        Name = name;
        Width = width;
        Height = height;
        Running = running;
        Focused = focused;
    }

    public string Name { get; }
    public float Width { get; }
    public float Height { get; }
    public bool Running { get; }
    public bool Focused { get; }

    public bool Expanded => Layout.IsExpanded(Running, Focused);
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

/// <summary>Decides where code windows go around the farm; pure data in, pure data out.</summary>
public static class Layout
{
    public const float Gap = 20;

    /// <summary>Windows that take part in a Run or have input focus are shown open; the rest are folded away.</summary>
    public static bool IsExpanded(bool running, bool focused) => running || focused;

    /// <summary>
    /// Expanded units stack in a left column and the others in a right column, each sorted by name, tops on the farm's top,
    /// a <see cref="Gap"/> between columns and farm and between units. The columns may run past the farm's bottom.
    /// </summary>
    public static List<Placement> Arrange(Box farm, IEnumerable<Unit> units)
    {
        var placed = new List<Placement>();
        foreach (var column in units.GroupBy(u => u.Expanded))
        {
            var top = farm.Top;
            foreach (var unit in column.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase))
            {
                placed.Add(new Placement(unit.Name, column.Key ? farm.Left - Gap - unit.Width : farm.Right + Gap, top));
                top -= unit.Height + Gap;
            }
        }

        return placed;
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
