namespace TFWRMCP;

/// <summary>What the game reports about Runs; see <see cref="IGame.RunEvents"/>. Ordering within a Run is Started, Output/Errored/LeaderboardFinished in any order, Ended.</summary>
public abstract class RunEvent { }

/// <summary>A Run began, whoever started it. Output of earlier Runs is discarded.</summary>
public sealed class RunStarted : RunEvent
{
    public string Script { get; }
    public RunStarted(string script) => Script = script;
}

/// <summary>One entry of the game's output, already cut to the game's per-entry limit.</summary>
public sealed class RunOutput : RunEvent
{
    public string Text { get; }
    public RunOutput(string text) => Text = text;
}

/// <summary>
/// A runtime error. <paramref name="startIndex"/> is a character offset into <paramref name="source"/>, the text of
/// <paramref name="file"/> (null when unknown). The game stops paused on it, so <see cref="RunEnded"/> follows.
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

/// <summary>The Run is over (for a Leaderboard Run: its result page is closed and the farm is usable again).</summary>
public sealed class RunEnded : RunEvent
{
    public RunEnd How { get; }
    public RunEnded(RunEnd how) => How = how;
}
