using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace UnofficialPatch;

/// <summary>
/// Reads the ReadyToRun inlining table of an assembly and answers "which precompiled method bodies contain an inlined
/// copy of method X". This is exact data written by crossgen2, not a heuristic.
///
/// Format (dotnet/runtime release/10.0):
///   - READYTORUN_HEADER at the CLI header's ManagedNativeHeader: 'RTR' signature, major/minor, flags, section count,
///     then {uint type, uint rva, uint size} per section (readytorun.h).
///   - Non-composite images carry section 119 CrossModuleInlineInfo of kind CrossModuleAllMethods, which reports every
///     inline (InliningInfoNode.cs:55 ReportAllInlinesInSearch, ReadyToRunCodegenNodeFactory.cs:779). Inliners are
///     recorded by their typical definition, i.e. the root method that was compiled, nested inlines included.
///   - Section body = NativeHashtable; each entry is a VertexSequence:
///       count, inlinee&lt;&lt;2 | 1 (cross-module inlinee) | 2 (has cross-module inliners),
///       [inlinee module index, only with READYTORUN_FLAG_MultiModuleVersionBubble 0x40 and not cross-module],
///       [cross-module inliner count + IL-body index deltas, only with flag 2],
///       local inliner RID deltas (multi-module: delta&lt;&lt;1 | 1 = followed by module index)
///     (InliningInfoNode.cs:286-352).
/// Any inconsistency throws, so the caller can refuse to run instead of silently missing inlined copies.
/// </summary>
internal static class R2RInlineInfo
{
    private const uint ReadyToRunSignature = 0x00525452; // "RTR"
    private const uint SectionInliningInfo2 = 114;
    private const uint SectionCrossModuleInlineInfo = 119;
    private const uint FlagMultiModuleVersionBubble = 0x40;
    private const ushort SupportedMajorVersion = 16;

    /// <summary>
    /// Returns inlinee MethodDef RID -> inliner MethodDef RIDs (same module only), or null when the image is plain IL
    /// (no precompiled code, so nothing can be inlined ahead of time).
    /// </summary>
    public static Dictionary<int, List<int>>? Read(string assemblyPath)
    {
        byte[] img = File.ReadAllBytes(assemblyPath);
        using var pe = new PEReader(new MemoryStream(img));
        MetadataReader md = pe.GetMetadataReader();
        CorHeader cor = pe.PEHeaders.CorHeader ?? throw new InvalidDataException("no CLI header");
        DirectoryEntry nativeHeader = cor.ManagedNativeHeaderDirectory;
        if (nativeHeader.Size == 0)
        {
            return null;
        }
        int h = FileOffset(pe, nativeHeader.RelativeVirtualAddress);
        if (BitConverter.ToUInt32(img, h) != ReadyToRunSignature)
        {
            throw new InvalidDataException("ManagedNativeHeader is not a ReadyToRun header");
        }
        ushort major = BitConverter.ToUInt16(img, h + 4);
        if (major != SupportedMajorVersion)
        {
            // Layout verified only for the R2R version shipped with Cosmoteer 0.30.5 (v16.0); fail closed otherwise.
            throw new NotSupportedException($"R2R major version {major} not supported (expected {SupportedMajorVersion})");
        }
        uint flags = BitConverter.ToUInt32(img, h + 8);
        int sectionCount = (int)BitConverter.ToUInt32(img, h + 12);
        int section = -1;
        for (int i = 0; i < sectionCount; i++)
        {
            int e = h + 16 + i * 12;
            uint type = BitConverter.ToUInt32(img, e);
            if (type == SectionCrossModuleInlineInfo)
            {
                section = FileOffset(pe, (int)BitConverter.ToUInt32(img, e + 4));
            }
            else if (type == SectionInliningInfo2)
            {
                // Composite/older layout; never seen in Cosmoteer 0.30.5. Refuse rather than guess.
                throw new NotSupportedException("R2R image uses InliningInfo2; decoder only supports CrossModuleInlineInfo");
            }
        }
        if (section < 0)
        {
            throw new InvalidDataException("R2R image has no inlining table; cannot prove which methods inline the targets");
        }

        bool multiModule = (flags & FlagMultiModuleVersionBubble) != 0;
        int maxRid = md.GetTableRowCount(TableIndex.MethodDef);
        var result = new Dictionary<int, List<int>>();

        // NativeHashtable: header byte = bucketShift<<2 | entryIndexSize; then the bucket offset table.
        byte header = img[section];
        int baseOffset = section + 1;
        int buckets = 1 << (header >> 2);
        int entryIndexSize = header & 3;
        int BucketStart(int b) => baseOffset + entryIndexSize switch
        {
            0 => img[baseOffset + b],
            1 => BitConverter.ToUInt16(img, baseOffset + 2 * b),
            _ => (int)BitConverter.ToUInt32(img, baseOffset + 4 * b),
        };

        for (int b = 0; b < buckets; b++)
        {
            int p = BucketStart(b), end = BucketStart(b + 1);
            while (p < end)
            {
                p++; // low byte of the hash code
                int relativeTo = p;
                int q = relativeTo + DecodeSigned(img, ref p);

                uint count = DecodeUnsigned(img, ref q);
                uint encodedInlinee = DecodeUnsigned(img, ref q);
                count--;
                // Fail closed on every layout we have not seen in Cosmoteer 0.30.5 (all entries there are local inlinee,
                // module 0, local inliners). Skipping such entries could hide an inlined ownership check.
                if ((encodedInlinee & 1) != 0)
                {
                    throw new NotSupportedException("R2R table has a cross-module inlinee entry");
                }
                if ((encodedInlinee & 2) != 0)
                {
                    throw new NotSupportedException("R2R table has cross-module inliners");
                }
                if (multiModule)
                {
                    uint inlineeModule = DecodeUnsigned(img, ref q);
                    count--;
                    if (inlineeModule != 0)
                    {
                        throw new NotSupportedException($"R2R table has an inlinee from module index {inlineeModule}");
                    }
                }
                var inliners = new List<int>();
                int rid = 0;
                while (count > 0)
                {
                    uint d = DecodeUnsigned(img, ref q);
                    count--;
                    if (multiModule)
                    {
                        if ((d & 1) != 0)
                        {
                            throw new NotSupportedException("R2R table has an inliner from another module");
                        }
                        rid += (int)(d >> 1);
                    }
                    else
                    {
                        rid += (int)d;
                    }
                    if (rid <= 0 || rid > maxRid)
                    {
                        throw new InvalidDataException($"inliner RID {rid} out of range (max {maxRid})");
                    }
                    inliners.Add(rid);
                }
                int inlineeRid = (int)(encodedInlinee >> 2);
                if (inlineeRid <= 0 || inlineeRid > maxRid)
                {
                    throw new InvalidDataException($"inlinee RID {inlineeRid} out of range (max {maxRid})");
                }
                if (result.TryGetValue(inlineeRid, out List<int>? existing))
                {
                    existing.AddRange(inliners);
                }
                else
                {
                    result.Add(inlineeRid, inliners);
                }
            }
        }
        return result;
    }

    private static int FileOffset(PEReader pe, int rva)
    {
        foreach (SectionHeader s in pe.PEHeaders.SectionHeaders)
        {
            if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData))
            {
                return rva - s.VirtualAddress + s.PointerToRawData;
            }
        }
        throw new InvalidDataException($"RVA 0x{rva:X} not in any section");
    }

    // NativeFormat variable-length integers (NativeFormatReader.h DecodeUnsigned / DecodeSigned).
    private static uint DecodeUnsigned(byte[] b, ref int p)
    {
        uint v = b[p];
        if ((v & 1) == 0) { p += 1; return v >> 1; }
        if ((v & 2) == 0) { uint r = (v >> 2) | ((uint)b[p + 1] << 6); p += 2; return r; }
        if ((v & 4) == 0) { uint r = (v >> 3) | ((uint)b[p + 1] << 5) | ((uint)b[p + 2] << 13); p += 3; return r; }
        if ((v & 8) == 0) { uint r = (v >> 4) | ((uint)b[p + 1] << 4) | ((uint)b[p + 2] << 12) | ((uint)b[p + 3] << 20); p += 4; return r; }
        if ((v & 16) == 0) { uint r = BitConverter.ToUInt32(b, p + 1); p += 5; return r; }
        throw new InvalidDataException($"bad NativeFormat unsigned at 0x{p:X}");
    }

    private static int DecodeSigned(byte[] b, ref int p)
    {
        uint v = b[p];
        if ((v & 1) == 0) { int r = (sbyte)b[p] >> 1; p += 1; return r; }
        if ((v & 2) == 0) { int r = (int)(v >> 2) | ((sbyte)b[p + 1] << 6); p += 2; return r; }
        if ((v & 4) == 0) { int r = (int)(v >> 3) | (b[p + 1] << 5) | ((sbyte)b[p + 2] << 13); p += 3; return r; }
        if ((v & 8) == 0) { int r = (int)(v >> 4) | (b[p + 1] << 4) | (b[p + 2] << 12) | ((sbyte)b[p + 3] << 20); p += 4; return r; }
        if ((v & 16) == 0) { int r = BitConverter.ToInt32(b, p + 1); p += 5; return r; }
        throw new InvalidDataException($"bad NativeFormat signed at 0x{p:X}");
    }

    /// <summary>MethodDef RID of a method in its module.</summary>
    public static int Rid(System.Reflection.MethodBase method) => method.MetadataToken & 0x00FFFFFF;
}
