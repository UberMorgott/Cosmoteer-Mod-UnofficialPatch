using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Halfling.Audio;
using HarmonyLib;
using Xunit;

namespace UnofficialPatch.Tests;

public class MusicSeekTests
{
    private const string Music = GameAssemblies.GameBin + @"\..\Data\music\Cluster_4.1_Battle_Layer.music";

    /// <summary>One MusicFileTrack-style stream operation: Advance (floats, clamped to Length), Seek (block), Reset, Read (floats).</summary>
    public enum Op { Advance, Seek, Reset, Read, ReadArray }

    [Fact]
    public void TranspilerMatchesRealGameIl()
    {
        var original = PatchProcessor.GetOriginalInstructions(MusicSeek.Target);
        Assert.Equal(original.Count, MusicSeek.Transpiler(original).Count());
    }

    [Fact]
    public void TranspilerRejectsChangedIl()
    {
        var original = PatchProcessor.GetOriginalInstructions(MusicSeek.Target);
        original.Insert(0, new CodeInstruction(OpCodes.Nop));

        Assert.Throws<InvalidOperationException>(() => MusicSeek.Transpiler(original).ToList());
    }

    [Fact]
    public void PostfixWrapsMusicOggStreams()
    {
        bool had = SoundStream._soundStreamFactories.TryGetValue("music", out Func<string, SoundStream>? saved);
        try
        {
            SoundStream.SetFactoryForExtension("music", path => new OggVorbisSoundStream(path));
            MusicSeek.Postfix();
            using SoundStream stream = SoundStream.FromFile(Music);
            Assert.IsType<DeferredSeekStream>(stream);
        }
        finally
        {
            if (had)
            {
                SoundStream._soundStreamFactories["music"] = saved!;
            }
            else
            {
                SoundStream._soundStreamFactories.Remove("music");
            }
        }
    }

    [Fact]
    public void SeeksOnlyWhenRead()
    {
        var inner = new OggVorbisSoundStream(Music);
        using var stream = new DeferredSeekStream(inner);
        int block = stream.BytesPerBlock;

        stream.Position = 1000 * block;
        stream.Position = 2000 * block + 1;
        Assert.Equal(0, inner.Position);
        Assert.Equal(2000 * block, stream.Position);

        int read = stream.Read(new byte[512 * 4]);
        Assert.Equal(2000 * block + read, inner.Position);
        Assert.Equal(inner.Position, stream.Position);
    }

    /// <summary>Codex counterexample: near the end, the seek before the last one changes what the last one decodes.</summary>
    [Fact]
    public void MatchesVanillaForBackToBackSeeksAtEnd()
    {
        long blocks = new OggVorbisSoundStream(Music).BlockCount;
        AssertSameAsVanilla([(Op.Seek, blocks - 65), (Op.Seek, blocks - 64), (Op.Read, 512), (Op.Seek, blocks - 64), (Op.Seek, blocks - 64), (Op.Read, 512)]);
    }

    /// <summary>Muted-layer pattern: runs of advances (to and past the end), resets and reads, near the end most of the time.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MatchesVanillaForRandomMusicSequences(int seed)
    {
        long blocks = new OggVorbisSoundStream(Music).BlockCount;
        var random = new Random(seed);
        var ops = new List<(Op, long)>();
        for (int i = 0; i < 200; i++)
        {
            int floats = random.Next(1, 3) * 1024 + random.Next(0, 3); // odd counts too: positions off the block grid
            ops.Add(random.Next(10) switch
            {
                < 4 => (Op.Advance, floats),
                4 => (Op.Seek, blocks - random.Next(0, 3000)),
                5 => (Op.Seek, random.Next(0, (int)Math.Min(blocks, int.MaxValue))),
                6 => (Op.Reset, 0),
                7 => (Op.ReadArray, floats),
                _ => (Op.Read, floats),
            });
        }
        AssertSameAsVanilla(ops);
    }

    private static void AssertSameAsVanilla(IEnumerable<(Op Op, long Value)> ops)
    {
        using SoundStream vanilla = new OggVorbisSoundStream(Music);
        using SoundStream deferred = new DeferredSeekStream(new OggVorbisSoundStream(Music));
        long length = vanilla.Length;
        int step = 0;
        foreach ((Op op, long value) in ops)
        {
            if (op is Op.Read or Op.ReadArray)
            {
                (int count, byte[] bytes) a = Read(vanilla, op, (int)value);
                (int count, byte[] bytes) b = Read(deferred, op, (int)value);
                Assert.Equal(a.count, b.count);
                Assert.True(a.bytes.AsSpan(0, a.count).SequenceEqual(b.bytes.AsSpan(0, b.count)), $"samples differ at step {step}"); // bitwise, not float ==
            }
            else
            {
                Apply(vanilla, op, value, length);
                Apply(deferred, op, value, length);
            }
            Assert.Equal(vanilla.Position, deferred.Position);
            step++;
        }
    }

    /// <summary>Same position arithmetic as MusicFileTrack.AdvanceImpl / ResetImpl.</summary>
    private static void Apply(SoundStream s, Op op, long value, long byteLength) =>
        s.Position = op switch
        {
            Op.Advance => Math.Min(s.Position + value * s.BytesPerSample, byteLength),
            Op.Seek => value * s.BytesPerBlock,
            _ => 0,
        };

    private static (int, byte[]) Read(SoundStream s, Op op, int floats)
    {
        var bytes = new byte[floats * sizeof(float)];
        int count = op == Op.Read ? s.Read(bytes.AsSpan()) : s.Read(bytes, 0, bytes.Length);
        return (count, bytes);
    }
}
