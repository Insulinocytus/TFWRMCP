using System;
using System.Collections.Generic;

namespace TFWRMCP;

/// <summary>The Agent's stop_on_failures: a function is failing once it has at least <see cref="MinCalls"/> calls in the window and more than <see cref="Ratio"/> of them failed.</summary>
public sealed class FailureRule
{
    public double Ratio { get; }
    public int MinCalls { get; }
    public HashSet<string> Ignore { get; }

    public FailureRule(double ratio, int minCalls, HashSet<string> ignore)
    {
        Ratio = ratio;
        MinCalls = minCalls;
        Ignore = ignore;
    }
}

/// <summary>
/// Counts each function's calls and failures over the last 10 game seconds for one Run. Time is cut into 0.1 s buckets
/// in a ring, so an event costs O(1) and allocates nothing however many calls the window holds; a call leaves the
/// window when its bucket does, so the window is 9.9 to 10 game seconds long.
/// </summary>
sealed class FailureWatch
{
    const double BucketSeconds = 0.1;
    const int Buckets = 100;

    sealed class Window
    {
        readonly int[] calls = new int[Buckets];
        readonly int[] failed = new int[Buckets];
        long head;
        public int TotalCalls { get; private set; }
        public int TotalFailed { get; private set; }

        public void Advance(long bucket)
        {
            if (bucket <= head) return;
            if (bucket - head >= Buckets)
            {
                Array.Clear(calls, 0, Buckets);
                Array.Clear(failed, 0, Buckets);
                TotalCalls = TotalFailed = 0;
            }
            else
            {
                for (var i = head + 1; i <= bucket; i++)
                {
                    var slot = (int)(i % Buckets);
                    TotalCalls -= calls[slot];
                    TotalFailed -= failed[slot];
                    calls[slot] = failed[slot] = 0;
                }
            }
            head = bucket;
        }

        public void Add(bool isFailed)
        {
            var slot = (int)(head % Buckets);
            calls[slot]++;
            TotalCalls++;
            if (!isFailed) return;
            failed[slot]++;
            TotalFailed++;
        }
    }

    readonly FailureRule rule;

    // A null window marks an ignored function.
    readonly Dictionary<string, Window?> windows = new();

    public FailureWatch(FailureRule rule) => this.rule = rule;

    static long BucketAt(double gameSeconds) => (long)Math.Floor(gameSeconds / BucketSeconds);

    bool IsFailing(Window window) =>
        window.TotalCalls >= rule.MinCalls && (double)window.TotalFailed / window.TotalCalls > rule.Ratio;

    /// <summary>Counts a call at <paramref name="gameSeconds"/>; true when it made its function fail the rule (only a failed call can).</summary>
    public bool Observe(string function, bool failed, double gameSeconds)
    {
        if (!windows.TryGetValue(function, out var window))
            windows[function] = window = rule.Ignore.Contains(function) ? null : new Window();
        if (window == null) return false;
        window.Advance(BucketAt(gameSeconds));
        window.Add(failed);
        return failed && IsFailing(window);
    }

    /// <summary>Every function that fails the rule at <paramref name="gameSeconds"/>, with its failed calls in the window.</summary>
    public Dictionary<string, int> Failing(double gameSeconds)
    {
        var failing = new Dictionary<string, int>();
        foreach (var (function, window) in windows)
        {
            if (window == null) continue;
            window.Advance(BucketAt(gameSeconds));
            if (IsFailing(window)) failing[function] = window.TotalFailed;
        }
        return failing;
    }
}
