namespace TFWRMCP;

/// <summary>
/// Whether the Mod or the player holds the camera. The player's camera operations take it from the Mod, which takes it back
/// 5 seconds after the last one, or at once when the farm's size changes or a Leaderboard Run starts or ends.
/// Times are in seconds on any monotonic clock.
/// </summary>
public sealed class CameraTakeover
{
    const double Idle = 5;

    double? lastOperation;

    /// <summary>WASD pan, scroll zoom with the cursor off the windows, or a drag pan.</summary>
    public void PlayerOperated(double now) => lastOperation = now;

    public void FarmResized() => lastOperation = null;

    public void LeaderboardRunStarted() => lastOperation = null;

    public void LeaderboardRunEnded() => lastOperation = null;

    public bool InControl(double now) => lastOperation == null || now - lastOperation >= Idle;
}
