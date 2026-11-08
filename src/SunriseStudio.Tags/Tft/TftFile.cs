using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;

namespace SunriseStudio.Tags.Tft;

/** How a symbolic reference is stored in the tag bytes. */
public enum TftReferenceKind : byte
{
    /** A u32 tag hash; the placeholder is 0xFFFFFFFF. */
    TagRef = 1,
    /** A u64 wide hash; the placeholder is 0. */
    WideRef = 2,
}

/**
 * One symbolic reference: a placeholder in the data at Offset and the tag it names. Target is a
 * project tag name, `#0x` and eight hex digits for an installed tag, or `##0x` and sixteen for a wide hash.
 */
public readonly record struct TftReference(int Offset, TftReferenceKind Kind, byte Flags, string Target)
{
    public bool IsProjectTag => !Target.StartsWith('#');
}

/** One direct dependency, as the `want` chunk lists it. */
public readonly record struct TftDependency(TftReferenceKind Kind, string Target)
{
    public bool IsProjectTag => !Target.StartsWith('#');
}

/**
 * A loose tag file (`.tft`, format version 1): one tag's bytes in the game's runtime layout, its
 * symbolic references, and the registry subset that decodes it. Every file this class reads
 * writes back to the same bytes; anything else is refused.
 */
public sealed class TftFile
{
    public const int Version = 1;
    public const int HeaderSize = 64;
    public const uint TagRefPlaceholder = 0xFFFFFFFF;
    private const int ChunkHeaderSize = 16, ChunkVersion = 1, Alignment = 8;
    private const ushort LayoutFlag = 1, ReferencesFlag = 2;
    private static readonly byte[] Magic = "STFT"u8.ToArray();

    /** Project-local name: the path under `content/` without `.tft`, lowercase, `/` separated. */
    public required string Name { get; init; }
    public required string TagType { get; init; }
    /** The name the game finds the tag by, such as `map:testbed:root`; null keeps the project name in the package. */
    public string? GameName { get; init; }
    public required uint ClassId { get; init; }
    /** The package entry's type word. It differs between tags of one class, so the file carries it. */
    public required uint TypeInfo { get; init; }
    /** SHA-256 of the registry build id the layout came from. */
    public required byte[] BuildHash { get; init; }
    public TftLayout? Layout { get; init; }
    /** Tag bytes with a placeholder at every reference. */
    public required byte[] Data { get; init; }
    public IReadOnlyList<TftReference> References { get; init; } = [];
    /** Provenance JSON (importer, sources); stored as given. */
    public string? Info { get; init; }

    /** The installed tag a clone was made from, read from `info`; null for a tag made from scratch. */
    public uint? SourceTag
    {
        get
        {
            if (Info is null) return null;
            try
            {
                return JsonNode.Parse(Info)?["source_tag"] is JsonValue value && value.TryGetValue(out string? text) && text.StartsWith("0x", StringComparison.Ordinal)
                    && uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint tag) ? tag : null;
            }
            catch (JsonException) { return null; }
        }
    }

    /** The `info` of a clone of an installed tag. */
    public static string CloneInfo(uint sourceTag) => $"{{\"source_tag\":\"0x{sourceTag:X8}\"}}";
    /** Editor state; the compiler ignores it. */
    public byte[]? Meta { get; init; }

    /** The `want` chunk: each distinct target once, sorted. */
    public IReadOnlyList<TftDependency> Dependencies => References.Select(x => new TftDependency(x.Kind, x.Target)).Distinct()
        .OrderBy(x => x.Target, StringComparer.Ordinal).ThenBy(x => x.Kind).ToList();

    public static byte[] HashBuildId(string buildId) => SHA256.HashData(Encoding.UTF8.GetBytes(buildId));

    /** @return The file bytes. Malformed names, targets, references or placeholders throw. */
    public byte[] Write()
    {
        Validate();
        var references = References.OrderBy(x => x.Offset).ToList();
        if (references.Count > 0 && Layout is null) throw new InvalidDataException("References need the layout chunk; its string table holds their targets.");
        var chunks = new List<(string Id, byte[] Payload)>();
        using (var name = new MemoryStream())
        {
            foreach (string text in new[] { Name, TagType })
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                TftBytes.U16(name, bytes.Length);
                name.Write(bytes);
            }
            chunks.Add(("name", name.ToArray()));
        }
        if (GameName is not null) chunks.Add(("gnam", Encoding.ASCII.GetBytes(GameName)));
        var strings = new TftStrings();
        if (Layout is not null)
        {
            using var layout = new MemoryStream();
            Layout.Write(layout, strings, references.Select(x => x.Target));
            chunks.Add(("layt", layout.ToArray()));
        }
        chunks.Add(("data", Data));
        if (references.Count > 0)
        {
            using var rows = new MemoryStream();
            TftBytes.U32(rows, (uint)references.Count);
            TftBytes.U32(rows, 0);
            foreach (var reference in references)
            {
                TftBytes.U32(rows, (uint)reference.Offset);
                rows.WriteByte((byte)reference.Kind);
                rows.WriteByte(reference.Flags);
                TftBytes.U16(rows, 0);
                TftBytes.U32(rows, (uint)strings[reference.Target]);
            }
            chunks.Add(("refs", rows.ToArray()));
            using var want = new MemoryStream();
            var dependencies = Dependencies;
            TftBytes.U32(want, (uint)dependencies.Count);
            TftBytes.U32(want, 0);
            foreach (var dependency in dependencies)
            {
                want.WriteByte((byte)dependency.Kind);
                want.WriteByte(dependency.IsProjectTag ? (byte)1 : (byte)0);
                TftBytes.U16(want, 0);
                TftBytes.U32(want, (uint)strings[dependency.Target]);
            }
            chunks.Add(("want", want.ToArray()));
        }
        if (Info is not null) chunks.Add(("info", Encoding.UTF8.GetBytes(Info)));
        if (Meta is not null) chunks.Add(("meta", Meta));

        using var body = new MemoryStream();
        foreach (var (id, payload) in chunks)
        {
            body.Write(Encoding.ASCII.GetBytes(id));
            TftBytes.U32(body, ChunkVersion);
            TftBytes.U64(body, (ulong)payload.Length);
            body.Write(payload);
            TftBytes.Pad(body, Alignment);
        }
        byte[] output = new byte[HeaderSize + body.Length];
        Magic.CopyTo(output, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(0x04), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(0x06),
            (ushort)((Layout is null ? 0 : LayoutFlag) | (references.Count == 0 ? 0 : ReferencesFlag)));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x08), ClassId);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x0C), (uint)Data.Length);
        BuildHash.CopyTo(output, 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x30), (uint)chunks.Count);
        body.ToArray().CopyTo(output, HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x34), Crc32.Compute(output.AsSpan(HeaderSize)));
        // The spec reserves 0x38..0x40; the low word holds the type word.
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x38), TypeInfo);
        return output;
    }

    private void Validate()
    {
        TftNames.Check(Name);
        if (GameName is not null) TftNames.CheckGameName(GameName);
        if (!TftNames.IsTagType(TagType) || TftNames.TagTypeOf(Name) != TagType)
            throw new InvalidDataException($"\"{Name}\" must end with its tag type \".{TagType}\".");
        if (BuildHash.Length != 32) throw new InvalidDataException("The build hash is not 32 bytes.");
        var seen = new HashSet<int>();
        foreach (var reference in References)
        {
            TftNames.CheckTarget(reference.Target, reference.Kind);
            int width = reference.Kind == TftReferenceKind.WideRef ? 8 : 4;
            if (reference.Offset < 0 || reference.Offset > Data.Length - width || !seen.Add(reference.Offset))
                throw new InvalidDataException($"Reference at {reference.Offset} is outside the data or repeats an offset.");
            bool placeholder = reference.Kind == TftReferenceKind.WideRef
                ? BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan(reference.Offset)) == 0
                : BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(reference.Offset)) == TagRefPlaceholder;
            if (!placeholder) throw new InvalidDataException($"Reference at {reference.Offset} has no placeholder in the data.");
        }
    }

    /** Reads a file; a bad checksum, an unknown chunk, or any form this class would not write throws. */
    public static TftFile Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || !bytes[..4].SequenceEqual(Magic)) throw new InvalidDataException("Not a .tft file.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[0x04..]) != Version) throw new InvalidDataException("Unsupported .tft version.");
        if (Crc32.Compute(bytes[HeaderSize..]) != BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x34..]))
            throw new InvalidDataException("The .tft checksum does not match; the file is damaged.");
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes[0x06..]);
        uint classId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x08..]);
        int chunkCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x30..]);
        byte[] buildHash = bytes.Slice(0x10, 32).ToArray();
        uint typeInfo = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x38..]);

        var chunks = new List<(string Id, byte[] Payload)>();
        int at = HeaderSize;
        for (int index = 0; index < chunkCount; index++)
        {
            if (at + ChunkHeaderSize > bytes.Length) throw new InvalidDataException("A .tft chunk runs past the file.");
            string id = Encoding.ASCII.GetString(bytes.Slice(at, 4));
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(at + 8)..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 4)..]) != ChunkVersion || size > (ulong)(bytes.Length - at - ChunkHeaderSize))
                throw new InvalidDataException($"The .tft chunk {id} has a bad header.");
            chunks.Add((id, bytes.Slice(at + ChunkHeaderSize, (int)size).ToArray()));
            at += ChunkHeaderSize + (int)size;
            at = (at + Alignment - 1) / Alignment * Alignment;
        }
        if (at != bytes.Length) throw new InvalidDataException("The .tft file has bytes after its last chunk.");
        string[] order = ["name", "gnam", "layt", "data", "refs", "want", "info", "meta"];
        if (chunks.Any(x => Array.IndexOf(order, x.Id) < 0)) throw new InvalidDataException("The .tft file has an unknown chunk.");
        if (!chunks.Select(x => Array.IndexOf(order, x.Id)).SequenceEqual(chunks.Select(x => Array.IndexOf(order, x.Id)).Distinct().Order()))
            throw new InvalidDataException("The .tft chunks are out of order or repeat.");
        byte[]? Chunk(string id) => chunks.FirstOrDefault(x => x.Id == id).Payload;

        byte[] name = Chunk("name") ?? throw new InvalidDataException("The .tft file has no name chunk.");
        byte[] data = Chunk("data") ?? throw new InvalidDataException("The .tft file has no data chunk.");
        int cursor = 0;
        string NameText()
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(name.AsSpan(cursor));
            string text = new UTF8Encoding(false, true).GetString(name, cursor + 2, length);
            cursor += 2 + length;
            return text;
        }
        string tagName = NameText(), tagType = NameText();
        byte[] strings = [];
        var layout = Chunk("layt") is { } layt ? TftLayout.Read(layt, "", out strings) : null;
        var references = new List<TftReference>();
        if (Chunk("refs") is { } refs)
        {
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(refs);
            if (refs.Length != 8 + count * 12) throw new InvalidDataException("The refs chunk length disagrees with its count.");
            for (int row = 0; row < count; row++)
            {
                var span = refs.AsSpan(8 + row * 12);
                references.Add(new((int)BinaryPrimitives.ReadUInt32LittleEndian(span), (TftReferenceKind)span[4], span[5],
                    TftStrings.At(strings, BinaryPrimitives.ReadUInt32LittleEndian(span[8..]))));
            }
        }
        var file = new TftFile
        {
            Name = tagName, TagType = tagType, GameName = Chunk("gnam") is { } game ? Encoding.ASCII.GetString(game) : null,
            ClassId = classId, TypeInfo = typeInfo, BuildHash = buildHash, Layout = layout, Data = data, References = references, Info = Chunk("info") is { } info ? new UTF8Encoding(false, true).GetString(info) : null, Meta = Chunk("meta"),
        };
        // One canonical form per tag: the want chunk, flags, padding and string order all follow from the rest.
        if (!file.Write().AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("The .tft file is not in the form Studio writes.");
        return file;
    }
}

/** Tag names and reference targets. */
public static class TftNames
{
    public const string FileExtension = ".tft";

    /** A name is lowercase ASCII segments of [a-z0-9_.-], `/` separated; `.`, `..`, `#` and `0x` starts are refused. */
    public static void Check(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > ushort.MaxValue || name.StartsWith("0x", StringComparison.Ordinal)
            || name.Split('/').Any(segment => segment is "" or "." or ".." || segment.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-'))))
            throw new InvalidDataException($"\"{name}\" is not a tag name: lowercase ASCII segments of a-z, 0-9, _, . and - separated by /.");
    }

    /** @return The tag name of a file under `contentDirectory`: its relative path without `.tft`, lowercase. */
    public static string FromPath(string contentDirectory, string path)
    {
        string relative = Path.GetRelativePath(contentDirectory, path).Replace('\\', '/');
        if (!relative.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase) || relative.StartsWith("../", StringComparison.Ordinal))
            throw new InvalidDataException($"{path} is not a .tft file under {contentDirectory}.");
        string name = relative[..^FileExtension.Length].ToLowerInvariant();
        Check(name);
        return name;
    }

    /** The tag type a name ends with: `littleroot.scenario` has type `scenario`. */
    public static string TagTypeOf(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > name.LastIndexOf('/') && dot < name.Length - 1 ? name[(dot + 1)..] : "";
    }

    /** Game names are printable ASCII with no space; the client builds a looked-up name in a 64-byte buffer. */
    public const int GameNameLimit = 63;

    public static void CheckGameName(string name)
    {
        if (name.Length is 0 or > GameNameLimit || name.Any(c => c is < '!' or > '~'))
            throw new InvalidDataException($"\"{name}\" is not a game name: 1 to {GameNameLimit} printable ASCII characters, no spaces.");
    }

    public static bool IsTagType(string type) => type.Length is > 0 and <= 64 && type.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    public static string Installed(uint tag) => "#0x" + tag.ToString("X8", CultureInfo.InvariantCulture);
    public static string InstalledWide(ulong hash) => "##0x" + hash.ToString("X16", CultureInfo.InvariantCulture);

    /** A target is a tag name, `#0x` and eight uppercase hex digits, or `##0x` and sixteen for a wide reference. */
    public static void CheckTarget(string target, TftReferenceKind kind)
    {
        if (target.StartsWith("##", StringComparison.Ordinal))
        {
            if (kind != TftReferenceKind.WideRef || ParseHex(target, 4, 16) is null)
                throw new InvalidDataException($"\"{target}\" is not a wide installed reference.");
        }
        else if (target.StartsWith('#'))
        {
            if (kind != TftReferenceKind.TagRef || ParseHex(target, 3, 8) is null)
                throw new InvalidDataException($"\"{target}\" is not an installed tag reference.");
        }
        else Check(target);
    }

    /** @return The installed hash a `#0x` or `##0x` target names. */
    public static ulong InstalledHash(string target) => target.StartsWith("##", StringComparison.Ordinal)
        ? ParseHex(target, 4, 16) ?? throw new InvalidDataException($"\"{target}\" is malformed.")
        : ParseHex(target, 3, 8) ?? throw new InvalidDataException($"\"{target}\" is malformed.");

    private static ulong? ParseHex(string text, int start, int digits)
    {
        if (text.Length != start + digits || !text.AsSpan(start - 2, 2).SequenceEqual("0x")
            || text.Skip(start).Any(c => !(c is >= '0' and <= '9' or >= 'A' and <= 'F'))) return null;
        return ulong.Parse(text.AsSpan(start), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }
}
