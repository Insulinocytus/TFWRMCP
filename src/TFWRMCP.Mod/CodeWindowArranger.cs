using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace TFWRMCP.Mod;

/// <summary>
/// Keeps the code windows around the farm (see <see cref="Layout"/>): whenever the set of windows the game marks as running
/// changes (a Run starting, ending, or a window being executed for the first time in a Run), a Script is created, deleted or
/// renamed (by the player or a Tool), the farm changes size, or a window's line count has stopped changing for a moment, and
/// when the option is switched on, windows that are running or focused open at their automatic size, the rest fold, and all
/// glide to their places. Windows docked together are one unit, moved by their top window; a group holding a documentation
/// window is left alone. Windows are tracked as offsets from the farm's top-left corner, so the game recentering its workspace
/// container does not matter.
/// </summary>
public sealed class CodeWindowArranger : MonoBehaviour
{
    const double Duration = 0.5;
    const double TypingPause = 0.5;
    // CodeWindow recomputes its automatic size two frames after it is folded or unfolded.
    const int SettleFrames = 3;

    sealed class Move
    {
        public Vector2 From;
        public Vector2 To;
        public double Start;
    }

    readonly Dictionary<Window, Move> moves = new();
    readonly Dictionary<CodeWindow, (bool Running, bool Focused)> decided = new();
    readonly List<List<CodeWindow>> groups = new();
    readonly Dictionary<CodeWindow, int> lineCounts = new();
    readonly HashSet<CodeWindow> running = new();
    readonly List<Window> finished = new();
    readonly List<Unit> units = new();
    readonly List<Unit> members = new();
    readonly Dictionary<string, Window> byName = new();
    static CodeWindowArranger? instance;
    Plugin plugin = null!;
    Workspace? workspace;
    bool wasOn, pending, planDue, broken, rearrange;
    int settle;
    double typedAt = -1;
    Vector2Int? farmSize;

    public void Install(Plugin owner)
    {
        plugin = owner;
        instance = this;
        FarmBounds.Install();
        Patching.Patch(typeof(CodeWindowArranger), AccessTools.Method(typeof(CodeWindow), nameof(CodeWindow.UpdateTokens)),
            "CodeWindow.UpdateTokens", postfix: nameof(TextChanged));
        Patching.Patch(typeof(CodeWindowArranger), AccessTools.Method(typeof(Workspace), nameof(Workspace.OpenCodeWindow)),
            "Workspace.OpenCodeWindow", postfix: nameof(ScriptsChanged));
        Patching.Patch(typeof(CodeWindowArranger), AccessTools.DeclaredMethod(typeof(Workspace), nameof(Workspace.RemoveWindow)),
            "Workspace.RemoveWindow", postfix: nameof(ScriptsChanged));
        Patching.Patch(typeof(CodeWindowArranger), AccessTools.DeclaredMethod(typeof(Workspace), nameof(Workspace.RenameWindow)),
            "Workspace.RenameWindow", postfix: nameof(ScriptsChanged));
    }

    // CodeWindow.UpdateTokens(), postfix: true when the text changed, possibly on the simulation thread (an import parsing).
    static void TextChanged(CodeWindow __instance, bool __result) => Patching.Safely(nameof(TextChanged), () =>
    {
        if (__result) MainThread.Post(() => instance?.CountLines(__instance));
    });

    // Workspace.OpenCodeWindow, RemoveWindow and RenameWindow, postfix: whoever creates, deletes or renames a Script, a Tool included.
    static void ScriptsChanged() => Patching.Safely(nameof(ScriptsChanged), () =>
    {
        if (instance != null) instance.rearrange = true;
    });

    // A changed line count starts the pause and any edit while it runs restarts it; edits within a line and focus changes alone do nothing.
    void CountLines(CodeWindow code)
    {
        if (code == null || code.CodeInput == null) return;
        var lines = 1;
        foreach (var c in code.CodeInput.text)
            if (c == '\n') lines++;
        var changed = lineCounts.TryGetValue(code, out var before) && before != lines;
        if (changed || typedAt >= 0) typedAt = Time.unscaledTimeAsDouble;
        lineCounts[code] = lines;
    }

    void Update()
    {
        if (broken) return;
        try { Tick(); }
        catch (Exception e)
        {
            broken = true;
            Patching.Warn($"Code window auto-arrange is off: {e.Message}");
        }
    }

    void Tick()
    {
        var main = MainSim.Inst;
        var space = main == null ? null : main.workspace;
        if (space == null || !plugin.AutoArrangeWindows.Value)
        {
            moves.Clear();
            wasOn = pending = planDue = rearrange = false;
            typedAt = -1;
            return;
        }

        if (space != workspace)
        {
            workspace = space;
            moves.Clear();
            running.Clear();
            farmSize = null;
            typedAt = -1;
            wasOn = false;
        }

        if (!space.gameObject.activeInHierarchy) return;
        if (!wasOn)
        {
            wasOn = true;
            pending = true;
        }

        var changed = RunningChanged(space);
        changed |= FarmResized(main!);
        changed |= TypingStopped();
        if (changed || rearrange) pending = true;
        rearrange = false;
        if (pending)
        {
            Decide(space);
            pending = planDue = false;
            settle = SettleFrames;
        }
        else if (settle > 0 && --settle == 0)
        {
            planDue = true;
        }

        if (planDue && Plan(space)) planDue = false;
        Glide(space);
    }

    // The size of the farm being shown, so expanding, set_world_size(), a Run restoring it and simulate() are all seen.
    bool FarmResized(MainSim main)
    {
        var size = main.GetRenderPackage()?.groundData?.sizexy;
        if (size == null) return false;
        var changed = farmSize != null && farmSize != size;
        farmSize = size;
        return changed;
    }

    bool TypingStopped()
    {
        if (typedAt < 0 || Time.unscaledTimeAsDouble - typedAt < TypingPause) return false;
        typedAt = -1;
        return true;
    }

    bool RunningChanged(Workspace space)
    {
        var count = 0;
        var changed = false;
        foreach (var pair in space.codeWindows)
        {
            if (!pair.Value.isExecuting) continue;
            count++;
            changed |= !running.Contains(pair.Value);
        }

        if (!changed && count == running.Count) return false;
        running.Clear();
        foreach (var pair in space.codeWindows)
            if (pair.Value.isExecuting) running.Add(pair.Value);
        return true;
    }

    // Unfolds the windows that should be open and folds the rest; their sizes settle over the next frames.
    void Decide(Workspace space)
    {
        foreach (var gone in lineCounts.Keys.Where(code => code == null).ToList()) lineCounts.Remove(gone);
        decided.Clear();
        groups.Clear();
        foreach (var pair in space.codeWindows)
        {
            var group = DockGroup(pair.Value.GetComponent<Window>());
            if (group == null) continue;
            groups.Add(group);
            foreach (var code in group)
            {
                var window = code.GetComponent<Window>();
                var state = (code.isExecuting, code.CodeInput.isFocused);
                decided[code] = state;
                if (Layout.IsExpanded(state.isExecuting, state.isFocused))
                {
                    if (window.isMinimized) window.SetMinmized(false);
                    window.playerSetSize = Vector2.zero;
                    window.UpdateSize();
                }
                else if (!window.isMinimized)
                {
                    window.SetMinmized(true);
                }
            }
        }
    }

    // The code windows docked together, top first, when <paramref name="top"/> is the top of its group and the group has only code windows.
    static List<CodeWindow>? DockGroup(Window top)
    {
        if (top.dockedParent != null) return null;
        var group = new List<CodeWindow>();
        for (var window = top; window != null; window = window.DockedChild)
        {
            if (!window.TryGetComponent<CodeWindow>(out var code)) return null;
            group.Add(code);
        }

        return group;
    }

    bool Plan(Workspace space)
    {
        if (!FarmBounds.TryGet(space, out var farm)) return false;
        units.Clear();
        byName.Clear();
        foreach (var group in groups)
        {
            if (!TryUnit(space, group, out var unit)) continue;
            units.Add(unit);
            byName[unit.Name] = group[0].GetComponent<Window>();
        }

        var origin = new Vector2(farm.Left, farm.Top);
        var now = Time.unscaledTimeAsDouble;
        moves.Clear();
        foreach (var place in Layout.Arrange(farm, units))
        {
            var window = byName[place.Name];
            moves[window] = new Move { From = Corner(space, window, false) - origin, To = new Vector2(place.Left, place.Top) - origin, Start = now };
        }

        return true;
    }

    // A group is gone from the plan when one of its windows was closed since it was decided.
    bool TryUnit(Workspace space, List<CodeWindow> group, out Unit unit)
    {
        unit = default;
        members.Clear();
        foreach (var code in group)
        {
            if (code == null) return false;
            var window = code.GetComponent<Window>();
            var topLeft = Corner(space, window, false);
            var bottomRight = Corner(space, window, true);
            var state = decided[code];
            members.Add(new Unit(window.windowName, bottomRight.x - topLeft.x, topLeft.y - bottomRight.y, state.Running, state.Focused));
        }

        unit = Layout.Group(members);
        return true;
    }

    void Glide(Workspace space)
    {
        if (moves.Count == 0 || !FarmBounds.TryGet(space, out var farm)) return;
        var origin = new Vector2(farm.Left, farm.Top);
        var now = Time.unscaledTimeAsDouble;
        foreach (var pair in moves)
        {
            var window = pair.Key;
            if (window == null)
            {
                finished.Add(window!);
                continue;
            }

            var move = pair.Value;
            var at = origin + new Vector2(
                Tween.EaseOutExpo(move.From.x, move.To.x, move.Start, Duration, now),
                Tween.EaseOutExpo(move.From.y, move.To.y, move.Start, Duration, now));
            window.WindowRect.anchoredPosition += at - Corner(space, window, false);
            if (now >= move.Start + Duration) finished.Add(window);
        }

        foreach (var window in finished)
        {
            moves.Remove(window);
            if (window != null) space.OnWindowRectChanged(window);
        }

        finished.Clear();
    }

    // A window corner in the workspace container's coordinates: the top-left, or the bottom-right.
    static Vector2 Corner(Workspace space, Window window, bool bottomRight)
    {
        var rect = window.WindowRect;
        var local = bottomRight ? new Vector3(rect.rect.xMax, rect.rect.yMin) : new Vector3(rect.rect.xMin, rect.rect.yMax);
        return space.container.InverseTransformPoint(rect.TransformPoint(local));
    }
}
