using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TFWRMCP.Mod;

/// <summary>
/// The "MCP" tab of the game's options menu. Its rows are the game's own cycle and slider rows, rewired to the BepInEx
/// config instead of options.txt: port (slider hidden, text field only), server status with a restart button, and the
/// switch that keeps Unlock docs from opening.
/// </summary>
public static class McpOptions
{
    const string Category = "MCP";
    const string PortRow = "tfwrmcp port";
    const string ServerRow = "tfwrmcp server";
    const string DocsRow = "tfwrmcp unlock docs";

    static Plugin plugin = null!;
    static List<OptionSO>? rows;

    static readonly AccessTools.FieldRef<OptionUI, TextMeshProUGUI> Title = AccessTools.FieldRefAccess<OptionUI, TextMeshProUGUI>("title");
    static readonly AccessTools.FieldRef<CycleOptionUI, ColoredButton> CycleButton = AccessTools.FieldRefAccess<CycleOptionUI, ColoredButton>("button");
    static readonly AccessTools.FieldRef<SliderOptionUI, Slider> SliderOf = AccessTools.FieldRefAccess<SliderOptionUI, Slider>("slider");
    static readonly AccessTools.FieldRef<SliderOptionUI, Image> SliderBackground = AccessTools.FieldRefAccess<SliderOptionUI, Image>("sliderBackground");
    static readonly AccessTools.FieldRef<SliderOptionUI, TMP_InputField> InputField = AccessTools.FieldRefAccess<SliderOptionUI, TMP_InputField>("inputField");
    static readonly AccessTools.FieldRef<OptionMenu, Dictionary<string, OptionUI>> OptionUIs = AccessTools.FieldRefAccess<OptionMenu, Dictionary<string, OptionUI>>("optionUIs");

    /// <summary>A missing private field (the type initializer fails) or method leaves the game's options menu untouched.</summary>
    public static void Install(Plugin owner) => Patching.Safely(nameof(McpOptions), () =>
    {
        plugin = owner;
        Patching.Patch(typeof(McpOptions), AccessTools.Method(typeof(ResourceManager), nameof(ResourceManager.GetAllOptions)),
            "ResourceManager.GetAllOptions", postfix: nameof(AddRows));
        Patching.Patch(typeof(McpOptions), AccessTools.Method(typeof(OptionMenu), "ChangeCategory"),
            "OptionMenu.ChangeCategory", postfix: nameof(Rewire));
    });

    // ResourceManager.GetAllOptions(), postfix: the menu builds one tab per category, so new rows bring their own tab.
    static void AddRows(ref IEnumerable<OptionSO> __result)
    {
        var options = __result.ToList();
        Patching.Safely(nameof(AddRows), () => rows ??= CreateRows(options));
        __result = rows == null ? options : options.Concat(rows);
    }

    static List<OptionSO>? CreateRows(List<OptionSO> options)
    {
        var cycle = options.OfType<CycleOptionSO>().FirstOrDefault();
        var slider = options.OfType<SliderOptionSO>().FirstOrDefault();
        if (cycle == null || slider == null) return null;

        T Row<T>(T template, string name, float importance) where T : OptionSO
        {
            var row = ScriptableObject.CreateInstance<T>();
            row.name = row.optionName = name;
            row.optionUI = template.optionUI;
            row.category = Category;
            row.importance = importance;
            row.hideFlags = HideFlags.HideAndDontSave;
            return row;
        }

        var port = Row(slider, PortRow, -1000);
        port.min = 1;
        port.max = 65535;
        port.stepSize = 1;
        var server = Row(cycle, ServerRow, -1001);
        server.options = new List<string> { "" };
        var docs = Row(cycle, DocsRow, -1002);
        docs.options = new List<string> { "enabled", "disabled" };
        return new List<OptionSO> { port, server, docs };
    }

    // OptionMenu.ChangeCategory(string), postfix: the rows were just instantiated and wired to OptionHolder by the game.
    static void Rewire(OptionMenu __instance, string __0) => Patching.Safely(nameof(Rewire), () =>
    {
        if (__0 != Category) return;
        var uis = OptionUIs(__instance);
        var zh = Localizer.Lang == "ZH";

        if (uis.TryGetValue(PortRow, out var portUi) && portUi is SliderOptionUI port)
        {
            Title(port).text = zh ? "端口" : "Port";
            SliderOf(port).onValueChanged.RemoveAllListeners();
            SliderOf(port).gameObject.SetActive(false);
            SliderBackground(port).gameObject.SetActive(false);
            var field = InputField(port);
            field.onEndEdit.RemoveAllListeners();
            field.contentType = TMP_InputField.ContentType.IntegerNumber;
            field.text = plugin.Port.Value.ToString(CultureInfo.InvariantCulture);
            field.onEndEdit.AddListener(text =>
            {
                if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 1 and <= 65535)
                    plugin.Port.Value = value;
                field.text = plugin.Port.Value.ToString(CultureInfo.InvariantCulture);
                if (uis.TryGetValue(ServerRow, out var status)) Title(status).text = Status(zh);
            });
        }

        if (uis.TryGetValue(ServerRow, out var serverUi) && serverUi is CycleOptionUI server)
        {
            Rebind(server, zh ? "重启" : "Restart", () =>
            {
                plugin.RestartServer();
                Title(server).text = Status(zh);
            });
            Title(server).text = Status(zh);
        }

        if (uis.TryGetValue(DocsRow, out var docsUi) && docsUi is CycleOptionUI docs)
        {
            string State() => plugin.SuppressUnlockDocs.Value ? "enabled" : "disabled";
            Title(docs).text = zh ? "禁止解锁后自动弹出说明" : "Don't open docs after an unlock";
            ColoredButton? button = null;
            button = Rebind(docs, State(), () =>
            {
                plugin.SuppressUnlockDocs.Value = !plugin.SuppressUnlockDocs.Value;
                button!.Text = State();
            });
        }
    });

    static ColoredButton Rebind(CycleOptionUI row, string text, Action clicked)
    {
        var button = CycleButton(row);
        button.OnClick.RemoveAllListeners();
        button.OnClick.AddListener(() => clicked());
        button.Text = text;
        return button;
    }

    static string Status(bool zh)
    {
        if (plugin.ServerError != null) return zh ? $"MCP 服务器：未运行（{plugin.ServerError}）" : $"MCP server: stopped ({plugin.ServerError})";
        var status = zh ? $"MCP 服务器：监听中 :{plugin.ListeningPort}" : $"MCP server: listening on :{plugin.ListeningPort}";
        if (plugin.Port.Value != plugin.ListeningPort) status += zh ? $"，重启后改用 :{plugin.Port.Value}" : $", :{plugin.Port.Value} after restart";
        return status;
    }
}
