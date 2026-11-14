using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TFWRMCP.Mod;

/// <summary>
/// While the config says so, holds the camera on the farm being shown (see <see cref="CameraTarget"/>): every frame the
/// farm's rectangle is polled, and when the view that fits it and the code windows changes (the farm grew, shrank or was
/// swapped for a temporary one, a window moved), the workspace's pan and zoom glide there over a second. The zoom is applied
/// the way <c>Workspace.Zoom</c> applies it. The player's own camera operations (WASD, scroll zoom off the windows, drag pan,
/// the game's jump to a window for an error or a search)
/// take the camera from the Mod, see <see cref="CameraTakeover"/>. They are noticed in the game's input code, which does not
/// run during the Input Lock, while the rest of this keeps working. A game member that is missing or not what it is assumed to
/// be turns the feature off, or only the noticing of the operations it belongs to.
/// </summary>
public sealed class CameraAutoCenter : MonoBehaviour
{
    const double Duration = 1;
    const float PanTolerance = 0.5f;
    const float ZoomTolerance = 0.001f;

    static AccessTools.FieldRef<Workspace, float>? minZoom;
    static AccessTools.FieldRef<Workspace, float>? maxZoom;
    static AccessTools.FieldRef<CameraController, RectTransform>? contentRect;
    static AccessTools.FieldRef<CameraController, RectTransform>? windowRect;

    readonly List<Box> windows = new();
    readonly Vector3[] corners = new Vector3[4];
    Plugin plugin = null!;
    bool ready, broken, glide;
    Vector2 fromPan, toPan;
    float fromZoom, toZoom;
    double start;

    static CameraAutoCenter? current;
    static Vector2 panBefore;

    readonly CameraTakeover takeover = new();
    Workspace? workspace;
    Vector2Int? farmSize;
    bool simulating;

    bool InControl => takeover.InControl(Time.unscaledTimeAsDouble);

    public void Install(Plugin owner)
    {
        plugin = owner;
        current = this;
        Patching.Patch(typeof(CameraAutoCenter), AccessTools.Method(typeof(Workspace), "Zoom"), "Workspace.Zoom", postfix: nameof(Operated));
        Patching.Patch(typeof(CameraAutoCenter), AccessTools.Method(typeof(Workspace), nameof(Workspace.MoveCameraTo)), "Workspace.MoveCameraTo", postfix: nameof(Operated));
        Patching.Patch(typeof(CameraAutoCenter), AccessTools.Method(typeof(ScaledScrollRect), "OnDrag"), "ScaledScrollRect.OnDrag", prefix: nameof(Dragged));
        Patching.Patch(typeof(CameraAutoCenter), AccessTools.Method(typeof(InputBoss), "Update"), "InputBoss.Update",
            prefix: nameof(InputBegin), postfix: nameof(InputEnd));
        FarmBounds.Install();
        Patching.Safely(nameof(CameraAutoCenter), () =>
        {
            minZoom = AccessTools.FieldRefAccess<Workspace, float>("minCanvasScaleFactor");
            maxZoom = AccessTools.FieldRefAccess<Workspace, float>("maxCanvasScaleFactor");
            contentRect = AccessTools.FieldRefAccess<CameraController, RectTransform>("contentRect");
            windowRect = AccessTools.FieldRefAccess<CameraController, RectTransform>("windowRect");
            ready = true;
        });
    }

    // Late: the camera has followed the pan and zoom of this frame's start, so the farm's projection matches the workspace.
    void LateUpdate()
    {
        if (!ready || broken) return;
        try { Tick(); }
        catch (Exception e)
        {
            broken = true;
            Patching.Warn($"Camera auto-center is off: {e.Message}");
        }
    }

    void Tick()
    {
        var main = MainSim.Inst;
        var space = main == null ? null : main.workspace;
        if (space == null || !plugin.AutoCenterCamera.Value)
        {
            glide = false;
            return;
        }

        if (space != workspace)
        {
            workspace = space;
            glide = false;
            farmSize = null;
            simulating = false;
            takeover.FarmChanged();
        }

        Track(main!);
        if (!InControl)
        {
            glide = false;
            return;
        }

        if (!space.gameObject.activeInHierarchy || !FarmBounds.TryGet(space, out var farm)) return;
        var camera = space.cameraController;
        var content = space.workspaceContainer;
        if (contentRect!(camera) != content || content.parent != space.zoomContainer || content.parent is not RectTransform parent)
            throw new InvalidOperationException("the workspace is not laid out as assumed");

        var zoom = camera.zoom;
        var pan = content.anchoredPosition + windowRect!(camera).anchoredPosition;
        var unit = content.lossyScale.x / zoom;
        windows.Clear();
        foreach (var pair in space.codeWindows) windows.Add(BoundsOf(space, pair.Value.GetComponent<Window>()));
        var view = CameraTarget.Fit(farm, windows, Screen.width / unit, Screen.height / unit, BarHeight(main!) / unit,
            minZoom!(space), maxZoom!(space));

        // Panning moves a content point in the zoom container's own coordinates, where the screen center sits 1/zoom as far out.
        RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, new Vector2(Screen.width / 2f, Screen.height / 2f), space.uiCam, out var screenCenter);
        var farmCenter = (Vector2)parent.InverseTransformPoint(space.container.TransformPoint(new Vector3(view.X, view.Y)));
        var target = pan + screenCenter * zoom / view.Zoom - farmCenter;

        var now = Time.unscaledTimeAsDouble;
        if (!glide || (target - toPan).magnitude > PanTolerance || Mathf.Abs(view.Zoom - toZoom) > ZoomTolerance)
        {
            // Mid-glide only the end moves, so a target that keeps changing (a dragged window) is followed instead of restarted from a standstill.
            if (!glide || now >= start + Duration)
            {
                glide = true;
                fromPan = pan;
                fromZoom = zoom;
                start = now;
            }

            toPan = target;
            toZoom = view.Zoom;
        }

        SetZoom(content, camera, space.zoomContainer, Tween.EaseOutExpo(fromZoom, toZoom, start, Duration, now));
        content.anchoredPosition += new Vector2(
            Tween.EaseOutExpo(fromPan.x, toPan.x, start, Duration, now),
            Tween.EaseOutExpo(fromPan.y, toPan.y, start, Duration, now)) - pan;
    }

    // The farm's size and a temporary farm (Leaderboard Run, simulate()) are noticed here, not at their causes, so they count while the player holds the camera.
    void Track(MainSim main)
    {
        var size = FarmBounds.Size();
        var temporary = main.storedSim != null;
        if (size == farmSize && temporary == simulating) return;
        farmSize = size;
        simulating = temporary;
        takeover.FarmChanged();
    }

    static RectTransform? Container()
    {
        var main = MainSim.Inst;
        return main == null || main.workspace == null ? null : main.workspace.workspaceContainer;
    }

    // Workspace.Zoom(float) and MoveCameraTo(...), postfix: only the scroll over no window zooms the workspace, and MoveCameraTo is the game's own jump (CodeWindow error, SearchBox), which its LateUpdate would otherwise undo.
    static void Operated() => Patching.Safely(nameof(Operated), () => current?.takeover.PlayerOperated(Time.unscaledTimeAsDouble));

    // ScaledScrollRect.OnDrag(PointerEventData), prefix: the workspace's drag pan, wherever the drag started.
    static void Dragged(ScaledScrollRect __instance) => Patching.Safely(nameof(Dragged), () =>
    {
        if (__instance.content == Container()) Operated();
    });

    // InputBoss.Update(), prefix and postfix: WASD is the only thing in it that moves the container besides the zoom.
    static void InputBegin() => Patching.Safely(nameof(InputBegin), () => panBefore = Container()?.anchoredPosition ?? default);

    static void InputEnd() => Patching.Safely(nameof(InputEnd), () =>
    {
        var container = Container();
        if (container != null && container.anchoredPosition != panBefore) Operated();
    });

    // Workspace.Zoom without the cursor as the fixed point: the camera's zoom, the zoom container's scale and the margin.
    static void SetZoom(RectTransform content, CameraController camera, RectTransform zoomContainer, float zoom)
    {
        if (Mathf.Approximately(camera.zoom, zoom)) return;
        camera.zoom = zoom;
        zoomContainer.localScale = Vector3.one * zoom;
        content.GetComponent<ContainerScaler>().UpdateMarginSize();
    }

    // The room the bar takes at the bottom of the screen, in pixels; nothing while it is elsewhere or hidden.
    float BarHeight(MainSim main)
    {
        if (!plugin.ResourceBarAtBottom.Value || main.inv == null || !main.inv.gameObject.activeInHierarchy) return 0;
        var bar = (RectTransform)main.inv.transform;
        return bar.rect.height * bar.lossyScale.y;
    }

    Box BoundsOf(Workspace space, Window window)
    {
        window.WindowRect.GetWorldCorners(corners);
        float left = float.MaxValue, right = float.MinValue, top = float.MinValue, bottom = float.MaxValue;
        foreach (var corner in corners)
        {
            var local = space.container.InverseTransformPoint(corner);
            left = Mathf.Min(left, local.x);
            right = Mathf.Max(right, local.x);
            top = Mathf.Max(top, local.y);
            bottom = Mathf.Min(bottom, local.y);
        }

        return new Box(left, top, right, bottom);
    }
}
