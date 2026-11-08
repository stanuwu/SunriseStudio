using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public sealed record InvestmentVendorBudget(int Vendors, long Sales, long Categories);
public sealed record InvestmentVendorInteraction(uint Tag, uint VendorHash, int HashOffset, string SourceSha256);

/** Derived vendors retain base policy fields while their indexed identity and display text are authored. */
public static class InvestmentVendorAuthoring
{
    public const uint DisplayClass = InvestmentTags.VendorDisplayClass, IndexClass = SVendorIndex.Class, DisplayIndexClass = SVendorDisplayIndex.Class;
    public const uint IndexRowClass = VendorIndexRow.Class, DisplayIndexRowClass = C808058ec.Class;
    public const uint InteractionPrimaryClass = SVendorInteractionPrimary.Class, InteractionResourceClass = SVendorInteractionResource.Class;
    /** Native hash scans increment a signed 16-bit cursor, so 32768 rows would wrap. */
    public const int MaximumVendors = short.MaxValue;
    /** These aggregate limits match the runtime catalog's bounded row banks. */
    public const int MaximumSales = 16384, MaximumCategories = 4096, MaximumCosts = 4;
    /** Display string and icon fields; SetText and ReadText take the first two. */
    public static readonly int NameOffset = TagView.FieldOffset<SInvestmentVendorDisplay>("DisplayProperties.NameBank"),
        SubtitleOffset = TagView.FieldOffset<SInvestmentVendorDisplay>("DisplayProperties.SubtitleBank"),
        IconOffset = TagView.FieldOffset<SInvestmentVendorDisplay>("DisplayProperties.IconIndex");
    private const uint ConfigClass = SEntityConfig.Class;
    private static readonly int IndexField = TagView.FieldOffset<SVendorIndex>(nameof(SVendorIndex.Table));
    private static readonly int DisplayIndexField = TagView.FieldOffset<SVendorDisplayIndex>(nameof(SVendorDisplayIndex.Table));
    private static readonly int IndexStride = Unsafe.SizeOf<VendorIndexRow>(), DisplayIndexStride = Unsafe.SizeOf<C808058ec>();
    private static readonly int ConfigSizeField = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.TagByteLength));
    private static readonly int PrimaryField = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.DefaultInstance));
    private static readonly int ResourceField = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition));
    private static readonly int PrimaryOwnerField = TagView.FieldOffset<SVendorInteractionPrimary>(nameof(SVendorInteractionPrimary.OwnerConfig));
    private static readonly int ActionsField = TagView.FieldOffset<SInvestmentVendor>(nameof(SInvestmentVendor.Actions)),
        InteractionsField = TagView.FieldOffset<SInvestmentVendor>(nameof(SInvestmentVendor.Interactions)),
        PolicyField = TagView.FieldOffset<SInvestmentVendor>(nameof(SInvestmentVendor.AdditionalPolicy96));

    /** Both indices must retain one unique hash at each matching native row position. */
    public static InvestmentVendorBudget ReadBudget(PackageCatalog catalog, InvestmentSources? sources = null,
        CancellationToken token = default)
    {
        sources ??= new();
        RequireClass(catalog, sources.Vendors, IndexClass);
        RequireClass(catalog, sources.VendorDisplays, DisplayIndexClass);
        byte[] gameplay = catalog.Read(sources.Vendors), display = catalog.Read(sources.VendorDisplays);
        var definitions = TagArray.Checked(gameplay, IndexField, IndexRowClass, IndexStride);
        var displays = TagArray.Checked(display, DisplayIndexField, DisplayIndexRowClass, DisplayIndexStride);
        if (definitions.Count != displays.Count || definitions.Count > MaximumVendors)
            throw new InvalidDataException("Vendor index counts do not fit their paired native range.");
        HashSet<uint> seen = [];
        long sales = 0, categories = 0;
        for (int i = 0; i < definitions.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            ref readonly var row = ref TagView.At<VendorIndexRow>(gameplay, definitions.Data + i * IndexStride);
            ref readonly var other = ref TagView.At<C808058ec>(display, displays.Data + i * DisplayIndexStride);
            uint hash = row.VendorHash, tag = row.DefinitionTag;
            if (hash is 0 or uint.MaxValue or StringHash.Empty || !seen.Add(hash) || other.Unk0 != hash)
                throw new InvalidDataException("Vendor index identities are missing, ambiguous or unpaired.");
            RequireClass(catalog, tag, InvestmentCatalog.VendorClass);
            RequireClass(catalog, other.Unk10, DisplayClass);
            byte[] definition = catalog.Read(tag);
            ref readonly var vendor = ref Definition(definition, hash);
            sales += TagArray.Checked(definition, TagView.OffsetOf(definition, in vendor.ItemList),
                SVendorSale.Class, Unsafe.SizeOf<SVendorSale>()).Count;
            categories += TagArray.Checked(definition, TagView.OffsetOf(definition, in vendor.CategoryList),
                VendorInstalledRow.Class, Unsafe.SizeOf<VendorInstalledRow>()).Count;
        }
        return new(definitions.Count, sales, categories);
    }

    public static void ValidateBudget(InvestmentVendorBudget current, int addedVendors, int addedSales, int addedCategories)
    {
        if (addedVendors < 0 || current.Vendors + (long)addedVendors > MaximumVendors)
            throw new InvalidDataException($"The native vendor index supports at most {MaximumVendors:N0} rows.");
        if (current.Sales + addedSales is < 0 or > MaximumSales)
            throw new InvalidDataException($"The runtime catalog supports at most {MaximumSales:N0} vendor sale rows in total.");
        if (current.Categories + addedCategories is < 0 or > MaximumCategories)
            throw new InvalidDataException($"The runtime catalog supports at most {MaximumCategories:N0} vendor category rows in total.");
    }

    /** Unknown non-stock policies may retain sale ordinals, so only empty-policy roots permit shifts. */
    public static void ValidateSaleOrder(ReadOnlyMemory<byte> bytes, int sourceCount, IReadOnlyList<int> sourceIndices)
    {
        bool shifts = sourceIndices.Count < sourceCount
            || Enumerable.Range(0, Math.Min(sourceCount, sourceIndices.Count)).Any(index => sourceIndices[index] != index);
        if (!shifts) return;
        var span = bytes.Span;
        if (!TagView.FitsThrough<SInvestmentVendor>(span, 0, "AdditionalPolicy96.Count"))
            throw new InvalidDataException("Vendor definition is truncated.");
        if (TagRead.I64(span, ActionsField) != 0 || TagRead.I64(span, InteractionsField) != 0 || TagRead.I64(span, PolicyField) != 0)
            throw new NotSupportedException("This base has additional policies whose sale references are not decoded. Keep its sale order or choose a base without those policies.");
    }

    /** Only the verified primary identity changes; the distinct secondary key and base policies remain. */
    public static (byte[] Definition, byte[] Display) Derive(ReadOnlyMemory<byte> definition,
        ReadOnlyMemory<byte> display, uint sourceHash, uint hash)
    {
        Definition(definition.Span, sourceHash);
        Display(display.Span, sourceHash);
        if (hash is 0 or uint.MaxValue or StringHash.Empty || hash == sourceHash)
            throw new InvalidDataException("Choose a new nonempty vendor identity.");
        byte[] nextDefinition = definition.ToArray(), nextDisplay = display.ToArray();
        TagView.Edit<SInvestmentVendor>(nextDefinition).VendorHash = hash;
        TagView.Edit<SInvestmentVendorDisplay>(nextDisplay).VendorHash = hash;
        return (nextDefinition, nextDisplay);
    }

    /** Display strings preserve the adjacent opaque 16 bits and replace only bank and hash. */
    public static byte[] SetText(ReadOnlyMemory<byte> display, uint identity, int offset, int bank, uint hash)
    {
        Display(display.Span, identity);
        if (offset != NameOffset && offset != SubtitleOffset || bank is < 0 or > short.MaxValue)
            throw new InvalidDataException("The vendor string target is outside its native range.");
        byte[] result = display.ToArray();
        ref var text = ref TagView.Edit<SInvestmentVendorDisplay>(result).DisplayProperties;
        if (offset == NameOffset) { text.NameBank = checked((ushort)bank); text.NameHash = hash; }
        else { text.SubtitleBank = checked((ushort)bank); text.SubtitleHash = hash; }
        return result;
    }

    public static (ushort Bank, uint Hash) ReadText(ReadOnlyMemory<byte> display, uint identity, int offset)
    {
        ref readonly var text = ref Display(display.Span, identity).DisplayProperties;
        if (offset != NameOffset && offset != SubtitleOffset) throw new ArgumentOutOfRangeException(nameof(offset));
        return offset == NameOffset ? (text.NameBank, text.NameHash) : (text.SubtitleBank, text.SubtitleHash);
    }

    public static byte[] CopyIcon(ReadOnlyMemory<byte> display, uint identity,
        ReadOnlyMemory<byte> source, uint sourceIdentity)
    {
        Display(display.Span, identity);
        ushort value = Display(source.Span, sourceIdentity).DisplayProperties.IconIndex;
        byte[] result = display.ToArray();
        TagView.Edit<SInvestmentVendorDisplay>(result).DisplayProperties.IconIndex = value;
        return result;
    }

    /** Reciprocal config links prove which resource the native activation handler reads. */
    public static InvestmentVendorInteraction ReadInteraction(PackageCatalog catalog, uint tag, ReadOnlyMemory<byte> bytes)
    {
        RequireClass(catalog, tag, ConfigClass);
        var data = bytes.Span;
        // The config is read only as far as its two instance pointers.
        if (!TagView.FitsThrough<SEntityConfig>(data, 0, nameof(SEntityConfig.Definition))
            || TagRead.U64(data, ConfigSizeField) != (ulong)bytes.Length)
            throw new InvalidDataException("Interaction config size is invalid.");
        int primary = TagPointer.Checked(data, PrimaryField), resource = TagPointer.Checked(data, ResourceField);
        if (primary < 4 || resource < 4
            || !TagView.FitsThrough<SVendorInteractionPrimary>(data, primary, nameof(SVendorInteractionPrimary.OwnerConfig))
            || !TagView.Fits<SVendorInteractionResource>(data, resource))
            throw new InvalidDataException("Choose a verified vendor interaction config.");
        var owner = TagOffsetPointer.Read(data, primary + PrimaryOwnerField);
        ref readonly var target = ref TagView.At<SVendorInteractionResource>(data, resource);
        if (TagView.ClassAt(data, primary) != InteractionPrimaryClass
            || TagView.ClassAt(data, resource) != InteractionResourceClass
            || owner.Tag != tag || owner.Word != InteractionResourceClass || owner.Offset != resource
            || target.OwnerConfig.Tag != tag || target.OwnerConfig.Word != InteractionPrimaryClass
            || target.OwnerConfig.Offset != primary)
            throw new InvalidDataException("Choose a verified vendor interaction config.");
        int offset = TagView.OffsetOf(data, in target.VendorHash);
        return new(tag, target.VendorHash, offset, Convert.ToHexString(SHA256.HashData(bytes.Span)));
    }

    public static byte[] RetargetInteraction(PackageCatalog catalog, ReadOnlyMemory<byte> bytes,
        InvestmentVendorInteraction target, uint hash)
    {
        if (ReadInteraction(catalog, target.Tag, bytes) != target)
            throw new InvalidDataException("The interaction changed. Choose it again.");
        if (hash is 0 or uint.MaxValue or StringHash.Empty) throw new InvalidDataException("Vendor identity is empty.");
        byte[] result = bytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(target.HashOffset), hash);
        return result;
    }

    private static ref readonly SInvestmentVendor Definition(ReadOnlySpan<byte> bytes, uint identity)
    {
        if (!TagView.Fits<SInvestmentVendor>(bytes, 0) || TagView.At<SInvestmentVendor>(bytes).TagSize != (ulong)bytes.Length
            || TagView.At<SInvestmentVendor>(bytes).VendorHash != identity)
            throw new InvalidDataException("Vendor definition identity or size differs from its index.");
        return ref TagView.At<SInvestmentVendor>(bytes);
    }

    private static ref readonly SInvestmentVendorDisplay Display(ReadOnlySpan<byte> bytes, uint identity)
    {
        if (!TagView.Fits<SInvestmentVendorDisplay>(bytes, 0) || TagView.At<SInvestmentVendorDisplay>(bytes).TagSize != (ulong)bytes.Length
            || TagView.At<SInvestmentVendorDisplay>(bytes).VendorHash != identity)
            throw new InvalidDataException("Vendor display identity or size differs from its index.");
        return ref TagView.At<SInvestmentVendorDisplay>(bytes);
    }

    private static void RequireClass(PackageCatalog catalog, uint tag, uint expected)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Type is not (8 or 16) || entry.Reference != expected)
            throw new InvalidDataException("Vendor asset has an unexpected native class.");
    }
}
