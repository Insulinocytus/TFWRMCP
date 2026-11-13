using System;
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

    // Steps the Run of the real farm has executed, as of its latest action or its end.
    static long ticks;

    public static long Ticks => Volatile.Read(ref ticks);

    public static void Configure(Action<RunEvent> emitEvent, Action<string> logError)
    {
        emit = emitEvent;
        log = logError;
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

    // Logger.WriteLog(string logMessage, bool addSeparator), prefix.
    public static void WriteLog(global::Logger __instance, string __0, bool __1) => Safely(nameof(WriteLog), () =>
    {
        if (!IsMainLogger(__instance)) return;
        var message = CodeUtilities.RemoveSyntaxTags(__1 ? __0 + "\n" + Separator : __0);
        if (message.Length > global::Logger.MESSAGE_LENGTH_LIMIT) message = message.Substring(0, global::Logger.MESSAGE_LENGTH_LIMIT);
        emit(new RunOutput(message));
    });

    // Logger.LogError(string errorMessage, ProgramState state), prefix. The game leaves the Run paused on the error; Core stops it.
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

        // Unity objects must be checked with ==, which ?. would skip.
        var file = window != null ? window.fileName : null;
        var source = window != null ? window.CodeInput.text : null;
        emit(new RunErrored(CodeUtilities.RemoveSyntaxTags(__0), CodeUtilities.RemoveSyntaxTags(stack), file, source, start));
    });

    // MainSim.StartMainExecution(CodeWindow, Node, bool), prefix: whoever starts a Run, it is tracked.
    public static void StartMainExecution(MainSim __instance, CodeWindow __0) => Safely(nameof(StartMainExecution), () =>
    {
        if (__instance.IsExecuting()) return;
        stopRequested = false;
        Volatile.Write(ref ticks, 0);
        emit(new RunStarted(__0.GetComponent<Window>().windowName, GameAdapter.Items(__instance.GetInventory(), includeZero: true)));
    });

    // MainSim.StopMainExecution(), prefix.
    public static void StopMainExecution(MainSim __instance) => Safely(nameof(StopMainExecution), () =>
    {
        if (!startingLeaderboard && __instance.IsExecuting()) stopRequested = true;
    });

    // Simulation.StopProgramExecution(), prefix: the end of the main simulation's Run, however it came about.
    public static void StopProgramExecution(Simulation __instance) => Safely(nameof(StopProgramExecution), () =>
    {
        if (__instance.leaderboardType != LeaderboardType.none || !__instance.IsExecuting() || !IsMainSim(__instance)) return;
        Volatile.Write(ref ticks, (long)__instance.Execution.GlobalOpCount);
        if (startingLeaderboard) return;
        var how = stopRequested ? RunEnd.Stopped : RunEnd.Terminated;
        stopRequested = false;
        emit(new RunEnded(how, GameAdapter.Items(__instance.farm.Items, includeZero: true), Ticks));
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
        // A scene reload during the rank wait destroys MainSim; the Run must still end.
        var main = MainSim.Inst;
        if (main != null && main.leaderboardManager.IsLeaderBoardScreenOpen) main.leaderboardManager.OkPressed();
        emit(new LeaderboardFinished(pending.ReachedGoal, pending.Seconds, pending.Rank));
        var inventory = main != null && !main.MightBeSimulating() ? GameAdapter.Items(main.GetInventory(), includeZero: true) : null;
        emit(new RunEnded(pending.Cancelled ? RunEnd.Stopped : RunEnd.Terminated, inventory, Ticks));
    }

    /// <summary>What <see cref="ActionEnd"/> needs from before the action: it may end the Run and clear the drones' states.</summary>
    public struct ActionState
    {
        public string? Function;
        public ProgramState? Program;
        public bool Unharvestable;
    }

    static string? ActionName(SideEffect effect)
    {
        switch (effect)
        {
            case SideEffect.Plant: return "plant";
            case SideEffect.Harvest: return "harvest";
            case SideEffect.Move: return "move";
            case SideEffect.Swap: return "swap";
            case SideEffect.UseItem: return "use_item";
            case SideEffect.Place: return "place";
            case SideEffect.Dig: return "dig";
            case SideEffect.Push: return "push";
            case SideEffect.Unlock: return "unlock";
            case SideEffect.Hover: return "hover";
            default: return null;
        }
    }

    // Execution.ApplySideEffect(int droneId), prefix. Actions on the temporary farm of simulate() and Leaderboard Runs are not tracked.
    public static void ActionBegin(Execution __instance, int __0, out ActionState __state)
    {
        __state = default;
        try
        {
            var sim = __instance.sim;
            if (sim.leaderboardType != LeaderboardType.none || !IsMainSim(sim)) return;
            Volatile.Write(ref ticks, (long)__instance.GlobalOpCount);
            var program = __instance.States[__0];
            var function = ActionName(program.currentSideEffect);
            if (function == null) return;
            __state.Function = function;
            __state.Program = program;
            // Harvesting something unripe returns True, so only the entity says whether it failed.
            __state.Unharvestable = program.currentSideEffect == SideEffect.Harvest && !sim.farm.drones[__0].CanHarvest();
        }
        catch (Exception e)
        {
            log($"Run hook {nameof(ActionBegin)} failed: {e}");
        }
    }

    // Execution.ApplySideEffect(int droneId), postfix; an action that throws a Script error never gets here.
    public static void ActionEnd(ActionState __state)
    {
        if (__state.Function == null) return;
        try
        {
            if (__state.Unharvestable || __state.Program!.ReturnValue is PyBool { num: 0 }) emit(new ActionFailed(__state.Function));
        }
        catch (Exception e)
        {
            log($"Run hook {nameof(ActionEnd)} failed: {e}");
        }
    }
}
