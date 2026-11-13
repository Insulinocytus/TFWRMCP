using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TFWRMCP.Mod;

/// <summary>
/// The Input Lock. While a Tool runs, InputBoss (hotkeys, scrolling, panning) and the uGUI EventSystem (clicks, drags,
/// typing) are disabled and a lock icon shows bottom-right on the Mod's own canvas, which the game's "hide UI" does
/// not touch. F10 gives control back. Both objects are looked up again every frame because a scene reload recreates them.
/// A disabled EventSystem makes EventSystem.current null, which game code dereferences (CodeWindow.ShowError), so an
/// inert stand-in EventSystem takes its place for the duration of the lock.
/// </summary>
[DefaultExecutionOrder(-10000)]
public sealed class InputLock : MonoBehaviour
{
    const float IconSize = 48;
    const float Margin = 16;

    string? tool;
    InputBoss? inputBoss;
    EventSystem? eventSystem;
    EventSystem? standIn;
    GameObject? canvas;
    RectTransform? icon;
    GameObject? tooltip;
    Text? tooltipText;

    /// <summary>Raised on the main thread after the lock was released by F10.</summary>
    public event Action TookOver = delegate { };

    void Awake()
    {
        standIn = new GameObject("TFWRMCP EventSystem").AddComponent<EventSystem>();
        DontDestroyOnLoad(standIn.gameObject);
        standIn.enabled = false;
        canvas = new GameObject("TFWRMCP Input Lock");
        DontDestroyOnLoad(canvas);
        var root = canvas.AddComponent<Canvas>();
        root.renderMode = RenderMode.ScreenSpaceOverlay;
        root.sortingOrder = short.MaxValue;
        var scaler = canvas.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        var background = new Color(0f, 0f, 0f, 0.7f);
        var glyph = new Color(1f, 0.8f, 0.2f);
        icon = Box("Icon", canvas.transform, background, new Vector2(1, 0), new Vector2(-Margin, Margin), new Vector2(IconSize, IconSize));
        Box("Body", icon, glyph, Vector2.zero, new Vector2(12, 8), new Vector2(24, 18));
        Box("ShackleLeft", icon, glyph, Vector2.zero, new Vector2(15, 26), new Vector2(4, 12));
        Box("ShackleRight", icon, glyph, Vector2.zero, new Vector2(29, 26), new Vector2(4, 12));
        Box("ShackleTop", icon, glyph, Vector2.zero, new Vector2(15, 34), new Vector2(18, 4));

        var box = Box("Tooltip", canvas.transform, background, new Vector2(1, 0), new Vector2(-(Margin * 2 + IconSize), Margin), new Vector2(380, IconSize));
        tooltip = box.gameObject;
        var text = new GameObject("Text", typeof(RectTransform)).GetComponent<RectTransform>();
        text.SetParent(box, false);
        text.anchorMin = Vector2.zero;
        text.anchorMax = Vector2.one;
        text.offsetMin = new Vector2(12, 0);
        text.offsetMax = new Vector2(-12, 0);
        tooltipText = text.gameObject.AddComponent<Text>();
        tooltipText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        tooltipText.fontSize = 18;
        tooltipText.alignment = TextAnchor.MiddleLeft;
        tooltipText.raycastTarget = false;

        tooltip.SetActive(false);
        canvas.SetActive(false);
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

    /// <summary>Locks for the named Tool, or unlocks when null. Idempotent.</summary>
    public void Set(string? name)
    {
        tool = name;
        if (name == null)
        {
            Release();
            return;
        }

        tooltipText!.text = $"Tool in progress: {name}\nPress F10 to take control";
        canvas!.SetActive(true);
        Enforce();
    }

    void Release()
    {
        tool = null;
        canvas!.SetActive(false);
        tooltip!.SetActive(false);
        if (inputBoss != null) inputBoss.enabled = true;
        if (eventSystem != null) eventSystem.enabled = true;
        standIn!.enabled = false;
    }

    void Enforce()
    {
        if (inputBoss == null) inputBoss = FindFirstObjectByType<InputBoss>();
        if (eventSystem == null)
            foreach (var candidate in FindObjectsByType<EventSystem>(FindObjectsSortMode.None))
                if (candidate != standIn) eventSystem = candidate;
        if (inputBoss != null) inputBoss.enabled = false;
        standIn!.enabled = true;
        if (eventSystem != null) eventSystem.enabled = false;
    }

    void Update()
    {
        if (tool == null) return;
        if (Input.GetKeyDown(KeyCode.F10))
        {
            Release();
            TookOver();
            return;
        }

        Enforce();
        // The EventSystem is off, so hover is hit-tested by hand.
        tooltip!.SetActive(RectTransformUtility.RectangleContainsScreenPoint(icon, Input.mousePosition, null));
    }

    /// <summary>F10 is the takeover key; warn when a game key binding already uses it.</summary>
    public static void WarnAboutF10Bindings(Action<string> warn)
    {
        foreach (var option in Resources.LoadAll<KeyBindOptionSO>("Options/"))
            if (OptionHolder.GetKeyCombination(option.optionName).key == KeyCode.F10)
                warn($"The game key binding '{option.optionName}' uses F10, which TFWRMCP reserves for taking back control " +
                     "during an Input Lock; pressing it then takes control back instead of running that binding.");
    }
}
