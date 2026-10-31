using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Cosmoteer.Ships.Rendering;

namespace Performance;

internal static class AtlasWriteCensus
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("PERFORMANCE_ATLAS_STATIC"), Environment.GetEnvironmentVariable("PERFORMANCE_ATLAS_WRITE_CENSUS"));
    internal static bool IsRequested(string? mode, string? flag) => mode == "3" && flag == "1";
    internal static readonly bool LayoutSupported = !RuntimeHelpers.IsReferenceOrContainsReferences<AtlasQuad>() && Unsafe.SizeOf<AtlasQuad>() == 464;
    private static long _writes, _identical, _different, _unknown, _successful, _identicalIntervals, _differentIntervals, _unknownIntervals, _emptyIntervals, _errors;
    internal static long Writes => Interlocked.Read(ref _writes);
    internal static long Identical => Interlocked.Read(ref _identical);
    internal static long Different => Interlocked.Read(ref _different);
    internal static long Unknown => Interlocked.Read(ref _unknown);
    internal static long Successful => Interlocked.Read(ref _successful);
    internal static long IdenticalIntervals => Interlocked.Read(ref _identicalIntervals);
    internal static long DifferentIntervals => Interlocked.Read(ref _differentIntervals);
    internal static long UnknownIntervals => Interlocked.Read(ref _unknownIntervals);
    internal static long EmptyIntervals => Interlocked.Read(ref _emptyIntervals);
    internal static long Errors => Interlocked.Read(ref _errors);
    internal static bool SameBytes(in AtlasQuad first, in AtlasQuad second) =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in first, 1)).SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in second, 1)));
    internal static void Record(Interval? interval, bool? identical, bool error)
    {
        try
        {
            Interlocked.Increment(ref _writes);
            if (identical == true) Interlocked.Increment(ref _identical);
            else if (identical == false) Interlocked.Increment(ref _different);
            else Interlocked.Increment(ref _unknown);
            if (error) Interlocked.Increment(ref _errors);
            interval?.Observe(identical);
        }
        catch (Exception) { Interlocked.Increment(ref _errors); Invalidate(interval); }
    }
    internal static Interval? CreateInterval()
    {
        try { return Requested ? new Interval() : null; }
        catch (Exception) { Interlocked.Increment(ref _errors); return null; }
    }
    internal static void Invalidate(Interval? interval)
    {
        try { interval?.Invalidate(); }
        catch (Exception) { Interlocked.Increment(ref _errors); }
    }
    internal static void Complete(Interval? interval)
    {
        if (interval == null) return;
        try
        {
            IntervalKind kind = interval.CloseSuccessful();
            Interlocked.Increment(ref _successful);
            switch (kind)
            {
                case IntervalKind.Unknown: Interlocked.Increment(ref _unknownIntervals); break;
                case IntervalKind.Different: Interlocked.Increment(ref _differentIntervals); break;
                case IntervalKind.Identical: Interlocked.Increment(ref _identicalIntervals); break;
                case IntervalKind.Empty: Interlocked.Increment(ref _emptyIntervals); break;
            }
        }
        catch (Exception) { Interlocked.Increment(ref _errors); Invalidate(interval); }
    }
    internal enum IntervalKind { Unknown, Different, Identical, Empty }
    internal sealed class Interval
    {
        private bool _hadUnknown = true, _hadDifferent, _anyWrite;
        internal void Observe(bool? identical)
        {
            _anyWrite = true;
            if (identical == null) _hadUnknown = true;
            else if (!identical.Value) _hadDifferent = true;
        }
        internal void Invalidate() => _hadUnknown = true;
        internal IntervalKind CloseSuccessful()
        {
            IntervalKind result = _hadUnknown ? IntervalKind.Unknown : _hadDifferent ? IntervalKind.Different : _anyWrite ? IntervalKind.Identical : IntervalKind.Empty;
            _hadUnknown = _hadDifferent = _anyWrite = false;
            return result;
        }
    }
}
