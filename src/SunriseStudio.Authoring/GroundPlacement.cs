using System.Buffers.Binary;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

namespace SunriseStudio.Authoring;

/**
 * Where a ground is placed in an installed bubble: a 23-slot geometry container whose terrain slot holds an
 * empty table, the companion container after it whose collision slot holds an empty table, and the bubble's
 * render registry. Census of the installation (2026-10-20): every 23-slot container is followed in its branch
 * by a 1- or 2-member container with the same mask; one registry per branch, in a 1-member container.
 */
public sealed record GroundSite(uint Bubble, uint Branch, uint Container, uint Companion, uint Registry,
    uint RegistryHeader, uint RegistryData, int NextIndex);

/** The installed edits that place grounds: the two container slots and the registry's bank 0. */
public sealed record GroundPlacementEdits(IReadOnlyDictionary<uint, byte[]> Assets);

public static class GroundPlacement
{
    public const uint BubbleClass = 0x80807DAE, BranchClass = 0x808091E0, BranchRowClass = 0x808084C1;
    public const uint ContainerClass = 0x80808A54, MemberClass = 0x80808BB0, RegistryClass = 0x80807268;
    public const uint RegistryResourceClass = 0x80806F38;
    // A geometry container lists 23 members; its companion's slot 0 holds the collision table (366 of 366).
    public const int GeometryMembers = 23, CollisionSlot = 0;
    private const int MaskOffset = 8, MaskSize = 32, MembersField = 0x28, BranchRowsField = 8;

    /** The containers of a bubble's branch in branch order. */
    public static IReadOnlyList<uint> Containers(PackageCatalog catalog, uint bubble)
    {
        uint branch = Branch(catalog, bubble);
        var data = catalog.ReadOriginal(branch);
        var result = new List<uint>();
        foreach (int row in Rows(data, BranchRowsField, 16))
        {
            uint child = U32(data, row) == uint.MaxValue ? catalog.Resolve64(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(row + 8))) ?? 0 : U32(data, row);
            if (child != 0 && Class(catalog, child) == ContainerClass) result.Add(child);
        }
        return result;
    }

    /**
     * The first geometry container of the bubble with an empty terrain slot and a companion with an empty
     * collision slot, preferring the mask of a container that already lists terrain.
     * @param usedIndices Terrain indices other grounds of the project already take in this registry.
     */
    public static GroundSite Find(PackageCatalog catalog, uint bubble, IReadOnlyCollection<int> usedIndices)
    {
        uint branch = Branch(catalog, bubble);
        var containers = Containers(catalog, bubble);
        var candidates = new List<(uint Container, uint Companion, byte[] Mask)>();
        var terrainMasks = new List<byte[]>();
        uint registry = 0;
        for (int i = 0; i < containers.Count; i++)
        {
            var data = catalog.ReadOriginal(containers[i]);
            var members = Members(data);
            if (members.Count == 1 && RegistryOf(catalog, members[0]) is uint found) registry = registry == 0 ? found
                : throw new InvalidDataException("This bubble lists two render registries.");
            if (members.Count != GeometryMembers) continue;
            var mask = data.AsSpan(MaskOffset, MaskSize).ToArray();
            if (!IsEmptyTable(catalog, members[GroundTerrain.ContainerSlot])) { terrainMasks.Add(mask); continue; }
            if (i + 1 >= containers.Count) continue;
            var companion = catalog.ReadOriginal(containers[i + 1]);
            var companionMembers = Members(companion);
            if (companionMembers.Count is < 1 or > 2 || !companion.AsSpan(MaskOffset, MaskSize).SequenceEqual(mask)) continue;
            if (IsEmptyTable(catalog, companionMembers[CollisionSlot])) candidates.Add((containers[i], containers[i + 1], mask));
        }
        if (registry == 0) throw new InvalidDataException("This bubble has no render registry.");
        if (candidates.Count == 0) throw new InvalidDataException("This bubble has no geometry container with free terrain and collision slots.");
        var chosen = candidates.FirstOrDefault(c => terrainMasks.Any(m => m.AsSpan().SequenceEqual(c.Mask)));
        if (chosen.Container == 0) chosen = candidates[0];
        var (header, dataTag, rows) = RegistryBank(catalog, registry);
        // Terrain rows carry 'kbrt' in the key's high word; other bank-0 rows are not terrain.
        int next = rows.Where(row => row.Key >> 32 == GroundTerrain.KeyTag).Select(row => (int)(row.Key & 0xFFFF))
            .Concat(usedIndices).DefaultIfEmpty(-1).Max() + 1;
        if (next > 0xFFFF) throw new InvalidDataException("This bubble has no free terrain index.");
        return new(bubble, branch, chosen.Container, chosen.Companion, registry, header, dataTag, next);
    }

    /**
     * Installed edits for every ground placed in one site's containers and registry, from the originals.
     * @param terrainTable Generated terrain table of the ground in this container.
     * @param collisionTable Generated collision table of that ground.
     * @param entries Registry rows of every ground placed in the registry: key and occlusion block.
     */
    public static Dictionary<uint, byte[]> Edits(PackageCatalog catalog, GroundSite site, uint terrainTable, uint collisionTable,
        IReadOnlyList<(ulong Key, byte[] Block)> entries)
    {
        var edits = new Dictionary<uint, byte[]>
        {
            [site.Container] = Repoint(catalog.ReadOriginal(site.Container), GroundTerrain.ContainerSlot, terrainTable),
            [site.Companion] = Repoint(catalog.ReadOriginal(site.Companion), CollisionSlot, collisionTable)
        };
        foreach (var (tag, bytes) in RegistryEdits(catalog, site.Registry, entries)) edits[tag] = bytes;
        return edits;
    }

    /** The registry with bank-0 rows for entries after the installed ones, its data with their blocks, and the header size. */
    public static Dictionary<uint, byte[]> RegistryEdits(PackageCatalog catalog, uint registry, IReadOnlyList<(ulong Key, byte[] Block)> entries)
    {
        var original = catalog.ReadOriginal(registry);
        var (header, dataTag, rows) = RegistryBank(catalog, registry);
        var headerBytes = catalog.ReadOriginal(header).ToArray();
        var data = catalog.ReadOriginal(dataTag);
        if (entries.Count == 0) return new() { [registry] = original, [header] = headerBytes, [dataTag] = data };
        if (entries.Select(e => e.Key).Concat(rows.Select(r => r.Key)).Distinct().Count() != entries.Count + rows.Count)
            throw new InvalidDataException("A terrain registry key is already in this bubble.");
        var newRows = rows.Select(row => GroundTerrain.RegistryRow(row.Key, row.First)).ToList();
        using var appended = new MemoryStream();
        appended.Write(data);
        foreach (var (key, block) in entries)
        {
            if (block.Length == 0 || block.Length % 4 != 0) throw new InvalidDataException("A terrain occlusion block is a nonempty multiple of four bytes.");
            newRows.Add(GroundTerrain.RegistryRow(key, checked((uint)appended.Length)));
            appended.Write(block);
        }
        // Bank 0 rows (class 0x8080726A) at registry +16; the old rows stay behind as unreferenced bytes.
        var edited = NativeArray.Append(original, 16, GroundTerrain.RegistryRowClass, newRows, out _);
        BinaryPrimitives.WriteUInt32LittleEndian(headerBytes, checked((uint)appended.Length));
        return new() { [registry] = edited, [header] = headerBytes, [dataTag] = appended.ToArray() };
    }

    /** Bank 0 of a registry: its stride-1 buffer header at +8, the header's data, and the installed rows. */
    public static (uint Header, uint Data, IReadOnlyList<(ulong Key, uint First)> Rows) RegistryBank(PackageCatalog catalog, uint registry)
    {
        if (Class(catalog, registry) != RegistryClass) throw new InvalidDataException("Not a render registry.");
        var data = catalog.ReadOriginal(registry);
        uint header = U32(data, 8);
        if (header == uint.MaxValue || !catalog.TryGetEntry(header, out var entry))
            throw new InvalidDataException("This bubble's render registry has no occlusion buffer.");
        var headerBytes = catalog.ReadOriginal(header);
        uint dataTag = entry.Reference;
        if (!catalog.TryGetEntry(dataTag, out _) || headerBytes.Length < 8 || BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.AsSpan(4)) != 1
            || U32(headerBytes, 0) != catalog.ReadOriginal(dataTag).Length)
            throw new InvalidDataException("This bubble's occlusion buffer is not a stride-1 buffer of its data's size.");
        var rows = Rows(data, 16, 16).Select(row => (BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(row)), U32(data, row + 8))).ToArray();
        return (header, dataTag, rows);
    }

    /** True for a terrain or collision table with no rows (installed slots share such tables). */
    public static bool IsEmptyTable(PackageCatalog catalog, uint tag) =>
        Class(catalog, tag) == GroundTerrain.TableClass && BinaryPrimitives.ReadInt64LittleEndian(catalog.ReadOriginal(tag).AsSpan(8)) == 0;

    /** The container with member slot pointing at tag. */
    public static byte[] Repoint(byte[] container, int slot, uint tag)
    {
        var members = MemberOffsets(container);
        if (slot >= members.Count) throw new InvalidDataException("The container has no such slot.");
        var edited = container.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(edited.AsSpan(members[slot]), tag);
        return edited;
    }

    public static IReadOnlyList<uint> Members(byte[] container) => MemberOffsets(container).Select(at => U32(container, at)).ToArray();

    private static List<int> MemberOffsets(byte[] container) => Rows(container, MembersField, 4).ToList();

    private static uint? RegistryOf(PackageCatalog catalog, uint table)
    {
        if (Class(catalog, table) != GroundTerrain.TableClass) return null;
        var data = catalog.ReadOriginal(table);
        foreach (int row in Rows(data, 8, 144))
        {
            long field = row + 0x78, relative = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan((int)field));
            if (relative == 0) continue;
            long resource = field + relative;
            if (resource < 4 || resource + 0x14 > data.Length || U32(data, (int)resource - 4) != RegistryResourceClass) continue;
            uint registry = U32(data, (int)resource + 0x10);
            if (Class(catalog, registry) == RegistryClass) return registry;
        }
        return null;
    }

    private static uint Branch(PackageCatalog catalog, uint bubble)
    {
        if (Class(catalog, bubble) != BubbleClass) throw new InvalidDataException("Choose a bubble (class 80807DAE).");
        uint branch = U32(catalog.ReadOriginal(bubble), 8);
        if (Class(catalog, branch) != BranchClass) throw new InvalidDataException("The bubble names no branch.");
        return branch;
    }

    /** Row offsets of the typed array whose descriptor (u64 count, i64 offset to the count) sits at field. */
    private static IEnumerable<int> Rows(byte[] data, int field, int stride)
    {
        long count = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(field)), relative = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(field + 8));
        if (count == 0) yield break;
        long start = field + 8 + relative + 16;
        if (count < 0 || start < 0 || start + count * stride > data.Length) throw new InvalidDataException("A native array is outside its tag.");
        for (int i = 0; i < count; i++) yield return checked((int)(start + i * stride));
    }

    private static uint Class(PackageCatalog catalog, uint tag) => catalog.TryGetEntry(tag, out var entry) ? entry.Reference : 0;
    private static uint U32(byte[] data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
}
