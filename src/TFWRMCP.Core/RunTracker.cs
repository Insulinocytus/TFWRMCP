using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

    /// <summary>Only with an <see cref="InventoryGoal"/>: the whole inventory at the end of the wait.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, double>? Inventory { get; set; }

    /// <summary>Only with an <see cref="InventoryGoal"/>: whether the inventory met it.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? Met { get; set; }

    /// <summary>Item name to net change since the Run began (the whole farm: Script, game and human alike), non-zero items only; absent when the Run's end inventory is unknown.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, double>? InventoryDelta { get; set; }

    /// <summary>Function name to the number of failed calls, non-zero functions only.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, int>? FailedCalls { get; set; }

    /// <summary>Game steps the Run has executed; fixed once it has ended.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public long? Ticks { get; set; }

    /// <summary>Real time since the Run began; fixed once it has ended.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? Seconds { get; set; }
}

/// <summary>Wait until every item is at least its amount; with <see cref="StopWhenMet"/> the Run is stopped as soon as that holds.</summary>
public sealed class InventoryGoal
{
    public Dictionary<string, double> AtLeast { get; }
    public bool StopWhenMet { get; }

    public InventoryGoal(Dictionary<string, double> atLeast, bool stopWhenMet)
    {
        AtLeast = atLeast;
        StopWhenMet = stopWhenMet;
    }

    public bool IsMet(Dictionary<string, double> inventory) =>
        AtLeast.All(item => inventory.TryGetValue(item.Key, out var have) && have >= item.Value);
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

        // Null for Runs that never started: they have no summary.
        public Dictionary<string, double>? StartInventory;
        public Stopwatch? Clock;
        public readonly Dictionary<string, int> Failed = new();
        public Dictionary<string, double>? EndInventory;
        public long EndTicks;
    }

    readonly object gate = new();
    readonly IGame game;
    readonly Action<string> log;
    Record current = new();

    public RunTracker(IGame game, Action<string> log)
    {
        this.game = game;
        this.log = log;
        game.RunEvents += OnEvent;
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
        var stopAfterError = false;
        lock (gate)
        {
            if (e is RunStarted started)
            {
                current.Over.TrySetResult(true);
                current = new Record
                {
                    Status = RunStatus.Running, Script = started.Script, StartInventory = started.Inventory, Clock = Stopwatch.StartNew(),
                };
                return;
            }

            if (current.Status != RunStatus.Running) return;
            switch (e)
            {
                case ActionFailed failed:
                    current.Failed[failed.Function] = current.Failed.GetValueOrDefault(failed.Function) + 1;
                    break;
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
                    stopAfterError = true;
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
                    current.Clock!.Stop();
                    current.EndInventory = ended.Inventory;
                    current.EndTicks = ended.Ticks;
                    current.Over.TrySetResult(true);
                    break;
            }
        }

        if (stopAfterError) _ = StopAfterError();
    }

    /// <summary>The game leaves a Run paused on a runtime error; stop it so the game is usable again and the Run ends as error.</summary>
    async Task StopAfterError()
    {
        try { await game.StopRun(); }
        catch (Exception e) { log($"Stopping the Run after a runtime error failed: {e}"); }
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

    public Task<RunSnapshot> Snapshot(bool withOutput)
    {
        Record run;
        lock (gate) run = current;
        return Report(run, withOutput);
    }

    /// <summary>Describes the Run; while it is running its inventory delta needs the current inventory, which <paramref name="live"/> may already hold.</summary>
    async Task<RunSnapshot> Report(Record run, bool withOutput, Dictionary<string, double>? live = null)
    {
        if (live == null && IsRunning(run)) live = await ReadInventory();
        lock (gate) return Describe(run, withOutput, live);
    }

    RunSnapshot Describe(Record r, bool withOutput, Dictionary<string, double>? live)
    {
        var snapshot = new RunSnapshot
        {
            Status = r.Status,
            Script = r.Script,
            Error = r.Error,
            Leaderboard = r.Leaderboard,
            Output = withOutput ? new List<string>(r.Output) : null,
            OutputDropped = withOutput ? r.Dropped : 0,
        };
        if (r.Clock == null) return snapshot;

        var running = r.Status == RunStatus.Running;
        var end = running ? live : r.EndInventory;
        if (end != null)
            snapshot.InventoryDelta = end.Keys.Union(r.StartInventory!.Keys)
                .Select(item => (Item: item, Change: end.GetValueOrDefault(item) - r.StartInventory.GetValueOrDefault(item)))
                .Where(c => c.Change != 0)
                .ToDictionary(c => c.Item, c => c.Change);
        snapshot.FailedCalls = new Dictionary<string, int>(r.Failed);
        snapshot.Ticks = running ? game.RunTicks : r.EndTicks;
        snapshot.Seconds = Math.Round(r.Clock.Elapsed.TotalSeconds, 3);
        return snapshot;
    }

    static readonly TimeSpan InventoryPollInterval = TimeSpan.FromMilliseconds(100);

    bool IsRunning(Record run)
    {
        lock (gate) return run.Status == RunStatus.Running;
    }

    async Task WaitOver(Record run, TimeSpan timeout, CancellationToken cancel)
    {
        using var stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        await Task.WhenAny(run.Over.Task, Task.Delay(timeout, stopWaiting.Token));
        stopWaiting.Cancel();
        cancel.ThrowIfCancellationRequested();
    }

    async Task<Dictionary<string, double>> ReadInventory() => (await game.GetState(StateParts.Inventory)).Inventory;

    /// <summary>
    /// Waits for the Run to end for at most <paramref name="timeout"/>, then reports it with its output; throws if
    /// <paramref name="cancel"/> is cancelled. With a <paramref name="goal"/> it also ends once the goal is met (and
    /// then stops the Run if asked to), and the result carries the inventory and whether the goal was met.
    /// </summary>
    public async Task<RunSnapshot> Wait(TimeSpan timeout, CancellationToken cancel, InventoryGoal? goal = null)
    {
        Record run;
        lock (gate) run = current;
        if (goal == null)
        {
            if (IsRunning(run)) await WaitOver(run, timeout, cancel);
            return await Report(run, withOutput: true);
        }

        var clock = Stopwatch.StartNew();
        Dictionary<string, double> inventory;
        bool met;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            inventory = await ReadInventory();
            met = goal.IsMet(inventory);
            var left = timeout - clock.Elapsed;
            if (met || !IsRunning(run) || left <= TimeSpan.Zero) break;
            await WaitOver(run, left < InventoryPollInterval ? left : InventoryPollInterval, cancel);
        }

        if (met && goal.StopWhenMet && IsRunning(run))
        {
            await StopByTool(Tools.StopTimeout, cancel);
            inventory = await ReadInventory();
        }
        var snapshot = await Report(run, withOutput: true, inventory);
        snapshot.Inventory = inventory;
        snapshot.Met = met;
        return snapshot;
    }

    /// <summary>Stops the Run in progress on the Agent's behalf and waits for it to end; with no Run in progress it only reports.</summary>
    public async Task<RunSnapshot> StopByTool(TimeSpan timeout, CancellationToken cancel)
    {
        Record run;
        bool running;
        lock (gate)
        {
            run = current;
            running = run.Status == RunStatus.Running;
            if (running) run.StopRequestedByTool = true;
        }
        if (!running) return await Report(run, withOutput: true);
        await game.StopRun();
        return await Wait(timeout, cancel);
    }

    /// <summary>A Run cannot outlive the loaded Save: stop it, then forget it.</summary>
    public async Task ResetForReload(TimeSpan timeout, CancellationToken cancel)
    {
        await StopByTool(timeout, cancel);
        lock (gate) current = new Record();
    }
}
