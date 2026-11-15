using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TFWRMCP.Mod;

/// <summary>
/// While the config says so, hides every code and documentation window: its CanvasGroup turns transparent and lets the
/// pointer through, and the game's own hit test for scrolling sees no window. The windows stay active, so Runs, errors and
/// Tools work as before. A game member that is missing turns the feature off.
/// </summary>
public sealed class WindowHiding : MonoBehaviour
{
    static WindowHiding? instance;
    readonly HashSet<CanvasGroup> hidden = new();
    Plugin plugin = null!;
    bool broken;

    public static bool Active => instance != null && !instance.broken && instance.plugin.HideWindows.Value;

    public void Install(Plugin owner)
    {
        plugin = owner;
        instance = this;
        Patching.Patch(typeof(WindowHiding), AccessTools.Method(typeof(WorkspaceBase), nameof(WorkspaceBase.GetOpenWindow), new[] { typeof(Vector2) }),
            "WorkspaceBase.GetOpenWindow(Vector2)", postfix: nameof(NoWindow));
    }

    // WorkspaceBase.GetOpenWindow(Vector2), postfix: the window under the cursor takes the scroll that would zoom the workspace; the title screen's workspace is not hidden.
    static void NoWindow(WorkspaceBase __instance, ref Window? __result)
    {
        if (Active && __instance is Workspace) __result = null;
    }

    void LateUpdate()
    {
        if (broken) return;
        try { Tick(); }
        catch (Exception e)
        {
            broken = true;
            Show();
            Patching.Warn($"Hiding windows is off: {e.Message}");
        }
    }

    void Tick()
    {
        var main = MainSim.Inst;
        var space = main == null ? null : main.workspace;
        if (space == null || !plugin.HideWindows.Value)
        {
            Show();
            return;
        }

        foreach (var window in space.OpenWindows)
        {
            if (!window.TryGetComponent<CanvasGroup>(out var group)) group = window.gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0;
            group.blocksRaycasts = group.interactable = false;
            hidden.Add(group);
        }
    }

    void Show()
    {
        foreach (var group in hidden)
        {
            if (group == null) continue;
            group.alpha = 1;
            group.blocksRaycasts = group.interactable = true;
        }

        hidden.Clear();
    }
}
