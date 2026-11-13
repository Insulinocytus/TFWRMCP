using System;
using System.Collections.Generic;
using Newtonsoft.Json;

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

/// <summary>One tile; in JSON a missing entity means the tile is empty and a missing depth means 0.</summary>
public sealed class TileState
{
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Entity { get; set; }

    public string Ground { get; set; } = "";

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public int Depth { get; set; }
}

/// <summary>The top-level parts of get_state; <see cref="Run"/> comes from the Run tracker, not from the game.</summary>
[Flags]
public enum StateParts
{
    Save = 1,
    Inventory = 2,
    Unlocks = 4,
    WorldSize = 8,
    Drones = 16,
    Tiles = 32,
    Run = 64,
    All = 127,
}

public sealed class GameState
{
    public string Save { get; set; } = "";
    public Dictionary<string, double> Inventory { get; set; } = new();
    public List<UnlockState> Unlocks { get; } = new();
    public Vec2 WorldSize { get; set; } = new();
    public List<DroneState> Drones { get; } = new();

    /// <summary>Indexed [y][x]; x grows east and y grows north, like the game's coordinates.</summary>
    public List<List<TileState>> Tiles { get; } = new();
}
