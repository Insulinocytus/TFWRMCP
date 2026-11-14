using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

    /// <summary>Runs once, inside the next StartRun before it looks at whether a Run is in progress: the human got there first.</summary>
    public Action? BeforeStartRun { get; set; }

    public bool RunActive { get; private set; }
    public int StopCalls { get; private set; }

    /// <summary>Steps the Run has executed so far; a stopped Run reports the value it has then.</summary>
    public long RunTicks { get; set; }

    /// <summary>The real farm's game clock; tests advance it by hand.</summary>
    public double GameSeconds { get; set; }

    public event Action<RunEvent>? RunEvents;

    public void Emit(RunEvent e)
    {
        if (e is RunStarted started)
        {
            RunActive = true;
            simulating = started.Simulated;
        }
        if (e is RunEnded)
        {
            RunActive = false;
            simulating = false;
        }
        RunEvents?.Invoke(e);
    }

    public Task<List<string>> ListScripts() => Task.FromResult(Scripts.Keys.ToList());

    public Task<string> ReadScript(string name) =>
        Scripts.TryGetValue(name, out var code) ? Task.FromResult(code) : throw new ToolException($"No Script named '{name}'.");

    public Task<SyntaxError?> ParseScript(string name)
    {
        if (!Scripts.ContainsKey(name)) throw new ToolException($"No Script named '{name}'.");
        return Task.FromResult(SyntaxErrors.GetValueOrDefault(name));
    }

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

    public Task<SyntaxError?> StartRun(string name, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (!Scripts.ContainsKey(name)) throw new ToolException($"No Script named '{name}'.");
        var before = BeforeStartRun;
        BeforeStartRun = null;
        before?.Invoke();
        if (RunActive) throw new ToolException("A Run is already in progress.");
        if (SyntaxErrors.TryGetValue(name, out var error)) return Task.FromResult<SyntaxError?>(error);
        Emit(new RunStarted(name, new Dictionary<string, double>(State.Inventory), startedByTool: true));
        OnStartRun?.Invoke(name);
        return Task.FromResult<SyntaxError?>(null);
    }

    public Task StopRun()
    {
        StopCalls++;
        if (!RunActive) return Task.CompletedTask;
        if (simulating)
            Emit(new RunEnded(RunEnd.Stopped, new Dictionary<string, double>(TemporaryInventory), RunTicks, TemporarySeconds));
        else
            Emit(new RunEnded(RunEnd.Stopped, new Dictionary<string, double>(State.Inventory), RunTicks));
        return Task.CompletedTask;
    }

    /// <summary>The simulated Runs started so far: Script name and what was asked for.</summary>
    public List<(string Name, SimulatedRunOptions Options)> SimulatedRuns { get; } = new();

    /// <summary>Runs synchronously inside StartSimulatedRun once the Run has started.</summary>
    public Action<string>? OnStartSimulatedRun { get; set; }

    /// <summary>The temporary farm's inventory and clock; tests change them by hand.</summary>
    public Dictionary<string, double> TemporaryInventory { get; set; } = new();

    public double TemporarySeconds { get; set; }

    bool simulating;

    public Task<SyntaxError?> StartSimulatedRun(string name, SimulatedRunOptions options, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (!Scripts.ContainsKey(name)) throw new ToolException($"No Script named '{name}'.");
        if (RunActive) throw new ToolException("A Run is already in progress.");
        if (SyntaxErrors.TryGetValue(name, out var error)) return Task.FromResult<SyntaxError?>(error);
        SimulatedRuns.Add((name, options));
        Emit(new RunStarted(name, new Dictionary<string, double>(TemporaryInventory), simulated: true));
        OnStartSimulatedRun?.Invoke(name);
        return Task.FromResult<SyntaxError?>(null);
    }

    public Task<TemporaryFarm?> ReadTemporaryFarm() => Task.FromResult(
        simulating ? new TemporaryFarm(new Dictionary<string, double>(TemporaryInventory), TemporarySeconds) : null);

    public async Task WaitForRun()
    {
        for (var i = 0; i < 200 && !RunActive; i++) await Task.Delay(10);
        Assert.True(RunActive, "the Run never started");
    }
}
