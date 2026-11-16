using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.World;

/**
 * One placement row an authored object reaches through a set tag, such as a squad's spawn point.
 * @param OwnerFile The authored object file that names the set.
 * @param SetTag The installed set tag between the object and the list.
 * @param ListTag The installed placement list.
 * @param Row The row's index in the list.
 */
public sealed record WorldObjectPoint(string OwnerFile, uint SetTag, uint ListTag, int Row, ulong Identity, Vector3 Position);

/**
 * Placement rows of a world copy's authored objects. A squad builds its members at such a row.
 * Moving a row gives the world its own copy of the row's list and of the set that leads to it.
 */
public static class WorldObjectPoints
{
    /** Every placement row the world's authored objects reach, with the position it holds now. */
    public static IReadOnlyList<WorldObjectPoint> List(WorldFiles world, PackageCatalog catalog, CancellationToken token = default)
    {
        var result = new List<WorldObjectPoint>();
        foreach (var owner in world.Files.Where(file => file.ClassId == SAuthoredObject.Class && file.SourceTag is not null).ToList())
        {
            token.ThrowIfCancellationRequested();
            // The installed bytes name the installed set; the world's copy names it through a reference.
            byte[] bytes = catalog.Read(owner.SourceTag!.Value);
            var seen = new HashSet<uint>();
            for (int at = 0; at + sizeof(uint) <= bytes.Length; at += sizeof(uint))
            {
                uint set = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
                if (set < TagHashes.Base || !catalog.TryGetEntry(set, out var entry) || entry.Reference != SAuthoredObjectSet.Class || !seen.Add(set))
                    continue;
                byte[] link = catalog.Read(set);
                if (!TagView.Fits<SAuthoredObjectSet>(link, 0)) continue;
                uint list = TagView.At<SAuthoredObjectSet>(link, 0).DirectTarget;
                if (!catalog.TryGetEntry(list, out var target) || target.Reference != SMapPlacements.Class) continue;
                byte[] data = world.Files.FirstOrDefault(file => file.SourceTag == list)?.Data ?? catalog.Read(list);
                var rows = TagView.Rows(data, in TagView.At<SMapPlacements>(data, 0).Placements, AuthoredPlacementEntry.Class);
                for (int row = 0; row < rows.Length; row++)
                    result.Add(new(owner.Name, set, list, row, rows[row].PlacementIdentity, rows[row].Position));
            }
        }
        return result;
    }

    /**
     * Moves the placement row that carries `identity` to `position`. The row keeps its rotation.
     * @param identity The row's placement identity, as a squad anchor of the activity SDK holds it.
     */
    public static void Move(WorldFiles world, RegistryIndex schema, PackageCatalog catalog, string destination, ulong identity,
        Vector3 position, CancellationToken token = default)
    {
        var found = List(world, catalog, token).Where(point => point.Identity == identity).ToList();
        var rows = found.Select(point => (point.ListTag, point.Row)).Distinct().ToList();
        if (rows.Count != 1)
            throw new InvalidDataException(rows.Count == 0 ? $"The world has no placement row 0x{identity:X16}."
                : $"The world has {rows.Count} placement rows 0x{identity:X16}.");
        foreach (var point in found)
        {
            string from = point.OwnerFile;
            foreach (uint hop in (ReadOnlySpan<uint>)[point.SetTag, point.ListTag])
            {
                string name = world.Files.FirstOrDefault(file => file.SourceTag == hop)?.Name ?? WorldDevices.Copy(world, schema, catalog, destination, hop);
                WorldDevices.Repoint(world, from, hop, name);
                from = name;
            }
        }
        var list = world.Files.Single(file => file.SourceTag == rows[0].ListTag);
        byte[] data = list.Data.ToArray();
        var placed = TagView.Rows(data, in TagView.At<SMapPlacements>(data, 0).Placements, AuthoredPlacementEntry.Class);
        TagView.Edit<AuthoredPlacementEntry>(data, TagView.OffsetOf(data, in placed[rows[0].Row])).Position = position;
        world.Replace(WorldDevices.With(list, data, list.References));
    }
}
