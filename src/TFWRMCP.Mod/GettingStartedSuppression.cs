using HarmonyLib;

namespace TFWRMCP.Mod;

/// <summary>Keeps the game from opening the Getting Started docs when a Save that was never saved is loaded, when that is switched on.</summary>
public static class GettingStartedSuppression
{
    const string Page = "docs/getting_started.md";

    static Plugin plugin = null!;
    static bool loading;

    public static void Install(Plugin owner)
    {
        plugin = owner;
        Patching.Patch(typeof(GettingStartedSuppression), AccessTools.Method(typeof(Saver), nameof(Saver.Load)),
            "Saver.Load", prefix: nameof(LoadStarting), finalizer: nameof(LoadFinished));
        Patching.Patch(typeof(GettingStartedSuppression), AccessTools.Method(typeof(Workspace), nameof(Workspace.AddNewDocsWindow)),
            "Workspace.AddNewDocsWindow", prefix: nameof(Opens));
    }

    // Saver.Load(MainSim), prefix and finalizer: it opens the page for a Save without data; elsewhere the player opens it.
    static void LoadStarting() => loading = true;

    static void LoadFinished() => loading = false;

    // Workspace.AddNewDocsWindow(string doc, Vector2 offset), prefix: false skips the window.
    static bool Opens(string __0) => !(loading && __0 == Page && plugin.SuppressGettingStarted.Value);
}
