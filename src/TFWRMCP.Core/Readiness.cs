using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace TFWRMCP;

/// <summary>Brings the game to a playable state before a Tool touches it.</summary>
public static class Readiness
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Waits out a scene reload and dismisses the title menu; throws if the game never becomes playable.</summary>
    public static async Task Ensure(IGame game)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var scene = await game.GetScene();
            if (scene == GameScene.Playing) return;
            if (scene == GameScene.TitleMenu) await game.CloseTitleMenu();
            if (clock.Elapsed > Timeout)
                throw new ToolException($"The game did not become playable within {Timeout.TotalSeconds:0} seconds (scene: {scene}).");
            await Task.Delay(PollInterval);
        }
    }
}
