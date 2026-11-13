using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Core.Platform;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace TFWRMCP.Mod;

/// <summary>Implements the Game seam against the live game. All game access happens on the Unity main thread.</summary>
public sealed class GameAdapter : IGame
{
    const BindingFlags NonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

    readonly Dictionary<string, string> unavailable = new();
    FieldInfo? simField;
    FieldInfo? lockField;

    // Set by LoadSave; Unity null semantics make it read as null once the old scene is destroyed, until then GetScene reports Loading.
    MainSim? reloadFrom;

    FieldInfo? parseExceptionField;
    FieldInfo? spawnOffsetField;
    readonly Harmony harmony = new("com.insulinocytus.tfwrmcp");

    public event Action<RunEvent> RunEvents = delegate { };

    /// <summary>Resolves every reflection target up front; failures are logged and degrade only the dependent Tools.</summary>
    public GameAdapter(Action<string> warn)
    {
        simField = Resolve(Capabilities.State, typeof(MainSim).GetField("sim", NonPublic), "MainSim.sim", warn);
        lockField = Resolve(Capabilities.State, typeof(MainSim).GetField("lockSimulation", NonPublic), "MainSim.lockSimulation", warn);

        const BindingFlags Public = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
        foreach (var (type, name) in new[]
                 {
                     (typeof(Saver), "Save"), (typeof(Saver), "WaitForPendingSaves"), (typeof(Saver), "StopFileWatcher"),
                     (typeof(Saver), "CreateNewSaveGame"), (typeof(Saver), "IsValidSaveName"),
                     (typeof(SaveChooser), "GenerateUnusedSaveName"), (typeof(Menu), "LoadSave"), (typeof(Menu), "Play"),
                 })
            Resolve(Capabilities.Saves, type.GetMethod(name, Public), $"{type.Name}.{name}", warn);

        InstallScriptAndRunSupport(warn);
    }

    static MethodInfo? Method(Type type, string name) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == name);

    void InstallScriptAndRunSupport(Action<string> warn)
    {
        spawnOffsetField = Resolve(Capabilities.Scripts, typeof(Workspace).GetField("spawnWindowOffset", NonPublic), "Workspace.spawnWindowOffset", warn);
        foreach (var (type, name) in new[]
                 {
                     (typeof(Saver), "SaveCode"), (typeof(Saver), "IsValidCodeFileName"), (typeof(Workspace), "OpenCodeWindow"),
                     (typeof(Workspace), "IsValidFileName"), (typeof(CodeWindow), "ApplyExternalCode"), (typeof(Window), "Close"),
                 })
            Resolve(Capabilities.Scripts, Method(type, name), $"{type.Name}.{name}", warn);

        parseExceptionField = Resolve(Capabilities.Run, typeof(CodeWindow).GetField("parseException", NonPublic), "CodeWindow.parseException", warn);
        Resolve(Capabilities.Run, Method(typeof(CodeWindow), "PressExecuteOrStop"), "CodeWindow.PressExecuteOrStop", warn);
        if (simField == null)
        {
            Fail(Capabilities.Run, "MainSim.sim not found", warn);
            return;
        }

        RunHooks.Configure(e => RunEvents(e), warn, simField);
        Patch("Logger.WriteLog", typeof(global::Logger), "WriteLog", warn, prefix: nameof(RunHooks.WriteLog));
        Patch("Logger.LogError", typeof(global::Logger), "LogError", warn, prefix: nameof(RunHooks.LogError));
        Patch("MainSim.StartMainExecution", typeof(MainSim), "StartMainExecution", warn, prefix: nameof(RunHooks.StartMainExecution));
        Patch("MainSim.StopMainExecution", typeof(MainSim), "StopMainExecution", warn, prefix: nameof(RunHooks.StopMainExecution));
        Patch("Simulation.StopProgramExecution", typeof(Simulation), "StopProgramExecution", warn, prefix: nameof(RunHooks.StopProgramExecution));
        Patch("MainSim.StartLeaderboard", typeof(MainSim), "StartLeaderboard", warn,
            prefix: nameof(RunHooks.StartLeaderboardBegin), finalizer: nameof(RunHooks.StartLeaderboardEnd));
        Patch("LeaderboardManager.StopLeaderboardRun", typeof(LeaderboardManager), "StopLeaderboardRun", warn, prefix: nameof(RunHooks.StopLeaderboardRun));
        Patch("Leaderboard.OnPlayerLoaded", typeof(Leaderboard), "OnPlayerLoaded", warn, postfix: nameof(RunHooks.PlayerLoaded));
    }

    void Patch(string what, Type type, string method, Action<string> warn, string? prefix = null, string? postfix = null, string? finalizer = null)
    {
        var target = Resolve(Capabilities.Run, Method(type, method), what, warn);
        if (target == null) return;
        HarmonyMethod? Hook(string? name) => name == null ? null : new HarmonyMethod(typeof(RunHooks).GetMethod(name));
        try
        {
            harmony.Patch(target, prefix: Hook(prefix), postfix: Hook(postfix), finalizer: Hook(finalizer));
        }
        catch (Exception e)
        {
            Fail(Capabilities.Run, $"patching {what} failed ({e.Message})", warn);
        }
    }

    T? Resolve<T>(string capability, T? target, string what, Action<string> warn) where T : class
    {
        if (target == null) Fail(capability, $"{what} not found", warn);
        return target;
    }

    void Fail(string capability, string reason, Action<string> warn)
    {
        unavailable[capability] = unavailable.TryGetValue(capability, out var prior) ? $"{prior}; {reason}" : reason;
        warn($"Capability '{capability}' unavailable: {reason}");
    }

    public string? UnavailableReason(string capability) =>
        unavailable.TryGetValue(capability, out var reason) ? reason : null;

    public Task<GameState> GetState() => MainThread.Run(ReadState);

    public Task<GameScene> GetScene() => MainThread.Run(ReadScene);

    GameScene ReadScene()
    {
        if (reloadFrom != null) return GameScene.Loading;

        var main = MainSim.Inst;
        if (main == null) return GameScene.Loading;
        if (simField != null && simField.GetValue(main) == null) return GameScene.Loading;
        return main.menu.gameObject.activeInHierarchy ? GameScene.TitleMenu : GameScene.Playing;
    }

    public Task CloseTitleMenu() => MainThread.Run(() =>
    {
        UnityEngine.Debug.Log("TFWRMCP: closing the title menu");
        MainSim.Inst.menu.Play();
    });

    public Task<SaveList> ListSaves() => MainThread.Run(() =>
    {
        var list = new SaveList { Current = OptionHolder.GetString("activeSave", "Save0") };
        var root = Helper.persistentDataPath + "/Saves";
        if (Directory.Exists(root))
            list.Saves.AddRange(Directory.GetDirectories(root).Select(d => new DirectoryInfo(d).Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        return list;
    });

    public Task<bool> IsValidSaveName(string name) => MainThread.Run(() => Saver.IsValidSaveName(name));

    public Task<string> GenerateSaveName() => MainThread.Run(SaveChooser.GenerateUnusedSaveName);

    public Task CreateSave(string name) => MainThread.Run(() => Saver.CreateNewSaveGame(name));

    public async Task SaveGame()
    {
        if (!await MainThread.Run(() => Saver.Save(MainSim.Inst)).Unwrap())
            throw new ToolException("Saving the game failed; see the game's save failure notice.");
    }

    public async Task LoadSave(string name)
    {
        await MainThread.Run(() => Saver.WaitForPendingSaves()).Unwrap();
        await SaveGame();

        // Same as SaveChooser.LoadSave after the "unsaved changes" popup was answered with "save".
        await MainThread.Run(() =>
        {
            reloadFrom = MainSim.Inst;
            Saver.StopFileWatcher();
            MainSim.Inst.menu.LoadSave(name);
            SceneManager.LoadScene(0);
        });
    }

    public Task<List<string>> ListScripts() => MainThread.Run(() =>
        MainSim.Inst.workspace.codeWindows.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());

    static CodeWindow FindScript(string name) =>
        MainSim.Inst.workspace.codeWindows.TryGetValue(name, out var window)
            ? window
            : throw new ToolException($"No Script named '{name}' (names are case-sensitive; see list_scripts).");

    static bool IsBeingRun(CodeWindow window) => window.isExecuting && MainSim.Inst.IsExecuting();

    public Task<string> ReadScript(string name) => MainThread.Run(() => FindScript(name).CodeInput.text);

    public async Task<bool> WriteScript(string name, string code)
    {
        var created = await MainThread.Run(() =>
        {
            var workspace = MainSim.Inst.workspace;
            if (workspace.codeWindows.TryGetValue(name, out var existing))
            {
                if (IsBeingRun(existing))
                    throw new ToolException($"Script '{name}' is being run; stop the Run before overwriting it.");
                existing.ApplyExternalCode(code);
                return false;
            }

            if (!Saver.IsValidCodeFileName(name) || name.Length == 0)
                throw new ToolException($"Invalid Script name '{name}': a Script name must not be empty, start with '.', " +
                    "end with '.' or a space, contain any of < > : \" / \\ | ? * or control characters, start with a " +
                    "reserved Windows device name (CON, PRN, AUX, NUL, COM1-9, LPT1-9) or be __builtins__.");
            if (!workspace.IsValidFileName(name))
                throw new ToolException($"Script name '{name}' clashes with an open window (names are compared " +
                    "case-insensitively and documentation windows count too).");

            var offset = -workspace.workspaceContainer.anchoredPosition + (UnityEngine.Vector2)spawnOffsetField!.GetValue(workspace);
            workspace.OpenCodeWindow(name, code, offset);
            if (!workspace.codeWindows.ContainsKey(name)) throw new ToolException($"The game did not open a code window for '{name}'.");
            return true;
        });
        await SaveScripts();
        return created;
    }

    public async Task DeleteScript(string name)
    {
        await MainThread.Run(() =>
        {
            var window = FindScript(name);
            if (IsBeingRun(window)) throw new ToolException($"Script '{name}' is being run; stop the Run before deleting it.");
            window.GetComponent<Window>().Close();
        });
        await SaveScripts();
    }

    async Task SaveScripts()
    {
        if (!await MainThread.Run(() => Saver.SaveCode(MainSim.Inst)).Unwrap())
            throw new ToolException("Writing the Scripts to disk failed; see the game's save failure notice.");
    }

    public Task<SyntaxError?> StartRun(string name) => MainThread.Run(() =>
    {
        var window = FindScript(name);
        var main = MainSim.Inst;
        if (main.IsExecuting())
            throw new ToolException("A Run is already in progress; use wait_run to wait for it or stop_run to stop it.");

        window.PressExecuteOrStop();
        if (main.IsExecuting()) return null;

        if (parseExceptionField!.GetValue(window) is not ParseException error)
            throw new ToolException($"The game did not start the Run of '{name}'.");
        return new SyntaxError { Message = Localizer.Localize(error.Message), Source = window.CodeInput.text, StartIndex = error.startIndex };
    });

    public Task StopRun() => MainThread.Run(() => MainSim.Inst.StopMainExecution());

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
