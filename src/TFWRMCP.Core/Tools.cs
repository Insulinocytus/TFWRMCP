using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TFWRMCP;

/// <summary>The Tool catalogue.</summary>
public static partial class Tools
{
    internal static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Adds JSON Schema keywords to one property of a <see cref="Schema"/>.</summary>
    static JObject WithSchema(JObject schema, string property, JObject extra)
    {
        ((JObject)schema["properties"]![property]!).Merge(extra);
        return schema;
    }

    static readonly (string Name, StateParts Part)[] StateFields =
    {
        ("save", StateParts.Save), ("inventory", StateParts.Inventory), ("unlocks", StateParts.Unlocks),
        ("world_size", StateParts.WorldSize), ("drones", StateParts.Drones), ("tiles", StateParts.Tiles),
        ("run", StateParts.Run),
    };

    static StateParts RequestedStateParts(JObject args)
    {
        var token = args["fields"];
        if (token == null || token.Type == JTokenType.Null) return StateParts.All;
        var options = string.Join(", ", StateFields.Select(f => f.Name));
        if (token is not JArray { Count: > 0 } names)
            throw new ToolException($"'fields' must be a non-empty array of field names: {options}.");
        var parts = (StateParts)0;
        foreach (var name in names)
        {
            var field = name is JValue { Value: string text } ? StateFields.FirstOrDefault(f => f.Name == text) : default;
            if (field.Name == null) throw new ToolException($"Unknown field '{name}'. Fields: {options}.");
            parts |= field.Part;
        }
        return parts;
    }

    static JObject Schema(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var schema = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };
        var required = new JArray();
        foreach (var (name, type, description, isRequired) in properties)
        {
            ((JObject)schema["properties"]!)[name] = new JObject { ["type"] = type, ["description"] = description };
            if (isRequired) required.Add(name);
        }
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    static string? OptionalString(JObject args, string name)
    {
        var value = args[name];
        if (value == null || value.Type == JTokenType.Null) return null;
        return value is JValue { Value: string text } ? text : throw new ToolException($"'{name}' must be a string.");
    }

    static string RequiredString(JObject args, string name) =>
        OptionalString(args, name) ?? throw new ToolException($"'{name}' is required.");

    static async Task<object?> LoadAndWait(IGame game, RunTracker runs, string name, CancellationToken cancel)
    {
        await runs.ResetForReload(StopTimeout, cancel);
        await game.LoadSave(name, cancel);
        await Readiness.Ensure(game, cancel);
        return new { save = name };
    }

    public static IReadOnlyList<Tool> Create(IGame game, Action<string> log)
    {
        var runs = new RunTracker(game, log);
        return Catalogue(game, runs).Concat(UnlockAndDocsTools(game)).Concat(ScriptAndRunTools(game, runs)).ToArray();
    }

    static Tool[] Catalogue(IGame game, RunTracker runs) => new[]
    {
        new Tool(
            "get_state",
            "Returns a snapshot of the game as the player sees it: current Save name, inventory, every Unlock " +
            "with its level, the cost of the next level (null at max level) and its prerequisites (parent: Unlock to own first; " +
            "also_requires: {name, level}; each omitted when there is none), world size, drone positions, " +
            "the latest Run (run: status idle, running, finished, error, stopped_by_tool, stopped_by_user or " +
            "parse_error, plus script, error, Leaderboard result and the run summary: " + RunSummaryDescription +
            " Use wait_run for its output), and the grid as tiles[y][x]: " +
            "the outer array index is y (south to north), the inner index is x (west to east), so tiles[0][0] is (0, 0) " +
            "and x grows east, y grows north. Each tile has the ground type, plus: the entity type unless the tile is " +
            "empty; depth unless it is 0; harvestable: true when the entity can be harvested (as can_harvest() says); " +
            "water (0-1) unless it is 0; grown (0-1) for a growing entity that is not yet mature. " +
            "The full snapshot is large; pass fields to get only what you need.",
            WithSchema(
                Schema(("fields", "array", "Top-level fields to return (default: all): save, inventory, unlocks, " +
                    "world_size, drones, tiles, run. Fields not listed are not read from the game.", false)),
                "fields",
                new JObject { ["items"] = new JObject { ["type"] = "string", ["enum"] = new JArray(StateFields.Select(f => f.Name)) } }),
            async (args, _) =>
            {
                var parts = RequestedStateParts(args);
                var state = await game.GetState(parts);
                var result = new Dictionary<string, object?>();
                if (parts.HasFlag(StateParts.Save)) result["save"] = state.Save;
                if (parts.HasFlag(StateParts.Inventory)) result["inventory"] = state.Inventory;
                if (parts.HasFlag(StateParts.Unlocks)) result["unlocks"] = state.Unlocks;
                if (parts.HasFlag(StateParts.WorldSize)) result["world_size"] = state.WorldSize;
                if (parts.HasFlag(StateParts.Drones)) result["drones"] = state.Drones;
                if (parts.HasFlag(StateParts.Tiles)) result["tiles"] = state.Tiles;
                if (parts.HasFlag(StateParts.Run)) result["run"] = await runs.Snapshot(withOutput: false);
                return result;
            },
            Capabilities.State),
        new Tool(
            "list_saves",
            "Lists every Save and which one is currently loaded.",
            Schema(),
            async (_, _) => await game.ListSaves(),
            Capabilities.Saves),
        new Tool(
            "create_save",
            "Creates a new Save and loads it, replacing the current game state with a fresh one. The current Save " +
            "is saved first. Returns once the new Save is loaded and playable.",
            Schema(("name", "string", "Name of the new Save. Omit to use the game's default name (SaveN).", false)),
            async (args, cancel) =>
            {
                var name = OptionalString(args, "name");
                if (name == null) name = await game.GenerateSaveName();
                else if (!await game.IsValidSaveName(name))
                    throw new ToolException($"Invalid Save name '{name}': a Save name must not be empty, start with '.', " +
                        "end with '.' or a space, contain any of < > : \" / \\ | ? * or control characters, or start " +
                        "with a reserved Windows device name (CON, PRN, AUX, NUL, COM1-9, LPT1-9).");
                if ((await game.ListSaves()).Saves.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase)))
                    throw new ToolException($"A Save named '{name}' already exists.");

                cancel.ThrowIfCancellationRequested();
                await game.CreateSave(name);
                return await LoadAndWait(game, runs, name, cancel);
            },
            Capabilities.Saves),
        new Tool(
            "load_save",
            "Saves the current Save, then loads another one. Returns once the loaded Save is playable.",
            Schema(("name", "string", "Name of an existing Save (see list_saves).", true)),
            async (args, cancel) =>
            {
                var name = RequiredString(args, "name");
                var saves = (await game.ListSaves()).Saves;
                var match = saves.FirstOrDefault(s => s == name) ??
                            saves.FirstOrDefault(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                    throw new ToolException($"No Save named '{name}'. Existing Saves: {string.Join(", ", saves)}.");
                return await LoadAndWait(game, runs, match, cancel);
            },
            Capabilities.Saves),
        new Tool(
            "save_game",
            "Writes the current Save (progress and Scripts) to disk.",
            Schema(),
            async (_, _) =>
            {
                await game.SaveGame();
                return new { saved = true };
            },
            Capabilities.Saves),
    };
}
