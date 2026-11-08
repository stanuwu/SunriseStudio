using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public sealed record CuiWidgetContent(uint TextHash = 0, string Text = "", uint BitmapTag = 0,
    uint TextureTag = 0, string Error = "", int Group = 0, int Frame = 0, uint StyleHash = 0, ushort FontSize = 0)
{
    public IReadOnlyList<IReadOnlyList<uint>> BitmapFrames { get; init; } = [];
    public CuiTextAppearance? NativeText { get; init; }
}

/** Package reads and bitmap decoding finish before the UI uploads or borrows texture views. */
public sealed partial class CuiPreviewData
{
    public CuiDocument? Document { get; private init; }
    public CuiComposition? Composition { get; private init; }
    public IReadOnlyList<CuiLayoutRectangle> Layouts { get; private init; } = [];
    public IReadOnlyDictionary<int, CuiWidgetContent> LocalContent { get; private init; } = new Dictionary<int, CuiWidgetContent>();
    public IReadOnlyDictionary<int, CuiWidgetContent> ComposedContent { get; private init; } = new Dictionary<int, CuiWidgetContent>();
    public IReadOnlyDictionary<uint, TextureData> Textures { get; private init; } = new Dictionary<uint, TextureData>();
    public IReadOnlyList<string> Diagnostics { get; private init; } = [];

    public static CuiPreviewData? Read(PackageCatalog catalog, uint tag, uint classHash, ReadOnlyMemory<byte> bytes,
        Func<uint, string?>? resolveHash = null, CancellationToken cancellationToken = default) =>
        Read(catalog, tag, classHash, bytes, resolveHash, cancellationToken, null);

    public static CuiPreviewData? Read(PackageCatalog catalog, uint tag, uint classHash, ReadOnlyMemory<byte> bytes,
        Func<uint, string?>? resolveHash, CancellationToken cancellationToken, Func<uint, byte[]>? readConfigured)
    {
        if (!CuiDocument.Supports(classHash)) return null;
        List<string> diagnostics = [];
        Dictionary<uint, TextureData> textures = [];
        Dictionary<int, CuiWidgetContent> local = [], composed = [];
        Dictionary<uint, IReadOnlyList<IReadOnlyList<uint>>> bitmapFrames = [];
        CuiFonts? fonts = null;
        string? fontSetupError = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var types = CuiTypeRegistry.BuiltIn;
            var document = CuiDocument.Read(tag, classHash, bytes, resolveHash, types);
            var layouts = CuiLayout.Read(document);
            CuiComposition? composition = null;
            if (classHash == SCuiHierarchy.Class)
            {
                composition = CuiComposition.Read(document, child =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!catalog.TryGetEntry(child, out var entry)) throw new InvalidDataException("CUI resource is missing.");
                    return CuiDocument.Read(child, entry.Reference, readConfigured?.Invoke(child) ?? catalog.Read(child), resolveHash, types);
                });
                foreach (var node in composition.Nodes)
                    composed[node.Id] = Content(node.Definition, node.Properties);
            }
            else
            {
                var nodes = document.Nodes.GroupBy(node => node.Id).ToDictionary(group => (ulong)group.Key, group => group.First());
                foreach (var component in document.Components)
                    if (nodes.TryGetValue(component.Key, out var node))
                        local[component.Offset] = Content(node, component.Properties.Select(property => new CuiAuthoredProperty(document, property)));
            }
            return new() { Document = document, Composition = composition, Layouts = layouts,
                LocalContent = local, ComposedContent = composed, Textures = textures, Diagnostics = diagnostics };
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException or ArgumentOutOfRangeException)
        { return new() { Diagnostics = [error.Message] }; }

        CuiWidgetContent Content(CuiNode node, IEnumerable<CuiAuthoredProperty> properties)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = properties.Where(property => property.Property.Direct).ToArray();
            if (node.TypeHash == 0x0CE30D0B && node.AuxiliaryTypeHash == 0)
            {
                var property = rows.LastOrDefault(property => property.Property.Selector == 0x405
                    && property.Property.ValueClass == C80804692.Class);
                uint hash = property is null ? 0 : CuiVisualProperties.ReadUInt(property.Source, property.Property);
                var style = rows.LastOrDefault(property => property.Property.Selector == 0x40C
                    && property.Property.ValueClass == C80804692.Class);
                var size = rows.LastOrDefault(property => property.Property.Selector == 0x420
                    && property.Property.ValueClass is CuiShortValue.Class or CuiIntegerValue.Class);
                uint fontSize = size is null ? 0 : CuiVisualProperties.ReadUInt(size.Source, size.Property);
                var content = new CuiWidgetContent(hash, resolveHash?.Invoke(hash) ?? "", StyleHash: style is null ? CuiFonts.DefaultStyle
                    : CuiVisualProperties.ReadUInt(style.Source, style.Property), FontSize: (ushort)fontSize);
                try
                {
                    if (fontSetupError is not null) return content with { Error = fontSetupError };
                    if (fonts is null)
                        try { fonts = new(catalog, cancellationToken); }
                        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException or ArgumentException)
                        { fontSetupError = error.Message; throw; }
                    var appearance = fonts.Resolve(content.StyleHash, content.FontSize, content.Text, rows);
                    return content with { NativeText = appearance, Error = appearance.Error };
                }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException or OverflowException or ArgumentException)
                { return content with { Error = error.Message }; }
            }
            if (node.TypeHash != 0x7D1BD221 || node.AuxiliaryTypeHash != 0) return new();
            var bitmap = rows.LastOrDefault(property => property.Property.Selector == 0x600
                && property.Property.ValueClass == C80804690.Class);
            if (bitmap is null) return new();
            uint bitmapTag = BinaryPrimitives.ReadUInt32LittleEndian(bitmap.Source.Data.Span[bitmap.Property.ValueOffset..]);
            int group = 0, frame = 0;
            IReadOnlyList<IReadOnlyList<uint>> groups = [];
            try
            {
                if (!catalog.TryGetEntry(bitmapTag, out var entry) || entry.Reference != SIconBitmap.Class)
                    throw new NotSupportedException("Bitmap source type is unsupported.");
                if (!bitmapFrames.TryGetValue(bitmapTag, out var known))
                { known = CuiBitmap.ReadGroups(catalog.Read(bitmapTag)); bitmapFrames.Add(bitmapTag, known); }
                groups = known;
                group = Integer(0x601); frame = Integer(0x602);
                if ((uint)group >= groups.Count || (uint)frame >= groups[group].Count)
                    throw new InvalidDataException("The stored bitmap group or frame is outside its source.");
                uint textureTag = groups[group][frame];
                if (!textures.ContainsKey(textureTag)) textures.Add(textureTag, new TextureDataReader(catalog).Read(textureTag));
                return new(BitmapTag: bitmapTag, TextureTag: textureTag, Group: group, Frame: frame) { BitmapFrames = groups };
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException)
            { return new(BitmapTag: bitmapTag, Error: error.Message, Group: group, Frame: frame) { BitmapFrames = groups }; }

            int Integer(ulong selector)
            {
                var row = rows.LastOrDefault(property => property.Property.Selector == selector);
                if (row is null) return 0;
                if (row.Property.ValueClass != CuiIntegerValue.Class)
                    throw new NotSupportedException("The bitmap index storage is unsupported.");
                return unchecked((int)CuiVisualProperties.ReadUInt(row.Source, row.Property));
            }
        }
    }
}

/** Bitmap groups contain frame arrays; the selected frame stores the native texture tag. */
public static class CuiBitmap
{
    public static uint ReadTexture(ReadOnlyMemory<byte> bytes, int group = 0, int frame = 0)
    {
        var groups = ReadGroups(bytes);
        if ((uint)group >= groups.Count) throw new InvalidDataException("Bitmap group is outside the source.");
        if ((uint)frame >= groups[group].Count) throw new InvalidDataException("Bitmap frame is outside the source.");
        return groups[group][frame];
    }

    public static IReadOnlyList<IReadOnlyList<uint>> ReadGroups(ReadOnlyMemory<byte> bytes)
    {
        var tag = bytes.Span;
        if (!TagView.Fits<SIconBitmap>(tag, 0)) throw new InvalidDataException("Truncated CUI bitmap.");
        ref readonly var bitmap = ref TagView.At<SIconBitmap>(tag, 0);
        int descriptor = TagPointer.Checked(tag, TagView.OffsetOf(tag, in bitmap.Data));
        // Two class sets share one layout: each frame is a texture header tag.
        uint? kind = descriptor < 4 ? null : TagView.TargetClass(tag, in bitmap.Data);
        if (kind is not (C80804a67.Class or C80804a65.Class)) throw new InvalidDataException("Invalid CUI bitmap group descriptor.");
        List<IReadOnlyList<uint>> result = [];
        int groupStride = Unsafe.SizeOf<C80804a6c>(), frameStride = Unsafe.SizeOf<CuiBitmapFrame>();
        if (kind == C80804a67.Class)
        {
            var groups = TagArray.Checked(tag, descriptor + TagView.FieldOffset<C80804a67>("Sequences"),
                C80804a6c.Class, groupStride);
            for (int group = 0; group < groups.Count; group++)
            {
                ref readonly var sequence = ref TagView.At<C80804a6c>(tag, groups.Data + group * groupStride);
                var frames = TagArray.Checked(tag, TagView.OffsetOf(tag, in sequence.Frames), CuiBitmapFrame.Class, frameStride);
                var textures = new uint[frames.Count];
                for (int frame = 0; frame < textures.Length; frame++)
                    textures[frame] = TagView.At<CuiBitmapFrame>(tag, frames.Data + frame * frameStride).Texture;
                result.Add(textures);
            }
        }
        else
        {
            int altGroupStride = Unsafe.SizeOf<C80804a6a>(), altFrameStride = Unsafe.SizeOf<CuiBitmapFrameAlt>();
            var groups = TagArray.Checked(tag, descriptor + TagView.FieldOffset<C80804a65>("Sequences"),
                C80804a6a.Class, altGroupStride);
            for (int group = 0; group < groups.Count; group++)
            {
                ref readonly var sequence = ref TagView.At<C80804a6a>(tag, groups.Data + group * altGroupStride);
                var frames = TagArray.Checked(tag, TagView.OffsetOf(tag, in sequence.Frames), CuiBitmapFrameAlt.Class, altFrameStride);
                var textures = new uint[frames.Count];
                for (int frame = 0; frame < textures.Length; frame++)
                    textures[frame] = TagView.At<CuiBitmapFrameAlt>(tag, frames.Data + frame * altFrameStride).Texture;
                result.Add(textures);
            }
        }
        return result;
    }
}
