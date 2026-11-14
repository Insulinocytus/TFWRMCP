using System.Collections.Generic;

namespace TFWRMCP;

/// <summary>What the game reports about Runs; see <see cref="IGame.RunEvents"/>. Ordering within a Run is Started, Output/ActionCalled/WarmupEnded/Errored/LeaderboardFinished in any order, Ended.</summary>
public abstract class RunEvent { }

/// <summary>
/// A Run began, whoever started it. Output of earlier Runs is discarded. <paramref name="simulated"/> marks a simulated
/// Run on a temporary farm (started by <see cref="IGame.StartSimulatedRun"/>): <see cref="Inventory"/> is then that farm's.
/// </summary>
public sealed class RunStarted : RunEvent
{
    public string Script { get; }

    /// <summary>The whole inventory of the farm the Run is on when it began.</summary>
    public Dictionary<string, double> Inventory { get; }

    public bool Simulated { get; }

    public RunStarted(string script, Dictionary<string, double> inventory, bool simulated = false)
    {
        Script = script;
        Inventory = inventory;
        Simulated = simulated;
    }
}

/// <summary>
/// The Script called an action (plant, harvest, move, swap, use_item, place, dig, push, unlock or hover), one event per
/// call. <paramref name="failed"/> is set when it returned False, or was harvest() on an entity that is not harvestable
/// (the game then returns True, clears the crop and yields nothing). Actions on the temporary farm of simulate() and
/// Leaderboard Runs are never reported; those of a simulated Run started by <see cref="IGame.StartSimulatedRun"/> are.
/// </summary>
public sealed class ActionCalled : RunEvent
{
    public string Function { get; }
    public bool Failed { get; }
    public ActionCalled(string function, bool failed)
    {
        Function = function;
        Failed = failed;
    }
}

/// <summary>
/// The warmup of a simulated Run is over: <see cref="Inventory"/> and <see cref="Seconds"/> are the temporary farm's
/// whole inventory and clock at that moment. <see cref="RunTracker"/> counts the Run's summary from here on.
/// </summary>
public sealed class WarmupEnded : RunEvent
{
    public Dictionary<string, double> Inventory { get; }
    public double Seconds { get; }

    public WarmupEnded(Dictionary<string, double> inventory, double seconds)
    {
        Inventory = inventory;
        Seconds = seconds;
    }
}

/// <summary>One entry of the game's output, already cut to the game's per-entry limit.</summary>
public sealed class RunOutput : RunEvent
{
    public string Text { get; }
    public RunOutput(string text) => Text = text;
}

/// <summary>
/// A runtime error. <paramref name="startIndex"/> is a character offset into <paramref name="source"/>, the text of
/// <paramref name="file"/> (null when unknown). The game leaves the Run paused on it; <see cref="RunTracker"/> stops it
/// with <see cref="IGame.StopRun"/>, so <see cref="RunEnded"/> follows.
/// </summary>
public sealed class RunErrored : RunEvent
{
    public string Message { get; }
    public string Stack { get; }
    public string? File { get; }
    public string? Source { get; }
    public int StartIndex { get; }

    public RunErrored(string message, string stack, string? file, string? source, int startIndex)
    {
        Message = message;
        Stack = stack;
        File = file;
        Source = source;
        StartIndex = startIndex;
    }
}

/// <summary>A Leaderboard Run's result; rank is null when Steam did not provide one in time.</summary>
public sealed class LeaderboardFinished : RunEvent
{
    public bool ReachedGoal { get; }
    public double AverageSeconds { get; }
    public int? Rank { get; }

    public LeaderboardFinished(bool reachedGoal, double averageSeconds, int? rank)
    {
        ReachedGoal = reachedGoal;
        AverageSeconds = averageSeconds;
        Rank = rank;
    }
}

public enum RunEnd
{
    /// <summary>The Script ran to its end.</summary>
    Terminated,

    /// <summary>Something stopped the Run: the stop button, editing the code, or <see cref="IGame.StopRun"/>.</summary>
    Stopped,
}

/// <summary>
/// The Run is over (for a Leaderboard Run: its result page is closed and the farm is usable again; for a simulated Run:
/// the real farm is back). <paramref name="inventory"/> is the whole inventory of the farm the Run was on at the moment
/// it ended, null when it can no longer be read; <paramref name="ticks"/> is the number of game steps the Run executed
/// (as get_tick_count counts them). <paramref name="simulatedSeconds"/> is the temporary farm's clock when it ended,
/// set for simulated Runs only.
/// </summary>
public sealed class RunEnded : RunEvent
{
    public RunEnd How { get; }
    public Dictionary<string, double>? Inventory { get; }
    public long Ticks { get; }
    public double? SimulatedSeconds { get; }

    public RunEnded(RunEnd how, Dictionary<string, double>? inventory, long ticks, double? simulatedSeconds = null)
    {
        How = how;
        Inventory = inventory;
        Ticks = ticks;
        SimulatedSeconds = simulatedSeconds;
    }
}

/// <summary>
/// The game is about to switch to another Save (a Tool or the human did it), or a scene has just set one up. A Run
/// cannot outlive the loaded Save, so whatever Run was latest is forgotten, summary included.
/// </summary>
public sealed class SaveLoading : RunEvent { }
