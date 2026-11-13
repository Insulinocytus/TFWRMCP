using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TFWRMCP;

/// <summary>Names of game features a Tool can depend on; the game layer reports which ones failed to resolve.</summary>
public static class Capabilities
{
    public const string State = "state";
    public const string Saves = "saves";
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
}

/// <summary>Thrown by Tools and Game implementations for failures the Agent should see verbatim.</summary>
public sealed class ToolException : Exception
{
    public ToolException(string message) : base(message) { }
}
