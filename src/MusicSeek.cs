using System;
using System.Collections.Generic;
using System.Reflection;
using Cosmoteer.Music;
using Halfling.Audio;
using Halfling.ObjectText;
using Halfling.Serialization.ObjectText;
using Halfling.Serialization;
using HarmonyLib;

namespace UnofficialPatch;

/// <summary>
/// Vanilla music keeps every layer of a track in step: a muted layer advances by setting its stream Position on every
/// audio buffer (MusicFileTrack.AdvanceImpl), and each set is an NVorbis seek that decodes pre-roll packets into fresh
/// float buffers (~190 MB per 20 s in an MP profile). Audio thread only, never sim. MusicRules registers the "music"
/// file factory on each load (vanilla SoundStream.SetFactoryForExtension); the postfix wraps the streams it makes in
/// <see cref="DeferredSeekStream"/>, which keeps seeks pending until the layer is read again.
/// </summary>
internal static class MusicSeek
{
    private const string Extension = "music";
    private const string CtorIlSha256 = "6F4EC0CC28C7ACD189BDD0D6DBEB45077F2246FA5CB797A5E66CD292CF9DDF4F";
    private const string PositionIlSha256 = "CCC0A8830EF91322AA45C4D23ADA81C27C3EBF7F528D3D2B1D4B5CA8CEEB94AE";

    /// <summary>NVorbis 0.10.4 build whose SeekTo behaviour <see cref="DeferredSeekStream"/> reproduces.</summary>
    private static readonly Guid NVorbisMvid = new("29622a92-8c01-4f05-9b21-eb74e2d703b8");

    internal static ConstructorInfo Target =>
        AccessTools.Constructor(typeof(MusicRules), [typeof(ObjectTextSerializer), typeof(IOTNode), typeof(ProgressTracker)])
        ?? throw new MissingMethodException(nameof(MusicRules), ".ctor");

    /// <summary>
    /// Changes nothing; refuses (Patcher rolls back to vanilla) unless the ctor still registers OggVorbisSoundStream for
    /// "music" and that stream's Position accessors and NVorbis are the exact builds the emulation was checked against.
    /// </summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        PropertyInfo position = AccessTools.Property(typeof(OggVorbisSoundStream), nameof(OggVorbisSoundStream.Position));
        Patcher.RequireIl(
            [.. PatchProcessor.GetOriginalInstructions(position.GetMethod), .. PatchProcessor.GetOriginalInstructions(position.SetMethod)],
            PositionIlSha256, "OggVorbisSoundStream.Position");
        Guid mvid = AccessTools.Field(typeof(OggVorbisSoundStream), "_reader").FieldType.Module.ModuleVersionId;
        if (mvid != NVorbisMvid)
        {
            throw new InvalidOperationException($"NVorbis build changed (MVID {mvid})");
        }
        return Patcher.RequireIl(instructions, CtorIlSha256, "MusicRules..ctor");
    }

    public static void Postfix()
    {
        if (SoundStream._soundStreamFactories.TryGetValue(Extension, out Func<string, SoundStream>? vanilla))
        {
            SoundStream.SetFactoryForExtension(Extension, path => vanilla(path) switch
            {
                OggVorbisSoundStream ogg => new DeferredSeekStream(ogg),
                var other => other,
            });
        }
    }
}

/// <summary>
/// An OggVorbisSoundStream whose Position sets are replayed only when it is read, giving the same samples and Position
/// as seeking at once. NVorbis SeekTo trims the end-of-stream packet using the position from before the seek, so the
/// decoder state after a seek depends on that seek and the one before it: the last two pending targets are replayed.
/// A seek that vanilla would have failed on and that is superseded before the next read never runs.
/// </summary>
internal sealed class DeferredSeekStream(OggVorbisSoundStream inner) : SoundStream
{
    private long? _previous;
    private long? _last;

    public override long Length => inner.Length;

    public override int ChannelCount => inner.ChannelCount;

    public override int Frequency => inner.Frequency;

    public override int BytesPerSample => inner.BytesPerSample;

    public override SoundStreamEncoding Encoding => inner.Encoding;

    /// <summary>Pending: what OggVorbisSoundStream returns after that set (sample position, rounded down to a block).</summary>
    public override long Position
    {
        get => _last is { } p ? p / BytesPerBlock * BytesPerBlock : inner.Position;
        set => (_previous, _last) = (_last, value);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_last is { } last)
        {
            long? previous = _previous;
            (_previous, _last) = (null, null);
            if (previous is { } p)
            {
                inner.Position = p;
            }
            inner.Position = last;
        }
        return inner.Read(buffer);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
