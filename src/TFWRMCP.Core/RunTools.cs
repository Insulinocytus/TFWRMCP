using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TFWRMCP;

/// <summary>Script CRUD and Run Tools.</summary>
public static class RunTools
{
    const double DefaultTimeoutSeconds = 30;
    const double MaxTimeoutSeconds = 600;
    static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    const string TimeoutDescription =
        "How long to wait for the Run to end, in seconds (default 30, at most 600). When it is still running " +
        "afterwards the result has status 'running' and the Run keeps going in the game.";

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

    static string String(JObject args, string name) =>
        args[name] is JValue { Type: JTokenType.String } value ? (string)value! : throw new ToolException($"'{name}' is required and must be a string.");

    static TimeSpan Timeout(JObject args)
    {
        var token = args["timeout_seconds"];
        if (token == null || token.Type == JTokenType.Null) return TimeSpan.FromSeconds(DefaultTimeoutSeconds);
        if (token.Type is not (JTokenType.Integer or JTokenType.Float))
            throw new ToolException("'timeout_seconds' must be a number.");
        var seconds = (double)token;
        if (double.IsNaN(seconds) || seconds < 0) throw new ToolException("'timeout_seconds' must not be negative.");
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxTimeoutSeconds));
    }

    public static IEnumerable<Tool> Create(IGame game, RunTracker runs) => new[]
    {
        new Tool(
            "list_scripts",
            "Lists the names of all Scripts (code windows) in the current Save.",
            Schema(),
            async _ => new { scripts = await game.ListScripts() },
            Capabilities.Scripts),
        new Tool(
            "read_script",
            "Returns the code of a Script, including Scripts written by the human.",
            Schema(("name", "string", "Script name (case-sensitive).", true)),
            async args =>
            {
                var name = String(args, "name");
                return new { name, code = await game.ReadScript(name) };
            },
            Capabilities.Scripts),
        new Tool(
            "write_script",
            "Creates a Script or overwrites an existing one. It shows up in the game's code windows immediately and " +
            "is saved to disk. Names follow the game's rules; a name that differs from an existing window only by " +
            "case is rejected. A Script that is being run cannot be overwritten (stop the Run first).",
            Schema(("name", "string", "Script name.", true), ("code", "string", "Full source code of the Script.", true)),
            async args =>
            {
                var name = String(args, "name");
                var code = String(args, "code");
                return new { name, created = await game.WriteScript(name, code) };
            },
            Capabilities.Scripts),
        new Tool(
            "delete_script",
            "Deletes a Script: closes its code window and removes it from disk. A Script that is being run cannot " +
            "be deleted (stop the Run first).",
            Schema(("name", "string", "Script name (case-sensitive).", true)),
            async args =>
            {
                var name = String(args, "name");
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
            "is closed automatically.",
            Schema(("name", "string", "Script name (case-sensitive).", true),
                ("timeout_seconds", "number", TimeoutDescription, false)),
            async args =>
            {
                var name = String(args, "name");
                var timeout = Timeout(args);
                var syntaxError = await game.StartRun(name);
                if (syntaxError == null) return await runs.Wait(timeout);
                runs.RecordSyntaxError(name, syntaxError);
                return runs.Snapshot(withOutput: true);
            },
            Capabilities.Run),
        new Tool(
            "wait_run",
            "Waits again for the latest Run (see run_script) and returns it. When the Run has already ended it " +
            "returns its final result immediately.",
            Schema(("timeout_seconds", "number", TimeoutDescription, false)),
            async args =>
            {
                var timeout = Timeout(args);
                if (!runs.HasRun) throw new ToolException("No Run has been started yet.");
                return await runs.Wait(timeout);
            },
            Capabilities.Run),
        new Tool(
            "stop_run",
            "Stops the Run in progress, as if the stop button was clicked, and returns it with status " +
            "stopped_by_tool. With no Run in progress it just returns the latest Run unchanged.",
            Schema(),
            async _ => await runs.StopByTool(StopTimeout),
            Capabilities.Run),
    };
}
