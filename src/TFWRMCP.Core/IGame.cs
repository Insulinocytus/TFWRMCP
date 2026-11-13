using System;
using System.Collections.Generic;
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

    Task<GameState> GetState();

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
    /// </summary>
    Task LoadSave(string name);

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
    /// null once the Run has started (<see cref="RunStarted"/> has been raised by then).
    /// </summary>
    Task<SyntaxError?> StartRun(string name);

    /// <summary>Same as the human clicking stop: ends the current Run (or Leaderboard Run); raises <see cref="RunEnded"/>.</summary>
    Task StopRun();

    /// <summary>
    /// Everything that happens to Runs, including those the human starts. Raised from any thread, in order, and must
    /// never block.
    /// </summary>
    event Action<RunEvent> RunEvents;

    /// <summary>Facts the Unlock checks need, as the research menu sees the Unlock; null when no such Unlock is listed there.</summary>
    Task<UnlockFacts?> GetUnlockFacts(string name);

    /// <summary>Buys or upgrades the Unlock exactly like the research menu does (including its hat); false if the game refused.</summary>
    Task<bool> BuyUnlock(string name);

    /// <summary>The page as the game's docs window shows it (placeholder for locked content); throws ToolException if there is no such page.</summary>
    Task<string> ReadDoc(string page);
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
