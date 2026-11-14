using UnityEngine;

namespace TFWRMCP.Mod;

/// <summary>
/// Anchors the main resource bar (<c>MainSim.inv</c>) at the bottom center of the screen while the config says so, and
/// puts back the anchoring the game gave it as soon as it does not. Only the RectTransform changes, so the game's
/// "hide UI" keeps working on whatever it hides. A game member that is missing turns the feature off.
/// </summary>
public sealed class ResourceBarPlacement : MonoBehaviour
{
    static readonly Vector2 BottomCenter = new(0.5f, 0);

    Plugin plugin = null!;
    bool broken;
    RectTransform? bar;
    Vector2 anchorMin, anchorMax, pivot, anchoredPosition, sizeDelta;
    bool moved;

    public void Install(Plugin owner) => plugin = owner;

    // Late: the game's own layout of the bar has run by now.
    void LateUpdate()
    {
        if (broken) return;
        try { Apply(); }
        catch (System.Exception e)
        {
            broken = true;
            Patching.Warn($"Resource bar placement is off: {e.Message}");
        }
    }

    void Apply()
    {
        var main = MainSim.Inst;
        var current = main == null || main.inv == null ? null : main.inv.transform as RectTransform;
        // A Save switch reloads the scene: the old bar is gone, the new one is the game's again.
        if (current != bar)
        {
            bar = current;
            moved = false;
        }
        if (bar == null) return;

        if (!plugin.ResourceBarAtBottom.Value)
        {
            if (!moved) return;
            bar.anchorMin = anchorMin;
            bar.anchorMax = anchorMax;
            bar.pivot = pivot;
            bar.anchoredPosition = anchoredPosition;
            bar.sizeDelta = sizeDelta;
            moved = false;
            return;
        }

        if (moved && bar.anchorMin == BottomCenter && bar.anchorMax == BottomCenter && bar.pivot == BottomCenter) return;
        if (!moved)
        {
            anchorMin = bar.anchorMin;
            anchorMax = bar.anchorMax;
            pivot = bar.pivot;
            anchoredPosition = bar.anchoredPosition;
            sizeDelta = bar.sizeDelta;
            moved = true;
        }
        var size = bar.rect.size;
        bar.anchorMin = bar.anchorMax = bar.pivot = BottomCenter;
        bar.anchoredPosition = Vector2.zero;
        bar.sizeDelta = size;
    }
}
