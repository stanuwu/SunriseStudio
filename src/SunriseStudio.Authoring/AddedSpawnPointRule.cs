using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

namespace SunriseStudio.Authoring;

/** Adds arrival rows to the installed bank without relocating its existing rows or references. */
public sealed class AddedSpawnPointRule : IObjectEditRule
{
    public ObjectEditLayout Layout => ObjectEditLayout.AddedSpawnPoint;

    public void Apply(ObjectEditContext context, IReadOnlyList<ObjectEdit> edits)
    {
        foreach (var bank in edits.GroupBy(edit => edit.Target.SourceTag))
        {
            uint tag = bank.Key;
            if (context.ClassOf(tag) != 0x80809162) // Spawn bank.
                throw new InvalidDataException("Added spawn source is not a spawn bank.");
            byte[] original = context.Original(tag), current = context.Current(tag);
            int[] rows = SpawnPointAuthoring.Rows(original), editedRows = SpawnPointAuthoring.Rows(current);
            if (current.Length != original.Length || !rows.SequenceEqual(editedRows))
                throw new InvalidDataException("A raw edit resized the spawn bank. Revert it before adding arrival points.");
            var additions = bank.OrderBy(edit => edit.Target.Index).ToArray();
            if (additions.Length > 10000 || additions.Select(edit => edit.Key).Distinct().Count() != additions.Length)
                throw new InvalidDataException("A bank accepts up to 10000 distinct added arrival points.");
            byte[] result = new byte[checked(current.Length + additions.Length * 48)];
            current.CopyTo(result, 0);
            for (int i = 0; i < additions.Length; i++)
            {
                var edit = additions[i];
                edit.Validate();
                if (edit.Target.Length != original.Length || edit.Target.OriginalSha256 != BinaryDelta.Hash(original))
                    throw new InvalidDataException("The installed spawn bank changed. Revert its additions or restore the package version.");
                int at = current.Length + i * 48;
                NativeRows.Write(result, at, edit.Rotation!); // Quaternion x, y, z, w at +0.
                NativeRows.Write(result, at + 16, edit.Position!);
                NativeRows.Write(result, at + 28, 1);
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(at + 32), (uint)edit.Target.Identity);
                context.Produced(edit, tag);
            }
            BinaryPrimitives.WriteUInt64LittleEndian(result, (ulong)result.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8), (ulong)(rows.Length + additions.Length));
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(rows[0] - 16), (ulong)(rows.Length + additions.Length));
            SpawnPointAuthoring.Rows(result);
            context.ReplaceSpawnBank(tag, result);
        }
    }
}

/** Added point identities are project ordinals; compiled rows are packed in ordinal order. */
public static class SpawnPointAuthoring
{
    /** Accepts one terminal typed array; unknown trailing data is never discarded or relocated. */
    internal static int[] Rows(byte[] bytes)
    {
        var data = new NativeRows(bytes);
        int[] rows = NativeRows.Rows(bytes, 8, 0x80809164, 48); // Spawn point rows.
        if (data.U64(0) != (ulong)bytes.Length || rows.Length == 0 || rows[0] < 48
            || rows[^1] + 48 != bytes.Length || data.U32(rows[0] - 4) != 0)
            throw new InvalidDataException("Spawn additions require a sized bank with one nonempty terminal point array.");
        return rows;
    }

    /** The caller chooses a bank already reached by the map; its routing and name hashes stay unchanged. */
    public static ObjectEdit Create(PackageCatalog catalog, IReadOnlyList<ObjectEdit> edits, uint bank,
        Vector3 position, Quaternion rotation, uint nameHash, string role = "arrival")
    {
        if (!catalog.TryGetEntry(bank, out var entry) || entry.Reference != 0x80809162)
            throw new InvalidDataException("Select an installed spawn bank.");
        byte[] original = catalog.ReadOriginal(bank);
        Rows(original);
        int index = checked(edits.Where(edit => edit.Target.SourceTag == bank && edit.Target.Layout == ObjectEditLayout.AddedSpawnPoint)
            .Select(edit => edit.Target.Index).DefaultIfEmpty(-1).Max() + 1);
        var target = new ObjectEditTarget(bank, ObjectEditLayout.AddedSpawnPoint, 0, original.Length,
            index, nameHash, BinaryDelta.Hash(original));
        var result = new ObjectEdit(ObjectEditKind.PlacementTransform, target, "Added arrival point",
            ObjectEditCompiler.Floats(position), ObjectEditCompiler.Floats(rotation))
        { SpawnRole = role };
        result.Validate();
        return result;
    }

    /** Resolves the saved addition behind a decoded marker, including after another addition was removed. */
    public static ObjectEdit? Find(PackageCatalog catalog, SceneMarker marker, IReadOnlyList<ObjectEdit> edits)
    {
        if (marker.Layer != SceneLayer.Spawns) return null;
        int[] original = NativeRows.Rows(catalog.ReadOriginal(marker.SourceTag), 8, 0x80809164, 48);
        int[] current = NativeRows.Rows(catalog.Read(marker.SourceTag), 8, 0x80809164, 48);
        int index = Array.IndexOf(current, marker.SourceOffset) - original.Length;
        if (index < 0) return null;
        var addition = edits.Where(edit => edit.Target.SourceTag == marker.SourceTag && edit.Target.Layout == ObjectEditLayout.AddedSpawnPoint)
            .OrderBy(edit => edit.Target.Index).ElementAtOrDefault(index);
        if (addition is null || addition.Target.Identity != marker.NameHash)
            throw new InvalidDataException("The added spawn selection changed. Reload the world.");
        return addition;
    }

    public static int Offset(PackageCatalog catalog, ObjectEditTarget target, IReadOnlyList<ObjectEdit> edits)
    {
        var additions = edits.Where(edit => edit.Target.SourceTag == target.SourceTag && edit.Target.Layout == ObjectEditLayout.AddedSpawnPoint)
            .OrderBy(edit => edit.Target.Index).ToArray();
        int index = Array.FindIndex(additions, edit => edit.Key == target.Key);
        return index < 0 ? -1 : checked(catalog.ReadOriginal(target.SourceTag).Length + index * 48);
    }
}
