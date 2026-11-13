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
        canvas = InputLock.OverlayCanvas("TFWRMCP Unlock Toasts", short.MaxValue - 1);
        for (var i = 0; i < UnlockToasts.Max; i++)
        {
            var box = InputLock.Box("Toast" + i, canvas.transform, InputLock.Background, Vector2.zero,
                new Vector2(Margin, Margin + i * (Height + Gap)), new Vector2(Width, Height));
            slots[i] = box.gameObject.AddComponent<CanvasGroup>();
            slots[i].blocksRaycasts = false;
            texts[i] = InputLock.Label(box);
            texts[i].color = InputLock.Glyph;
        }
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
