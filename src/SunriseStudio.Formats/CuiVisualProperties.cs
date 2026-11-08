using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

public enum CuiVisualPropertyKind { Text, Style, Bitmap, Group, Frame, FontSize, TextCase, Alignment, AutoWidth, Palette, Tint }
public sealed record CuiVisualProperty(string Name, CuiVisualPropertyKind Kind, CuiProperty Property, uint Value);
public sealed record CuiTintProperty(CuiProperty Property, Vector4 Value);
public sealed record CuiOverrideChoice(string Name, CuiVisualPropertyKind Kind, ulong Selector, int PoolDescriptor, uint InitialValue = 0);

/** Visual edits use verified selectors and preserve other widgets' stored values. */
public static class CuiVisualProperties
{
    public static IReadOnlyList<CuiOverrideChoice> Missing(CuiNode node, CuiComponent component)
    {
        if (node.AuxiliaryTypeHash != 0) return [];
        CuiOverrideChoice[] choices = node.TypeHash switch
        {
            (uint)CuiTypeRegistry.TextType => [new("Text", CuiVisualPropertyKind.Text, 0x405, CuiDocument.StringHashes, StringHash.Empty),
                new("Style", CuiVisualPropertyKind.Style, 0x40C, CuiDocument.StringHashes, CuiFonts.DefaultStyle),
                new("Font size", CuiVisualPropertyKind.FontSize, 0x420, CuiDocument.Integers),
                new("Case", CuiVisualPropertyKind.TextCase, 0x40A, CuiDocument.Integers),
                new("Alignment", CuiVisualPropertyKind.Alignment, 0x40E, CuiDocument.Integers),
                new("Auto width", CuiVisualPropertyKind.AutoWidth, 0x41A, CuiDocument.Bools),
                new("Palette color", CuiVisualPropertyKind.Palette, 0x40D, CuiDocument.StringHashes, 0xBD77BB98),
                new("Text tint", CuiVisualPropertyKind.Tint, 0x414, CuiDocument.Vectors)],
            (uint)CuiTypeRegistry.BitmapType => [new("Bitmap", CuiVisualPropertyKind.Bitmap, 0x600, CuiDocument.Handles),
                new("Group", CuiVisualPropertyKind.Group, 0x601, CuiDocument.Integers),
                new("Frame", CuiVisualPropertyKind.Frame, 0x602, CuiDocument.Integers)],
            _ => []
        };
        return choices.Where(choice => !component.Properties.Any(property => property.Direct && property.Selector == choice.Selector)).ToArray();
    }

    /** Explicit overrides create typed storage without changing the constructor or another widget. */
    public static byte[] Add(ReadOnlySpan<byte> bytes, CuiDocument document, CuiNode node, CuiComponent component,
        CuiVisualPropertyKind kind, uint value, Vector4 tint = default)
    {
        if (!document.Nodes.Contains(node) || node.Id != component.Key)
            throw new InvalidDataException("Choose a local widget before adding a property.");
        var choice = Missing(node, component).SingleOrDefault(choice => choice.Kind == kind)
            ?? throw new InvalidDataException("This property already exists or is unsupported for the widget.");
        if (kind is CuiVisualPropertyKind.TextCase or CuiVisualPropertyKind.Alignment && value > 2
            || kind == CuiVisualPropertyKind.AutoWidth && value > 1
            || kind == CuiVisualPropertyKind.FontSize && value > ushort.MaxValue
            || kind is CuiVisualPropertyKind.Group or CuiVisualPropertyKind.Frame && value > int.MaxValue)
            throw new InvalidDataException("The override exceeds its native range.");
        byte[] encoded = kind == CuiVisualPropertyKind.Tint ? MemoryMarshal.AsBytes(new[] { tint }.AsSpan()).ToArray()
            : choice.PoolDescriptor == CuiDocument.Bools ? [checked((byte)value)] : BitConverter.GetBytes(value);
        return CuiPropertyEdits.Upsert(bytes, document, component, [new(choice.Selector, choice.PoolDescriptor, encoded)]);
    }
    /**
     * Missing selectors remain constructor defaults, without editable placeholder fields.
     * @param document Owning resource bytes.
     * @param node Verified widget type.
     * @param component Authored property owner.
     * @return Supported stored visual properties.
     */
    public static IReadOnlyList<CuiVisualProperty> Read(CuiDocument document, CuiNode node, CuiComponent component)
    {
        List<CuiVisualProperty> result = [];
        if (node.AuxiliaryTypeHash != 0) return result;
        if (node.TypeHash == CuiTypeRegistry.TextType)
        {
            Add(0x405, "Text", CuiVisualPropertyKind.Text, C80804692.Class);
            Add(0x40C, "Style", CuiVisualPropertyKind.Style, C80804692.Class);
            Add(0x420, "Font size", CuiVisualPropertyKind.FontSize, CuiIntegerValue.Class, CuiShortValue.Class);
            Add(0x40A, "Case", CuiVisualPropertyKind.TextCase, CuiIntegerValue.Class);
            Add(0x40E, "Alignment", CuiVisualPropertyKind.Alignment, CuiIntegerValue.Class);
            Add(0x41A, "Auto width", CuiVisualPropertyKind.AutoWidth, SCuiBoolValue.Class);
            Add(0x40D, "Palette color", CuiVisualPropertyKind.Palette, C80804692.Class);
        }
        else if (node.TypeHash == CuiTypeRegistry.BitmapType)
        {
            Add(0x600, "Bitmap", CuiVisualPropertyKind.Bitmap, C80804690.Class);
            Add(0x601, "Group", CuiVisualPropertyKind.Group, CuiIntegerValue.Class);
            Add(0x602, "Frame", CuiVisualPropertyKind.Frame, CuiIntegerValue.Class);
        }
        return result;

        void Add(ulong selector, string name, CuiVisualPropertyKind kind, params uint[] classes)
        {
            var property = component.Properties.LastOrDefault(property => property.Direct && property.Selector == selector
                && classes.Contains(property.ValueClass));
            if (property is not null) result.Add(new(name, kind, property, ReadUInt(document, property)));
        }
    }

    public static uint ReadUInt(CuiDocument document, CuiProperty property) => property.ValueClass switch
    {
        SCuiBoolValue.Class => document.Data.Span[property.ValueOffset],
        CuiShortValue.Class => BinaryPrimitives.ReadUInt16LittleEndian(document.Data.Span[property.ValueOffset..]),
        _ => BinaryPrimitives.ReadUInt32LittleEndian(document.Data.Span[property.ValueOffset..])
    };

    public static CuiTintProperty? ReadTint(CuiDocument document, CuiNode node, CuiComponent component)
    {
        if (node.TypeHash != CuiTypeRegistry.TextType || node.AuxiliaryTypeHash != 0) return null;
        var property = component.Properties.LastOrDefault(property => property.Direct && property.Selector == 0x414
            && property.ValueClass == C80804693.Class);
        return property is null ? null : new(property, MemoryMarshal.Read<Vector4>(document.Data.Span[property.ValueOffset..]));
    }

    /**
     * Visual changes retain the original property's storage and native numeric range.
     * @param bytes Current resource bytes.
     * @param document Parsed source identity.
     * @param node Selected widget type.
     * @param component Selected property owner.
     * @param values Desired values for stored selectors only.
     * @param tint Optional stored RGBA tint override.
     * @return A resource suitable for the project's delta writer.
     */
    public static byte[] Apply(ReadOnlySpan<byte> bytes, CuiDocument document, CuiNode node, CuiComponent component,
        IReadOnlyDictionary<CuiVisualPropertyKind, uint> values, Vector4? tint = null)
    {
        var fields = Read(document, node, component);
        Dictionary<CuiProperty, ReadOnlyMemory<byte>> changes = [];
        foreach (var (kind, value) in values)
        {
            var field = fields.SingleOrDefault(field => field.Kind == kind)
                ?? throw new InvalidDataException("This property uses a constructor default and has no stored override.");
            if (kind is CuiVisualPropertyKind.Group or CuiVisualPropertyKind.Frame && value > int.MaxValue
                || kind == CuiVisualPropertyKind.FontSize && value > ushort.MaxValue
                || kind is CuiVisualPropertyKind.TextCase or CuiVisualPropertyKind.Alignment && value > 2
                || kind == CuiVisualPropertyKind.AutoWidth && value > 1)
                throw new InvalidDataException("The visual property exceeds its native range.");
            byte[] encoded = field.Property.ValueClass switch
            {
                CuiShortValue.Class => BitConverter.GetBytes(checked((ushort)value)), SCuiBoolValue.Class => [checked((byte)value)],
                _ => BitConverter.GetBytes(value)
            };
            changes.Add(field.Property, encoded);
        }
        if (tint is { } color)
        {
            var field = ReadTint(document, node, component)
                ?? throw new InvalidDataException("Text tint has no stored override.");
            changes.Add(field.Property, MemoryMarshal.AsBytes(new[] { color }.AsSpan()).ToArray());
        }
        return CuiPropertyEdits.Apply(bytes, document, changes);
    }
}
