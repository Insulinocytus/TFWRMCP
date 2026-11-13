using System;
using System.Threading.Tasks;

namespace TFWRMCP;

/// <summary>Names of game features a Tool can depend on; the game layer reports which ones failed to resolve.</summary>
public static class Capabilities
{
    public const string State = "state";
}

/// <summary>The only seam between Core and the game. Implementations marshal onto the Unity main thread themselves.</summary>
public interface IGame
{
    /// <summary>Null when the capability works, otherwise why it does not.</summary>
    string? UnavailableReason(string capability);

    Task<GameState> GetState();
}

/// <summary>Thrown by Tools and Game implementations for failures the Agent should see verbatim.</summary>
public sealed class ToolException : Exception
{
    public ToolException(string message) : base(message) { }
}
