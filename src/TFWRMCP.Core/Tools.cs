using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TFWRMCP;

/// <summary>The Tool catalogue; each ticket adds its Tools here.</summary>
public static partial class Tools
{
    static JObject NoArguments() => new()
    {
        ["type"] = "object",
        ["properties"] = new JObject(),
        ["additionalProperties"] = false,
    };

    static JObject Schema(string? property = null, string? description = null, bool required = false)
    {
        var schema = NoArguments();
        if (property == null) return schema;
        schema["properties"] = new JObject { [property] = new JObject { ["type"] = "string", ["description"] = description } };
        if (required) schema["required"] = new JArray(property);
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

    static async Task<object?> LoadAndWait(IGame game, RunTracker runs, string name)
    {
        await runs.ResetForReload(TimeSpan.FromSeconds(10));
        await game.LoadSave(name);
        await Readiness.Ensure(game);
        return new { save = name };
    }

    public static IReadOnlyList<Tool> Create(IGame game)
    {
        var runs = new RunTracker(game);
        return Catalogue(game, runs).Concat(RunTools.Create(game, runs)).ToArray();
    }

    static Tool[] Catalogue(IGame game, RunTracker runs) => new[]
    {
        new Tool(
            "get_state",
            "Returns a snapshot of the game as the player sees it: current Save name, inventory, every Unlock " +
            "with its level and the cost of the next level (null at max level), world size, drone positions, " +
            "the latest Run (run: status idle, running, finished, error, stopped_by_tool, stopped_by_user or " +
            "parse_error, plus script, error and Leaderboard result; use wait_run for its output), and the grid as tiles[y][x] (x grows east, y grows " +
            "north). Each tile has the ground type, plus the entity type unless the tile is empty and the " +
            "depth unless it is 0.",
            NoArguments(),
            async _ =>
            {
                var state = await game.GetState();
                state.Run = runs.Snapshot(withOutput: false);
                return state;
            },
            Capabilities.State),
        new Tool(
            "list_saves",
            "Lists every Save and which one is currently loaded.",
            NoArguments(),
            async _ => await game.ListSaves(),
            Capabilities.Saves),
        new Tool(
            "create_save",
            "Creates a new Save and loads it, replacing the current game state with a fresh one. The current Save " +
            "is saved first. Returns once the new Save is loaded and playable.",
            Schema("name", "Name of the new Save. Omit to use the game's default name (SaveN)."),
            async args =>
            {
                var name = OptionalString(args, "name");
                if (name == null) name = await game.GenerateSaveName();
                else if (!await game.IsValidSaveName(name))
                    throw new ToolException($"Invalid Save name '{name}': a Save name must not be empty, start with '.', " +
                        "end with '.' or a space, contain any of < > : \" / \\ | ? * or control characters, or start " +
                        "with a reserved Windows device name (CON, PRN, AUX, NUL, COM1-9, LPT1-9).");
                if ((await game.ListSaves()).Saves.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase)))
                    throw new ToolException($"A Save named '{name}' already exists.");

                await game.CreateSave(name);
                return await LoadAndWait(game, runs, name);
            },
            Capabilities.Saves),
        new Tool(
            "load_save",
            "Saves the current Save, then loads another one. Returns once the loaded Save is playable.",
            Schema("name", "Name of an existing Save (see list_saves).", required: true),
            async args =>
            {
                var name = RequiredString(args, "name");
                var saves = (await game.ListSaves()).Saves;
                var match = saves.FirstOrDefault(s => s == name) ??
                            saves.FirstOrDefault(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                    throw new ToolException($"No Save named '{name}'. Existing Saves: {string.Join(", ", saves)}.");
                return await LoadAndWait(game, runs, match);
            },
            Capabilities.Saves),
        new Tool(
            "save_game",
            "Writes the current Save (progress and Scripts) to disk.",
            NoArguments(),
            async _ =>
            {
                await game.SaveGame();
                return new { saved = true };
            },
            Capabilities.Saves),
    }.Concat(UnlockAndDocsTools(game)).ToArray();
}
