using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TFWRMCP;

public static partial class Tools
{
    const string SimulateUnlocksDescription =
        "Unlock name to level, e.g. {\"megafarm\": 3}: replaces the Save's level of each listed Unlock on the " +
        "temporary farm (a whole number from 0 to the Unlock's maximum; names as in get_state unlocks); Unlocks " +
        "not listed keep the Save's level. 0 removes the Save's level of that Unlock, but the game's base Unlocks " +
        "(grass, soil, harvest, ... ) are always there and cannot be switched off, so the temporary farm is never " +
        "blank. An unknown name or a level above the maximum fails the call and starts nothing.";

    const string SimulateItemsDescription =
        "Item name to amount, e.g. {\"power\": 0} or {\"pumpkin\": 2e7}: replaces the Save's amount of each " +
        "listed item on the temporary farm (a number that is not negative); items not listed keep the Save's " +
        "amount. An unknown item or a negative amount fails the call and starts nothing.";

    const string SimulateGlobalsDescription =
        "Variable name to value, e.g. {\"K\": 4, \"mode\": \"fast\", \"fast\": true}: defined in the Script's " +
        "global scope before it starts, so the Script reads them like its own globals and the same Script can " +
        "be compared with different parameters without being edited. Values must be numbers, strings or " +
        "booleans; anything else fails the call and starts nothing.";

    const string SimulateSeedDescription =
        "Random seed of the temporary farm, an integer. The same seed, Script and parameters give the same " +
        "simulation, so use it to remove randomness from an A/B comparison. Default -1 (any negative number): a " +
        "random seed.";

    const string SimulateWarmupDescription =
        "Game seconds the temporary farm runs before the summary starts counting (a number that is not " +
        "negative, default 0), so the Script can set up its farm first. The temporary farm runs for warmup_seconds " +
        "+ game_seconds in all; inventory_delta, failed_calls, rates and game_seconds then cover only the part " +
        "after the warmup (ticks and output still cover everything). Until the warmup is over a still-running " +
        "Run's summary counts from its start, and a Run that ends during the warmup has an empty summary.";

    static JObject SimulateSchema() => WithSchema(WithSchema(WithSchema(WithSchema(Schema(
            ("name", "string", "Script name (case-sensitive).", true),
            ("game_seconds", "number", "How long the temporary farm runs after the warmup, in game seconds (the " +
                "clock get_time() reads there, starting at 0); a number greater than 0.", true),
            ("warmup_seconds", "number", SimulateWarmupDescription, false),
            ("unlocks", "object", SimulateUnlocksDescription, false),
            ("items", "object", SimulateItemsDescription, false),
            ("globals", "object", SimulateGlobalsDescription, false),
            ("seed", "integer", SimulateSeedDescription, false),
            ("stop_current", "boolean", "Stop the Run in progress first (like stop_run), whoever started it. " +
                "Default false.", false),
            ("timeout_seconds", "number", TimeoutDescription, false)),
            "unlocks", new JObject { ["additionalProperties"] = new JObject { ["type"] = "integer", ["minimum"] = 0 } }),
            "items", new JObject { ["additionalProperties"] = new JObject { ["type"] = "number", ["minimum"] = 0 } }),
            "globals", new JObject { ["additionalProperties"] = new JObject { ["type"] = new JArray("number", "string", "boolean") } }),
            "warmup_seconds", new JObject { ["minimum"] = 0 });

    static bool IsNumber(JToken token) => token.Type is JTokenType.Integer or JTokenType.Float;

    /// <summary>Reads and checks every simulate_script parameter against the Save; throws before anything is started or stopped.</summary>
    static SimulationOptions SimulationOptionsFrom(JObject args, GameState state)
    {
        var options = new SimulationOptions
        {
            GameSeconds = args["game_seconds"] is { } seconds && IsNumber(seconds) && (double)seconds > 0 && double.IsFinite((double)seconds)
                ? (double)seconds
                : throw new ToolException("'game_seconds' is required and must be a number greater than 0."),
        };

        if (args["warmup_seconds"] is { Type: not JTokenType.Null } warmup)
            options.WarmupSeconds = IsNumber(warmup) && (double)warmup >= 0 && double.IsFinite((double)warmup)
                ? (double)warmup
                : throw new ToolException("'warmup_seconds' must be a number that is not negative.");

        if (args["seed"] is { Type: not JTokenType.Null } seed)
            options.Seed = seed is JValue { Type: JTokenType.Integer, Value: long value and >= int.MinValue and <= int.MaxValue }
                ? (int)value
                : throw new ToolException("'seed' must be an integer.");

        foreach (var (name, token) in Entries(args, "unlocks", "{\"megafarm\": 3}"))
        {
            var unlock = state.Unlocks.FirstOrDefault(u => u.Name == name)
                ?? throw new ToolException($"Unknown Unlock '{name}' in 'unlocks'. Unlocks: {string.Join(", ", state.Unlocks.Select(u => u.Name))}.");
            if (!IsNumber(token) || (double)token != Math.Floor((double)token) || (double)token < 0 || (double)token > unlock.MaxLevel)
                throw new ToolException($"'unlocks' level for '{name}' must be a whole number from 0 to {unlock.MaxLevel}.");
            options.Unlocks[name] = (int)(double)token;
        }

        foreach (var (name, token) in Entries(args, "items", "{\"power\": 0}"))
        {
            if (!state.Inventory.ContainsKey(name))
                throw new ToolException($"Unknown item '{name}' in 'items'. Items: {string.Join(", ", state.Inventory.Keys)}.");
            if (!IsNumber(token) || !((double)token >= 0) || double.IsInfinity((double)token))
                throw new ToolException($"'items' amount for '{name}' must be a number that is not negative.");
            options.Items[name] = (double)token;
        }

        foreach (var (name, token) in Entries(args, "globals", "{\"K\": 4}"))
            options.Globals[name] = token.Type switch
            {
                JTokenType.Integer or JTokenType.Float => (double)token,
                JTokenType.String => (string)token!,
                JTokenType.Boolean => (bool)token,
                _ => throw new ToolException($"'globals' value for '{name}' must be a number, a string or a boolean."),
            };
        return options;
    }

    static IEnumerable<(string Name, JToken Value)> Entries(JObject args, string parameter, string example)
    {
        var token = args[parameter];
        if (token == null || token.Type == JTokenType.Null) return Enumerable.Empty<(string, JToken)>();
        if (token is not JObject entries) throw new ToolException($"'{parameter}' must be an object: {example}.");
        return entries.Properties().Select(p => (p.Name, p.Value));
    }
}
