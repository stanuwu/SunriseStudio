using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

public enum ScalarEncoding { None, UInt8, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float32 }

public sealed record AssetProperty(string Name, string Value, int Offset = -1,
    ScalarEncoding Encoding = ScalarEncoding.None, bool Editable = false, string Evidence = "")
{
    public CuiPropertyOwner? CuiOwner { get; init; }
}

public sealed record CuiPropertyOwner(int ObjectIndex, int ComponentIndex, int PropertyOffset);

public sealed record AssetNode(string Name, IReadOnlyList<AssetProperty> Properties,
    IReadOnlyList<AssetNode>? Children = null)
{
    /** Children listed until the user asks for all of them; the rest stay unbuilt. */
    public int FoldedRows { get; init; } = int.MaxValue;
}

public sealed record AssetReference(int Offset, uint TagHash, string Name, bool IsCandidate = false);
public sealed record AssetGraphNode(string Id, string Label, int Offset);
public sealed record AssetGraphEdge(string SourceId, string TargetId, string Label);
public sealed record AssetGraph(IReadOnlyList<AssetGraphNode> Nodes, IReadOnlyList<AssetGraphEdge> Edges);

public sealed record AssetInspection(string Kind, string Title, IReadOnlyList<AssetNode> Sections,
    IReadOnlyList<AssetReference> References, IReadOnlyList<string> Diagnostics)
{
    public AssetGraph? Graph { get; init; }
}

public sealed record AssetInspectionContext(uint TagHash, uint ClassHash, ReadOnlyMemory<byte> Data,
    string Name = "", Func<uint, string?>? ResolveName = null,
    Func<uint, bool>? ContainsTag = null, Func<uint, byte[]>? ReadTag = null);

public interface IAssetInspector
{
    string Id { get; }
    string DisplayName { get; }
    bool CanInspect(AssetInspectionContext context);
    AssetInspection Inspect(AssetInspectionContext context);
}

/** Inspectors run in registration order; the binary fallback is always available. */
public sealed class AssetInspectionService
{
    private readonly List<IAssetInspector> inspectors = [];
    public IReadOnlyList<IAssetInspector> Inspectors => inspectors.AsReadOnly();

    public AssetInspectionService(bool includeDefaults = true)
    {
        if (!includeDefaults) return;
        Register(new CuiInspector());
        Register(new BehaviorInspector());
        Register(new MaterialInspector());
    }

    public void Register(IAssetInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        if (inspectors.Any(x => x.Id == inspector.Id))
            throw new ArgumentException("An inspector with this ID is already registered.", nameof(inspector));
        inspectors.Add(inspector);
    }

    /** A malformed typed asset retains its binary view and reports the reader error. */
    public AssetInspection Inspect(AssetInspectionContext context)
    {
        var inspector = inspectors.FirstOrDefault(x => x.CanInspect(context));
        var fallback = BinaryInspector.Inspect(context);
        if (inspector is null) return fallback;
        try
        {
            var result = inspector.Inspect(context);
            return result with
            {
                Sections = [.. result.Sections, .. fallback.Sections],
                References = result.References.Concat(fallback.References)
                    .DistinctBy(x => (x.Offset, x.TagHash)).ToArray()
            };
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException
            or OverflowException or IndexOutOfRangeException)
        {
            return fallback with { Kind = inspector.DisplayName, Diagnostics = [error.Message] };
        }
    }
}

/** Scalar edits preserve every byte outside the selected field. */
public static class AssetEdit
{
    public static int SizeOf(ScalarEncoding encoding) => encoding switch
    {
        ScalarEncoding.UInt8 => 1,
        ScalarEncoding.Int16 or ScalarEncoding.UInt16 => 2,
        ScalarEncoding.Int32 or ScalarEncoding.UInt32 or ScalarEncoding.Float32 => 4,
        ScalarEncoding.Int64 or ScalarEncoding.UInt64 => 8,
        _ => throw new ArgumentException("The field has no scalar encoding.", nameof(encoding))
    };

    /** Read-only fields cannot be edited through the property editor. */
    public static byte[] Apply(ReadOnlySpan<byte> source, AssetProperty property, string value)
    {
        if (!property.Editable) throw new InvalidOperationException("This field is read-only.");
        int size = SizeOf(property.Encoding);
        if (property.Offset < 0 || property.Offset > source.Length - size)
            throw new InvalidDataException("Field extends past the asset.");
        var output = source.ToArray();
        var bytes = output.AsSpan(property.Offset, size);
        var text = value.Trim();
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var number = hex ? text[2..] : text;
        var style = hex ? NumberStyles.HexNumber : NumberStyles.Integer;
        var culture = CultureInfo.InvariantCulture;
        switch (property.Encoding)
        {
            case ScalarEncoding.UInt8: bytes[0] = byte.Parse(number, style, culture); break;
            case ScalarEncoding.Int16:
                BinaryPrimitives.WriteInt16LittleEndian(bytes, short.Parse(number, style, culture)); break;
            case ScalarEncoding.UInt16:
                BinaryPrimitives.WriteUInt16LittleEndian(bytes, ushort.Parse(number, style, culture)); break;
            case ScalarEncoding.Int32:
                BinaryPrimitives.WriteInt32LittleEndian(bytes, int.Parse(number, style, culture)); break;
            case ScalarEncoding.UInt32:
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, uint.Parse(number, style, culture)); break;
            case ScalarEncoding.Int64:
                BinaryPrimitives.WriteInt64LittleEndian(bytes, long.Parse(number, style, culture)); break;
            case ScalarEncoding.UInt64:
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, ulong.Parse(number, style, culture)); break;
            case ScalarEncoding.Float32:
                float scalar = float.Parse(text, NumberStyles.Float, culture);
                if (!float.IsFinite(scalar)) throw new FormatException("Enter a finite number.");
                BinaryPrimitives.WriteSingleLittleEndian(bytes, scalar); break;
        }
        return output;
    }

    /** Raw byte replacement never changes asset length. */
    public static byte[] ReplaceBytes(ReadOnlySpan<byte> source, int offset, ReadOnlySpan<byte> bytes)
    {
        if (offset < 0 || offset > source.Length - bytes.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        var result = source.ToArray();
        bytes.CopyTo(result.AsSpan(offset));
        return result;
    }
}

internal static class BinaryInspector
{
    /** Aligned tag matches are candidates, because scalar values can equal valid tag hashes. */
    public static AssetInspection Inspect(AssetInspectionContext context)
    {
        var data = context.Data.Span;
        List<AssetNode> sections = [];
        List<AssetReference> references = [];
        List<AssetProperty> strings = [];
        for (int offset = 0; offset < data.Length; offset++)
        {
            if (data[offset] is < 32 or > 126) continue;
            int end = offset;
            while (end < data.Length && data[end] is >= 32 and <= 126) end++;
            if (end - offset >= 5 && end < data.Length && data[end] == 0)
                strings.Add(new("Text", Encoding.UTF8.GetString(data[offset..end]), offset));
            offset = end;
            if (strings.Count == 1024) break;
        }
        if (strings.Count != 0) sections.Add(new("Strings", strings));
        if (context.ContainsTag is not null)
            for (int offset = 0; offset <= data.Length - 4 && references.Count < 4096; offset += 4)
            {
                uint tag = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
                if (context.ContainsTag(tag))
                    references.Add(new(offset, tag, context.ResolveName?.Invoke(tag) ?? $"{tag:X8}", true));
            }
        return new("Binary", context.Name.Length == 0 ? $"{context.TagHash:X8}" : context.Name,
            sections, references, []);
    }
}
