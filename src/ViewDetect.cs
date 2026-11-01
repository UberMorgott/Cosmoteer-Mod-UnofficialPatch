using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading;
using Cosmoteer.Game;
using Cosmoteer.Game.Gui;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Modes;
using Cosmoteer.Modes.Creative;
using Cosmoteer.Simulation;
using Halfling.Scene2D;
using HarmonyLib;

namespace Performance;

/// <summary>
/// Live-read detector (decoupling spec §5, step S2 runtime part): debug-only, opt-in with <c>PERFORMANCE_VIEW_DETECT=1</c>.
/// Off (the default) installs nothing: <see cref="Patches"/> is empty and <see cref="Initialize"/> returns at once.
///
/// Phase flag: brackets (prefix + finalizer, restore on exit) on the main-thread frame entry points mark the render phases
/// Update (GameRoot.Update minus ticks: non-det update buckets, ExecuteQueued(false), Mode.Update), Input (GameRoot.Input),
/// Draw (GameRoot.Draw, BeforeDraw included) and Gui (GameGui.Input/Draw/InputSelectionState/UpdateIndicators), and the
/// non-render phases Tick (SceneRoot.DoFixedUpdates, the whole outer fixed-update loop) and BetweenTickInput
/// (SimRoot.OnInputTick, GameModeManager/CreativeGameModeManager.OnInputTick, SPManager/BaseMPManager.Update and every
/// AdvanceNetworkTime that executes inputs). Brackets restore only scopes they entered (token in __state). The flag is one static word written only by the
/// main thread; FastParallel pool threads ("FastParallel Pool") inherit it, because they only run work the main thread
/// dispatched in its current phase. Hits from any other thread are counted separately and not attributed.
///
/// Getter guards: one postfix per guarded getter (the hottest sim-owned getters of the S2 IL scan), emitted per getter so
/// the hot path takes no <c>__originalMethod</c> (that allocates per call). Outside a render phase a guard is one static
/// read. In a render phase it counts per getter and phase, and resolves the caller on sampled hits (all of the first
/// <see cref="FullSampleHits"/>, then 1 in <see cref="SampleEvery"/>); the first <see cref="Detector.MaxStacksPerGuard"/>
/// distinct callers per getter keep a call stack. Report: <c>Performance.view-detect-&lt;pid&gt;.md</c> next to
/// Performance.log, rewritten every 10 s from the Draw bracket, at process exit and on rollback.
///
/// Shared generic code is never patched: a getter whose body is shared (__Canon) is skipped and listed in the report.
/// Inlining: a guard sees only calls that reach the getter's entry point. Every guard is marked NoInlining, so callers
/// JIT-compiled after install call it; callers compiled before install, and ReadyToRun code that inlined it ahead of time
/// (count per getter in the report, from the exact R2R table), escape. Run with <c>DOTNET_ReadyToRun=0</c> so no
/// precompiled inlined copy runs. The IL scan and the S3 lag test remain the backstop for anything that escapes.
///
/// IL-pin policy: no target is IL-hash pinned. Pins protect transpilers and replacements that depend on the exact vanilla
/// body; these patches are prefix/finalizer/postfix only, never change arguments, results or control flow, and read nothing
/// of the game. A changed body cannot make them wrong, only change what they observe; a missing bracket target throws
/// (rollback, as for every patch), a guard that cannot be resolved or patched is skipped and listed.
/// </summary>
internal static class ViewDetect
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("PERFORMANCE_VIEW_DETECT"));
    internal static bool IsRequested(string? value) => value == "1";

    internal enum Phase
    {
        None = 0,
        Tick = 1,
        BetweenTickInput = 2,
        Update = 3,
        Input = 4,
        Draw = 5,
        Gui = 6,
    }

    internal const int PhaseCount = 7;
    internal const int FirstRender = (int)Phase.Update;
    internal const int RenderPhases = PhaseCount - FirstRender;
    internal const long FullSampleHits = 4096;
    internal const long SampleEvery = 256;
    internal const string FastParallelThreadName = "FastParallel Pool";
    /// <summary>Known to <see cref="BlueprintToggleUpdates"/>' assembly scan, which otherwise pauses on unknown dynamic code.</summary>
    internal const string GuardAssemblyName = "Performance.ViewDetectGuards";

    /// <summary>
    /// Top getters by reaching roots in the S2 IL scan (scratchpad ilscan\report.json, all phase groups summed), in ilscan
    /// rank order; non-getters (EnqueueNonDeterministic, events, calls) left out. Names as ilscan prints them.
    /// </summary>
    internal static readonly string[] DefaultGuards =
    [
        "Cosmoteer.Simulation.SimRoot::get_Camera",
        "Cosmoteer.Ships.Ship::get_Parts",
        "Cosmoteer.Ships.Ship::get_WorldBoundingCircle",
        "Cosmoteer.Simulation.SimRoot::get_Game",
        "Cosmoteer.Simulation.SimRoot::get_Rendering",
        "Cosmoteer.Ships.Parts.PartsManager::get_BoundingRect",
        "Cosmoteer.Ships.Parts.Part::get_Rules",
        "Cosmoteer.Ships.Ship::get_Metadata",
        "Cosmoteer.Ships.Ship::get_IsSignificant",
        "Cosmoteer.Simulation.SimRoot::get_Viewport",
        "Cosmoteer.Ships.Ship::get_StructureContiguity",
        "Cosmoteer.Ships.Ship::get_Sim",
        "Cosmoteer.Simulation.SimRoot::get_Ships",
        "Cosmoteer.Simulation.SimRoot::get_Mode",
        "Cosmoteer.Ships.Parts.Part::get_Ship",
        "Cosmoteer.Simulation.SimRoot::get_Rules",
        "Cosmoteer.Crew.SpaceCrew::Cosmoteer.Simulation.Cameras.IFocusTarget.get_IsSignificant",
        "Cosmoteer.Crew.CrewSoul::Cosmoteer.Simulation.Cameras.IFocusTarget.get_IsSignificant",
        "Cosmoteer.Ships.Crew.ShipCrew::Cosmoteer.Simulation.Cameras.IFocusTarget.get_IsSignificant",
        "Cosmoteer.Ships.Parts.Part::Cosmoteer.Simulation.Cameras.IFocusTarget.get_IsSignificant",
        "Cosmoteer.Simulation.SimRoot::get_Doodads",
        "Cosmoteer.Ships.Ship::Cosmoteer.Simulation.Cameras.IFrameOfRotation.get_WorldRotation",
        "Cosmoteer.Ships.Ship::get_Crew",
        "Cosmoteer.Crew.CrewSoul::get_Role",
        "Cosmoteer.Ships.Crew.ShipCrew::get_Soul",
        "Cosmoteer.Ships.Ship::get_Weapons",
        "Cosmoteer.Simulation.SimRoot::get_Crew",
        "Halfling.Scene2D.Physics.ScenePhysicsWrapper::get_LinearVelocity",
        "Cosmoteer.Simulation.SimRoot::get_WorldViewRect",
        "Cosmoteer.Simulation.SimRoot::get_Nuggets",
    ];

    // ---- phase flag ----

    private static int _phase;
    private static int _mainThread = -1;
    private static readonly long[] PhaseEntries = new long[PhaseCount];
    [ThreadStatic] private static int _threadKind; // 0 unknown, 2 FastParallel pool, 3 other (main is checked by id)

    internal static Phase Current => (Phase)Volatile.Read(ref _phase);
    internal static bool IsRender(Phase phase) => (int)phase >= FirstRender;
    internal static long Entries(Phase phase) => Interlocked.Read(ref PhaseEntries[(int)phase]);

    /// <summary>The thread whose brackets drive the flag (the game's main thread; the test thread in tests).</summary>
    internal static void BindMainThread(int managedThreadId) => Volatile.Write(ref _mainThread, managedThreadId);

    /// <summary>
    /// Main thread only: sets the phase and returns an entered token (previous phase + 1, always &gt; 0). Any other thread
    /// gets 0 and changes nothing. 0 is also what a finalizer receives as <c>__state</c> when its prefix never ran (an
    /// earlier prefix threw or skipped), so <see cref="Exit"/> restores only scopes that were actually entered.
    /// </summary>
    internal static int Enter(Phase phase)
    {
        if (Environment.CurrentManagedThreadId != Volatile.Read(ref _mainThread)) return 0;
        int previous = Volatile.Read(ref _phase);
        Volatile.Write(ref _phase, (int)phase);
        Interlocked.Increment(ref PhaseEntries[(int)phase]);
        return previous + 1;
    }

    internal static void Exit(int token)
    {
        if (token <= 0 || Environment.CurrentManagedThreadId != Volatile.Read(ref _mainThread)) return;
        Volatile.Write(ref _phase, token - 1);
    }

    internal enum ThreadKind { Main = 1, Worker = 2, Other = 3 }

    internal static ThreadKind CurrentThreadKind()
    {
        if (Environment.CurrentManagedThreadId == Volatile.Read(ref _mainThread)) return ThreadKind.Main;
        int kind = _threadKind;
        if (kind == 0) _threadKind = kind = Thread.CurrentThread.Name == FastParallelThreadName ? (int)ThreadKind.Worker : (int)ThreadKind.Other;
        return (ThreadKind)kind;
    }

    /// <summary>Phase seen by the current thread: the main thread's phase on the main thread and pool workers, else None.</summary>
    internal static Phase EffectivePhase() => CurrentThreadKind() == ThreadKind.Other ? Phase.None : Current;

    /// <summary>Test seam: clears the phase and its entry counters.</summary>
    internal static void ResetPhase()
    {
        Volatile.Write(ref _phase, 0);
        for (int i = 0; i < PhaseCount; i++) Interlocked.Exchange(ref PhaseEntries[i], 0);
    }

    // ---- bracket patches (registered through Patcher.Patches when requested) ----

    internal static class UpdateBracket
    {
        public static void Prefix(out int __state) => __state = Enter(Phase.Update);
        public static Exception? Finalizer(int __state, Exception? __exception) { Exit(__state); return __exception; }
    }

    internal static class InputBracket
    {
        public static void Prefix(out int __state) => __state = Enter(Phase.Input);
        public static Exception? Finalizer(int __state, Exception? __exception) { Exit(__state); return __exception; }
    }

    internal static class DrawBracket
    {
        public static void Prefix(out int __state) => __state = Enter(Phase.Draw);
        public static Exception? Finalizer(int __state, Exception? __exception)
        {
            Exit(__state);
            MaybeFlush();
            return __exception;
        }
    }

    internal static class GuiBracket
    {
        public static void Prefix(out int __state) => __state = Enter(Phase.Gui);
        public static Exception? Finalizer(int __state, Exception? __exception) { Exit(__state); return __exception; }
    }

    internal static class TickBracket
    {
        public static void Prefix(out int __state) => __state = Enter(Phase.Tick);
        public static Exception? Finalizer(int __state, Exception? __exception) { Exit(__state); return __exception; }
    }

    internal static class InputTickBracket
    {
        public static void Prefix(out int __state) => __state = Enter(Phase.BetweenTickInput);
        public static Exception? Finalizer(int __state, Exception? __exception) { Exit(__state); return __exception; }
    }

    private static MethodBase Require(Type type, string name, Type[]? parameters) =>
        (parameters == null ? AccessTools.DeclaredMethod(type, name) : AccessTools.DeclaredMethod(type, name, parameters))
        ?? throw new MissingMethodException(type.FullName, name);

    /// <summary>Every phase bracket. Resolved eagerly: a missing target throws (rollback), like every other patch.</summary>
    internal static (MethodBase Target, Type Patch)[] Targets =>
    [
        (Require(typeof(GameRoot), "Update", [typeof(Action)]), typeof(UpdateBracket)),
        (Require(typeof(GameRoot), "Input", [typeof(Action)]), typeof(InputBracket)),
        (Require(typeof(GameRoot), "Draw", [typeof(Action)]), typeof(DrawBracket)),
        (Require(typeof(GameGui), nameof(GameGui.Input), Type.EmptyTypes), typeof(GuiBracket)),
        (Require(typeof(GameGui), nameof(GameGui.Draw), Type.EmptyTypes), typeof(GuiBracket)),
        (Require(typeof(GameGui), nameof(GameGui.InputSelectionState), Type.EmptyTypes), typeof(GuiBracket)),
        (Require(typeof(GameGui), nameof(GameGui.UpdateIndicators), Type.EmptyTypes), typeof(GuiBracket)),
        (Require(typeof(SceneRoot), "DoFixedUpdates", Type.EmptyTypes), typeof(TickBracket)),
        (Require(typeof(SimRoot), nameof(SimRoot.OnInputTick), null), typeof(InputTickBracket)),
        (Require(typeof(SPManager), nameof(SPManager.Update), Type.EmptyTypes), typeof(InputTickBracket)),
        (Require(typeof(BaseMPManager), nameof(BaseMPManager.Update), Type.EmptyTypes), typeof(InputTickBracket)),
        // Network input execution and its deterministic ExecuteQueued drain inside the GameRoot.Update loop condition
        // (BaseMPManager.cs:908/:941, RecordableSPManager.cs:98-); every override is bracketed (nesting is harmless).
        (Require(typeof(BaseMPManager), nameof(BaseMPManager.AdvanceNetworkTime), null), typeof(InputTickBracket)),
        (Require(typeof(RecordableSPManager), nameof(RecordableSPManager.AdvanceNetworkTime), null), typeof(InputTickBracket)),
        (Require(typeof(MPTempSetupManager), nameof(MPTempSetupManager.AdvanceNetworkTime), null), typeof(InputTickBracket)),
        // GameRoot.cs:1620 Mode.OnInputTick (deterministic, per input tick). Only Creative overrides it among game-mode
        // managers; the SimModeManager overrides run inside SimRoot.OnInputTick.
        (Require(typeof(GameModeManager), nameof(GameModeManager.OnInputTick), null), typeof(InputTickBracket)),
        (Require(typeof(CreativeGameModeManager), nameof(CreativeGameModeManager.OnInputTick), null), typeof(InputTickBracket)),
    ];

    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? Targets : [];

    // ---- getter guards ----

    private static Detector? _detector;
    private static int _flushing;
    private static long _lastFlush;
    private static bool _exitHooked;

    internal static Detector? Active => Volatile.Read(ref _detector);

    /// <summary>Hot path, called by the emitted guard postfixes.</summary>
    internal static void Guard(int id)
    {
        int phase = Volatile.Read(ref _phase);
        if (phase < FirstRender) return;
        Hit(id, phase);
    }

    private static void Hit(int id, int phase)
    {
        Detector? detector = Volatile.Read(ref _detector);
        if (detector == null || (uint)id >= (uint)detector.Guards.Length) return;
        if (CurrentThreadKind() == ThreadKind.Other)
        {
            detector.CountOtherThread(id);
            return;
        }
        if (detector.Count(id, phase)) Sample(detector, id);
    }

    internal static bool ShouldSample(long hit) => hit <= FullSampleHits || hit % SampleEvery == 0;

    private static void Sample(Detector detector, int id)
    {
        try
        {
            StackFrame[] frames = new StackTrace(1, false).GetFrames();
            MethodBase guarded = detector.Guards[id].Method;
            int callerIndex = -1;
            var methods = new MethodBase?[frames.Length];
            for (int i = 0; i < frames.Length; i++)
            {
                MethodBase? method = Harmony.GetMethodFromStackframe(frames[i]);
                methods[i] = method;
                if (callerIndex >= 0 || method == null || method.DeclaringType == typeof(ViewDetect) || method.DeclaringType == typeof(ViewDetectHook) ||
                    method.Module.Assembly.IsDynamic || SameMethod(method, guarded)) continue;
                callerIndex = i;
            }
            if (callerIndex < 0)
            {
                detector.RecordSample(id, "(unresolved)", static () => "");
                return;
            }
            detector.RecordSample(id, MethodName(methods[callerIndex]!), () => FormatStack(methods, callerIndex));
        }
        catch (Exception ex)
        {
            Log.Error("view-detect-sample", ex);
        }
    }

    private static bool SameMethod(MethodBase a, MethodBase b) => a.MetadataToken == b.MetadataToken && a.Module == b.Module;

    private static string FormatStack(MethodBase?[] methods, int from)
    {
        var sb = new StringBuilder();
        int written = 0;
        for (int i = from; i < methods.Length && written < 24; i++)
        {
            if (methods[i] is not { } method) continue;
            sb.Append("    at ").Append(MethodName(method)).Append('\n');
            written++;
        }
        return sb.ToString();
    }

    internal static string MethodName(MethodBase method) => (method.DeclaringType?.FullName ?? "?") + "::" + method.Name;

    private static (Detector Detector, MethodInfo[] Postfixes)? _prepared;

    /// <summary>
    /// Resolves the guards and emits their postfix assembly. Patcher calls it before any initializer, so the guard assembly
    /// exists, and is registered by instance, before BlueprintToggleUpdates installs its assembly-load watcher and scans.
    /// No exemption by name or thread exists for an assembly that loads later.
    /// </summary>
    internal static void Prepare()
    {
        if (!Requested || _prepared != null) return;
        try
        {
            Detector detector = Detector.Resolve(DefaultGuards, Environment.ProcessId);
            _prepared = (detector, EmitGuards(detector.Guards.Length));
        }
        catch (Exception ex)
        {
            Log.Error("view-detect-prepare", ex); // Initialize reports the detector off
        }
    }

    /// <summary>Installs the prepared guards, opens the report. False keeps the detector off (brackets stay inert).</summary>
    internal static bool Initialize(Harmony harmony)
    {
        if (!Requested) return true;
        BindMainThread(Environment.CurrentManagedThreadId);
        Detector? detector = null;
        try
        {
            (detector, MethodInfo[] postfixes) = _prepared ?? throw new InvalidOperationException("guards not prepared (see view-detect-prepare error)");
            Install(harmony, detector, postfixes);
            Activate(detector);
            Log.Info($"View detect active: {detector.Guards.Count(g => g.Installed)} getter guards, {detector.Skipped.Count + detector.Guards.Count(g => !g.Installed)} skipped; report {ReportPath(detector)}");
            foreach (GuardInfo guard in detector.Guards)
                Log.Info($"  guard {guard.Name}: IL {guard.IlBytes} B, R2R inliners {guard.R2RInliners}, inline {guard.InlineRisk}{(guard.Installed ? "" : ", NOT installed: " + guard.Failure)}");
            foreach ((string name, string reason) in detector.Skipped) Log.Info($"  skipped {name}: {reason}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("view-detect", ex);
            if (detector != null) Uninstall(harmony, detector);
            Disable();
            Log.Info("View detect disabled: " + ex.Message);
            return false;
        }
    }

    internal static void Activate(Detector detector)
    {
        Volatile.Write(ref _detector, detector);
        Volatile.Write(ref _lastFlush, Environment.TickCount64);
        if (_exitHooked) return;
        _exitHooked = true;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    private static void OnProcessExit(object? sender, EventArgs e) => Flush();

    /// <summary>Patches one emitted postfix per guard. A guard that fails is kept in the list, marked, and reported.</summary>
    internal static void Install(Harmony harmony, Detector detector, MethodInfo[] postfixes)
    {
        if (postfixes.Length != detector.Guards.Length) throw new ArgumentException("one postfix per guard", nameof(postfixes));
        for (int i = 0; i < detector.Guards.Length; i++)
        {
            GuardInfo guard = detector.Guards[i];
            try
            {
                harmony.Patch(guard.Method, postfix: new HarmonyMethod(postfixes[i]));
                guard.Postfix = postfixes[i];
                guard.Installed = true;
                if (!Patcher.TryDisableInlining(guard.Method, out string? error)) guard.InlineError = error;
            }
            catch (Exception ex)
            {
                guard.Failure = "patch failed: " + (ex.InnerException ?? ex).Message;
            }
        }
    }

    internal static void Uninstall(Harmony harmony, Detector detector)
    {
        foreach (GuardInfo guard in detector.Guards)
        {
            if (guard.Postfix == null) continue;
            try { harmony.Unpatch(guard.Method, guard.Postfix); }
            catch (Exception ex) { Log.Error("view-detect-unpatch", ex); }
            guard.Postfix = null;
            guard.Installed = false;
        }
    }

    private static readonly object EmitGate = new();
    private static readonly List<Assembly> Emitted = [];

    /// <summary>
    /// Provenance, not name: true only for an assembly instance <see cref="EmitGuards"/> created and registered (the builder
    /// and the runtime assembly behind it, which AppDomain.GetAssemblies lists). A foreign assembly with the same name is
    /// never trusted, including one loaded while the guards are emitted.
    /// </summary>
    internal static bool IsGuardAssembly(Assembly assembly)
    {
        if (!assembly.IsDynamic) return false;
        lock (EmitGate) return Emitted.Any(e => ReferenceEquals(e, assembly));
    }

    /// <summary>
    /// One static method per guard, <c>static void G{i}() =&gt; ViewDetect.Guard(i)</c>, in a run-only dynamic assembly, so
    /// each postfix knows its getter without Harmony's per-call <c>__originalMethod</c> lookup.
    /// </summary>
    internal static MethodInfo[] EmitGuards(int count)
    {
        MethodInfo guard = typeof(ViewDetectHook).GetMethod(nameof(ViewDetectHook.Guard))!;
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(GuardAssemblyName), AssemblyBuilderAccess.Run);
        TypeBuilder type = assembly.DefineDynamicModule("Guards").DefineType("ViewDetectGuards",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class);
        for (int i = 0; i < count; i++)
        {
            MethodBuilder method = type.DefineMethod("G" + i.ToString(CultureInfo.InvariantCulture), MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Call, guard);
            il.Emit(OpCodes.Ret);
        }
        Type created = type.CreateType();
        lock (EmitGate)
        {
            Emitted.Add(assembly);
            if (!ReferenceEquals(created.Assembly, assembly)) Emitted.Add(created.Assembly);
        }
        return Enumerable.Range(0, count).Select(i => created.GetMethod("G" + i.ToString(CultureInfo.InvariantCulture))!).ToArray();
    }

    internal static string ReportPath(Detector detector) => Path.Combine(Log.ModFolder, $"Performance.view-detect-{detector.ProcessId}.md");

    /// <summary>Main thread, from the Draw bracket: rewrites the report at most every 10 s.</summary>
    internal static void MaybeFlush()
    {
        if (Volatile.Read(ref _detector) == null) return;
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastFlush) < 10_000) return;
        Volatile.Write(ref _lastFlush, now);
        Flush();
    }

    internal static void Flush()
    {
        Detector? detector = Volatile.Read(ref _detector);
        if (detector == null || Interlocked.Exchange(ref _flushing, 1) != 0) return;
        try
        {
            string path = ReportPath(detector);
            string temp = path + ".tmp";
            File.WriteAllText(temp, detector.FormatReport(DateTime.Now, Enumerable.Range(0, PhaseCount).Select(p => Entries((Phase)p)).ToArray()));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("view-detect-report", ex);
        }
        finally
        {
            Volatile.Write(ref _flushing, 0);
        }
    }

    /// <summary>Kill switch (rollback, failed init): writes the last report, then every guard returns at once.</summary>
    internal static void Disable()
    {
        Flush();
        Volatile.Write(ref _detector, null);
        Volatile.Write(ref _phase, 0);
        if (!_exitHooked) return;
        _exitHooked = false;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
    }

    // ---- per-run state ----

    internal sealed class GuardInfo(string name, MethodBase method, int ilBytes, int precompiledInliners)
    {
        internal readonly string Name = name;
        internal readonly MethodBase Method = method;
        internal readonly int IlBytes = ilBytes;
        /// <summary>Precompiled bodies holding an inlined copy (exact, R2R table); they bypass the guard unless DOTNET_ReadyToRun=0.</summary>
        internal readonly int R2RInliners = precompiledInliners;
        internal MethodInfo? Postfix;
        internal bool Installed;
        internal string? Failure;
        internal string? InlineError;

        /// <summary>RyuJIT: IL &lt;= 16 B is always inlined, &lt;= 100 B may be (unless marked NoInlining).</summary>
        internal string InlineRisk => IlBytes <= 16 ? "always" : IlBytes <= 100 ? "candidate" : "unlikely";
    }

    internal sealed class Detector
    {
        internal const int MaxStacksPerGuard = 8;

        internal readonly GuardInfo[] Guards;
        internal readonly List<(string Name, string Reason)> Skipped;
        internal readonly int ProcessId;
        internal readonly DateTime Started = DateTime.Now;
        private readonly long[] _counts;
        private readonly long[] _totals;
        private readonly long[] _otherThread;
        private readonly Dictionary<string, long>[] _callers;
        private readonly List<(string Caller, string Stack)>[] _stacks;
        private readonly long[] _samples;
        private readonly object _gate = new();

        internal Detector(GuardInfo[] guards, List<(string, string)> skipped, int processId)
        {
            Guards = guards;
            Skipped = skipped;
            ProcessId = processId;
            _counts = new long[guards.Length * RenderPhases];
            _totals = new long[guards.Length];
            _otherThread = new long[guards.Length];
            _samples = new long[guards.Length];
            _callers = guards.Select(_ => new Dictionary<string, long>()).ToArray();
            _stacks = guards.Select(_ => new List<(string, string)>()).ToArray();
        }

        /// <summary>
        /// Resolves "Type::method" names (ilscan format) in Cosmoteer.dll / HalflingCore.dll. Missing, bodiless, open or
        /// shared generic (__Canon) methods and duplicates are skipped with a reason, never patched.
        /// </summary>
        internal static Detector Resolve(IEnumerable<string> names, int processId)
        {
            Assembly[] assemblies = [typeof(SimRoot).Assembly, typeof(SceneRoot).Assembly];
            return Resolve(names.Select(name =>
            {
                int split = name.IndexOf("::", StringComparison.Ordinal);
                Type? type = split <= 0 ? null : assemblies.Select(a => a.GetType(name[..split])).FirstOrDefault(t => t != null);
                return (name, type == null ? null : AccessTools.Method(type, name[(split + 2)..]));
            }), processId);
        }

        internal static Detector Resolve(IEnumerable<(string Name, MethodInfo? Method)> candidates, int processId)
        {
            var guards = new List<GuardInfo>();
            var skipped = new List<(string, string)>();
            var inlineTables = new Dictionary<Module, Dictionary<int, List<int>>?>();
            foreach ((string name, MethodInfo? method) in candidates)
            {
                string? reason = method switch
                {
                    null => "not found",
                    _ when method.IsAbstract || method.GetMethodBody() == null => "no IL body",
                    _ when method.ContainsGenericParameters || method.DeclaringType?.ContainsGenericParameters == true => "open generic",
                    _ when SharedGenericRetarget.IsShared(method) => "shared generic (__Canon body run by other instantiations)",
                    _ when guards.Any(g => SameMethod(g.Method, method)) => "duplicate of " + guards.First(g => SameMethod(g.Method, method)).Name,
                    _ => null,
                };
                if (reason != null)
                {
                    skipped.Add((name, reason));
                    continue;
                }
                if (!inlineTables.TryGetValue(method!.Module, out Dictionary<int, List<int>>? table))
                    inlineTables[method.Module] = table = R2RInlineInfo.Read(method.Module.Assembly.Location);
                int inliners = table != null && table.TryGetValue(R2RInlineInfo.Rid(method), out List<int>? rids) ? rids.Distinct().Count() : 0;
                int split = name.IndexOf("::", StringComparison.Ordinal);
                string resolved = split <= 0 || method.DeclaringType?.FullName == name[..split] ? name : $"{name} (declared {MethodName(method)})";
                guards.Add(new GuardInfo(resolved, method, method.GetMethodBody()!.GetILAsByteArray()!.Length, inliners));
            }
            return new Detector(guards.ToArray(), skipped, processId);
        }

        /// <summary>Counts one render-phase hit; true when this hit is sampled for caller attribution.</summary>
        internal bool Count(int id, int phase)
        {
            Interlocked.Increment(ref _counts[id * RenderPhases + (phase - FirstRender)]);
            return ShouldSample(Interlocked.Increment(ref _totals[id]));
        }

        internal void CountOtherThread(int id) => Interlocked.Increment(ref _otherThread[id]);

        internal long Total(int id) => Interlocked.Read(ref _totals[id]);
        internal long PhaseHits(int id, Phase phase) => Interlocked.Read(ref _counts[id * RenderPhases + ((int)phase - FirstRender)]);
        internal long OtherThread(int id) => Interlocked.Read(ref _otherThread[id]);

        /// <summary>Per-caller sample count; a call stack is kept for the first <see cref="MaxStacksPerGuard"/> distinct callers.</summary>
        internal void RecordSample(int id, string caller, Func<string> stack)
        {
            lock (_gate)
            {
                _samples[id]++;
                bool known = _callers[id].TryGetValue(caller, out long n);
                _callers[id][caller] = n + 1;
                if (!known && _stacks[id].Count < MaxStacksPerGuard) _stacks[id].Add((caller, stack()));
            }
        }

        internal IReadOnlyList<(string Caller, long Samples)> Callers(int id)
        {
            lock (_gate)
                return _callers[id].OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Select(c => (c.Key, c.Value)).ToList();
        }

        internal IReadOnlyList<(string Caller, string Stack)> Stacks(int id)
        {
            lock (_gate) return _stacks[id].ToList();
        }

        internal string FormatReport(DateTime now, long[] phaseEntries)
        {
            var sb = new StringBuilder();
            CultureInfo c = CultureInfo.InvariantCulture;
            sb.Append("# Performance view-detect report\n\n");
            sb.Append(c, $"pid {ProcessId}, started {Started:yyyy-MM-dd HH:mm:ss}, written {now:yyyy-MM-dd HH:mm:ss}. ");
            sb.Append(c, $"Guards installed {Guards.Count(g => g.Installed)}, skipped {Skipped.Count + Guards.Count(g => !g.Installed)}.\n");
            sb.Append(c, $"Counts are exact; callers are sampled (every hit up to {FullSampleHits} per getter, then 1 in {SampleEvery}). ");
            sb.Append("Inlined calls escape the guards (see R2R inliners / inline columns); the IL scan and the lag test are the backstop.\n\n");

            sb.Append("## Phase entries\n\n| Phase | Entries |\n|---|---:|\n");
            for (int p = 1; p < PhaseCount; p++) sb.Append(c, $"| {(Phase)p} | {(p < phaseEntries.Length ? phaseEntries[p] : 0)} |\n");

            sb.Append("\n## Guarded getters (render-phase hits)\n\n");
            sb.Append("| # | Getter | Total | Update | Input | Draw | Gui | Other thread | Callers (sampled) | IL B | R2R inliners | Inline |\n");
            sb.Append("|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|\n");
            int[] order = Enumerable.Range(0, Guards.Length).OrderByDescending(Total).ThenBy(i => i).ToArray();
            int rank = 0;
            foreach (int i in order)
            {
                GuardInfo g = Guards[i];
                string inline = g.InlineRisk + (g.InlineError != null ? " (NoInlining failed)" : "");
                sb.Append(c, $"| {++rank} | `{g.Name}`{(g.Installed ? "" : " (not installed)")} | {Total(i)} | {PhaseHits(i, Phase.Update)} | {PhaseHits(i, Phase.Input)} | {PhaseHits(i, Phase.Draw)} | {PhaseHits(i, Phase.Gui)} | {OtherThread(i)} | {Callers(i).Count} | {g.IlBytes} | {g.R2RInliners} | {inline} |\n");
            }

            sb.Append("\n## Callers per getter\n");
            foreach (int i in order.Where(i => Total(i) > 0))
            {
                long sampled;
                lock (_gate) sampled = _samples[i];
                sb.Append(c, $"\n### `{Guards[i].Name}` ({Total(i)} hits, {sampled} sampled)\n\n| Caller | Sampled hits |\n|---|---:|\n");
                foreach ((string caller, long samples) in Callers(i)) sb.Append(c, $"| `{caller}` | {samples} |\n");
                foreach ((string caller, string stack) in Stacks(i))
                    sb.Append(c, $"\nFirst stack from `{caller}`:\n\n```\n{stack}```\n");
            }

            sb.Append("\n## Skipped\n\n| Getter | Reason |\n|---|---|\n");
            foreach ((string name, string reason) in Skipped) sb.Append(c, $"| `{name}` | {reason} |\n");
            foreach (GuardInfo g in Guards.Where(g => !g.Installed)) sb.Append(c, $"| `{g.Name}` | {g.Failure ?? "not installed"} |\n");
            return sb.ToString();
        }
    }
}

/// <summary>Entry point of the emitted guard postfixes (public: called from a dynamic assembly).</summary>
public static class ViewDetectHook
{
    public static void Guard(int id) => ViewDetect.Guard(id);
}
