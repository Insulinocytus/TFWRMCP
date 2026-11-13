using System.Collections.Generic;

namespace TFWRMCP;

/// <summary>What the game reports about Runs; see <see cref="IGame.RunEvents"/>. Ordering within a Run is Started, Output/ActionFailed/Errored/LeaderboardFinished in any order, Ended.</summary>
public abstract class RunEvent { }

/// <summary>A Run began, whoever started it. Output of earlier Runs is discarded.</summary>
public sealed class RunStarted : RunEvent
{
    public string Script { get; }

    /// <summary>The real farm's whole inventory when the Run began.</summary>
    public Dictionary<string, double> Inventory { get; }

    public RunStarted(string script, Dictionary<string, double> inventory)
    {
        Script = script;
        Inventory = inventory;
    }
}

/// <summary>
/// The Script called an action that counts as failed: it returned False, or it was harvest() on an entity that is
/// not harvestable (the game then returns True, clears the crop and yields nothing). Actions on the temporary farm
/// of simulate() and Leaderboard Runs are never reported.
/// </summary>
public sealed class ActionFailed : RunEvent
{
    public string Function { get; }
    public ActionFailed(string function) => Function = function;
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

    /// <summary>Something stopped the Run: the stop button, F10, editing the code, or <see cref="IGame.StopRun"/>.</summary>
    Stopped,
}

/// <summary>
/// The Run is over (for a Leaderboard Run: its result page is closed and the farm is usable again).
/// <paramref name="inventory"/> is the real farm's whole inventory at that moment, null when it can no longer be
/// read; <paramref name="ticks"/> is the number of game steps the Run executed (as get_tick_count counts them).
/// </summary>
public sealed class RunEnded : RunEvent
{
    public RunEnd How { get; }
    public Dictionary<string, double>? Inventory { get; }
    public long Ticks { get; }

    public RunEnded(RunEnd how, Dictionary<string, double>? inventory = null, long ticks = 0)
    {
        How = how;
        Inventory = inventory;
        Ticks = ticks;
    }
}
