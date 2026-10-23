using System.Buffers.Binary;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

/** A navigation group owns CB6/CB8/CBA and one identity placement of entity 80C70D01. */
public sealed record NavigationPlacementPlan(uint ContainerTag, byte[] Container, int TableField,
    IReadOnlyDictionary<uint, string> Fingerprints)
{
    /** An existing navigation slot is replaced; a nav-less collision container gains one slot. */
    public byte[] Attach(uint table)
    {
        if (TableField >= 0)
        {
            var bytes = Container.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(TableField), table); return bytes;
        }
        var d = new NativeRows(Container);
        var rows = d.Rows(0x28, 0x80808BB0, 4).Select(row => d.Span(row, 4).ToArray()).ToList();
        rows.Add(BitConverter.GetBytes(table));
        return InstanceIsolationWriter.Array(Container, 0x28, 0x80808BB0, rows);
    }
}

public static class NavigationPlacement
{
    public const string Limitations = "Links in game are unverified; retail meshes are sparse (Moon: 92 faces) where ours are dense; jump/drop links are unverified. "
        + "May not work in game.";

    public static NavigationPlacementPlan Plan(PackageCatalog catalog, uint bubble)
    {
        var fingerprints = new Dictionary<uint, string>();
        NativeRows Read(uint tag, uint type)
        {
            if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != type) throw new InvalidDataException("Navigation ownership has an unknown class.");
            byte[] bytes = catalog.Read(tag); fingerprints[tag] = BinaryDelta.Hash(bytes); return new(bytes);
        }
        var b = Read(bubble, 0x80807DAE); uint branchTag = b.U32(8);
        var branch = Read(branchTag, 0x808091E0);
        var candidates = new List<(uint Tag, byte[] Bytes, int Slot, bool Collision)>();
        foreach (int row in branch.Rows(8, 0x808084C1, 16))
        {
            uint containerTag = branch.U32(row) == uint.MaxValue
                ? catalog.Resolve64(branch.U64(row + 8)) ?? throw new InvalidDataException("Navigation container has an unresolved wide tag.") : branch.U32(row);
            var container = Read(containerTag, 0x80808A54);
            bool collision = false; int navSlot = -1;
            foreach (int slot in container.Rows(0x28, 0x80808BB0, 4))
            {
                uint tableTag = container.U32(slot);
                if (!catalog.TryGetEntry(tableTag, out var entry) || entry.Reference != 0x808099D6) continue;
                var table = Read(tableTag, 0x808099D6);
                foreach (int placement in table.Rows(8, 0x808099D8, 0x90))
                {
                    long relative = table.I64(placement + 0x78); if (relative == 0) continue;
                    int resource = checked((int)(placement + 0x78 + relative));
                    uint cls = table.U32(resource - 4);
                    if (cls == 0x80808A13) collision = true;
                    if (cls != 0x80808C76) continue;
                    // Replacing a mixed table would remove unrelated content.
                    if (ReadRoot(table.Bytes) == 0 || navSlot >= 0) throw new InvalidDataException("Navigation ownership is ambiguous.");
                    navSlot = slot;
                }
            }
            if (navSlot >= 0 || collision) candidates.Add((containerTag, container.Bytes, navSlot, collision));
        }
        var owners = candidates.Where(c => c.Slot >= 0).DistinctBy(c => c.Tag).ToArray();
        if (owners.Length == 0) owners = candidates.Where(c => c.Collision).DistinctBy(c => c.Tag).ToArray();
        if (owners.Length != 1) throw new InvalidDataException("Choose a bubble with one navigation or collision container.");
        var owner = owners[0];
        foreach (var entry in catalog.Entries.Where(e => e.Reference == 0x80807DAE && e.TagHash != bubble))
            if (new NativeRows(catalog.Read(entry.TagHash)).U32(8) == branchTag)
                throw new InvalidDataException("Navigation branch is shared by another bubble.");
        // A container shared by another branch cannot be retargeted for just this bubble.
        foreach (var entry in catalog.Entries.Where(e => e.Reference == 0x808091E0 && e.TagHash != branchTag))
        {
            var other = new NativeRows(catalog.Read(entry.TagHash));
            foreach (int row in other.Rows(8, 0x808084C1, 16))
            {
                uint target = other.U32(row) == uint.MaxValue ? catalog.Resolve64(other.U64(row + 8)) ?? 0 : other.U32(row);
                if (target == owner.Tag) throw new InvalidDataException("Navigation container is shared by another branch.");
            }
        }
        return new(owner.Tag, owner.Bytes, owner.Slot, fingerprints);
    }

    /** A fresh package reserves entries 0..3 before publication; the factory checks those allocations. */
    public static ProjectAssetAddition[] Additions(GeneratedNavigation generated, ushort package)
    {
        uint first = checked(0x80800000u + ((uint)package << 13));
        byte[] root = generated.Root.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(8), first + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(12), first + 2);
        return
        [
            new("Navigation root", root, new(package, 0x80808CB6, 0x1000, EntryIndex: 0)),
            new("Navigation mesh", generated.Mesh, new(package, 0x80808CB8, 0x1000, EntryIndex: 1)),
            new("Navigation point graph", generated.Graph, new(package, 0x80808CBA, 0x1000, EntryIndex: 2)),
            new("Navigation placement", Table(first), new(package, 0x808099D6, 0x1000, EntryIndex: 3))
        ];
    }

    /** The asset transaction checks reserved identities before attaching the allocated table. */
    public static ProjectAssetEdits Bind(NavigationPlacementPlan plan, GeneratedNavigation generated, IReadOnlyList<AddedProjectAsset> assets)
    {
        if (assets.Count != 4) throw new ArgumentException("Navigation requires four allocated entries.");
        uint Tag(int i)
        {
            var native = assets[i].Native ?? throw new InvalidDataException("Navigation entry has no native metadata.");
            return checked(0x80800000u + ((uint)native.PackageId << 13) + (uint)native.EntryIndex!.Value);
        }
        var root = generated.Root.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(8), Tag(1));
        BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(12), Tag(2));
        var expected = Additions(generated, assets[0].Native!.PackageId);
        for (int i = 0; i < 4; i++)
            if (assets[i].Native != expected[i].Native || assets[i].Sha256 != BinaryDelta.Hash(expected[i].Data))
                throw new InvalidDataException("Navigation allocation differs from its reserved package entries.");
        var table = Table(Tag(0));
        if (ReadRoot(table) != Tag(0)) throw new InvalidDataException("Navigation placement did not decode.");
        _ = Scene.NavigationDecoder.Decode(root, generated.Mesh, generated.Graph);
        return new(new Dictionary<uint, byte[]> { [plan.ContainerTag] = plan.Attach(Tag(3)) });
    }

    /** Retail identity placement: row +0x78 points at the 32-byte 80808C76 record, root at record +0x18. */
    public static byte[] Table(uint root)
    {
        byte[] bytes = new byte[232];
        void U32(int p, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(p), v);
        void I64(int p, long v) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(p), v);
        I64(0, bytes.Length); I64(8, 1); I64(16, 16); U32(28, 0x80809FBD); I64(32, 1); U32(40, 0x808099D8);
        int row = 48; U32(row, 0x80C70D01);
        U32(row + 28, 0x3F800000); U32(row + 44, 0x3F800000); I64(row + 0x30, -1);
        for (int p = 0x40; p <= 0x4C; p += 4) U32(row + p, uint.MaxValue);
        U32(row + 0x54, uint.MaxValue); U32(row + 0x5C, 0x3F800000); U32(row + 0x60, uint.MaxValue);
        U32(row + 0x64, 0x811C9DC5); U32(row + 0x68, 0x304); I64(row + 0x70, -1);
        I64(row + 0x78, 32); U32(196, 0x80808C76); U32(224, root); return bytes;
    }

    public static uint ReadRoot(byte[] bytes)
    {
        var d = new NativeRows(bytes); var rows = d.Rows(8, 0x808099D8, 144);
        if (rows.Length != 1) throw new InvalidDataException("Navigation placement must hold exactly one row.");
        int record = checked((int)(rows[0] + 0x78 + d.I64(rows[0] + 0x78)));
        if (d.U32(rows[0]) != 0x80C70D01 || d.U32(record - 4) != 0x80808C76)
            throw new InvalidDataException("Navigation placement has an unknown entity or record.");
        uint root = d.U32(record + 0x18);
        if (!bytes.AsSpan().SequenceEqual(Table(root))) throw new InvalidDataException("Navigation placement is not the static identity profile.");
        return root;
    }
}
