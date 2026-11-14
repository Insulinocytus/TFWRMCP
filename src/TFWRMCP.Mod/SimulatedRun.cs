using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace TFWRMCP.Mod;

/// <summary>
/// The simulated Run (ADR 0002): the game's own simulation-type temporary farm, started by the Mod through
/// MainSim.ScheduleLeaderboardStart instead of by a Script's simulate(). The hooks add what simulate() never needed:
/// the exact game_seconds cutoff on the simulation thread, and restoring the real farm even when the game's own
/// restore (LeaderboardExecutionFinished) returns early because the Leaderboard overlay is not active.
/// </summary>
public static class SimulatedRun
{
    // MainSim.TimeFactor's upper clamp: the game's top speed.
    const double TopSpeed = 1_000_000;

    /// <summary>The simulated Run in progress: its temporary farm, the clock it ends at and, once its Script is over, how it ended.</summary>
    public sealed class Active
    {
        public Simulation Sim { get; }
        public Duration Deadline { get; }

        // Taken when the temporary farm's program ends, emitted once the real farm is back.
        public volatile RunEnded? Ended;

        public Active(Simulation sim, Duration deadline)
        {
            Sim = sim;
            Deadline = deadline;
        }
    }

    sealed class Pending
    {
        public string Script { get; }
        public double Seconds { get; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Pending(string script, double seconds)
        {
            Script = script;
            Seconds = seconds;
        }
    }

    static Action<RunEvent> emit = _ => { };
    static Action<string> log = _ => { };
    static FieldInfo? simField;
    static FieldInfo? lockField;

    // Between scheduling the start and the game's next MainSim.Update consuming it.
    static Pending? starting;
    static volatile Active? current;

    public static Active? Current => current;

    /// <summary>Whether a simulated Run is scheduled or in progress.</summary>
    public static bool Busy => starting != null || current != null;

    /// <summary>The steps the simulated Run has executed, frozen once its Script is over; null when there is none.</summary>
    public static long? Ticks
    {
        get
        {
            var run = current;
            if (run == null) return null;
            return run.Ended?.Ticks ?? (long)(run.Sim.Execution?.GlobalOpCount ?? 0);
        }
    }

    public static void Configure(Action<RunEvent> emitEvent, Action<string> logError, FieldInfo? sim, FieldInfo? simulationLock)
    {
        emit = emitEvent;
        log = logError;
        simField = sim;
        lockField = simulationLock;
    }

    /// <summary>Completes once the game has started the simulation (RunStarted raised) and fails when it could not.</summary>
    public static Task Expect(string script, double seconds)
    {
        var pending = new Pending(script, seconds);
        starting = pending;
        return pending.Started.Task;
    }

    // MainSim.StartLeaderboard(LeaderboardStartArgs), finalizer; runs in MainSim.Update, under the simulation lock.
    public static Exception? StartLeaderboardEnd(MainSim __instance, Exception? __exception)
    {
        var pending = Interlocked.Exchange(ref starting, null);
        if (pending == null) return __exception;
        try
        {
            var sim = (Simulation?)simField!.GetValue(__instance);
            if (__exception != null || __instance.storedSim == null || sim == null)
            {
                pending.Started.TrySetException(new ToolException(__exception == null
                    ? "The game did not start the simulation."
                    : $"The game failed to start the simulation: {__exception.Message}"));
                return __exception;
            }

            RunHooks.ConsumeEnd();
            __instance.TimeFactor = TopSpeed;
            current = new Active(sim, Duration.FromSeconds(pending.Seconds));
            emit(new RunStarted(pending.Script, GameAdapter.Items(sim.farm.Items, includeZero: true), simulated: true));
            pending.Started.TrySetResult(true);
        }
        catch (Exception e)
        {
            log($"Simulated Run hook {nameof(StartLeaderboardEnd)} failed: {e}");
            pending.Started.TrySetException(e);
        }
        return __exception;
    }

    // Simulation.RunNextStep(Duration goalTime, object lockSimulation, bool), prefix, on the simulation thread: the temporary
    // farm never gets a goal past its deadline, and its Script is stopped as soon as the clock is there.
    public static void BeforeStep(Simulation __instance, ref Duration __0, object __1)
    {
        var run = current;
        if (run == null || !ReferenceEquals(run.Sim, __instance)) return;
        try
        {
            if (__0 > run.Deadline) __0 = run.Deadline;
            if (__instance.CurrentTime < run.Deadline || !__instance.IsExecuting()) return;
            lock (__1)
            {
                if (__instance.IsExecuting()) __instance.StopProgramExecution();
            }
        }
        catch (Exception e)
        {
            log($"Simulated Run hook {nameof(BeforeStep)} failed: {e}");
        }
    }

    // Simulation.StopProgramExecution(), prefix: the end of the temporary farm's Script, however it came about.
    public static void ProgramEnding(Simulation __instance)
    {
        var run = current;
        if (run == null || !ReferenceEquals(run.Sim, __instance) || run.Ended != null || !__instance.IsExecuting()) return;
        try
        {
            run.Ended = new RunEnded(RunHooks.ConsumeEnd(), GameAdapter.Items(__instance.farm.Items, includeZero: true),
                (long)__instance.Execution.GlobalOpCount, __instance.CurrentTime.Seconds);
        }
        catch (Exception e)
        {
            log($"Simulated Run hook {nameof(ProgramEnding)} failed: {e}");
        }
    }

    // MainSim.Update(), postfix. The game restores the real farm in Update once the temporary farm's Script is over, but
    // only while the Leaderboard overlay is active; otherwise it would stay paused behind the temporary farm forever.
    public static void AfterUpdate(MainSim __instance)
    {
        var run = current;
        if (run == null) return;
        try
        {
            // A new scene's MainSim: the Run it belonged to is gone.
            if (__instance.storedSim == null)
            {
                current = null;
                return;
            }
            if (run.Ended == null) return;

            var real = __instance.storedSim;
            lock (lockField!.GetValue(__instance))
            {
                __instance.leaderboardManager.RemoveOverlay();
                __instance.RestoreMainSim();
                real.Paused = false;
                __instance.TimeFactor = 1;
            }
        }
        catch (Exception e)
        {
            log($"Simulated Run hook {nameof(AfterUpdate)} failed: {e}");
        }
    }

    // MainSim.RestoreMainSim(), postfix: the real farm is back, whichever path restored it.
    public static void Restored(MainSim __instance)
    {
        var run = current;
        if (run?.Ended is not { } ended || __instance.storedSim != null) return;
        current = null;
        try { emit(ended); }
        catch (Exception e) { log($"Simulated Run hook {nameof(Restored)} failed: {e}"); }
    }
}
