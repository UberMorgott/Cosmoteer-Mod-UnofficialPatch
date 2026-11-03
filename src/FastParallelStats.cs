using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Cosmoteer.Simulation;
using Halfling.Performance;
using HarmonyLib;

namespace UnofficialPatch;

/// <summary>
/// Diagnostic only (<c>UNOFFICIAL_PATCH_FP_STATS=1</c>): turns on vanilla FastParallel profiling and logs, per top-level
/// parallel region, wall time, summed batch wall time per thread, main-thread share, the longest single batch and
/// the first worker's join delay, to tell load imbalance from late worker arrival. Changes no sim state.
/// </summary>
internal static class FastParallelStats
{
    internal static readonly bool Requested = Environment.GetEnvironmentVariable("UNOFFICIAL_PATCH_FP_STATS") == "1";
    internal static readonly MethodInfo Target = AccessTools.Method(typeof(SimRoot), nameof(SimRoot.Update), Type.EmptyTypes)
        ?? throw new MissingMethodException(nameof(SimRoot), "Update");
    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? [(Target, typeof(FastParallelStats))] : [];

    /// <summary>Experiment (<c>UNOFFICIAL_PATCH_FP_THREADS=N</c>): pool worker count; vanilla = physical cores - 1.</summary>
    internal static void ApplyThreads()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("UNOFFICIAL_PATCH_FP_THREADS"), out int n) || n < 1) return;
        if (FastParallel.s_createdThreads) { Log.Info("FP threads: workers already created"); return; }
        Log.Info($"FP threads: {FastParallel.ThreadCount} -> {n}");
        FastParallel.ThreadCount = n;
    }

    private sealed class Agg
    {
        public long Calls, Jobs;
        public double Wall, Work, MainWork, MaxJob, Join;
    }

    private static readonly Dictionary<string, Agg> ByName = new();
    private static int _frames;

    // Profiling is read at the next frame's start, before FastParallel.Start clears it.
    public static void Prefix()
    {
        if (!FastParallel.EnableProfiling)
        {
            FastParallel.EnableProfiling = true;
            return;
        }
        if (FastParallel.IsRunning) return;
        Harvest();
        if (++_frames % 600 == 0) Flush();
    }

    private static void Harvest()
    {
        FastParallel.GetProfilerResults(out _, out IReadOnlyList<FastParallel.ProfilerTask> tasks, out int threads);
        var jobs = new List<(double S, double E)>[threads];
        for (int t = 0; t < threads; t++)
        {
            FastParallel.GetProfilerThreadInfo(t, out _, out IReadOnlyList<FastParallel.ProfilerThreadInterval> list);
            jobs[t] = list.Select(j => (j.Start.TotalMicroseconds, j.End.TotalMicroseconds)).OrderBy(j => j.Item1).ToList();
        }
        double topEnd = double.MinValue;
        foreach (FastParallel.ProfilerTask task in tasks.OrderBy(t => t.Start))
        {
            double s = task.Start.TotalMicroseconds, e = task.End.TotalMicroseconds;
            if (s < topEnd) continue; // nested For inside a batch
            topEnd = e;
            string name = task.Name ?? "?";
            if (!ByName.TryGetValue(name, out Agg? a)) ByName[name] = a = new Agg();
            a.Calls++;
            a.Wall += e - s;
            double join = double.MaxValue, maxJob = 0;
            bool joined = false;
            for (int t = 0; t < threads; t++)
            {
                double cursor = s;
                foreach ((double js, double je) in jobs[t])
                {
                    if (je <= s || js >= e) continue;
                    double from = Math.Max(js, cursor), to = Math.Min(je, e);
                    if (to > from) { a.Work += to - from; if (t == 0) a.MainWork += to - from; cursor = to; }
                    maxJob = Math.Max(maxJob, je - js);
                    a.Jobs++;
                    if (t != 0) { join = Math.Min(join, js - s); joined = true; }
                }
            }
            a.MaxJob += maxJob;
            if (joined) a.Join += join;
        }
    }

    private static void Flush()
    {
        int threads = FastParallel.ThreadCount + 1;
        foreach ((string name, Agg a) in ByName.OrderByDescending(p => p.Value.Wall).Take(16))
        {
            double n = a.Calls;
            Log.Info(string.Create(CultureInfo.InvariantCulture,
                $"FP {name}: calls {a.Calls} wall {a.Wall / n:F0}us work {a.Work / n:F0}us main {a.MainWork / n:F0}us main idle {(a.Wall - a.MainWork) / n:F0}us maxJob {a.MaxJob / n:F0}us join {a.Join / n:F0}us jobs {a.Jobs / n:F0} eff {a.Work / Math.Max(1, a.Wall * threads):P0}"));
        }
        Log.Info(string.Create(CultureInfo.InvariantCulture, $"FP total over {_frames} frames: wall {ByName.Values.Sum(a => a.Wall) / 1000:F0}ms work {ByName.Values.Sum(a => a.Work) / 1000:F0}ms"));
        ByName.Clear();
    }
}
