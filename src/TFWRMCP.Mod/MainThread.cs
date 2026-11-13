using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace TFWRMCP.Mod;

/// <summary>Runs work submitted from HTTP threads on the Unity main thread; drained from the Mod's Update.</summary>
public static class MainThread
{
    static readonly ConcurrentQueue<Action> Queue = new();

    /// <summary>Cancelling means the work is dropped if it has not started by the time the main thread gets to it.</summary>
    public static Task<T> Run<T>(Func<T> work, CancellationToken cancel = default)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Enqueue(() =>
        {
            if (cancel.IsCancellationRequested)
            {
                done.SetCanceled();
                return;
            }

            try { done.SetResult(work()); }
            catch (Exception e) { done.SetException(e); }
        });
        return done.Task;
    }

    public static Task Run(Action work, CancellationToken cancel = default) => Run(() =>
    {
        work();
        return true;
    }, cancel);

    /// <summary>Fire and forget; failures are logged instead of lost.</summary>
    public static void Post(Action work) => Queue.Enqueue(() =>
    {
        try { work(); }
        catch (Exception e) { UnityEngine.Debug.LogException(e); }
    });

    public static void Drain()
    {
        while (Queue.TryDequeue(out var work)) work();
    }
}
