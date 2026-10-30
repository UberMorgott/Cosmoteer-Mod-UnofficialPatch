using System;
using System.Numerics;

namespace Performance;

internal readonly record struct AtlasUploadRange(int Start, int Count);

internal readonly struct AtlasUploadPlan
{
    private readonly AtlasUploadRange[]? _ranges;
    private readonly int _capacity;

    internal AtlasUploadPlan(long version, bool full, int capacity, AtlasUploadRange[]? ranges, int rangeCount)
    {
        Version = version;
        Full = full;
        _capacity = capacity;
        _ranges = ranges;
        RangeCount = rangeCount;
    }

    internal long Version { get; }
    internal bool Full { get; }
    internal int RangeCount { get; }

    internal AtlasUploadRange GetRange(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, RangeCount);
        return Full ? new AtlasUploadRange(0, _capacity) : _ranges![index];
    }
}

/// <summary>Single-threaded dirty index tracking; a plan remains borrowed until Complete or Fail.</summary>
internal sealed class AtlasDirtyRanges
{
    private readonly int _maxRegions;
    private ulong[] _dirtyBits;
    private AtlasUploadRange[] _ranges;
    private bool _planOutstanding;
    private long _plannedVersion;

    internal AtlasDirtyRanges(int capacity, int maxRegions = 32)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRegions);
        _maxRegions = maxRegions;
        Capacity = capacity;
        _dirtyBits = new ulong[WordCount(capacity)];
        _ranges = new AtlasUploadRange[Math.Min(capacity, maxRegions)];
    }

    internal int Capacity { get; private set; }
    internal int DirtyCount { get; private set; }
    internal bool RequiresFullUpload { get; private set; } = true;
    internal bool HasPendingChanges => RequiresFullUpload || DirtyCount != 0;
    internal long Version { get; private set; } = 1;

    internal void Mark(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Capacity);
        int word = index / 64;
        ulong mask = 1UL << (index % 64);
        if ((_dirtyBits[word] & mask) == 0)
        {
            _dirtyBits[word] |= mask;
            DirtyCount++;
        }
        // A second write to an already-dirty index can invalidate an upload of that index.
        Version++;
    }

    internal void RequireFull()
    {
        RequiresFullUpload = true;
        Version++;
    }

    internal void Resize(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (capacity != Capacity)
        {
            // Allocate both before changing state. Old borrowed plans retain their original scratch array.
            var bits = new ulong[WordCount(capacity)];
            var ranges = new AtlasUploadRange[Math.Min(capacity, _maxRegions)];
            _dirtyBits = bits;
            _ranges = ranges;
            Capacity = capacity;
            DirtyCount = 0;
        }
        // Also covers resource replacement at unchanged capacity.
        RequireFull();
    }

    internal AtlasUploadPlan Plan(bool singleRange = false)
    {
        // Event-driven reentrancy must never overwrite the outer caller's borrowed ranges.
        if (_planOutstanding)
        {
            RequireFull();
            return FullPlan();
        }
        _planOutstanding = true;
        _plannedVersion = Version;
        if (RequiresFullUpload) return FullPlan();
        if (DirtyCount == 0) return new AtlasUploadPlan(Version, false, Capacity, _ranges, 0);
        if (singleRange)
        {
            int firstWord = 0;
            while (_dirtyBits[firstWord] == 0) firstWord++;
            int lastWord = _dirtyBits.Length - 1;
            while (_dirtyBits[lastWord] == 0) lastWord--;
            int first = firstWord * 64 + BitOperations.TrailingZeroCount(_dirtyBits[firstWord]);
            int last = lastWord * 64 + 63 - BitOperations.LeadingZeroCount(_dirtyBits[lastWord]);
            int count = last - first + 1;
            if (count == Capacity)
            {
                RequiresFullUpload = true;
                return FullPlan();
            }
            // The established CPU/GPU baseline makes unchanged gaps safe to include in one native upload.
            _ranges[0] = new AtlasUploadRange(first, count);
            return new AtlasUploadPlan(Version, false, Capacity, _ranges, 1);
        }
        if (DirtyCount >= Capacity / 2 + Capacity % 2)
        {
            RequiresFullUpload = true;
            return FullPlan();
        }

        int rangeCount = 0;
        for (int word = 0; word < _dirtyBits.Length; word++)
        {
            ulong bits = _dirtyBits[word];
            while (bits != 0)
            {
                int index = word * 64 + BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                if (rangeCount != 0)
                {
                    AtlasUploadRange previous = _ranges[rangeCount - 1];
                    if (previous.Start + previous.Count == index)
                    {
                        _ranges[rangeCount - 1] = new AtlasUploadRange(previous.Start, previous.Count + 1);
                        continue;
                    }
                }
                if (rangeCount == _ranges.Length)
                {
                    RequiresFullUpload = true;
                    return FullPlan();
                }
                _ranges[rangeCount++] = new AtlasUploadRange(index, 1);
            }
        }
        return new AtlasUploadPlan(Version, false, Capacity, _ranges, rangeCount);
    }

    internal bool Complete(long version)
    {
        bool ownsPlan = _planOutstanding && version == _plannedVersion;
        if (!ownsPlan || version != Version)
        {
            // A nested completion cannot release the outer caller's scratch buffer.
            if (ownsPlan) _planOutstanding = false;
            RequireFull();
            return false;
        }
        _planOutstanding = false;
        if (DirtyCount != 0) Array.Clear(_dirtyBits);
        DirtyCount = 0;
        RequiresFullUpload = false;
        return true;
    }

    internal void Fail()
    {
        _planOutstanding = false;
        RequireFull();
    }

    private AtlasUploadPlan FullPlan() => new(Version, true, Capacity, null, Capacity == 0 ? 0 : 1);

    private static int WordCount(int capacity) => capacity / 64 + (capacity % 64 == 0 ? 0 : 1);
}
