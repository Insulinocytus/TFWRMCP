using System;
using System.Collections.Generic;
using UnityEngine;

namespace TFWRMCP.Mod;

/// <summary>
/// Keeps the single code windows around the farm (see <see cref="Layout"/>): whenever the set of windows the game marks as
/// running changes (a Run starting, ending, or a window being executed for the first time in a Run) and when the option is
/// switched on, windows that are running or focused open at their automatic size, the rest fold, and all glide to their places.
/// Windows are tracked as offsets from the farm's top-left corner, so the game recentering its workspace container does not matter.
/// </summary>
public sealed class CodeWindowArranger : MonoBehaviour
{
    const double Duration = 0.5;
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
    readonly HashSet<CodeWindow> running = new();
    readonly List<Window> finished = new();
    readonly List<Unit> units = new();
    readonly Dictionary<string, Window> byName = new();
    Plugin plugin = null!;
    Workspace? workspace;
    bool wasOn, pending, planDue, broken;
    int settle;

    public void Install(Plugin owner)
    {
        plugin = owner;
        FarmBounds.Install();
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
            wasOn = pending = planDue = false;
            return;
        }

        if (space != workspace)
        {
            workspace = space;
            moves.Clear();
            running.Clear();
            wasOn = false;
        }

        if (!space.gameObject.activeInHierarchy) return;
        if (!wasOn)
        {
            wasOn = true;
            pending = true;
        }

        if (RunningChanged(space)) pending = true;
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
    // Windows docked to others are left to the dock group arrangement.
    void Decide(Workspace space)
    {
        decided.Clear();
        foreach (var pair in space.codeWindows)
        {
            var code = pair.Value;
            var window = code.GetComponent<Window>();
            if (window.dockedParent != null || window.DockedChild != null) continue;
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

    bool Plan(Workspace space)
    {
        if (!FarmBounds.TryGet(space, out var farm)) return false;
        units.Clear();
        byName.Clear();
        foreach (var pair in decided)
        {
            if (pair.Key == null) continue;
            var window = pair.Key.GetComponent<Window>();
            var topLeft = Corner(space, window, false);
            var bottomRight = Corner(space, window, true);
            units.Add(new Unit(window.windowName, bottomRight.x - topLeft.x, topLeft.y - bottomRight.y, pair.Value.Running, pair.Value.Focused));
            byName[window.windowName] = window;
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
