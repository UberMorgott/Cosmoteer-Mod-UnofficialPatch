using System;

namespace UnofficialPatch;

/// <summary>
/// Render-view identity (decoupling spec §R1): a slot plus a 32-bit generation. Generation 0 means "none"; a slot's
/// generation is incremented when the slot is freed, and a slot freed at <see cref="uint.MaxValue"/> is retired for the
/// session, so a stale handle can never resolve to another object (no ABA for leases, commands or selection handles).
/// </summary>
internal readonly struct ViewId : IEquatable<ViewId>
{
    public readonly uint Slot;
    public readonly uint Gen;

    public ViewId(uint slot, uint gen)
    {
        Slot = slot;
        Gen = gen;
    }

    public static ViewId None => default;
    public bool IsNone => Gen == 0;

    public bool Equals(ViewId other) => Slot == other.Slot && Gen == other.Gen;
    public override bool Equals(object? obj) => obj is ViewId other && Equals(other);
    public override int GetHashCode() => (int)(Slot * 0x9E3779B1u ^ Gen);
    public static bool operator ==(ViewId a, ViewId b) => a.Equals(b);
    public static bool operator !=(ViewId a, ViewId b) => !a.Equals(b);
    public override string ToString() => $"{Slot}:{Gen}";
}

/// <summary>
/// One slot map per object kind (spec §2.1, §R1). Slots are dense indices reused LIFO through a free list; every holder
/// resolves through <see cref="TryResolve"/>, which checks alive and generation equality. Not thread-safe: owned by the
/// sim side. Growth allocates; steady-state alloc/free does not.
/// </summary>
internal sealed class ViewSlotMap
{
    /// <summary>Slots are encoded with the kind into one int handle (5 kind bits), so a slot index stays below 2^26.</summary>
    internal const int MaxSlots = 1 << 26;

    private uint[] _gen = new uint[16];
    private bool[] _alive = new bool[16];
    private uint[] _free = new uint[16];
    private int _freeCount;
    private int _count;

    /// <summary>High-water slot count (slots ever handed out, retired ones included).</summary>
    public int Count => _count;
    public int Live { get; private set; }
    public int Retired { get; private set; }

    public ViewId Alloc()
    {
        uint slot;
        if (_freeCount > 0)
        {
            slot = _free[--_freeCount];
        }
        else
        {
            if (_count >= MaxSlots)
            {
                throw new InvalidOperationException("view slot space exhausted");
            }
            slot = (uint)_count++;
            if (slot >= _gen.Length)
            {
                Array.Resize(ref _gen, _gen.Length * 2);
                Array.Resize(ref _alive, _alive.Length * 2);
            }
            _gen[slot] = 1;
        }
        _alive[slot] = true;
        Live++;
        return new ViewId(slot, _gen[slot]);
    }

    /// <summary>Frees a live handle. A slot freed at generation <see cref="uint.MaxValue"/> is retired, never reused.</summary>
    public bool Free(ViewId id)
    {
        if (!TryResolve(id))
        {
            return false;
        }
        uint slot = id.Slot;
        _alive[slot] = false;
        Live--;
        if (_gen[slot] == uint.MaxValue)
        {
            Retired++;
            return true;
        }
        _gen[slot]++;
        if (_freeCount == _free.Length)
        {
            Array.Resize(ref _free, _free.Length * 2);
        }
        _free[_freeCount++] = slot;
        return true;
    }

    public bool TryResolve(ViewId id) => id.Gen != 0 && id.Slot < (uint)_count && _alive[id.Slot] && _gen[id.Slot] == id.Gen;

    public bool IsRetired(uint slot) => slot < (uint)_count && !_alive[slot] && _gen[slot] == uint.MaxValue;

    /// <summary>Test seam: sets the generation of a live slot (gen-retirement tests).</summary>
    internal ViewId ForceGeneration(ViewId id, uint gen)
    {
        if (!TryResolve(id) || gen == 0)
        {
            throw new ArgumentException("not a live handle", nameof(id));
        }
        _gen[id.Slot] = gen;
        return new ViewId(id.Slot, gen);
    }
}
