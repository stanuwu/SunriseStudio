using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public sealed record CuiPropertyOverride(ulong Selector, int PoolDescriptor, ReadOnlyMemory<byte> Value);

public static partial class CuiPropertyEdits
{
    /** Added overrides join the same typed pools and selector paths as native stored properties. */
    public static byte[] Upsert(ReadOnlySpan<byte> bytes, CuiDocument document, CuiComponent component,
        IReadOnlyList<CuiPropertyOverride> values)
    {
        if (document.ClassHash != CuiResource.Class || document.SourceSha256 != Convert.ToHexString(SHA256.HashData(bytes)))
            throw new InvalidDataException("The CUI resource changed. Reload it before editing.");
        var owner = document.Objects.SingleOrDefault(row => row.Components.Contains(component))
            ?? throw new InvalidDataException("The component does not belong to this resource.");
        if (values.Select(value => value.Selector).Distinct().Count() != values.Count)
            throw new InvalidDataException("A property override was supplied twice.");
        Dictionary<CuiProperty, ReadOnlyMemory<byte>> replacements = [];
        List<CuiPropertyOverride> added = [];
        foreach (var value in values)
        {
            var schema = CuiDocument.PoolSchema(value.PoolDescriptor);
            if (value.Value.Length != schema.Stride || schema.Class is not (SCuiBoolValue.Class or CuiShortValue.Class or SCuiFloatValue.Class
                or C80804693.Class or CuiIntegerValue.Class or C80804692.Class or C80804690.Class or C8080468f.Class))
                throw new InvalidDataException("The property has unsupported typed storage.");
            if (schema.Class is SCuiFloatValue.Class or C80804693.Class)
                for (int at = 0; at < value.Value.Length; at += 4)
                    if (!float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(value.Value.Span[at..])))
                        throw new InvalidDataException("Enter finite property values.");
            var current = component.Properties.LastOrDefault(property => property.Direct && property.Selector == value.Selector);
            if (current is null) added.Add(value);
            else
            {
                if (current.PoolDescriptor != value.PoolDescriptor)
                    throw new InvalidDataException("Keep the stored property's native pool and units.");
                if (!bytes.Slice(current.ValueOffset, value.Value.Length).SequenceEqual(value.Value.Span))
                    replacements.Add(current, value.Value);
            }
        }
        if (added.Count == 0 && replacements.Count == 0) return bytes.ToArray();
        var offsets = component.Properties.Select(property => property.RecordOffset).ToHashSet();
        if (document.Components.Any(other => !ReferenceEquals(other, component)
            && (other.Offset == component.Offset || other.Properties.Any(property => offsets.Contains(property.RecordOffset)))))
        {
            byte[] detached = bytes.ToArray();
            int rowStride = Unsafe.SizeOf<CuiComponentRow>();
            int ownerField = TagView.OffsetOf(detached, in TagView.At<CuiObjectRow>(detached, owner.Offset).Components);
            var oldComponents = TagArray.Checked(detached, ownerField, CuiComponentRow.Class, rowStride);
            int index = (component.Offset - oldComponents.Data) / rowStride;
            int newComponents = CloneArray(ref detached, ownerField, CuiComponentRow.Class, rowStride, [ComponentPointer]);
            int newComponent = newComponents + index * rowStride;
            _ = CloneArray(ref detached, TagView.OffsetOf(detached, in TagView.At<CuiComponentRow>(detached, newComponent).Properties),
                CuiPropertyRow.Class, Unsafe.SizeOf<CuiPropertyRow>(), PropertyPointers);
            BinaryPrimitives.WriteInt64LittleEndian(detached.AsSpan(SizeWord), detached.Length);
            var copy = CuiDocument.Read(document.TagHash, document.ClassHash, detached, nativeTypes: document.NativeTypes);
            return Upsert(detached, copy, copy.Objects[owner.Index].Components[index], values);
        }
        byte[] output = Apply(bytes, document, replacements);
        if (added.Count == 0) return output;
        var prepared = CuiDocument.Read(document.TagHash, document.ClassHash, output, nativeTypes: document.NativeTypes);
        Dictionary<ulong, int> addresses = [];
        foreach (var group in added.GroupBy(value => value.PoolDescriptor))
        {
            var (type, stride) = CuiDocument.PoolSchema(group.Key);
            var old = TagArray.Checked(output, group.Key, type, stride);
            int count = checked(old.Count + group.Count());
            if (count > 1_048_576) throw new InvalidDataException("CUI value pool exceeds its supported size.");
            int start = AppendArray(group.Key, count, stride, type);
            if (old.Count > 0) output.AsSpan(old.Data, old.Count * stride).CopyTo(output.AsSpan(start));
            foreach (var property in prepared.Properties.Where(property => property.PoolDescriptor == group.Key))
                TagView.SetTarget(output, in TagView.At<CuiPropertyRow>(output, property.RecordOffset).Value,
                    start + property.ValueOffset - old.Data);
            int index = old.Count;
            foreach (var value in group)
            {
                int at = start + index++ * stride;
                value.Value.Span.CopyTo(output.AsSpan(at)); addresses.Add(value.Selector, at);
            }
        }
        int empty = EmptySlot();
        int targetComponent = component.Offset;
        int componentStride = Unsafe.SizeOf<CuiComponentRow>(), propertyStride = Unsafe.SizeOf<CuiPropertyRow>();
        if (document.Components.Count(row => row.Offset == component.Offset) > 1)
        {
            int componentsField = TagView.OffsetOf(output, in TagView.At<CuiObjectRow>(output, owner.Offset).Components);
            var components = TagArray.Checked(output, componentsField, CuiComponentRow.Class, componentStride);
            int start = AppendArray(componentsField, components.Count, componentStride, CuiComponentRow.Class);
            output.AsSpan(components.Data, components.Count * componentStride).CopyTo(output.AsSpan(start));
            for (int i = 0; i < components.Count; i++)
            {
                int source = components.Data + i * componentStride, target = start + i * componentStride;
                Rebase(source + ComponentPointer, target + ComponentPointer);
                if (source == component.Offset) targetComponent = target;
            }
        }
        int rowsField = TagView.OffsetOf(output, in TagView.At<CuiComponentRow>(output, targetComponent).Properties);
        var oldRows = TagArray.Checked(output, rowsField, CuiPropertyRow.Class, propertyStride);
        int propertyCount = checked(oldRows.Count + added.Count);
        if (propertyCount > 65536) throw new InvalidDataException("CUI component has too many properties.");
        int properties = AppendArray(rowsField, propertyCount, propertyStride, CuiPropertyRow.Class);
        if (oldRows.Count > 0) output.AsSpan(oldRows.Data, oldRows.Count * propertyStride).CopyTo(output.AsSpan(properties));
        for (int i = 0; i < oldRows.Count; i++)
        {
            int source = oldRows.Data + i * propertyStride, target = properties + i * propertyStride;
            foreach (int pointer in PropertyPointers) Rebase(source + pointer, target + pointer);
        }
        for (int i = 0; i < added.Count; i++)
        {
            var value = added[i];
            ref var row = ref TagView.Edit<CuiPropertyRow>(output, properties + (oldRows.Count + i) * propertyStride);
            TagView.SetTarget(output, in row.PathSlot, empty);
            row.Selector = value.Selector;
            TagView.SetTarget(output, in row.Value, addresses[value.Selector]);
        }
        Q(SizeWord, output.Length);
        _ = CuiDocument.Read(document.TagHash, document.ClassHash, output, nativeTypes: document.NativeTypes);
        return output;

        int EmptySlot()
        {
            int slotStride = Unsafe.SizeOf<SCuiPathSlot>();
            int slotsField = TagView.FieldOffset<CuiResource>("PathSlots");
            var slots = TagArray.Checked(output, slotsField, SCuiPathSlot.Class, slotStride);
            for (int i = 0; i < slots.Count; i++)
                if (TagView.At<SCuiPathSlot>(output, slots.Data + i * slotStride).Elements.Count == 0) return slots.Data + i * slotStride;
            int start = AppendArray(slotsField, checked(slots.Count + 1), slotStride, SCuiPathSlot.Class);
            if (slots.Count != 0) output.AsSpan(slots.Data, slots.Count * slotStride).CopyTo(output.AsSpan(start));
            int elementsWord = TagView.FieldOffset<SCuiPathSlot>("Elements.Relative");
            for (int i = 0; i < slots.Count; i++) Rebase(slots.Data + i * slotStride + elementsWord, start + i * slotStride + elementsWord);
            foreach (var property in prepared.Properties)
                Redirect(TagView.OffsetOf(output, in TagView.At<CuiPropertyRow>(output, property.RecordOffset).PathSlot));
            int bindingStride = Unsafe.SizeOf<CuiBindingRow>();
            var bindings = TagArray.Checked(output, TagView.FieldOffset<CuiResource>("BindingSet.Bindings"),
                CuiBindingRow.Class, bindingStride);
            for (int i = 0; i < bindings.Count; i++)
            {
                ref readonly var binding = ref TagView.At<CuiBindingRow>(output, bindings.Data + i * bindingStride);
                int sourceSlot = TagView.OffsetOf(output, in binding.SourcePathSlot), targetSlot = TagView.OffsetOf(output, in binding.TargetPathSlot);
                Redirect(sourceSlot); Redirect(targetSlot);
            }
            return start + slots.Count * slotStride;

            void Redirect(int pointer)
            {
                int target = TagPointer.Checked(output, pointer);
                if (target >= slots.Data && target < slots.Data + slots.Count * slotStride && (target - slots.Data) % slotStride == 0)
                    Q(pointer, start + target - slots.Data - pointer);
            }
        }
        int AppendArray(int descriptor, int count, int stride, uint type) =>
            TagRelocator.AppendArray(ref output, descriptor, type, count, checked(count * stride), 8);
        void Rebase(int source, int target)
        { int address = TagPointer.Checked(output, source); Q(target, address < 0 ? 0 : address - target); }
        void Q(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(at), value);
    }

    private static readonly int SizeWord = TagView.FieldOffset<CuiResource>("TagSize");
    // Row-relative pointer words that a copied row must rebase.
    private static readonly int ComponentPointer = TagView.FieldOffset<CuiComponentRow>("Properties.Relative");
    private static readonly int[] PropertyPointers =
        [TagView.FieldOffset<CuiPropertyRow>("PathSlot"), TagView.FieldOffset<CuiPropertyRow>("Value")];

    /** A shared component or reference array is detached before any selected property changes. */
    private static int CloneArray(ref byte[] bytes, int descriptor, uint type, int stride, int[] pointers)
    {
        byte[] original = bytes;
        var source = TagArray.Checked(original, descriptor, type, stride);
        if (source.Count == 0) return 0;
        int data = TagRelocator.AppendArray(ref bytes, descriptor, type, source.Count, checked(source.Count * stride), 8);
        bytes.AsSpan(source.Data, source.Count * stride).CopyTo(bytes.AsSpan(data));
        for (int i = 0; i < source.Count; i++)
            foreach (int pointer in pointers)
            {
                int from = source.Data + i * stride + pointer, target = data + i * stride + pointer;
                int address = TagPointer.Checked(original, from);
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(target), address < 0 ? 0 : address - target);
            }
        return data;
    }
}
