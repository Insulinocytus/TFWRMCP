using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Core.Platform;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace TFWRMCP.Mod;

/// <summary>Implements the Game seam against the live game. All game access happens on the Unity main thread.</summary>
public sealed partial class GameAdapter : IGame
{
    const BindingFlags AnyMember = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    const string SceneLoading = "The game scene is loading; try again shortly.";

    readonly Dictionary<string, string> unavailable = new();
    FieldInfo? simField;
    FieldInfo? lockField;

    // Set by LoadSave; Unity null semantics make it read as null once the old scene is destroyed, until then GetScene reports Loading.
    MainSim? reloadFrom;

    FieldInfo? parseExceptionField;
    FieldInfo? spawnOffsetField;
    readonly Harmony harmony = new("com.insulinocytus.tfwrmcp");
    readonly Action<string> info;

    public event Action<RunEvent> RunEvents = delegate { };

    /// <summary>
    /// Resolves every reflection target up front, by type name so that a renamed game type degrades only the dependent
    /// Tools instead of failing the Mod's startup; failures are logged.
    /// </summary>
    public GameAdapter(Action<string> warn, Action<string> info, InputLock inputLock)
    {
        this.info = info;
        this.inputLock = inputLock;
        simField = Resolve(Capabilities.State, Field("MainSim", "sim"), "MainSim.sim", warn);
        lockField = Resolve(Capabilities.State, Field("MainSim", "lockSimulation"), "MainSim.lockSimulation", warn);

        ResolveMethods(Capabilities.Saves, warn,
            ("Saver", "Save"), ("Saver", "WaitForPendingSaves"), ("Saver", "StopFileWatcher"), ("Saver", "CreateNewSaveGame"),
            ("Saver", "IsValidSaveName"), ("SaveChooser", "GenerateUnusedSaveName"), ("Menu", "LoadSave"));
        ResolveMethods(Capabilities.TitleMenu, warn, ("Menu", "Play"));

        InstallScriptAndRunSupport(warn);
        ResolveUnlockDocs(warn);
    }

    // Overload-safe: the first method of that name, which is enough to tell that it exists.
    static MethodInfo? Method(Type? type, string name) => type?.GetMethods(AnyMember).FirstOrDefault(m => m.Name == name);

    static MethodInfo? Method(string type, string name) => Method(AccessTools.TypeByName(type), name);

    static FieldInfo? Field(string type, string name) => AccessTools.TypeByName(type)?.GetField(name, AnyMember);

    static string ActiveSave() => OptionHolder.GetString("activeSave", "Save0");

    void ResolveMethods(string capability, Action<string> warn, params (string Type, string Name)[] targets)
    {
        foreach (var (type, name) in targets) Resolve(capability, Method(type, name), $"{type}.{name}", warn);
    }

    void InstallScriptAndRunSupport(Action<string> warn)
    {
        spawnOffsetField = Resolve(Capabilities.Scripts, Field("Workspace", "spawnWindowOffset"), "Workspace.spawnWindowOffset", warn);
        ResolveMethods(Capabilities.Scripts, warn,
            ("Saver", "SaveCode"), ("Saver", "IsValidCodeFileName"), ("Workspace", "OpenCodeWindow"),
            ("Workspace", "IsValidFileName"), ("CodeWindow", "ApplyExternalCode"), ("Window", "Close"));

        parseExceptionField = Resolve(Capabilities.Run, Field("CodeWindow", "parseException"), "CodeWindow.parseException", warn);
        ResolveMethods(Capabilities.Run, warn, ("CodeWindow", "PressExecuteOrStop"));

        RunHooks.Configure(e => RunEvents(e), warn);
        Patch("Logger", "WriteLog", warn, prefix: nameof(RunHooks.WriteLog));
        Patch("Logger", "LogError", warn, prefix: nameof(RunHooks.LogError));
        Patch("MainSim", "StartMainExecution", warn, prefix: nameof(RunHooks.StartMainExecution));
        Patch("MainSim", "StopMainExecution", warn, prefix: nameof(RunHooks.StopMainExecution));
        Patch("Simulation", "StopProgramExecution", warn, prefix: nameof(RunHooks.StopProgramExecution));
        Patch("MainSim", "StartLeaderboard", warn,
            prefix: nameof(RunHooks.StartLeaderboardBegin), finalizer: nameof(RunHooks.StartLeaderboardEnd));
        Patch("LeaderboardManager", "StopLeaderboardRun", warn, prefix: nameof(RunHooks.StopLeaderboardRun));
        Patch("Leaderboard", "OnPlayerLoaded", warn, postfix: nameof(RunHooks.PlayerLoaded));
    }

    void Patch(string type, string method, Action<string> warn, string? prefix = null, string? postfix = null, string? finalizer = null)
    {
        var what = $"{type}.{method}";
        var target = Resolve(Capabilities.Run, Method(type, method), what, warn);
        if (target == null) return;
        HarmonyMethod? Hook(string? name) => name == null ? null : new HarmonyMethod(Method(typeof(RunHooks), name));
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

    public Task<GameState> GetState(StateParts parts) => MainThread.Run(() => ReadState(parts));

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
        info("closing the title menu");
        MainSim.Inst.menu.Play();
    });

    public Task<SaveList> ListSaves() => MainThread.Run(() =>
    {
        var list = new SaveList { Current = ActiveSave() };
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

    public async Task LoadSave(string name, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        await MainThread.Run(() => Saver.WaitForPendingSaves()).Unwrap();
        await SaveGame();

        // Same as SaveChooser.LoadSave after the "unsaved changes" popup was answered with "save".
        await MainThread.Run(() =>
        {
            reloadFrom = MainSim.Inst;
            Saver.StopFileWatcher();
            MainSim.Inst.menu.LoadSave(name);
            SceneManager.LoadScene(0);
        }, cancel);
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

    public Task<SyntaxError?> StartRun(string name, CancellationToken cancel) => MainThread.Run(() =>
    {
        var window = FindScript(name);
        var main = MainSim.Inst;
        if (main.IsExecuting())
            throw new ToolException("A Run is already in progress; use wait_run to wait for it or stop_run to stop it.");

        window.PressExecuteOrStop();
        if (main.IsExecuting()) return null;

        if (parseExceptionField!.GetValue(window) is not ParseException error)
            throw new ToolException($"The game did not start the Run of '{name}'.");
        return new SyntaxError { Message = CodeUtilities.RemoveSyntaxTags(Localizer.Localize(error.Message)), Source = window.CodeInput.text, StartIndex = error.startIndex };
    }, cancel);

    public Task StopRun() => MainThread.Run(() =>
    {
        if (MainSim.Inst.IsExecuting()) MainSim.Inst.StopMainExecution();
    });

    GameState ReadState(StateParts parts)
    {
        var main = MainSim.Inst;
        if (main == null) throw new ToolException(SceneLoading);
        var sim = (Simulation?)simField!.GetValue(main);
        if (sim == null) throw new ToolException("No Save is loaded yet; try again shortly.");

        // The simulation thread mutates the grid under this lock.
        lock (lockField!.GetValue(main))
        {
            var grid = sim.farm.grid;
            var size = grid.WorldSize;
            var state = new GameState();
            if (parts.HasFlag(StateParts.Save)) state.Save = ActiveSave();
            if (parts.HasFlag(StateParts.Inventory)) state.Inventory = Items(main.GetInventory(), includeZero: true);
            if (parts.HasFlag(StateParts.WorldSize)) state.WorldSize = new Vec2 { X = size.x, Y = size.y };

            if (parts.HasFlag(StateParts.Unlocks))
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

            if (parts.HasFlag(StateParts.Drones))
                foreach (var drone in sim.farm.drones.Where(d => d != null))
                    state.Drones.Add(new DroneState { Id = drone.DroneId, X = drone.pos.x, Y = drone.pos.y, Depth = drone.depth });

            if (parts.HasFlag(StateParts.Tiles))
                for (var y = 0; y < size.y; y++)
                {
                    var row = new List<TileState>(size.x);
                    for (var x = 0; x < size.x; x++)
                    {
                        ref var cell = ref grid.CellAt(x, y);
                        var entity = cell.entity;
                        var harvestable = entity?.Harvestable ?? false;
                        row.Add(new TileState
                        {
                            Entity = entity?.objectSO.objectName,
                            Ground = grid.GroundAt(x, y).groundSo?.objectName ?? "",
                            Depth = cell.depth,
                            Harvestable = harvestable,
                            Water = Math.Round(cell.waterVolume, 2),
                            // Same rule as the game's hover tooltip: every Growable except Apple.
                            Grown = entity is Growable growable && entity.objectSO.className != "Apple" && !harvestable
                                ? Math.Round(Math.Min(growable.GrownPercent, 1.0), 2)
                                : null,
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
