using System.Runtime.CompilerServices;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/**
 * The HUD config row that the weapon's gear key selects, and the installed row it was copied from.
 * Row and BaseRow are byte offsets into Hud and BaseHud.
 */
public sealed record WeaponFiringRow(uint Root, uint Hud, int Row, uint BaseHud, int BaseRow)
{
    public static readonly int
        MuzzleSlot = TagView.FieldOffset<SWeaponFiringRow>(nameof(SWeaponFiringRow.MuzzleSlotEntityRestartsEachShot)),
        ReloadSlot = TagView.FieldOffset<SWeaponFiringRow>(nameof(SWeaponFiringRow.ReloadSlotEntityNeverPaused)),
        ProjectileSlot = TagView.FieldOffset<SWeaponFiringRow>(nameof(SWeaponFiringRow.ProjectileSlotEntity));

    /**
     * Finds the weapon's own firing row; the gameplay root and the HUD config must be owned.
     * @param read Reads the root and its configs with the caller's pending changes.
     */
    public static WeaponFiringRow Find(StudioProject project, PackageCatalog catalog, Func<uint, byte[]> read, uint weaponTag)
    {
        var owned = project.AddedAssets.Where(asset => asset.Native is not null).ToDictionary(asset => NativeTag(asset.Native!));
        var art = WeaponArchetypeAuthoring.Read(catalog, catalog.Read(weaponTag));
        uint root = art.EntityTag;
        if (!owned.ContainsKey(root)) throw new InvalidDataException("The weapon's gameplay root must be authored in this project.");

        uint key = WeaponArchetypeAuthoring.GearKey(catalog, art);
        uint hud = 0; int row = -1;
        foreach (uint config in WeaponConfigEdits.ConstructionConfigs(read(root)).Distinct())
            if (FiringRow(read(config), key) is int found and >= 0) { hud = config; row = found; break; }
        if (row < 0) throw new InvalidDataException("No config holds a firing row for the weapon's gear key.");
        if (!owned.ContainsKey(hud)) throw new InvalidDataException("Give the weapon its own base records first; its firing row is shared.");

        // The stock slot entities come from the same row of the config this firing row was cloned from.
        if (owned[hud].BaseTagHash is not uint baseHud || RowOffsets(catalog.ReadOriginal(baseHud)) is not { } baseRows
            || RowOffsets(read(hud)) is not { } rows || baseRows.Count != rows.Count || !rows.Contains(row))
            throw new InvalidDataException("The weapon's firing row is not a copy of an installed firing row.");
        return new(root, hud, row, baseHud, baseRows[rows.IndexOf(row)]);
    }

    /** The raw tag at this slot of the installed base row; 0 when empty. Not checked to be an entity. */
    public uint StockTag(PackageCatalog catalog, int slot) => TagRead.U32(catalog.ReadOriginal(BaseHud), BaseRow + slot);

    public static uint NativeTag(NativeAssetMetadata native) =>
        TagHashes.Make(native.PackageId, native.EntryIndex!.Value);

    /** The offset of the HUD row with the key, or -1 when the config is not a HUD config or has none. */
    private static int FiringRow(byte[] bytes, uint key) =>
        RowOffsets(bytes)?.FirstOrDefault(at => TagView.At<SWeaponFiringRow>(bytes, at).GearKey == key, -1) ?? -1;

    /** The offsets of a HUD config's firing rows, or null when the config is not a HUD config. */
    private static List<int>? RowOffsets(byte[] bytes)
    {
        if (!TagView.FitsThrough<SEntityConfig>(bytes, 0, nameof(SEntityConfig.Definition))) return null;
        ref readonly var definition = ref TagView.At<RelativePointer<Unknown>>(bytes,
            TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition)));
        if (definition.IsNull) return null;
        long resource = TagView.OffsetOf(bytes, in definition) + definition.Relative;
        const string rowsMember = nameof(SWeaponHudConfigResource.FiringRows448Byte);
        if (resource < TagView.ClassWordSize || resource > bytes.Length
            || !TagView.FitsThrough<SWeaponHudConfigResource>(bytes, (int)resource, rowsMember)
            || TagView.ClassAt(bytes, (int)resource) != SWeaponHudConfigResource.Class) return null;
        var firing = new TagArray(bytes, (int)resource + TagView.FieldOffset<SWeaponHudConfigResource>(rowsMember));
        var header = firing.Header;
        long count = firing.Count;
        if (count is <= 0 or > 1024 || header.Offset < TagView.ClassWordSize || header.Offset > bytes.Length - TagArray.HeaderSize
            || header.ElementClass != SWeaponFiringRow.Class) return null;
        int data = (int)header.DataOffset, stride = Unsafe.SizeOf<SWeaponFiringRow>();
        return Enumerable.Range(0, (int)count).Select(index => data + index * stride)
            .Where(at => at + stride <= bytes.Length).ToList();
    }
}

/** Typed array rows for the weapon writers; a zero count is empty and its pointer is not read. */
internal static class WeaponTagRows
{

    /**
     * Reads the row offsets of the array whose field is at `field`; each row is one T.
     * @param elementClass The class the array header must name.
     * @return The row offsets; InvalidDataException for a truncated field, a bad header or rows past the tag.
     */
    internal static int[] Starts<T>(ReadOnlySpan<byte> tag, int field, uint elementClass) where T : unmanaged
    {
        int stride = Unsafe.SizeOf<T>();
        if (field < 0 || field > tag.Length - sizeof(long)) throw new InvalidDataException("A typed native array is truncated.");
        var array = new TagArray(tag, field);
        long count = array.Count;
        if (count == 0) return [];
        if (!TagView.Fits<TagArray<T>>(tag, field)) throw new InvalidDataException("A typed native array is truncated.");
        var header = array.Header;
        if (count is < 0 or > TagArray.MaximumReaderRows || header.Offset < TagView.ClassWordSize
            || header.Offset > tag.Length - TagArray.HeaderSize || count > (tag.Length - header.DataOffset) / stride || !TagArray.MarkerAt(tag, header.Offset)
            || header.Count != count || header.ElementClass != elementClass)
            throw new InvalidDataException("A typed native array is invalid.");
        int data = (int)header.DataOffset;
        return Enumerable.Range(0, (int)count).Select(index => data + index * stride).ToArray();
    }
}
