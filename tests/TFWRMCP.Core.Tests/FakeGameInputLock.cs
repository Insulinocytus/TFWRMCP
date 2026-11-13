using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TFWRMCP;

namespace TFWRMCP.Core.Tests;

public sealed partial class FakeGame
{
    /// <summary>The Tool the Input Lock is held for, null when the human may play.</summary>
    public string? LockedBy { get; private set; }

    /// <summary>"lock:tool" / "unlock" for every SetInputLock call, "f10" for every takeover.</summary>
    public List<string> LockEvents { get; } = new();

    public event Action? UserTookOver;

    public Task SetInputLock(string? tool)
    {
        LockedBy = tool;
        LockEvents.Add(tool == null ? "unlock" : "lock:" + tool);
        return Task.CompletedTask;
    }

    /// <summary>Like the real game: F10 only counts during an Input Lock, and the game itself releases it first.</summary>
    public void PressF10()
    {
        if (LockedBy == null) return;
        LockedBy = null;
        LockEvents.Add("f10");
        UserTookOver?.Invoke();
    }

    public async Task WaitForLock(string tool)
    {
        for (var i = 0; i < 200 && LockedBy != tool; i++) await Task.Delay(10);
        if (LockedBy != tool) throw new TimeoutException($"the Input Lock was never held for '{tool}'");
    }
}
