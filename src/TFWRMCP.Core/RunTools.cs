using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TFWRMCP;

/// <summary>Script CRUD and Run Tools.</summary>
public static partial class Tools
{
    const double DefaultTimeoutSeconds = 20;
    const double MaxTimeoutSeconds = 600;

    const string TimeoutDescription =
        "How long to wait for the Run to end, in seconds (default 20, at most 600). The call blocks that long, so " +
        "keep it below your MCP client's request timeout (30 seconds in many clients); to wait longer, call " +
        "wait_run again. When the Run is still running afterwards the result has status 'running' and the Run " +
        "keeps going in the game. 0 does not wait: it returns right away.";

    const string RunSummaryDescription =
        "inventory_delta (item name to net change since the Run began, non-zero items only: the farm's current " +
        "inventory, or its inventory when the Run ended, minus the inventory when it began, so it includes every " +
        "change: Script actions, the game's own production and consumption such as water, fertilizer and power, " +
        "Unlocks bought in the research menu and the cost of unlock() calls; absent when unknown), failed_calls " +
        "(function name to count, non-zero only: a plant, harvest, move, swap, use_item, place, dig, push, " +
        "unlock or hover call that returned False counts once; harvest() on an entity that is not harvestable " +
        "also counts as a failed harvest, because the game then returns True, clears the crop and yields " +
        "nothing; till() has no return value and is not counted), ticks (game steps the Run has executed, the " +
        "unit of get_tick_count()), seconds (real elapsed time), game_seconds (elapsed time on the game clock " +
        "that get_time() reads, of the real farm) and rates (item name to inventory_delta divided by " +
        "game_seconds, the net change per game second; absent when game_seconds is 0 or inventory_delta is " +
        "absent); ticks, seconds and game_seconds stop growing when the Run ends. Actions and items on the " +
        "temporary farm of simulate() and Leaderboard Runs do not count; Runs started by the human do. " +
        "parse_error results have none of these. Use them to notice a Script that spins without progress, " +
        "e.g. plant() failing on occupied tiles while items are consumed.";

    const string StopAfterSecondsDescription =
        "Stop the Run (like stop_run) once the game clock (the one get_time() reads) has advanced this many " +
        "seconds since it began; a number greater than 0. The deadline belongs to the Run, not to this call: " +
        "when the call returns 'running' first, the Run is still stopped on time and wait_run returns it. It is " +
        "dropped when the Run ends first. Combined with until_inventory and stop_when_met, whichever holds first " +
        "stops the Run. To measure production: stop_after_seconds=60, timeout_seconds=0, then wait_run until the " +
        "Run has ended and read rates.";

    const string UntilInventoryDescription =
        "Item name to amount, e.g. {\"carrot\": 600}: the inventory must hold at least that much of every listed " +
        "item. The wait then also ends as soon as this holds (or the Run ends, or the timeout passes), and the " +
        "result gains inventory (the whole inventory) and met (whether it holds).";

    const string StopWhenMetDescription =
        "With until_inventory: stop the Run (like stop_run) as soon as the inventory meets it. Default false.";

    static JObject WaitSchema(params (string Name, string Type, string Description, bool Required)[] properties) =>
        WithSchema(Schema(properties.Concat(new[]
            {
                ("timeout_seconds", "number", TimeoutDescription, false),
                ("until_inventory", "object", UntilInventoryDescription, false),
                ("stop_when_met", "boolean", StopWhenMetDescription, false),
            }).ToArray()),
            "until_inventory", new JObject { ["additionalProperties"] = new JObject { ["type"] = "number", ["minimum"] = 0 } });

    /// <summary>Reads until_inventory and stop_when_met; null when the Agent asked for no inventory condition.</summary>
    static async Task<InventoryGoal?> InventoryGoalFrom(JObject args, IGame game)
    {
        var stopWhenMet = OptionalBool(args, "stop_when_met");

        var token = args["until_inventory"];
        if (token == null || token.Type == JTokenType.Null)
            return stopWhenMet ? throw new ToolException("'stop_when_met' needs 'until_inventory'.") : null;
        if (token is not JObject { Count: > 0 } items)
            throw new ToolException("'until_inventory' must be an object with at least one item: {\"carrot\": 600}.");

        var atLeast = new Dictionary<string, double>();
        foreach (var item in items.Properties())
        {
            if (item.Value.Type is not (JTokenType.Integer or JTokenType.Float) || (double)item.Value < 0)
                throw new ToolException($"'until_inventory' amount for '{item.Name}' must be a number that is not negative.");
            atLeast[item.Name] = (double)item.Value;
        }

        var known = (await game.GetState(StateParts.Inventory)).Inventory;
        var unknown = atLeast.Keys.FirstOrDefault(item => !known.ContainsKey(item));
        if (unknown != null)
            throw new ToolException($"Unknown item '{unknown}' in 'until_inventory'. Items: {string.Join(", ", known.Keys)}.");
        return new InventoryGoal(atLeast, stopWhenMet);
    }

    /// <summary>Every wait parameter that run_script, wait_run and write_script with run accept.</summary>
    static async Task<(TimeSpan Timeout, InventoryGoal? Goal)> WaitArgs(JObject args, IGame game) =>
        (WaitTimeout(args), await InventoryGoalFrom(args, game));

    static TimeSpan WaitTimeout(JObject args)
    {
        var token = args["timeout_seconds"];
        if (token == null || token.Type == JTokenType.Null) return TimeSpan.FromSeconds(DefaultTimeoutSeconds);
        if (token.Type is not (JTokenType.Integer or JTokenType.Float))
            throw new ToolException("'timeout_seconds' must be a number.");
        var seconds = (double)token;
        if (double.IsNaN(seconds) || seconds < 0) throw new ToolException("'timeout_seconds' must not be negative.");
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxTimeoutSeconds));
    }

    static double? StopAfterSeconds(JObject args)
    {
        var token = args["stop_after_seconds"];
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token.Type is not (JTokenType.Integer or JTokenType.Float) || !((double)token > 0))
            throw new ToolException("'stop_after_seconds' must be a number greater than 0.");
        return (double)token;
    }

    static async Task<RunSnapshot> StartAndWait(IGame game, RunTracker runs, string name, TimeSpan timeout,
        InventoryGoal? goal, CancellationToken cancel, double? stopAfterSeconds = null)
    {
        var syntaxError = await game.StartRun(name, cancel);
        if (syntaxError != null) runs.RecordSyntaxError(name, syntaxError);
        else if (stopAfterSeconds is { } seconds) runs.StopAfter(seconds);
        return await runs.Wait(timeout, cancel, goal);
    }

    static IEnumerable<Tool> ScriptAndRunTools(IGame game, RunTracker runs) => new[]
    {
        new Tool(
            "list_scripts",
            "Lists the names of all Scripts (code windows) in the current Save.",
            Schema(),
            async (_, _) => new { scripts = await game.ListScripts() },
            Capabilities.Scripts),
        new Tool(
            "read_script",
            "Returns the code of a Script, including Scripts written by the human.",
            Schema(("name", "string", "Script name (case-sensitive).", true)),
            async (args, _) =>
            {
                var name = RequiredString(args, "name");
                return new { name, code = await game.ReadScript(name) };
            },
            Capabilities.Scripts),
        new Tool(
            "write_script",
            "Creates a Script or overwrites an existing one. It shows up in the game's code windows immediately and " +
            "is saved to disk. Names follow the game's rules; a name that differs from an existing window only by " +
            "case is rejected. A Script that is being run cannot be overwritten (stop the Run first, or pass " +
            "stop_current). Returns {name, created}; with stop_current and a Run in progress also stopped (the " +
            "stopped Run, as stop_run returns it); with run also run (the Run, as run_script returns it; parse_error " +
            "on a syntax error, the Script is still written). Use stop_current and run together to replace and " +
            "rerun a running Script in one call.",
            WaitSchema(
                ("name", "string", "Script name.", true),
                ("code", "string", "Full source code of the Script.", true),
                ("stop_current", "boolean", "Before writing, stop the Run in progress (like stop_run), whoever " +
                    "started it. Default false.", false),
                ("run", "boolean", "After writing, run the Script (like run_script; timeout_seconds, " +
                    "until_inventory and stop_when_met apply as there). With a Run in progress this needs " +
                    "stop_current, otherwise the call fails and writes nothing. Default false.", false)),
            async (args, cancel) =>
            {
                var name = RequiredString(args, "name");
                var code = RequiredString(args, "code");
                var stopCurrent = OptionalBool(args, "stop_current");
                var run = OptionalBool(args, "run");
                if ((stopCurrent || run) && game.UnavailableReason(Capabilities.Run) is { } reason)
                    throw new ToolException($"'stop_current' and 'run' are unavailable: capability '{Capabilities.Run}' " +
                        $"failed to resolve ({reason}).");
                var (timeout, goal) = run ? await WaitArgs(args, game) : default;
                if (run && !stopCurrent && runs.InProgress)
                    throw new ToolException("A Run is in progress; pass stop_current to stop it first. Nothing was written.");

                var result = new Dictionary<string, object?> { ["name"] = name };
                if (stopCurrent && runs.InProgress) result["stopped"] = await runs.StopByTool(StopTimeout, cancel);
                result["created"] = await game.WriteScript(name, code);
                if (run) result["run"] = await StartAndWait(game, runs, name, timeout, goal, cancel);
                return result;
            },
            Capabilities.Scripts),
        new Tool(
            "delete_script",
            "Deletes a Script: closes its code window and removes it from disk. A Script that is being run cannot " +
            "be deleted (stop the Run first).",
            Schema(("name", "string", "Script name (case-sensitive).", true)),
            async (args, _) =>
            {
                var name = RequiredString(args, "name");
                await game.DeleteScript(name);
                return new { deleted = name };
            },
            Capabilities.Scripts),
        new Tool(
            "run_script",
            "Runs a Script, exactly as if the human clicked run on its code window, and waits for the Run to end. " +
            "Fails when a Run is already in progress (that Run is left untouched). Returns the Run: status " +
            "(running, finished, error, stopped_by_tool, stopped_by_user or parse_error), the output (print and " +
            "quick_print, at most the newest 1000 entries; output_dropped counts older ones), error " +
            "(message, call stack and 1-based line/column; also for syntax errors, which return parse_error without " +
            "starting a Run) and, when the Script ran leaderboard_run(), leaderboard (reached_goal, average_seconds, " +
            "rank or null without Steam). A runtime error stops the Run automatically. The Leaderboard result page " +
            "is closed automatically. To run until the inventory reaches a target, pass until_inventory (and " +
            "stop_when_met to stop the Run then) instead of polling get_state; to run for a fixed game time, pass " +
            "stop_after_seconds. The Run also carries a summary: " +
            RunSummaryDescription,
            WaitSchema(
                ("name", "string", "Script name (case-sensitive).", true),
                ("stop_after_seconds", "number", StopAfterSecondsDescription, false)),
            async (args, cancel) =>
            {
                var name = RequiredString(args, "name");
                var stopAfterSeconds = StopAfterSeconds(args);
                var (timeout, goal) = await WaitArgs(args, game);
                return await StartAndWait(game, runs, name, timeout, goal, cancel, stopAfterSeconds);
            },
            Capabilities.Run),
        new Tool(
            "wait_run",
            "Waits again for the latest Run (see run_script) and returns it. When the Run has already ended it " +
            "returns its final result immediately. Takes the same until_inventory and stop_when_met as run_script, " +
            "and returns the same Run, including its summary: " + RunSummaryDescription,
            WaitSchema(),
            async (args, cancel) =>
            {
                if (!runs.HasRun) throw new ToolException("No Run has been started yet.");
                var (timeout, goal) = await WaitArgs(args, game);
                return await runs.Wait(timeout, cancel, goal);
            },
            Capabilities.Run),
        new Tool(
            "stop_run",
            "Stops the Run in progress, as if the stop button was clicked, and returns it with status " +
            "stopped_by_tool, with the same summary as run_script: " + RunSummaryDescription + " With no Run in progress it just returns the latest Run unchanged.",
            Schema(),
            async (_, cancel) => await runs.StopByTool(StopTimeout, cancel),
            Capabilities.Run),
    };
}
