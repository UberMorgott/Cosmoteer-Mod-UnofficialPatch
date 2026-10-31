using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Cosmoteer.Ships.Rendering;
using Halfling.Graphics;

namespace Performance;

internal static class AtlasWriteCensus
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("PERFORMANCE_ATLAS_STATIC"), Environment.GetEnvironmentVariable("PERFORMANCE_ATLAS_WRITE_CENSUS"));
    internal static bool IsRequested(string? mode, string? flag) => mode == "3" && flag == "1";
    internal static readonly bool LayoutSupported = !RuntimeHelpers.IsReferenceOrContainsReferences<AtlasQuad>() && Unsafe.SizeOf<AtlasQuad>() == 464;
    internal static readonly bool FieldCensusAvailable = ValidateFieldLayout();
    internal enum Difference { None, Color, Other, Both }
    private static bool ValidateFieldLayout()
    {
        try
        {
            return LayoutSupported && !RuntimeHelpers.IsReferenceOrContainsReferences<AtlasVert>() && Unsafe.SizeOf<AtlasVert>() == 116 &&
                !RuntimeHelpers.IsReferenceOrContainsReferences<Color>() && Unsafe.SizeOf<Color>() == 16 &&
                Marshal.OffsetOf<AtlasQuad>(nameof(AtlasQuad.V1)).ToInt32() == 0 && Marshal.OffsetOf<AtlasQuad>(nameof(AtlasQuad.V2)).ToInt32() == 116 &&
                Marshal.OffsetOf<AtlasQuad>(nameof(AtlasQuad.V3)).ToInt32() == 232 && Marshal.OffsetOf<AtlasQuad>(nameof(AtlasQuad.V4)).ToInt32() == 348 &&
                Marshal.OffsetOf<AtlasVert>(nameof(AtlasVert.Color)).ToInt32() == 40;
        }
        catch (Exception) { return false; }
    }
    internal static Difference? Compare(in AtlasQuad first, in AtlasQuad second)
    {
        if (!FieldCensusAvailable) return null;
        ReadOnlySpan<byte> a = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in first, 1));
        ReadOnlySpan<byte> b = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in second, 1));
        bool color = false, other = false;
        for (int vertex = 0; vertex < 4; vertex++)
        {
            int start = vertex * 116;
            color |= !a.Slice(start + 40, 16).SequenceEqual(b.Slice(start + 40, 16));
            other |= !a.Slice(start, 40).SequenceEqual(b.Slice(start, 40)) || !a.Slice(start + 56, 60).SequenceEqual(b.Slice(start + 56, 60));
            if (color && other) return Difference.Both;
        }
        return color ? Difference.Color : other ? Difference.Other : Difference.None;
    }
    private static long _writes, _identical, _different, _unknown, _successful, _identicalIntervals, _differentIntervals, _unknownIntervals, _emptyIntervals, _errors;
    private static long _colorOnly, _colorAndOther, _otherOnly;
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
    internal static long ColorOnly => Interlocked.Read(ref _colorOnly);
    internal static long ColorAndOther => Interlocked.Read(ref _colorAndOther);
    internal static long OtherOnly => Interlocked.Read(ref _otherOnly);
    internal static void Record(Interval? interval, Difference? difference, bool error)
    {
        try
        {
            Interlocked.Increment(ref _writes);
            switch (difference)
            {
                case Difference.None: Interlocked.Increment(ref _identical); break;
                case Difference.Color: Interlocked.Increment(ref _different); Interlocked.Increment(ref _colorOnly); break;
                case Difference.Other: Interlocked.Increment(ref _different); Interlocked.Increment(ref _otherOnly); break;
                case Difference.Both: Interlocked.Increment(ref _different); Interlocked.Increment(ref _colorAndOther); break;
                default: Interlocked.Increment(ref _unknown); break;
            }
            if (error) Interlocked.Increment(ref _errors);
            interval?.Observe(difference);
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
        internal void Observe(Difference? difference)
        {
            _anyWrite = true;
            if (difference == null) _hadUnknown = true;
            else if (difference != Difference.None) _hadDifferent = true;
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
