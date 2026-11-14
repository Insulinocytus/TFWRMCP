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
        var scheduledStart = Resolve(Capabilities.Simulation, Field("MainSim", "startLeaderboardNow"), "MainSim.startLeaderboardNow", warn);
        if (UnavailableReason(Capabilities.Run) != null || UnavailableReason(Capabilities.State) != null)
            Fail(Capabilities.Simulation, "the Run or State capability is unavailable", warn);
        if (UnavailableReason(Capabilities.Simulation) != null) return;

        SimulatedRun.Configure(e => RunEvents(e), warn, simField, lockField, scheduledStart);
        PatchSimulation("MainSim", "StartLeaderboard", warn, finalizer: nameof(SimulatedRun.StartLeaderboardEnd));
        PatchSimulation("Simulation", "RunNextStep", warn, prefix: nameof(SimulatedRun.BeforeStep));
        PatchSimulation("Simulation", "StopProgramExecution", warn, prefix: nameof(SimulatedRun.ProgramEnding));
        PatchSimulation("MainSim", "Update", warn, prefix: nameof(SimulatedRun.BeforeUpdate), postfix: nameof(SimulatedRun.AfterUpdate));
        PatchSimulation("MainSim", "RestoreMainSim", warn, postfix: nameof(SimulatedRun.Restored));
    }

    void PatchSimulation(string type, string method, Action<string> warn, string? prefix = null, string? postfix = null, string? finalizer = null) =>
        Patch(type, method, warn, prefix, postfix, finalizer, Capabilities.Simulation, typeof(SimulatedRun));

    public async Task<SyntaxError?> StartSimulatedRun(string name, SimulatedRunOptions options, CancellationToken cancel)
    {
        var (error, pending) = await MainThread.Run<(SyntaxError? Error, SimulatedRun.Pending? Pending)>(() =>
        {
            var window = FindScript(name);
            var main = MainSim.Inst;
            if (main.IsExecuting() || SimulatedRun.Busy)
                throw new ToolException("A Run is already in progress; use wait_run to wait for it or stop_run to stop it.");

            if (ParseError(window, name) is { } syntaxError) return (syntaxError, null);

            var args = new MainSim.LeaderboardStartArgs(name, OverrideUnlocks(main.GetSerializedUnlocks(), options.Unlocks),
                OverrideItems(main.GetInventory(), options.Items), Globals(options.Globals), "", "", LeaderboardType.simulation, true,
                EDlcType.None, options.Seed);
            main.Logger.Clear();
            main.ScheduleLeaderboardStart(args);
            return (null, SimulatedRun.Expect(main, name, options.WarmupSeconds, options.GameSeconds));
        }, cancel);

        if (pending != null) await SimulatedRun.WaitForStart(pending, cancel);
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

    public Task<TemporaryFarm?> ReadTemporaryFarm() => MainThread.Run(() =>
    {
        var main = MainSim.Inst;
        return (object)main == null ? null : SimulatedRun.Read(main);
    });
}
