using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TFWRMCP.Mod;

/// <summary>
/// While the config says so, holds the camera on the farm being shown (see <see cref="CameraTarget"/>): every frame the
/// farm's rectangle is polled, and when the view that fits it and the code windows changes (the farm grew, shrank or was
/// swapped for a temporary one, a window moved), the workspace's pan and zoom glide there over a second. The zoom is applied
/// the way <c>Workspace.Zoom</c> applies it. A game member that is missing or not what it is assumed to be turns the feature off.
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

    // The one place that says whether the Mod owns the camera now.
    bool InControl => true;

    public void Install(Plugin owner)
    {
        plugin = owner;
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
        if (space == null || !plugin.AutoCenterCamera.Value || !InControl)
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
            glide = true;
            fromPan = pan;
            fromZoom = zoom;
            toPan = target;
            toZoom = view.Zoom;
            start = now;
        }

        SetZoom(content, camera, space.zoomContainer, Tween.EaseOutExpo(fromZoom, toZoom, start, Duration, now));
        content.anchoredPosition += new Vector2(
            Tween.EaseOutExpo(fromPan.x, toPan.x, start, Duration, now),
            Tween.EaseOutExpo(fromPan.y, toPan.y, start, Duration, now)) - pan;
    }

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
