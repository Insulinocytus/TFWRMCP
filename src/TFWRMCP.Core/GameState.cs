using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace TFWRMCP;

public sealed class Vec2
{
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>An Unlock's extra purchase requirement: another Unlock at a minimum level.</summary>
public sealed class UnlockRequirement
{
    public string Name { get; set; } = "";
    public int Level { get; set; }
}

public sealed class UnlockState
{
    public string Name { get; set; } = "";
    public int Level { get; set; }
    public int MaxLevel { get; set; }

    /// <summary>Unlock that must be owned first; omitted from JSON when there is none.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Parent { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public UnlockRequirement? AlsoRequires { get; set; }

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

/// <summary>
/// One tile; in JSON a missing entity means the tile is empty, a missing depth means 0, a missing harvestable means
/// false, a missing water means 0 and a missing grown means the entity is fully grown or has no growth progress.
/// </summary>
public sealed class TileState
{
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Entity { get; set; }

    public string Ground { get; set; } = "";

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public int Depth { get; set; }

    /// <summary>Same test as the Script's can_harvest().</summary>
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public bool Harvestable { get; set; }

    /// <summary>Water level 0-1, rounded to 2 decimals.</summary>
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public double Water { get; set; }

    /// <summary>Growth progress 0-1, rounded to 2 decimals; only for a growing entity that is not yet mature.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? Grown { get; set; }
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
