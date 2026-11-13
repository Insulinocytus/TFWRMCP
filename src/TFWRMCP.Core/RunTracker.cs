using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TFWRMCP;

public static class RunStatus
{
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Finished = "finished";
    public const string Error = "error";
    public const string StoppedByTool = "stopped_by_tool";
    public const string StoppedByUser = "stopped_by_user";
    public const string ParseError = "parse_error";
}

/// <summary>Line and column are 1-based; absent when the source text is unknown.</summary>
public sealed class RunErrorInfo
{
    public string Message { get; set; } = "";

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Stack { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? File { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? Line { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? Column { get; set; }

    public static RunErrorInfo At(string message, string? stack, string? file, string? source, int offset)
    {
        var info = new RunErrorInfo { Message = message, Stack = stack, File = file };
        if (source == null) return info;
        offset = Math.Max(0, Math.Min(offset, source.Length));
        var lineStart = offset == 0 ? -1 : source.LastIndexOf('\n', offset - 1);
        var line = 1;
        for (var i = source.IndexOf('\n'); i >= 0 && i < offset; i = source.IndexOf('\n', i + 1)) line++;
        info.Line = line;
        info.Column = offset - lineStart;
        return info;
    }
}

public sealed class LeaderboardOutcome
{
    public bool ReachedGoal { get; set; }
    public double AverageSeconds { get; set; }

    /// <summary>Null when Steam did not provide a rank.</summary>
    public int? Rank { get; set; }
}

/// <summary>The latest Run as the Agent sees it. Output is only filled by the Run tools, not by get_state.</summary>
public sealed class RunSnapshot
{
    public string Status { get; set; } = RunStatus.Idle;

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Script { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public RunErrorInfo? Error { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public LeaderboardOutcome? Leaderboard { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Output { get; set; }

    /// <summary>Older output entries that no longer fit the buffer.</summary>
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public int OutputDropped { get; set; }
}

/// <summary>
/// Follows Runs through <see cref="IGame.RunEvents"/>. The only thing it adds to the game's events is who asked for
/// a stop: <see cref="StopByTool"/> marks it, any other stop is the human's.
/// </summary>
public sealed class RunTracker
{
    public const int MaxOutput = 1000;

    sealed class Record
    {
        public string Status = RunStatus.Idle;
        public string? Script;
        public readonly Queue<string> Output = new();
        public int Dropped;
        public RunErrorInfo? Error;
        public LeaderboardOutcome? Leaderboard;
        public bool StopRequestedByTool;
        public readonly TaskCompletionSource<bool> Over = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    readonly object gate = new();
    readonly IGame game;
    Record current = new();

    public RunTracker(IGame game)
    {
        this.game = game;
        game.RunEvents += OnEvent;
    }

    public bool IsRunning
    {
        get
        {
            lock (gate) return current.Status == RunStatus.Running;
        }
    }

    public bool HasRun
    {
        get
        {
            lock (gate) return current.Status != RunStatus.Idle;
        }
    }

    void OnEvent(RunEvent e)
    {
        lock (gate)
        {
            if (e is RunStarted started)
            {
                current.Over.TrySetResult(true);
                current = new Record { Status = RunStatus.Running, Script = started.Script };
                return;
            }

            if (current.Status != RunStatus.Running) return;
            switch (e)
            {
                case RunOutput output:
                    current.Output.Enqueue(output.Text);
                    if (current.Output.Count > MaxOutput)
                    {
                        current.Output.Dequeue();
                        current.Dropped++;
                    }
                    break;
                case RunErrored error:
                    current.Error = RunErrorInfo.At(error.Message, error.Stack, error.File, error.Source, error.StartIndex);
                    break;
                case LeaderboardFinished result:
                    current.Leaderboard = new LeaderboardOutcome
                    {
                        ReachedGoal = result.ReachedGoal, AverageSeconds = result.AverageSeconds, Rank = result.Rank,
                    };
                    break;
                case RunEnded ended:
                    current.Status =
                        current.Error != null ? RunStatus.Error :
                        ended.How == RunEnd.Terminated ? RunStatus.Finished :
                        current.StopRequestedByTool ? RunStatus.StoppedByTool : RunStatus.StoppedByUser;
                    current.Over.TrySetResult(true);
                    break;
            }
        }
    }

    /// <summary>Records a Run that never started because its Script does not parse.</summary>
    public void RecordSyntaxError(string script, SyntaxError error)
    {
        lock (gate)
        {
            current.Over.TrySetResult(true);
            current = new Record
            {
                Status = RunStatus.ParseError,
                Script = script,
                Error = RunErrorInfo.At(error.Message, null, script, error.Source, error.StartIndex),
            };
            current.Over.SetResult(true);
        }
    }

    public RunSnapshot Snapshot(bool withOutput)
    {
        lock (gate) return Describe(current, withOutput);
    }

    static RunSnapshot Describe(Record r, bool withOutput) => new()
    {
        Status = r.Status,
        Script = r.Script,
        Error = r.Error,
        Leaderboard = r.Leaderboard,
        Output = withOutput ? new List<string>(r.Output) : null,
        OutputDropped = withOutput ? r.Dropped : 0,
    };

    /// <summary>Waits for the Run to end for at most <paramref name="timeout"/>, then reports it with its output.</summary>
    public async Task<RunSnapshot> Wait(TimeSpan timeout)
    {
        Record run;
        lock (gate) run = current;
        if (run.Status == RunStatus.Running)
        {
            using var cancel = new CancellationTokenSource();
            await Task.WhenAny(run.Over.Task, Task.Delay(timeout, cancel.Token));
            cancel.Cancel();
        }
        lock (gate) return Describe(run, withOutput: true);
    }

    /// <summary>Stops the Run in progress on the Agent's behalf and waits for it to end; with no Run in progress it only reports.</summary>
    public async Task<RunSnapshot> StopByTool(TimeSpan timeout)
    {
        lock (gate)
        {
            if (current.Status != RunStatus.Running) return Describe(current, withOutput: true);
            current.StopRequestedByTool = true;
        }
        await game.StopRun();
        return await Wait(timeout);
    }

    /// <summary>A Run cannot outlive the loaded Save: stop it, then forget it.</summary>
    public async Task ResetForReload(TimeSpan timeout)
    {
        await StopByTool(timeout);
        lock (gate) current = new Record();
    }
}
