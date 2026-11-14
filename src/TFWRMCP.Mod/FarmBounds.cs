using HarmonyLib;
using UnityEngine;

namespace TFWRMCP.Mod;

/// <summary>
/// Where the farm being shown is on screen, as a <see cref="Box"/> in the workspace container's coordinates, the space code
/// windows are positioned in. The farm's grid corners are projected the way the game's own FarmRenderer projects them.
/// </summary>
public static class FarmBounds
{
    static AccessTools.FieldRef<FarmRenderer, Camera>? farmCamera;

    /// <summary>Without the renderer's private camera there is no farm rectangle and <see cref="TryGet"/> never succeeds.</summary>
    public static void Install() => Patching.Safely(nameof(FarmBounds),
        () => farmCamera = AccessTools.FieldRefAccess<FarmRenderer, Camera>("farmCamera"));

    public static bool TryGet(Workspace workspace, out Box farm)
    {
        farm = default;
        var main = MainSim.Inst;
        if (farmCamera == null || main == null) return false;
        var package = main.GetRenderPackage();
        var renderer = main.GetComponent<FarmRenderer>();
        if (package?.groundData == null || renderer == null) return false;
        var camera = farmCamera(renderer);
        var size = package.groundData.sizexy;
        if (camera == null || size.x <= 0 || size.y <= 0) return false;

        var scene = main.SceneScaler.localToWorldMatrix;
        float left = float.MaxValue, right = float.MinValue, top = float.MinValue, bottom = float.MaxValue;

        bool Project(float x, float y, int cell)
        {
            var depth = package.cellDepths != null && cell < package.cellDepths.Length ? package.cellDepths[cell] : package.averageDepth;
            var screen = camera.WorldToScreenPoint(scene.MultiplyPoint(new Vector3(x, y, -depth)));
            if (screen.z <= 0 || !RectTransformUtility.ScreenPointToLocalPointInRectangle(workspace.container, screen, workspace.uiCam, out var local))
                return false;
            left = Mathf.Min(left, local.x);
            right = Mathf.Max(right, local.x);
            top = Mathf.Max(top, local.y);
            bottom = Mathf.Min(bottom, local.y);
            return true;
        }

        if (!(Project(0.5f, -0.5f, 0)
              & Project(0.5f - size.x, -0.5f, size.x - 1)
              & Project(0.5f, size.y - 0.5f, (size.y - 1) * size.x)
              & Project(0.5f - size.x, size.y - 0.5f, size.x * size.y - 1)))
            return false;
        farm = new Box(left, top, right, bottom);
        return true;
    }
}
