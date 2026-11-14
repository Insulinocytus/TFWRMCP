using System;
using System.Reflection;
using HarmonyLib;

namespace TFWRMCP.Mod;

/// <summary>Harmony patches of the Mod's game UI features; a missing target or a failing hook only turns its feature off.</summary>
public static class Patching
{
    static readonly Harmony Harmony = new("com.insulinocytus.tfwrmcp.ui");
    static Action<string> warn = _ => { };

    public static void Configure(Action<string> warning) => warn = warning;

    public static void Warn(string message) => warn(message);

    public static void Patch(Type hooks, MethodBase? target, string what, string? prefix = null, string? postfix = null)
    {
        HarmonyMethod? Hook(string? name) => name == null ? null : new HarmonyMethod(AccessTools.Method(hooks, name));
        try
        {
            if (target == null) throw new MissingMethodException(what);
            Harmony.Patch(target, prefix: Hook(prefix), postfix: Hook(postfix));
        }
        catch (Exception e)
        {
            warn($"Patching {what} failed ({e.Message}); the feature depending on it is off.");
        }
    }

    public static void Safely(string hook, Action body)
    {
        try { body(); }
        catch (Exception e) { warn($"Hook {hook} failed: {e}"); }
    }
}
