using System.Collections.Generic;

namespace TFWRMCP;

public sealed class Vec2
{
    public int X { get; set; }
    public int Y { get; set; }
}

public sealed class UnlockState
{
    public string Name { get; set; } = "";
    public int Level { get; set; }
    public int MaxLevel { get; set; }

    /// <summary>Cost of the next level by item name; null at max level.</summary>
    public Dictionary<string, double>? NextCost { get; set; }
}

public sealed class DroneState
{
    public int Id { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Depth { get; set; }
}

public sealed class TileState
{
    public int X { get; set; }
    public int Y { get; set; }

    /// <summary>Entity name, null when the tile is empty.</summary>
    public string? Entity { get; set; }
    public string Ground { get; set; } = "";
    public int Depth { get; set; }
}

public sealed class RunState
{
    public const string Idle = "idle";
    public const string Running = "running";

    public string Status { get; set; } = Idle;
}

public sealed class GameState
{
    public string Save { get; set; } = "";
    public Dictionary<string, double> Inventory { get; set; } = new();
    public List<UnlockState> Unlocks { get; } = new();
    public Vec2 WorldSize { get; set; } = new();
    public List<DroneState> Drones { get; } = new();
    public List<TileState> Tiles { get; } = new();
    public RunState Run { get; set; } = new();
}
