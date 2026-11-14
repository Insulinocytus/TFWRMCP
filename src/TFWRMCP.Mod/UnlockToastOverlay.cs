using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TFWRMCP.Mod;

/// <summary>
/// Shows <see cref="UnlockToasts"/> bottom-left for every Unlock bought on the main farm, whatever bought it, and keeps
/// the game from opening an Unlock's docs when that is switched off. The toasts hide with the game's "hide UI".
/// </summary>
public sealed class UnlockToastOverlay : MonoBehaviour
{
    const float Margin = 16;
    const float Height = 36;
    const float Gap = 6;
    const float Width = 420;

    static readonly Color Background = new(0f, 0f, 0f, 0.7f);
    static readonly Color Glyph = new(1f, 0.8f, 0.2f);

    static readonly UnlockToasts Toasts = new();
    static Plugin plugin = null!;

    readonly CanvasGroup[] slots = new CanvasGroup[UnlockToasts.Max];
    readonly Text[] texts = new Text[UnlockToasts.Max];
    AccessTools.FieldRef<InputBoss, GameObject[]>? uisToHide;
    GameObject? canvas;
    InputBoss? inputBoss;

    public void Install(Plugin owner)
    {
        plugin = owner;
        Patching.Patch(typeof(UnlockToastOverlay), AccessTools.Method(typeof(Farm), nameof(Farm.UnlockOrUpgrade)),
            "Farm.UnlockOrUpgrade", postfix: nameof(Bought));
        Patching.Patch(typeof(UnlockToastOverlay), AccessTools.Method(typeof(UnlockBox), nameof(UnlockBox.SetupRec)),
            "UnlockBox.SetupRec", prefix: nameof(MarkDocsSeen));
        Patching.Safely("InputBoss.UIsToHide", () => uisToHide = AccessTools.FieldRefAccess<InputBoss, GameObject[]>("UIsToHide"));
    }

    // Farm.UnlockOrUpgrade(UnlockSO, bool), postfix; on the simulation thread for a Script's unlock().
    static void Bought(Farm __instance, UnlockSO __0, bool __result) => Patching.Safely(nameof(Bought), () =>
    {
        // Leaderboard Runs, simulate() and the docs' example farms do not record achievements.
        if (!__result || __instance.sim == null || !__instance.sim.RecordsAchievements) return;
        var name = __0.unlockName;
        var level = __instance.NumUnlocked(__0);
        MainThread.Post(() => Toasts.Add(UnlockToasts.Text(Localizer.Lang ?? "EN", Localizer.Localize(name), level), Time.unscaledTimeAsDouble));
    });

    // UnlockBox.SetupRec(bool, HashSet<string> openedUnlockDocs, ItemBlock, Dictionary<string,int> unlocks, out bool), prefix.
    // Docs the game counts as opened are not opened again, now or when a theme change re-runs the check.
    static void MarkDocsSeen(UnlockBox __instance, HashSet<string> __1, Dictionary<string, int> __3) => Patching.Safely(nameof(MarkDocsSeen), () =>
    {
        if (!plugin.SuppressUnlockDocs.Value) return;
        var unlock = __instance.unlockSO;
        var level = __3.GetValueOrDefault(unlock.unlockName, 0);
        if (level >= 1 && !string.IsNullOrEmpty(unlock.docs)) __1.Add(unlock.docs);
        if (level >= 2 && unlock.unlockName == "expand") __1.Add("docs/unlocks/expand_2.md");
    });

    void Awake()
    {
        canvas = OverlayCanvas("TFWRMCP Unlock Toasts", short.MaxValue - 1);
        for (var i = 0; i < UnlockToasts.Max; i++)
        {
            var box = Box("Toast" + i, canvas.transform, Background, Vector2.zero,
                new Vector2(Margin, Margin + i * (Height + Gap)), new Vector2(Width, Height));
            slots[i] = box.gameObject.AddComponent<CanvasGroup>();
            slots[i].blocksRaycasts = false;
            texts[i] = Label(box);
            texts[i].color = Glyph;
        }
    }

    /// <summary>A screen overlay canvas of the Mod's own that survives scene reloads and the game's "hide UI".</summary>
    static GameObject OverlayCanvas(string name, int sortingOrder)
    {
        var canvas = new GameObject(name);
        DontDestroyOnLoad(canvas);
        var root = canvas.AddComponent<Canvas>();
        root.renderMode = RenderMode.ScreenSpaceOverlay;
        root.sortingOrder = sortingOrder;
        var scaler = canvas.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        return canvas;
    }

    static RectTransform Box(string name, Transform parent, Color color, Vector2 anchor, Vector2 position, Vector2 size)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = anchor;
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        var image = rect.gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return rect;
    }

    /// <summary>Left-aligned text filling the box.</summary>
    static Text Label(RectTransform box)
    {
        var rect = new GameObject("Text", typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(box, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(12, 0);
        rect.offsetMax = new Vector2(-12, 0);
        var text = rect.gameObject.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 18;
        text.alignment = TextAnchor.MiddleLeft;
        text.raycastTarget = false;
        return text;
    }

    void Update()
    {
        var shown = Toasts.Visible(Time.unscaledTimeAsDouble);
        canvas!.SetActive(shown.Count > 0 && !UIHidden());
        for (var i = 0; i < slots.Length; i++)
        {
            var visible = i < shown.Count;
            slots[i].gameObject.SetActive(visible);
            if (!visible) continue;
            var (text, alpha) = shown[shown.Count - 1 - i];
            texts[i].text = text;
            slots[i].alpha = alpha;
        }
    }

    bool UIHidden()
    {
        if (uisToHide == null) return false;
        if (inputBoss == null) inputBoss = FindFirstObjectByType<InputBoss>();
        if (inputBoss == null) return false;
        foreach (var ui in uisToHide(inputBoss))
            if (ui != null && !ui.activeSelf) return true;
        return false;
    }
}
