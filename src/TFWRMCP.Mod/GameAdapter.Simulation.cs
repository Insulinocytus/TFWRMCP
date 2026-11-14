using System;
using System.Collections.Generic;
using System.Linq;
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

    public async Task<SyntaxError?> StartSimulatedRun(string name, SimulatedRunOptions options, CancellationToken cancel)
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

            var args = new MainSim.LeaderboardStartArgs(name, OverrideUnlocks(main.GetSerializedUnlocks(), options.Unlocks),
                OverrideItems(main.GetInventory(), options.Items), Globals(options.Globals), "", "", LeaderboardType.simulation, true,
                EDlcType.None, options.Seed);
            main.Logger.Clear();
            main.ScheduleLeaderboardStart(args);
            return ((SyntaxError?)null, SimulatedRun.Expect(name, options.WarmupSeconds, options.GameSeconds));
        }, cancel);

        await started;
        return error;
    }

    // The Save's serialized Unlocks are "name" for level 1 and "name_N" above it, as Farm's constructor reads them.
    static string UnlockName(string serialized)
    {
        var underscore = serialized.LastIndexOf('_');
        return underscore > 0 && int.TryParse(serialized.Substring(underscore + 1), out _) ? serialized.Substring(0, underscore) : serialized;
    }

    static List<string> OverrideUnlocks(List<string> serialized, IReadOnlyDictionary<string, int> levels)
    {
        var result = serialized.Where(s => !levels.ContainsKey(UnlockName(s))).ToList();
        foreach (var (name, level) in levels)
            if (level > 0) result.Add(level == 1 ? name : $"{name}_{level}");
        return result;
    }

    static ItemBlock OverrideItems(ItemBlock items, IReadOnlyDictionary<string, double> amounts)
    {
        foreach (var (name, amount) in amounts) items.SetNumber(StringIds.GetItemId(name), amount);
        return items;
    }

    static List<KeyValuePair<string, IPyObject>> Globals(IReadOnlyDictionary<string, object> globals) =>
        globals.Select(g => new KeyValuePair<string, IPyObject>(g.Key, g.Value switch
        {
            bool flag => new PyBool(flag),
            double number => new PyNumber(number),
            _ => new PyString((string)g.Value),
        })).ToList();

    public Task<TemporaryFarm?> ReadTemporaryFarm() => MainThread.Run<TemporaryFarm?>(() =>
    {
        var main = MainSim.Inst;
        var run = SimulatedRun.Current;
        if (run == null || (object)main == null) return null;
        lock (lockField!.GetValue(main))
            return new TemporaryFarm(Items(run.Sim.farm.Items, includeZero: true), run.Sim.CurrentTime.Seconds);
    });
}
