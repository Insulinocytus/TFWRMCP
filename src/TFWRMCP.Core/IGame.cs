using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TFWRMCP;

/// <summary>Names of game features a Tool can depend on; the game layer reports which ones failed to resolve.</summary>
public static class Capabilities
{
    public const string State = "state";
    public const string Saves = "saves";
    public const string Scripts = "scripts";
    public const string Run = "run";
    public const string Unlock = "unlock";
    public const string Docs = "docs";
    public const string Builtins = "builtins";

    /// <summary>Dismissing the title menu; needed by every Tool, but only when the menu is actually open.</summary>
    public const string TitleMenu = "title_menu";
}

public enum GameScene
{
    /// <summary>The scene is (re)loading or no Save is set up yet.</summary>
    Loading,

    /// <summary>A Save is loaded but the title menu covers it.</summary>
    TitleMenu,

    /// <summary>A Save is loaded and the farm is usable.</summary>
    Playing,
}

public sealed class SaveList
{
    public string Current { get; set; } = "";
    public List<string> Saves { get; } = new();
}

/// <summary>The only seam between Core and the game. Implementations marshal onto the Unity main thread themselves.</summary>
public interface IGame
{
    /// <summary>Null when the capability works, otherwise why it does not.</summary>
    string? UnavailableReason(string capability);

    /// <summary>Reads only the requested parts of the game; the others are left empty. <see cref="StateParts.Run"/> is ignored.</summary>
    Task<GameState> GetState(StateParts parts);

    Task<GameScene> GetScene();

    /// <summary>Equivalent to clicking "Play" on the title menu.</summary>
    Task CloseTitleMenu();

    Task<SaveList> ListSaves();

    Task<bool> IsValidSaveName(string name);

    Task<string> GenerateSaveName();

    /// <summary>Creates the Save on disk without loading it.</summary>
    Task CreateSave(string name);

    /// <summary>
    /// Persists the current Save, then starts reloading the scene with <paramref name="name"/> active. Returns as soon
    /// as the reload has started: <see cref="GetScene"/> must report Loading from then until the new scene is up.
    /// Throws <see cref="OperationCanceledException"/> without reloading when <paramref name="cancel"/> is cancelled
    /// by the time the game would act.
    /// </summary>
    Task LoadSave(string name, CancellationToken cancel);

    /// <summary>Writes the current Save to disk; returns once it is persisted.</summary>
    Task SaveGame();

    Task<List<string>> ListScripts();

    /// <summary>Throws a <see cref="ToolException"/> when there is no Script of that name (exact match).</summary>
    Task<string> ReadScript(string name);

    /// <summary>
    /// Creates or overwrites the Script so that it shows in its code window and is persisted to disk. Throws a
    /// <see cref="ToolException"/> for a name the game rejects (invalid, or clashing with another window) and when
    /// the Script is being run. Returns true when the Script was newly created.
    /// </summary>
    Task<bool> WriteScript(string name, string code);

    /// <summary>Closes the Script's code window and removes it from disk; refuses while the Script is being run.</summary>
    Task DeleteScript(string name);

    /// <summary>
    /// Same as the human clicking run on the Script's code window. Throws a <see cref="ToolException"/> when the Script
    /// does not exist or any Run is in progress. Returns the syntax error when the Script does not parse, otherwise
    /// null once the Run has started (<see cref="RunStarted"/> has been raised by then). Throws
    /// <see cref="OperationCanceledException"/> without starting anything when <paramref name="cancel"/> is cancelled
    /// by the time the game would act.
    /// </summary>
    Task<SyntaxError?> StartRun(string name, CancellationToken cancel);

    /// <summary>Same as the human clicking stop: ends the current Run (or Leaderboard Run); raises <see cref="RunEnded"/>.</summary>
    Task StopRun();

    /// <summary>
    /// Everything that happens to Runs, including those the human starts. Raised from any thread, in order, and must
    /// never block.
    /// </summary>
    event Action<RunEvent> RunEvents;

    /// <summary>Game steps the Run in progress has executed so far, as <see cref="RunEnded.Ticks"/> counts them; never blocks.</summary>
    long RunTicks { get; }

    /// <summary>
    /// Looks the Unlock up as the research menu does and, in the same game tick, buys or upgrades it (including its
    /// hat) when <paramref name="whyNot"/> returns null for its facts. Facts are null when no such Unlock is listed.
    /// </summary>
    Task<UnlockAttempt> TryUnlock(string name, Func<UnlockFacts, string?> whyNot);

    /// <summary>
    /// Every Unlock the research menu lists, with its parent, extra requirement and the cost of each level as the game's
    /// Farm.GetUnlockCost computes it, plus the inventory; no side effects.
    /// </summary>
    Task<UnlockTree> GetUnlockTree();

    /// <summary>
    /// Starts (<paramref name="tool"/> is the running Tool's name) or ends (null) the Input Lock: while it lasts the
    /// game ignores every human input except F10 and shows the lock icon. Idempotent, and must also hold across a
    /// scene reload.
    /// </summary>
    Task SetInputLock(string? tool);

    /// <summary>
    /// The human pressed F10 during an Input Lock. The game has already released the lock by the time this is raised
    /// (from any thread, never blocking); Core stops the Run and abandons the Tool in progress.
    /// </summary>
    event Action UserTookOver;

    /// <summary>The page as the game's docs window shows it (placeholder for locked content); throws ToolException if there is no such page.</summary>
    Task<string> ReadDoc(string page);

    /// <summary>The names usable in a Script right now, and for the others which Unlocks grant them (and at which level).</summary>
    Task<BuiltinList> ListBuiltins();
}

/// <summary>Thrown by Tools and Game implementations for failures the Agent should see verbatim.</summary>
public sealed class ToolException : Exception
{
    public ToolException(string message) : base(message) { }
}

/// <summary>A Script that failed to parse; <see cref="StartIndex"/> is a character offset into <see cref="Source"/>.</summary>
public sealed class SyntaxError
{
    public string Message { get; set; } = "";
    public string Source { get; set; } = "";
    public int StartIndex { get; set; }
}

/// <summary>An Unlock that grants a name once it reaches <see cref="Level"/>.</summary>
public sealed class UnlockGrant
{
    public string Unlock { get; set; } = "";
    public int Level { get; set; }
}

/// <summary>Names a Script can use, spelled as in a Script. Names the game does not register for Scripts are absent.</summary>
public sealed class BuiltinList
{
    public List<string> Functions { get; } = new();
    public List<string> Keywords { get; } = new();
    public List<string> Entities { get; } = new();
    public List<string> Items { get; } = new();
    public List<string> Grounds { get; } = new();
    public List<string> Hats { get; } = new();

    /// <summary>Names not usable yet, by every Unlock that grants them.</summary>
    public Dictionary<string, List<UnlockGrant>> Locked { get; } = new();
}
