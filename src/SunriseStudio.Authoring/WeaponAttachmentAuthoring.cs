using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

public sealed record WeaponAttachmentInput(string Name, int BoneIndex,
    Quaternion Rotation, Vector3 Translation, float Scale = 1);
public sealed record PreparedWeaponAttachments(ProjectAssetAddition Asset, byte[] GameplayRoot,
    IReadOnlyDictionary<string, uint> NameHashes);

/** Adds and moves the named points of a weapon's point provider; effects play on these points. */
public static class WeaponAttachmentAuthoring
{
    public const uint TemplateTag = 0x80B9EAC3;
    // Highest name hash among the checked template's six rows.
    private const uint LastTemplateHash = 0x9697BFD2;
    private const string UncheckedLayout = "The weapon point provider is not in the checked layout.";

    /** New points keep the checked provider's frame imports and eligibility bit zero. */
    public static PreparedWeaponAttachments Prepare(PackageCatalog catalog, byte[] gameplayRoot,
        ushort packageId, int entryIndex, string name, IReadOnlyList<WeaponAttachmentInput> points)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (points.Count > 64 || points.Select(point => point.Name).Distinct().Count() != points.Count)
            throw new InvalidDataException("Weapon attachment points need unique names and a count of at most 64.");
        if (!PackageWriter.IsNewPackageId(packageId) || catalog.ContainsInstalledPackage(packageId)
            || entryIndex is < 0 or >= TagHashes.EntriesPerPackage)
            throw new InvalidDataException("Weapon points need an unused native package entry.");
        uint tag = TagHashes.Make(packageId, entryIndex);
        if (catalog.ContainsTag(tag)) throw new InvalidDataException("Weapon point destination is already used.");
        if (!catalog.TryGetEntry(TemplateTag, out var entry) || entry.Reference != SEntityConfig.Class)
            throw new InvalidDataException("The weapon point provider is missing.");
        byte[] config = catalog.Read(TemplateTag).ToArray();
        ValidateTemplate(config);
        // The default instance and the definition name each other in three pairs.
        ref readonly var provider = ref TagView.At<SEntityConfig>(config);
        ref var owner = ref TagView.Edit<C80808506>(config, Target(config, in provider.DefaultInstance));
        ref var source = ref TagView.Edit<C80808507>(config, Target(config, in provider.Definition));
        Pair(ref owner.Unk0, ref source.Unk0, C80808506.Class, C80808507.Class);
        Pair(ref owner.H09da30c1.OwnerConfig, ref source.H09da30c1.OwnerConfig,
            UnknownDeclaredPointSourcePrimary.Class, UnknownDeclaredPointSourceSecondary.Class);
        Pair(ref owner.H09da30c1.H9a8ce8f3.OwnerPair0x80809bd8, ref source.H09da30c1.H9a8ce8f3.OwnerPair0x80809bd9,
            C80809bd6.Class, C80809bd7.Class);
        int pointsField = PointsField(config), pointSize = Unsafe.SizeOf<SWeaponAttachmentPoint>();
        int[] oldRows = PointRows(config, pointsField);
        if (oldRows.Length != 6 || oldRows.Any(row => Point(config, row).Eligibility0 != 0 || Point(config, row).BoneIndex04 != 0)
            || oldRows.Max(row => Point(config, row).NameHashSortedAscending) != LastTemplateHash)
            throw new InvalidDataException("The checked weapon attachment layout changed.");
        var hashes = oldRows.Select(row => Point(config, row).NameHashSortedAscending).ToHashSet();
        var resultNames = new Dictionary<string, uint>();
        foreach (var point in points)
        {
            uint hash = Hash(point);
            if (!hashes.Add(hash)) throw new InvalidDataException("A weapon attachment point name is already used.");
            config = InvestmentItemClone.AppendRow(config, pointsField, pointSize, Row(point, hash));
            resultNames.Add(point.Name, hash);
        }
        Sort(config);
        config = Compact(config);
        byte[] root = gameplayRoot.ToArray();
        int construction = 0, links = 0;
        foreach (int row in WeaponTagRows.Starts<SPatternComponent>(root, TagView.FieldOffset<SEntity>(nameof(SEntity.Components)),
            SPatternComponent.Class))
            Retag(ref TagView.Edit<SPatternComponent>(root, row).Config, true);
        RetagJoins(TagView.FieldOffset<SEntity>("HE3929a53.ConnectionList"));
        RetagJoins(TagView.FieldOffset<SEntity>("HE3929a53.Unknown0x30MustBe0"));
        foreach (int row in WeaponTagRows.Starts<ActorDescriptorRow>(root, TagView.FieldOffset<SEntity>("H83bfb495.InterfaceDescriptors"),
            ActorDescriptorRow.Class))
            RetagPointer(ref TagView.Edit<ActorDescriptorRow>(root, row).Config);
        foreach (int row in WeaponTagRows.Starts<ActorDescriptorMetadata>(root,
            TagView.FieldOffset<SEntity>("H83bfb495.LinksD24ByteRowsConfigAt0"), ActorDescriptorMetadata.Class))
            RetagPointer(ref TagView.Edit<ActorDescriptorMetadata>(root, row).Config);
        if (construction != 1 || links < 2 || Contains(root, TemplateTag) || Contains(config, TemplateTag))
            throw new NotSupportedException("The weapon root has an unclassified point-provider link.");
        return new(new(name + ".points", config,
            new(packageId, entry.Reference, entry.TypeInfo, name + " points", entryIndex), TemplateTag),
            root, resultNames);

        void Pair(ref TagOffsetPointer primary, ref TagOffsetPointer resource, uint primaryClass, uint resourceClass)
        {
            int primaryAt = TagView.OffsetOf(config, in primary), resourceAt = TagView.OffsetOf(config, in resource);
            if (primary.Tag != TemplateTag || primary.Word != resourceClass || primary.Offset != resourceAt
                || resource.Tag != TemplateTag || resource.Word != primaryClass || resource.Offset != primaryAt)
                throw new InvalidDataException("Weapon point owner references are not reciprocal.");
            primary = primary with { Tag = tag };
            resource = resource with { Tag = tag };
        }
        void RetagJoins(int field)
        {
            foreach (int row in WeaponTagRows.Starts<SEntityBindingRow>(root, field, SEntityBindingRow.Class))
            {
                ref var join = ref TagView.Edit<SEntityBindingRow>(root, row);
                RetagPointer(ref join.ConfigA);
                RetagPointer(ref join.ConfigB);
            }
        }
        void RetagPointer(ref TagOffsetPointer pointer)
        {
            uint value = pointer.Tag;
            Retag(ref value, false);
            pointer = pointer with { Tag = value };
        }
        void Retag(ref uint value, bool builds)
        {
            if (value != TemplateTag) return;
            value = tag; links++;
            if (builds) construction++;
        }
    }

    /**
     * The name itself, or the first numbered form of it, that sorts after every checked template row.
     * Template rows then keep their stock row indices.
     */
    public static string AfterTemplateRows(string name)
    {
        for (int suffix = 0; suffix < 1_000_000; suffix++)
        {
            string candidate = suffix == 0 ? name : $"{name}.{suffix}";
            if (StringBankWriter.Hash(candidate) > LastTemplateHash) return candidate;
        }
        throw new InvalidDataException($"No form of the point name {name} sorts after the stock points.");
    }

    /**
     * Adds points to a weapon's own provider; a point whose name is already there moves instead.
     * @param config The provider config the weapon owns.
     * @param retire Name hashes of earlier points to remove, unless a new point has the same name.
     * @return The provider bytes and each point's name hash.
     */
    public static (byte[] Config, IReadOnlyDictionary<string, uint> NameHashes) SetPoints(ReadOnlyMemory<byte> config,
        IReadOnlyList<WeaponAttachmentInput> points, IReadOnlySet<uint>? retire = null)
    {
        byte[] output = config.ToArray();
        if (retire is not null)
            RemoveRows(output, retire.Except(points.Select(Hash)).ToHashSet());
        var names = new Dictionary<string, uint>();
        foreach (var point in points)
        {
            uint hash = Hash(point);
            byte[] row = Row(point, hash);
            int existing = Array.Find(PointRows(output, PointsField(output)), at => Point(output, at).NameHashSortedAscending == hash);
            if (existing > 0) row.CopyTo(output, existing);
            else output = InvestmentItemClone.AppendRow(output, PointsField(output), Unsafe.SizeOf<SWeaponAttachmentPoint>(), row);
            names[point.Name] = hash;
        }
        Sort(output);
        return (Compact(output), names);
    }

    /** Moves the provider row named hash to the point's bone and transform; the row keeps its name. */
    public static byte[] MovePoint(ReadOnlyMemory<byte> config, uint hash, WeaponAttachmentInput point)
    {
        Validate(point);
        byte[] output = config.ToArray();
        int row = Array.Find(PointRows(output, PointsField(output)), at => Point(output, at).NameHashSortedAscending == hash);
        if (row <= 0) throw new InvalidDataException($"The weapon point provider has no point {hash:X8} to move.");
        Transform(Row(point, Point(output, row).NameHashSortedAscending), 0).CopyTo(Transform(output, row));
        Sort(output);
        return Compact(output);
    }

    /**
     * Moves every barrel point row named hash to the point's bone and transform.
     * Barrel sets are arrays of 64-byte point rows of class 0x80809C00 inside the weapon's firing config.
     * @return The config bytes and the number of rows moved.
     */
    public static (byte[] Config, int Moved) MoveBarrelPoints(ReadOnlyMemory<byte> config, uint hash, WeaponAttachmentInput point)
    {
        Validate(point);
        byte[] output = config.ToArray();
        int[] rows = BarrelRows(output, hash);
        // Barrel point rows have the same layout as provider rows.
        foreach (int row in rows)
            Transform(Row(point, TagView.At<SWeaponBarrelPoint>(output, row).NameHash), 0).CopyTo(BarrelTransform(output, row));
        return (output, rows.Length);
    }

    /** Copies the stock transform of the provider row named hash back; the row keeps its name. */
    public static byte[] RestorePoint(ReadOnlyMemory<byte> config, uint hash, ReadOnlyMemory<byte> stockConfig)
    {
        byte[] output = config.ToArray(), stock = stockConfig.ToArray();
        int to = Array.Find(PointRows(output, PointsField(output)), at => Point(output, at).NameHashSortedAscending == hash);
        int from = Array.Find(PointRows(stock, PointsField(stock)), at => Point(stock, at).NameHashSortedAscending == hash);
        if (to <= 0 || from <= 0) throw new InvalidDataException($"The weapon point provider has no point {hash:X8} to restore.");
        Transform(stock, from).CopyTo(Transform(output, to));
        return output;
    }

    /**
     * Copies the stock transforms of the barrel point rows named hash back, pairing rows in order.
     * @return The config bytes and the number of rows that changed.
     */
    public static (byte[] Config, int Restored) RestoreBarrelPoints(ReadOnlyMemory<byte> config, uint hash,
        ReadOnlyMemory<byte> stockConfig)
    {
        byte[] output = config.ToArray(), stock = stockConfig.ToArray();
        int[] rows = BarrelRows(output, hash), stockRows = BarrelRows(stock, hash);
        if (rows.Length != stockRows.Length)
            throw new InvalidDataException($"The barrel points {hash:X8} no longer match the base weapon.");
        int restored = 0;
        for (int index = 0; index < rows.Length; index++)
        {
            var source = BarrelTransform(stock, stockRows[index]);
            if (source.SequenceEqual(BarrelTransform(output, rows[index]))) continue;
            source.CopyTo(BarrelTransform(output, rows[index]));
            restored++;
        }
        return (output, restored);
    }

    /** Offsets of the barrel point rows named hash, in file order. */
    private static int[] BarrelRows(byte[] bytes, uint hash)
    {
        var rows = new List<int>();
        int stride = Unsafe.SizeOf<SWeaponBarrelPoint>();
        for (int at = TagView.ClassWordSize; at <= bytes.Length - TagArray.HeaderSize; at += 4)
        {
            var header = new TagArrayHeader(bytes, at);
            if (!TagArray.MarkerAt(bytes, at) || header.ElementClass != SWeaponBarrelPoint.Class) continue;
            long count = header.Count;
            if (count is <= 0 or > 64 || header.DataOffset + count * stride > bytes.Length) continue;
            for (int index = 0; index < count; index++)
            {
                int row = (int)header.DataOffset + index * stride;
                if (TagView.At<SWeaponBarrelPoint>(bytes, row).NameHash == hash) rows.Add(row);
            }
        }
        return rows.ToArray();
    }

    /** Bone, rotation, translation and scale of a provider row; the eligibility word and the name are outside it. */
    private static Span<byte> Transform(byte[] bytes, int row)
    {
        ref readonly var point = ref TagView.At<SWeaponAttachmentPoint>(bytes, row);
        int start = TagView.OffsetOf(bytes, in point.BoneIndex04);
        return bytes.AsSpan(start, TagView.OffsetOf(bytes, in point.NameHashSortedAscending) - start);
    }

    /** Bone, rotation, translation and scale of a barrel point row. */
    private static Span<byte> BarrelTransform(byte[] bytes, int row)
    {
        ref readonly var point = ref TagView.At<SWeaponBarrelPoint>(bytes, row);
        int start = TagView.OffsetOf(bytes, in point.BoneIndex);
        return bytes.AsSpan(start, TagView.OffsetOf(bytes, in point.NameHash) - start);
    }

    /**
     * Refuses a template that Compact cannot rebuild without loss: it must hold its own size, keep its
     * owner classes and already be in the stock layout, with the table naming an object before the rows.
     * @param config The provider template bytes.
     */
    private static void ValidateTemplate(byte[] config)
    {
        ref readonly var provider = ref TagView.Checked<SEntityConfig>(config, 0);
        if ((long)provider.TagByteLength != config.Length)
            throw new InvalidDataException("The weapon point provider does not hold its own size.");
        int owner = Target(config, in provider.DefaultInstance), source = Target(config, in provider.Definition);
        if (TagView.ClassAt(config, owner) != C80808506.Class || !TagView.Fits<C80808506>(config, owner)
            || TagView.ClassAt(config, source) != C80808507.Class || !TagView.Fits<C80808507>(config, source))
            throw new InvalidDataException("The weapon point provider's instance or definition changed class.");
        int pointSize = Unsafe.SizeOf<SWeaponAttachmentPoint>(), linkSize = Unsafe.SizeOf<PlacedConfigComponentRow>();
        int rowsHeader = StockRowsHeader(config);
        var points = TagArray.Checked(config, PointsField(config), SWeaponAttachmentPoint.Class, pointSize);
        if (points.Count == 0 || points.Data != rowsHeader + TagArray.HeaderSize)
            throw new InvalidDataException("The weapon point rows do not directly follow the eligibility list.");
        var links = TagArray.Checked(config, TagView.OffsetOf(config, in provider.ComponentLinks), PlacedConfigComponentRow.Class, linkSize);
        if (links.Count != 1
            || links.Data - TagArray.HeaderSize != TagView.BodyAfter(points.Data + points.Count * pointSize, TagArray.HeaderAlignment)
            || links.Data + linkSize != config.Length)
            throw new InvalidDataException("The interface table does not directly follow the point rows and end the provider.");
        int? target = TagView.TargetOffset(config, in TagView.At<PlacedConfigComponentRow>(config, links.Data).ComponentHeader);
        if (target is not { } component || component < 0 || component >= rowsHeader)
            throw new InvalidDataException("The interface table names an object at or after the point rows.");
    }

    /**
     * Rewrites the provider in the stock layout: rows directly after the eligibility list, then the
     * interface table, and nothing after it.
     */
    private static byte[] Compact(byte[] config)
    {
        int pointsField = PointsField(config);
        int[] rows = PointRows(config, pointsField);
        int pointSize = Unsafe.SizeOf<SWeaponAttachmentPoint>(), linkSize = Unsafe.SizeOf<PlacedConfigComponentRow>();
        int linksField = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.ComponentLinks));
        ref readonly var links = ref TagView.At<TagArray<PlacedConfigComponentRow>>(config, linksField);
        int table = TagView.OffsetOf(config, in links.Relative) + (int)links.Relative;
        var tableHeader = new TagArrayHeader(config, table);
        if (links.Count != 1 || table < TagView.ClassWordSize || table > config.Length - TagArray.HeaderSize - linkSize
            || !TagArray.MarkerAt(config, table) || tableHeader.Count != 1 || tableHeader.ElementClass != PlacedConfigComponentRow.Class)
            throw new InvalidDataException(UncheckedLayout);
        int rowsHeader = StockRowsHeader(config);
        if (TagRead.U32(config, rowsHeader - TagView.ClassWordSize) != TagArray.Marker
            || TagRead.U32(config, rowsHeader + TagArray.ElementClassOffset) != SWeaponAttachmentPoint.Class)
            throw new InvalidDataException(UncheckedLayout);
        ref readonly var link = ref TagView.At<PlacedConfigComponentRow>(config, table + TagArray.HeaderSize);
        int target = TagView.OffsetOf(config, in link.ComponentHeader) + (int)link.ComponentHeader.Relative;
        int newTable = TagView.BodyAfter(rowsHeader + TagArray.HeaderSize + rows.Length * pointSize, TagArray.HeaderAlignment);
        byte[] output = new byte[newTable + TagArray.HeaderSize + linkSize];
        config.AsSpan(0, rowsHeader + TagArray.HeaderSize).CopyTo(output);
        for (int index = 0; index < rows.Length; index++)
            config.AsSpan(rows[index], pointSize).CopyTo(output.AsSpan(rowsHeader + TagArray.HeaderSize + index * pointSize));
        TagView.Edit<STypedArrayMarker>(output, rowsHeader).Count = (ulong)rows.Length;
        TagArray.WriteField(output, pointsField, rows.Length, rowsHeader);
        TagArray.WriteHeader(output, newTable, 1, PlacedConfigComponentRow.Class);
        ref var newLink = ref TagView.Edit<PlacedConfigComponentRow>(output, newTable + TagArray.HeaderSize);
        TagView.SetTarget(output, in newLink.ComponentHeader, target);
        newLink.ComponentClass = link.ComponentClass;
        newLink.FourthWord = link.FourthWord;
        TagArray.WriteField(output, linksField, 1, newTable);
        TagView.Edit<SEntityConfig>(output).TagByteLength = (ulong)output.Length;
        return output;
    }

    /** Removes the named rows; the kept rows stay in order at the front of the same array. */
    private static void RemoveRows(byte[] config, IReadOnlySet<uint> hashes)
    {
        int pointsField = PointsField(config), pointSize = Unsafe.SizeOf<SWeaponAttachmentPoint>();
        int[] rows = PointRows(config, pointsField);
        byte[][] kept = rows.Where(row => !hashes.Contains(Point(config, row).NameHashSortedAscending))
            .Select(row => config.AsSpan(row, pointSize).ToArray()).ToArray();
        if (kept.Length == rows.Length) return;
        if (kept.Length == 0) throw new InvalidDataException("A weapon point provider cannot lose every point.");
        for (int index = 0; index < rows.Length; index++)
            if (index < kept.Length) kept[index].CopyTo(config, rows[index]);
            else config.AsSpan(rows[index], pointSize).Clear();
        int header = rows[0] - TagArray.HeaderSize;
        TagView.Edit<STypedArrayMarker>(config, header).Count = (ulong)kept.Length;
        TagArray.WriteField(config, pointsField, kept.Length, header);
    }

    /** Refuses a point without a weapon bone or with a transform that is not finite and uniform. */
    private static void Validate(WeaponAttachmentInput point)
    {
        if (string.IsNullOrWhiteSpace(point.Name) || point.Name.Length > 200
            || point.BoneIndex is < 0 or >= 5 || !Finite(point.Translation)
            || !float.IsFinite(point.Scale) || point.Scale <= 0
            || !float.IsFinite(point.Rotation.LengthSquared())
            || MathF.Abs(point.Rotation.LengthSquared() - 1) > .001f)
            throw new InvalidDataException("Weapon points need a valid bone and finite uniform transform.");
    }

    private static uint Hash(WeaponAttachmentInput point)
    {
        Validate(point);
        // Authored points sort after the template rows, so those keep their stock indices.
        uint hash = StringBankWriter.Hash(AfterTemplateRows(point.Name));
        if (hash is 0 or uint.MaxValue or StringHash.Empty)
            throw new InvalidDataException("A weapon attachment point name is empty.");
        return hash;
    }

    private static byte[] Row(WeaponAttachmentInput point, uint hash)
    {
        byte[] row = new byte[Unsafe.SizeOf<SWeaponAttachmentPoint>()];
        ref var value = ref TagView.Edit<SWeaponAttachmentPoint>(row);
        value.BoneIndex04 = checked((uint)point.BoneIndex);
        value.Rotation = point.Rotation;
        value.Translation = point.Translation;
        value.Scale = point.Scale;
        value.NameHashSortedAscending = hash;
        return row;
    }

    /** Point lookups search the rows by name hash, so they stay sorted. */
    private static void Sort(byte[] config)
    {
        int pointSize = Unsafe.SizeOf<SWeaponAttachmentPoint>();
        int[] rows = PointRows(config, PointsField(config));
        byte[][] sorted = rows.Select(row => config.AsSpan(row, pointSize).ToArray())
            .OrderBy(row => TagView.At<SWeaponAttachmentPoint>(row).NameHashSortedAscending).ToArray();
        for (int index = 0; index < rows.Length; index++) sorted[index].CopyTo(config, rows[index]);
    }

    /** The point array field, inside the provider's definition. */
    private static int PointsField(byte[] config) =>
        Definition(config) + TagView.FieldOffset<C80808507>("H09da30c1.H7bf0a1ea.Points");

    /**
     * The stock point array header: the first header slot after the eligibility list's rows.
     * Only the point array moves, so every provider made from the template has the same slot.
     */
    private static int StockRowsHeader(byte[] config)
    {
        int field = Definition(config) + TagView.FieldOffset<C80808507>(nameof(C80808507.H7ee1fa95));
        int stride = Unsafe.SizeOf<UnknownLogicalPart>();
        if (!TagArray.TryChecked(config, field, UnknownLogicalPart.Class, stride, out var eligibility) || eligibility.Count == 0)
            throw new InvalidDataException(UncheckedLayout);
        return TagView.BodyAfter(eligibility.Data + eligibility.Count * stride, TagArray.HeaderAlignment);
    }

    private static int Definition(byte[] config) =>
        Target(config, in TagView.At<RelativePointer<Unknown>>(config, TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition))));

    private static int[] PointRows(byte[] config, int field) =>
        WeaponTagRows.Starts<SWeaponAttachmentPoint>(config, field, SWeaponAttachmentPoint.Class);

    private static ref readonly SWeaponAttachmentPoint Point(byte[] config, int row) =>
        ref TagView.At<SWeaponAttachmentPoint>(config, row);

    private static int Target(byte[] config, ref readonly RelativePointer<Unknown> pointer) =>
        TagView.TargetOffset(config, in pointer) ?? throw new InvalidDataException(UncheckedLayout);

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Contains(byte[] bytes, uint value)
    {
        for (int offset = 0; offset <= bytes.Length - 4; offset += 4)
            if (TagRead.U32(bytes, offset) == value) return true;
        return false;
    }
}
