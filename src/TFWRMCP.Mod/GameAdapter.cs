using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Core.Platform;

namespace TFWRMCP.Mod;

/// <summary>Implements the Game seam against the live game. All game access happens on the Unity main thread.</summary>
public sealed class GameAdapter : IGame
{
    const BindingFlags NonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

    readonly Dictionary<string, string> unavailable = new();
    FieldInfo? simField;
    FieldInfo? lockField;

    /// <summary>Resolves every reflection target up front; failures are logged and degrade only the dependent Tools.</summary>
    public GameAdapter(Action<string> warn)
    {
        simField = Resolve(Capabilities.State, typeof(MainSim).GetField("sim", NonPublic), "MainSim.sim", warn);
        lockField = Resolve(Capabilities.State, typeof(MainSim).GetField("lockSimulation", NonPublic), "MainSim.lockSimulation", warn);
    }

    T? Resolve<T>(string capability, T? target, string what, Action<string> warn) where T : class
    {
        if (target == null)
        {
            var reason = $"{what} not found";
            unavailable[capability] = unavailable.TryGetValue(capability, out var prior) ? $"{prior}; {reason}" : reason;
            warn($"Capability '{capability}' unavailable: {reason}");
        }
        return target;
    }

    public string? UnavailableReason(string capability) =>
        unavailable.TryGetValue(capability, out var reason) ? reason : null;

    public Task<GameState> GetState() => MainThread.Run(ReadState);

    GameState ReadState()
    {
        // Unity null semantics: a destroyed MainSim compares equal to null but is not a null reference.
        var main = MainSim.Inst;
        if (main == null) throw new ToolException("The game scene is loading; try again shortly.");
        var sim = (Simulation?)simField!.GetValue(main);
        if (sim == null) throw new ToolException("No Save is loaded yet; try again shortly.");

        // The simulation thread mutates the grid under this lock.
        lock (lockField!.GetValue(main))
        {
            var grid = sim.farm.grid;
            var size = grid.WorldSize;
            var state = new GameState
            {
                Save = OptionHolder.GetString("activeSave", "Save0"),
                Inventory = Items(main.GetInventory(), includeZero: true),
                WorldSize = new Vec2 { X = size.x, Y = size.y },
                Run = new RunState { Status = main.IsExecuting() ? RunState.Running : RunState.Idle },
            };

            foreach (var unlock in ResourceManager.GetAllUnlocks()
                         .Where(u => u.enabled && PlatformManager.IsDlcInstalled(u.dlc))
                         .OrderBy(u => u.order).ThenBy(u => u.unlockName))
            {
                var level = main.NumUnlocked(unlock.unlockName);
                state.Unlocks.Add(new UnlockState
                {
                    Name = unlock.unlockName,
                    Level = level,
                    MaxLevel = unlock.maxUnlockLevel,
                    NextCost = level >= unlock.maxUnlockLevel ? null : Items(main.GetUnlockCost(unlock), includeZero: false),
                });
            }

            foreach (var drone in sim.farm.drones.Where(d => d != null))
                state.Drones.Add(new DroneState { Id = drone.DroneId, X = drone.pos.x, Y = drone.pos.y, Depth = drone.depth });

            for (var y = 0; y < size.y; y++)
            {
                var row = new List<TileState>(size.x);
                for (var x = 0; x < size.x; x++)
                {
                    ref var cell = ref grid.CellAt(x, y);
                    row.Add(new TileState
                    {
                        Entity = cell.entity?.objectSO.objectName,
                        Ground = grid.GroundAt(x, y).groundSo?.objectName ?? "",
                        Depth = cell.depth,
                    });
                }
                state.Tiles.Add(row);
            }

            return state;
        }
    }

    static Dictionary<string, double> Items(ItemBlock? block, bool includeZero)
    {
        var result = new Dictionary<string, double>();
        if (block?.items == null) return result;
        for (var id = 0; id < block.items.Length; id++)
            if (includeZero || block.items[id] != 0)
                result[StringIds.GetItemName(id)] = block.items[id];
        return result;
    }
}
