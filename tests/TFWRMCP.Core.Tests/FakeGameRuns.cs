using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using TFWRMCP;

namespace TFWRMCP.Core.Tests;

/// <summary>Script and Run behaviour of the fake game: tests drive Run events through <see cref="Emit"/>.</summary>
public sealed partial class FakeGame
{
    public Dictionary<string, string> Scripts { get; } = new();
    public Dictionary<string, SyntaxError> SyntaxErrors { get; } = new();

    /// <summary>Runs synchronously inside StartRun once the Run has started; use it to script the Run's events.</summary>
    public Action<string>? OnStartRun { get; set; }

    public bool RunActive { get; private set; }
    public int StopCalls { get; private set; }

    public event Action<RunEvent>? RunEvents;

    public void Emit(RunEvent e)
    {
        if (e is RunStarted) RunActive = true;
        if (e is RunEnded) RunActive = false;
        RunEvents?.Invoke(e);
    }

    public Task<List<string>> ListScripts() => Task.FromResult(Scripts.Keys.ToList());

    public Task<string> ReadScript(string name) =>
        Scripts.TryGetValue(name, out var code) ? Task.FromResult(code) : throw new ToolException($"No Script named '{name}'.");

    public Task<bool> WriteScript(string name, string code)
    {
        var created = !Scripts.ContainsKey(name);
        if (created)
        {
            if (name.Length == 0 || name.Contains('/')) throw new ToolException($"Invalid Script name '{name}'.");
            if (Scripts.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)))
                throw new ToolException($"A window named '{name}' is already open.");
        }
        Scripts[name] = code;
        return Task.FromResult(created);
    }

    public Task DeleteScript(string name)
    {
        if (!Scripts.Remove(name)) throw new ToolException($"No Script named '{name}'.");
        return Task.CompletedTask;
    }

    public Task<SyntaxError?> StartRun(string name)
    {
        if (!Scripts.ContainsKey(name)) throw new ToolException($"No Script named '{name}'.");
        if (RunActive) throw new ToolException("A Run is already in progress.");
        if (SyntaxErrors.TryGetValue(name, out var error)) return Task.FromResult<SyntaxError?>(error);
        Emit(new RunStarted(name));
        OnStartRun?.Invoke(name);
        return Task.FromResult<SyntaxError?>(null);
    }

    public Task StopRun()
    {
        StopCalls++;
        if (RunActive) Emit(new RunEnded(RunEnd.Stopped));
        return Task.CompletedTask;
    }

    public async Task WaitForRun()
    {
        for (var i = 0; i < 200 && !RunActive; i++) await Task.Delay(10);
        Assert.True(RunActive, "the Run never started");
    }
}
