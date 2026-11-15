using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace TFWRMCP.Mod;

/// <summary>
/// Pins the code windows to the screen in two columns (see <see cref="Layout"/>): every window is folded, those taking part
/// in a Run go middle left, the rest middle right, and all glide to their places. Windows docked together are one unit,
/// moved by their top window; a group holding a documentation window is left alone. The windows stay in the game's
/// zooming workspace container and are scaled against its zoom every frame (docs/adr/0003), so their size on screen is
/// that at zoom 1 and the game's own window handling and Saves keep working.
/// </summary>
[DefaultExecutionOrder(1000)]
public sealed class CodeWindowArranger : MonoBehaviour
{
    const double Duration = 0.5;
    // CodeWindow recomputes its automatic size two frames after it is folded or unfolded.
    const int SettleFrames = 3;

    // Top-left corners in screen units (pixels over the canvas scale), y up.
    sealed class Pin
    {
        public Vector2 From;
        public Vector2 To;
        public double Start;
    }

    readonly Dictionary<Window, Pin> pins = new();
    readonly Dictionary<Window, Vector3> baseScales = new();
    readonly List<List<CodeWindow>> groups = new();
    readonly HashSet<CodeWindow> running = new();
    readonly List<Window> gone = new();
    readonly List<Unit> units = new();
    readonly List<Unit> members = new();
    readonly Dictionary<string, Window> byName = new();
    static CodeWindowArranger? instance;
    Plugin plugin = null!;
    Workspace? workspace;
    bool wasOn, pending, broken, rearrange, docksChanged;
    int settle;
    (int Width, int Height, float Scale, float Bar) view;
    Box? room;

    /// <summary>The screen between the two columns in pixels, y up, while windows are arranged.</summary>
    public static Box? Room => instance == null ? null : instance.room;

    /// <summary>Whether the window is pinned to the screen, alone or docked under a pinned window.</summary>
    public static bool IsPinned(Window window) => instance != null && window != null && instance.pins.ContainsKey(window.HighestDockedParent);

    public void Install(Plugin owner)
    {
        plugin = owner;
        instance = this;
        Patching.Patch(typeof(CodeWindowArranger), AccessTools.Method(typeof(Workspace), nameof(Workspace.OpenCodeWindow)),
            "Workspace.OpenCodeWindow", postfix: nameof(ScriptsChanged));
        Patching.Patch(typeof(CodeWindowArranger), AccessTools.DeclaredMethod(typeof(Workspace), nameof(Workspace.RemoveWindow)),
            "Workspace.RemoveWindow", postfix: nameof(ScriptsChanged));
        Patching.Patch(typeof(CodeWindowArranger), AccessTools.DeclaredMethod(typeof(Workspace), nameof(Workspace.RenameWindow)),
            "Workspace.RenameWindow", postfix: nameof(ScriptsChanged));
        Patching.Patch(typeof(CodeWindowArranger), AccessTools.Method(typeof(Window), nameof(Window.OnDrag)),
            "Window.OnDrag", postfix: nameof(Dragged));
        Patching.Patch(typeof(CodeWindowArranger), AccessTools.Method(typeof(Window), nameof(Window.Undock)),
            "Window.Undock", prefix: nameof(Undocking));
        Patching.Patch(typeof(CodeWindowArranger), AccessTools.Method(typeof(Window), nameof(Window.DockOnto)),
            "Window.DockOnto", postfix: nameof(Docked));
        Patching.Patch(typeof(CodeWindowArranger), AccessTools.Method(typeof(Window), nameof(Window.OnEndDrag)),
            "Window.OnEndDrag", postfix: nameof(Dropped));
        Patching.Patch(typeof(CodeWindowArranger), AccessTools.Method(typeof(Workspace), nameof(Workspace.MoveCameraTo)),
            "Workspace.MoveCameraTo", prefix: nameof(CameraJump));
    }

    // Workspace.OpenCodeWindow, RemoveWindow and RenameWindow, postfix: whoever creates, deletes or renames a Script, a Tool included.
    static void ScriptsChanged() => Patching.Safely(nameof(ScriptsChanged), () =>
    {
        if (instance != null) instance.rearrange = true;
    });

    // Window.OnDrag, postfix: the player moved a pinned window; it stays where it was dropped until the next arrangement.
    static void Dragged(Window __instance) => Patching.Safely(nameof(Dragged), () =>
    {
        if (instance == null || !instance.pins.TryGetValue(__instance, out var pin)) return;
        pin.From = pin.To = instance.TopLeft(__instance);
        pin.Start = 0;
    });

    // Window.Undock, prefix, and DockOnto, postfix: the groups change, which counts once the window is dropped.
    static void Undocking(Window __instance) => Patching.Safely(nameof(Undocking), () =>
    {
        if (instance != null && __instance.dockedParent != null) instance.docksChanged = true;
    });

    static void Docked(Window __instance) => Patching.Safely(nameof(Docked), () =>
    {
        if (instance != null && __instance.dockedParent != null) instance.docksChanged = true;
    });

    // Window.OnEndDrag, postfix.
    static void Dropped() => Patching.Safely(nameof(Dropped), () =>
    {
        if (instance != null && instance.docksChanged) instance.rearrange = true;
    });

    // Workspace.MoveCameraTo, prefix: the jump to a window for an error or a search makes no sense for a window pinned on screen.
    static bool CameraJump(Window target)
    {
        var jump = true;
        Patching.Safely(nameof(CameraJump), () => jump = !IsPinned(target));
        return jump;
    }

    // Late, after the camera and the workspace's scrolling have moved the container this frame.
    void LateUpdate()
    {
        if (broken) return;
        try { Tick(); }
        catch (Exception e)
        {
            broken = true;
            Unpin();
            Patching.Warn($"Code window auto-arrange is off: {e.Message}");
        }
    }

    void Tick()
    {
        var main = MainSim.Inst;
        var space = main == null ? null : main.workspace;
        if (space == null || !plugin.AutoArrangeWindows.Value)
        {
            Unpin();
            wasOn = pending = rearrange = false;
            return;
        }

        if (space != workspace)
        {
            workspace = space;
            pins.Clear();
            baseScales.Clear();
            running.Clear();
            room = null;
            wasOn = false;
            var canvas = space.container.GetComponentInParent<Canvas>();
            if (canvas == null || canvas.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
                throw new InvalidOperationException("the workspace is not on a screen space overlay canvas");
        }

        if (!space.gameObject.activeInHierarchy) return;
        if (!wasOn)
        {
            wasOn = true;
            pending = true;
        }

        var scale = Scale(space);
        var now = (Screen.width, Screen.height, scale, ResourceBarPlacement.Height(plugin));
        var resized = now != view;
        view = now;

        if (RunningChanged(space) || rearrange) pending = true;
        rearrange = false;
        if (pending)
        {
            Decide(space);
            pending = false;
            settle = SettleFrames;
        }
        else if ((settle > 0 && --settle == 0) || resized)
        {
            Plan(scale);
        }

        Hold(scale);
    }

    // Pixels per screen unit: the canvas scale, which the zoom container sits right under.
    static float Scale(Workspace space) => space.zoomContainer.parent.lossyScale.x;

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

    // Folds every window that is arranged; their sizes settle over the next frames.
    void Decide(Workspace space)
    {
        groups.Clear();
        docksChanged = false;
        foreach (var pair in space.codeWindows)
        {
            var window = pair.Value.GetComponent<Window>();
            if (!baseScales.ContainsKey(window)) baseScales[window] = window.transform.localScale;
        }

        foreach (var pair in space.codeWindows)
        {
            var group = DockGroup(pair.Value.GetComponent<Window>());
            if (group == null) continue;
            groups.Add(group);
            for (var i = 0; i < group.Count; i++)
            {
                var window = group[i].GetComponent<Window>();
                // A window undocked from a pinned one kept the scale against the zoom; under a dock it needs its own again.
                if (i > 0) window.transform.localScale = baseScales[window];
                if (!window.isMinimized) window.SetMinmized(true);
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

    void Plan(float scale)
    {
        units.Clear();
        byName.Clear();
        foreach (var group in groups)
        {
            if (!TryUnit(group, out var unit)) continue;
            units.Add(unit);
            byName[unit.Name] = group[0].GetComponent<Window>();
        }

        var bounds = new Box(0, view.Height / scale, view.Width / scale, view.Bar / scale);
        var now = Time.unscaledTimeAsDouble;
        foreach (var pair in pins)
            if (pair.Key == null || !byName.ContainsValue(pair.Key)) gone.Add(pair.Key!);
        foreach (var window in gone) Unpin(window);
        gone.Clear();

        foreach (var place in Layout.Arrange(bounds, units))
        {
            var window = byName[place.Name];
            if (!pins.TryGetValue(window, out var pin)) pins[window] = pin = new Pin { To = TopLeft(window) };
            pin.From = At(pin, now);
            pin.To = new Vector2(place.Left, place.Top);
            pin.Start = now;
        }

        var between = Layout.Between(bounds, units);
        room = new Box(between.Left * scale, between.Top * scale, between.Right * scale, between.Bottom * scale);
    }

    // A group is gone from the plan when one of its windows was closed since it was decided.
    bool TryUnit(List<CodeWindow> group, out Unit unit)
    {
        unit = default;
        members.Clear();
        foreach (var code in group)
        {
            if (code == null) return false;
            var window = code.GetComponent<Window>();
            var size = window.WindowRect.rect.size;
            var baseScale = baseScales[window];
            members.Add(new Unit(window.windowName, size.x * baseScale.x, size.y * baseScale.y, code.isExecuting));
        }

        unit = Layout.Group(members);
        return true;
    }

    static Vector2 At(Pin pin, double now) => new(
        Tween.EaseOutExpo(pin.From.x, pin.To.x, pin.Start, Duration, now),
        Tween.EaseOutExpo(pin.From.y, pin.To.y, pin.Start, Duration, now));

    // Every frame: undo the zoom on each pinned window's scale and put its top-left corner where it belongs on screen.
    void Hold(float scale)
    {
        var now = Time.unscaledTimeAsDouble;
        foreach (var pair in pins)
        {
            var window = pair.Key;
            if (window == null || window.dockedParent != null) continue;
            var transform = window.transform;
            transform.localScale = baseScales[window] * (scale / transform.parent.lossyScale.x);
            var to = At(pair.Value, now) * scale;
            window.WindowRect.position += (Vector3)(to - TopLeftPixel(window));
        }
    }

    void Unpin()
    {
        foreach (var window in pins.Keys.ToList()) Unpin(window);
        room = null;
    }

    // Back to the game's scale, the top-left corner kept where it is on screen, or in its dock slot.
    void Unpin(Window window)
    {
        pins.Remove(window);
        if (window == null) return;
        var at = TopLeftPixel(window);
        window.transform.localScale = baseScales[window];
        if (window.dockedParent != null) window.WindowRect.anchoredPosition = Vector2.zero;
        else window.WindowRect.position += (Vector3)(at - TopLeftPixel(window));
    }

    Vector2 TopLeft(Window window) => TopLeftPixel(window) / Scale(workspace!);

    // On a screen space overlay canvas world coordinates are pixels.
    static Vector2 TopLeftPixel(Window window)
    {
        var rect = window.WindowRect;
        return rect.TransformPoint(new Vector3(rect.rect.xMin, rect.rect.yMax));
    }
}
