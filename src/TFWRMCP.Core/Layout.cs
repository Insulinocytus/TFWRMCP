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

    /// <summary>The window takes part in a Run; such units go to the left column.</summary>
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
    /// Running units stack in a column <see cref="Gap"/> from the screen's left edge and the others in one <see cref="Gap"/>
    /// from its right edge, each sorted by name with no space between units and centered vertically on the
    /// screen; a column taller than the screen starts <see cref="Gap"/> below its top and runs past its bottom.
    /// </summary>
    public static List<Placement> Arrange(Box screen, IEnumerable<Unit> units)
    {
        var placed = new List<Placement>();
        foreach (var column in units.GroupBy(u => u.Running))
        {
            var height = column.Sum(u => u.Height);
            var top = height > screen.Top - screen.Bottom - 2 * Gap ? screen.Top - Gap : (screen.Top + screen.Bottom + height) / 2;
            foreach (var unit in column.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase))
            {
                placed.Add(new Placement(unit.Name, column.Key ? screen.Left + Gap : screen.Right - Gap - unit.Width, top));
                top -= unit.Height;
            }
        }

        return placed;
    }

    /// <summary>The part of the screen between the columns <see cref="Arrange"/> makes, a <see cref="Gap"/> clear of each.</summary>
    public static Box Between(Box screen, IEnumerable<Unit> units)
    {
        float left = screen.Left, right = screen.Right;
        foreach (var unit in units)
        {
            if (unit.Running) left = Math.Max(left, screen.Left + unit.Width + 2 * Gap);
            else right = Math.Min(right, screen.Right - unit.Width - 2 * Gap);
        }

        return new Box(left, screen.Top, right, screen.Bottom);
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
