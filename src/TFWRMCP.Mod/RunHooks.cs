using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace TFWRMCP.Mod;

/// <summary>
/// Harmony hooks that turn what the game does to Runs into <see cref="RunEvent"/>s. Hooks on the simulation thread
/// only emit events and queue work for the main thread; they never touch Unity objects beyond what the game itself
/// touches there.
/// </summary>
public static class RunHooks
{
    static readonly TimeSpan RankTimeout = TimeSpan.FromSeconds(3);
    const string Separator = "--------------------------------------------------------------------";

    static Action<RunEvent> emit = _ => { };
    static Action<string> log = _ => { };
    static FieldInfo simField = null!;

    // Set by the stop hook, consumed by whatever ends the Run: tells Stopped from Terminated.
    static volatile bool stopRequested;

    // True while MainSim.StartLeaderboard runs: it stops the caller's Run, which then continues as the Leaderboard Run.
    static volatile bool startingLeaderboard;

    sealed class PendingLeaderboard
    {
        public bool ReachedGoal;
        public double Seconds;
        public bool Cancelled;
        public int? Rank;
    }

    static PendingLeaderboard? pendingLeaderboard;

    public static void Configure(Action<RunEvent> emitEvent, Action<string> logError, FieldInfo mainSimSim)
    {
        emit = emitEvent;
        log = logError;
        simField = mainSimSim;
    }

    static bool IsMainLogger(global::Logger logger)
    {
        var main = MainSim.Inst;
        return (object)main != null && ReferenceEquals(main.Logger, logger);
    }

    static bool IsMainSim(Simulation sim) => (object)MainSim.Inst != null && ReferenceEquals(sim.simRunner, MainSim.Inst);

    static void Safely(string hook, Action body)
    {
        try { body(); }
        catch (Exception e) { log($"Run hook {hook} failed: {e}"); }
    }

    /// <summary>Game text carries TextMeshPro syntax-colour tags for its UI; the Agent gets plain text.</summary>
    public static string Plain(string text) => CodeUtilities.RemoveSyntaxTags(text);

    // Logger.WriteLog(string logMessage, bool addSeparator), prefix.
    public static void WriteLog(global::Logger __instance, string __0, bool __1) => Safely(nameof(WriteLog), () =>
    {
        if (!IsMainLogger(__instance)) return;
        var message = Plain(__1 ? __0 + "\n" + Separator : __0);
        if (message.Length > global::Logger.MESSAGE_LENGTH_LIMIT) message = message.Substring(0, global::Logger.MESSAGE_LENGTH_LIMIT);
        emit(new RunOutput(message));
    });

    // Logger.LogError(string errorMessage, ProgramState state), prefix. The game leaves the Run paused on the error.
    public static void LogError(global::Logger __instance, string __0, ProgramState __1) => Safely(nameof(LogError), () =>
    {
        if (!IsMainLogger(__instance)) return;
        var node = __1.CurrentExecutingNode;
        var window = node?.boxedParams.codeWindow;
        var exception = __1.currentExecuteException;
        var start = exception != null && exception.startIndex >= 0 ? exception.startIndex : node?.boxedParams.wordStart ?? 0;
        string stack;
        try { stack = __1.GetTrace(); }
        catch (Exception) { stack = ""; }

        var sim = (Simulation?)simField.GetValue(MainSim.Inst);
        var execution = sim?.Execution;
        emit(new RunErrored(Plain(__0), Plain(stack), window?.fileName, window?.CodeInput.text, start));
        MainThread.Post(() => StopAfterError(execution));
    });

    static void StopAfterError(Execution? failed)
    {
        var main = MainSim.Inst;
        if (main == null) return;
        var sim = (Simulation?)simField.GetValue(main);
        // The human may have stopped or restarted in the meantime.
        if (sim != null && failed != null && ReferenceEquals(sim.Execution, failed)) main.StopMainExecution();
    }

    // MainSim.StartMainExecution(CodeWindow, Node, bool), prefix: whoever starts a Run, it is tracked.
    public static void StartMainExecution(MainSim __instance, CodeWindow __0) => Safely(nameof(StartMainExecution), () =>
    {
        if (__instance.IsExecuting()) return;
        stopRequested = false;
        emit(new RunStarted(__0.GetComponent<Window>().windowName));
    });

    // MainSim.StopMainExecution(), prefix.
    public static void StopMainExecution(MainSim __instance) => Safely(nameof(StopMainExecution), () =>
    {
        if (!startingLeaderboard && __instance.IsExecuting()) stopRequested = true;
    });

    // Simulation.StopProgramExecution(), prefix: the end of the main simulation's Run, however it came about.
    public static void StopProgramExecution(Simulation __instance) => Safely(nameof(StopProgramExecution), () =>
    {
        if (startingLeaderboard || __instance.leaderboardType != LeaderboardType.none ||
            !__instance.IsExecuting() || !IsMainSim(__instance)) return;
        var how = stopRequested ? RunEnd.Stopped : RunEnd.Terminated;
        stopRequested = false;
        emit(new RunEnded(how));
    });

    // MainSim.StartLeaderboard(LeaderboardStartArgs), prefix and finalizer.
    public static void StartLeaderboardBegin() => startingLeaderboard = true;

    public static void StartLeaderboardEnd() => startingLeaderboard = false;

    // LeaderboardManager.StopLeaderboardRun(bool finished, string, string, TimeSpan), prefix (the Steam rank may arrive during the call).
    public static void StopLeaderboardRun(bool __0, TimeSpan __3) => Safely(nameof(StopLeaderboardRun), () =>
    {
        var pending = new PendingLeaderboard { ReachedGoal = __0, Seconds = __3.TotalSeconds, Cancelled = stopRequested };
        stopRequested = false;
        pendingLeaderboard = pending;
        Task.Delay(RankTimeout).ContinueWith(_ => MainThread.Post(() => FinishLeaderboard(pending)));
    });

    // Leaderboard.OnPlayerLoaded(LeaderboardEntryData[]), postfix.
    public static void PlayerLoaded(LeaderboardEntryData[] __0) => Safely(nameof(PlayerLoaded), () =>
    {
        var pending = pendingLeaderboard;
        if (pending == null) return;
        if (__0 != null && __0.Length > 0) pending.Rank = __0[0].rank;
        MainThread.Post(() => FinishLeaderboard(pending));
    });

    static void FinishLeaderboard(PendingLeaderboard pending)
    {
        if (Interlocked.CompareExchange(ref pendingLeaderboard, null, pending) != pending) return;
        var manager = MainSim.Inst.leaderboardManager;
        if (manager.IsLeaderBoardScreenOpen) manager.OkPressed();
        emit(new LeaderboardFinished(pending.ReachedGoal, pending.Seconds, pending.Rank));
        emit(new RunEnded(pending.Cancelled ? RunEnd.Stopped : RunEnd.Terminated));
    }
}
