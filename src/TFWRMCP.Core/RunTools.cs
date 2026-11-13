using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace TFWRMCP;

/// <summary>Script CRUD and Run Tools.</summary>
public static partial class Tools
{
    const double DefaultTimeoutSeconds = 30;
    const double MaxTimeoutSeconds = 600;

    const string TimeoutDescription =
        "How long to wait for the Run to end, in seconds (default 30, at most 600). When it is still running " +
        "afterwards the result has status 'running' and the Run keeps going in the game.";

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
            "case is rejected. A Script that is being run cannot be overwritten (stop the Run first).",
            Schema(("name", "string", "Script name.", true), ("code", "string", "Full source code of the Script.", true)),
            async (args, _) =>
            {
                var name = RequiredString(args, "name");
                var code = RequiredString(args, "code");
                return new { name, created = await game.WriteScript(name, code) };
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
            "is closed automatically.",
            Schema(("name", "string", "Script name (case-sensitive).", true),
                ("timeout_seconds", "number", TimeoutDescription, false)),
            async (args, cancel) =>
            {
                var name = RequiredString(args, "name");
                var timeout = WaitTimeout(args);
                var syntaxError = await game.StartRun(name, cancel);
                if (syntaxError == null) return await runs.Wait(timeout, cancel);
                runs.RecordSyntaxError(name, syntaxError);
                return runs.Snapshot(withOutput: true);
            },
            Capabilities.Run),
        new Tool(
            "wait_run",
            "Waits again for the latest Run (see run_script) and returns it. When the Run has already ended it " +
            "returns its final result immediately.",
            Schema(("timeout_seconds", "number", TimeoutDescription, false)),
            async (args, cancel) =>
            {
                var timeout = WaitTimeout(args);
                if (!runs.HasRun) throw new ToolException("No Run has been started yet.");
                return await runs.Wait(timeout, cancel);
            },
            Capabilities.Run),
        new Tool(
            "stop_run",
            "Stops the Run in progress, as if the stop button was clicked, and returns it with status " +
            "stopped_by_tool. With no Run in progress it just returns the latest Run unchanged.",
            Schema(),
            async (_, cancel) => await runs.StopByTool(StopTimeout, cancel),
            Capabilities.Run),
    };
}
