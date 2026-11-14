using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Platforms;

namespace TFWRMCP.Mod;

public sealed partial class GameAdapter
{
    void InstallSimulationSupport(Action<string> warn)
    {
        ResolveMethods(Capabilities.Simulation, warn,
            ("MainSim", "ScheduleLeaderboardStart"), ("MainSim", "GetSerializedUnlocks"), ("MainSim", "GetInventory"),
            ("LeaderboardManager", "RemoveOverlay"), ("CodeWindow", "Parse"));
        if (UnavailableReason(Capabilities.Run) != null || UnavailableReason(Capabilities.State) != null)
            Fail(Capabilities.Simulation, "the Run or State capability is unavailable", warn);
        if (UnavailableReason(Capabilities.Simulation) != null) return;

        SimulatedRun.Configure(e => RunEvents(e), warn, simField, lockField);
        PatchSimulation("MainSim", "StartLeaderboard", warn, finalizer: nameof(SimulatedRun.StartLeaderboardEnd));
        PatchSimulation("Simulation", "RunNextStep", warn, prefix: nameof(SimulatedRun.BeforeStep));
        PatchSimulation("Simulation", "StopProgramExecution", warn, prefix: nameof(SimulatedRun.ProgramEnding));
        PatchSimulation("MainSim", "Update", warn, postfix: nameof(SimulatedRun.AfterUpdate));
        PatchSimulation("MainSim", "RestoreMainSim", warn, postfix: nameof(SimulatedRun.Restored));
    }

    void PatchSimulation(string type, string method, Action<string> warn, string? prefix = null, string? postfix = null, string? finalizer = null) =>
        Patch(type, method, warn, prefix, postfix, finalizer, Capabilities.Simulation, typeof(SimulatedRun));

    public async Task<SyntaxError?> StartSimulation(string name, double gameSeconds, CancellationToken cancel)
    {
        var (error, started) = await MainThread.Run<(SyntaxError? Error, Task Started)>(() =>
        {
            var window = FindScript(name);
            var main = MainSim.Inst;
            if (main.IsExecuting() || SimulatedRun.Busy)
                throw new ToolException("A Run is already in progress; use wait_run to wait for it or stop_run to stop it.");

            if (window.Parse() == null)
            {
                if (parseExceptionField!.GetValue(window) is not ParseException parseError)
                    throw new ToolException($"The game did not parse '{name}'.");
                return (SyntaxErrorOf(window, parseError), Task.CompletedTask);
            }

            // Everything simulate() would otherwise get from its arguments: this Save's Unlocks and items, no globals, a random seed.
            var args = new MainSim.LeaderboardStartArgs(name, main.GetSerializedUnlocks(), main.GetInventory(),
                new List<KeyValuePair<string, IPyObject>>(), "", "", LeaderboardType.simulation, true, EDlcType.None);
            main.Logger.Clear();
            main.ScheduleLeaderboardStart(args);
            return ((SyntaxError?)null, SimulatedRun.Expect(name, gameSeconds));
        }, cancel);

        await started;
        return error;
    }

    public Task<TemporaryFarm?> ReadTemporaryFarm() => MainThread.Run<TemporaryFarm?>(() =>
    {
        var main = MainSim.Inst;
        var run = SimulatedRun.Current;
        if (run == null || (object)main == null) return null;
        lock (lockField!.GetValue(main))
            return new TemporaryFarm(Items(run.Sim.farm.Items, includeZero: true), run.Sim.CurrentTime.Seconds);
    });
}
