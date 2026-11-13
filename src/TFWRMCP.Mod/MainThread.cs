using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace TFWRMCP.Mod;

/// <summary>Runs work submitted from HTTP threads on the Unity main thread; drained from the plugin's Update.</summary>
public static class MainThread
{
    static readonly ConcurrentQueue<Action> Queue = new();

    public static Task<T> Run<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Enqueue(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception e) { done.SetException(e); }
        });
        return done.Task;
    }

    public static Task Run(Action work) => Run(() =>
    {
        work();
        return true;
    });

    public static void Drain()
    {
        while (Queue.TryDequeue(out var work)) work();
    }
}
