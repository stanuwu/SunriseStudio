using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Projects;

public static partial class ProjectBuilder
{
    static partial void WriteMergeSidecars(MergeSidecarRequest request) =>
        request.Written.AddRange(MergeSidecarPlanner.Write(request.Catalog, request.BuildRoot, request.Artifacts,
            request.Edited, request.Added, request.Manifests));
}

/** Describes each built package's entries for the runtime table merge and writes `<stem>.merge` beside it. */
internal static class MergeSidecarPlanner
{
    public const string DumpName = "merge-sidecars.json";
    // A self-relative pointer lands on a typed object, whose class word sits just before it.
    private const uint ClassWordMask = 0xFFFF0000, ClassWordPrefix = 0x80800000;
    // Array field and header words, from the generated layout.
    private static readonly int ArrayRelative = TagView.FieldOffset<TagArray<byte>>(nameof(TagArray<byte>.Relative));
    private static readonly int ArrayField = Unsafe.SizeOf<TagArray<byte>>();
    // A header's count word ends where its element class starts.
    private const int HeaderCount = TagArray.ElementClassOffset;
    private const int ElementWords = TagArray.HeaderSize - TagArray.ElementClassOffset;

    /**
     * Writes one sidecar per built package, except `_unp1_` replacements, then the readable dump.
     * @return Each sidecar with its package and SHA-256, in package path order.
     */
    public static IReadOnlyList<ProjectBuilder.MergeSidecarArtifact> Write(PackageCatalog catalog, string buildRoot,
        IReadOnlyList<PackageBuildArtifact> artifacts, IReadOnlyDictionary<uint, byte[]> edited,
        IReadOnlyDictionary<uint, byte[]> added, IReadOnlySet<uint> manifests)
    {
        var context = new Context(catalog, edited);
        var written = new List<ProjectBuilder.MergeSidecarArtifact>();
        var dump = new List<object>();
        var planned = new List<(PackageBuildArtifact Artifact, byte[] Digest, List<EntryPlan> Plans)>();
        foreach (var artifact in artifacts.OrderBy(artifact => artifact.Path, StringComparer.Ordinal))
        {
            if (Path.GetFileName(artifact.Path).Contains("_unp1_", StringComparison.OrdinalIgnoreCase)) continue;
            var plans = new List<EntryPlan>();
            if (catalog.ContainsInstalledPackage(artifact.PackageId))
            {
                foreach (var (tag, bytes) in edited.OrderBy(pair => pair.Key))
                    if (catalog.TryGetEntry(tag, out var entry) && entry.PackageId == artifact.PackageId)
                        plans.Add(PlanInstalled(context, entry, bytes, manifests.Contains(tag)));
            }
            else
                foreach (var (tag, bytes) in added.OrderBy(pair => pair.Key))
                {
                    if ((ushort)TagHashes.Package(tag) != artifact.PackageId || bytes.Length == 0) continue;
                    int index = TagHashes.Entry(tag);
                    if (index >= artifact.EntryClasses.Length) throw new InvalidDataException($"Added entry {tag:X8} is not in its package.");
                    var plan = new EntryPlan(tag, (uint)index, artifact.EntryClasses[index], bytes, []) { Kind = SidecarEntryKind.Added };
                    FindSites(context, plan);
                    if (plan.Sites.Count > 0) plans.Add(plan);
                }
            foreach (var plan in plans)
                if (plan.Index >= artifact.EntrySizes.Length || artifact.EntrySizes[plan.Index] != plan.Built.Length)
                    throw new InvalidDataException($"Entry {plan.Tag:X8} in {Path.GetFileName(artifact.Path)} differs from the build.");
            planned.Add((artifact, artifact.MetadataDigest, plans));
        }
        var pairs = CheckPairGroups(catalog, planned.SelectMany(item => item.Plans));
        foreach (var (artifact, digest, plans) in planned)
        {
            var file = Assemble(context, artifact, digest, plans);
            byte[] sidecar = MergeSidecarFormat.Write(file);
            string path = Path.ChangeExtension(artifact.Path, MergeSidecarFormat.Extension);
            ProjectPaths.AtomicWrite(path, sidecar);
            written.Add(new(path, artifact.Path, Convert.ToHexString(SHA256.HashData(sidecar))));
            dump.Add(Describe(artifact, path, file, plans));
        }
        ProjectPaths.AtomicWrite(Path.Combine(buildRoot, DumpName), JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = MergeSidecarFormat.Version,
            bankIndexStockAscending = context.BankAscending,
            pairGroups = pairs,
            sidecars = dump
        }, ProjectJson.Options));
        return written;
    }

    private sealed class EntryPlan(uint tag, uint index, uint entryClass, byte[] built, byte[] stock)
    {
        public uint Tag { get; } = tag;
        public uint Index { get; } = index;
        public uint Class { get; } = entryClass;
        public byte[] Built { get; } = built;
        public byte[] Stock { get; } = stock;
        public SidecarEntryKind Kind { get; set; }
        public string? Recipe { get; set; }
        public string? Reason { get; set; }
        public List<SidecarDiff> Diffs { get; } = [];
        public List<OpPlan> Ops { get; } = [];
        public SortedDictionary<uint, SiteHit> Sites { get; } = [];
        public List<SidecarTarget> Growth { get; } = [];
        public long? Unreached { get; set; }
    }

    private sealed record OpPlan(string Name, MergeOpKind Kind, int Descriptor, uint ParentOp, uint ParentRow, MergeShape Row,
        uint StockCount, uint ModCount, uint Limit, string? PairGroup, byte Flags, int KeyOffset);

    private readonly record struct SiteHit(uint Offset, int Width, bool Signed, uint Table, int Array, bool OpLocal, string Rule);

    private readonly record struct View(int Count, int Header, int Stride)
    {
        public int Rows => Header + TagArray.HeaderSize;
    }

    /** A table that cannot be described exactly; its entry is written as opaque with this reason. */
    private sealed class NotMergeable(string reason) : Exception(reason);

    private sealed class Context(PackageCatalog catalog, IReadOnlyDictionary<uint, byte[]> edited)
    {
        private readonly Dictionary<(uint, int), (uint Stock, uint Mod)> counts = [];
        public PackageCatalog Catalog { get; } = catalog;
        public bool? BankAscending { get; set; }

        /** Row counts of a target array before and after this build; both 0 when it is not installed. */
        public (uint Stock, uint Mod) Counts(uint table, int descriptor)
        {
            if (counts.TryGetValue((table, descriptor), out var known)) return known;
            // An entry too short for a descriptor is not a table, so nothing can name its rows.
            if (!Catalog.TryGetEntry(table, out var entry) || entry.Size < descriptor + ArrayField) return counts[(table, descriptor)] = (0, 0);
            byte[] stock = Catalog.ReadOriginal(table);
            uint before = Count(stock, descriptor);
            uint after = edited.TryGetValue(table, out var built) ? Count(built, descriptor) : before;
            return counts[(table, descriptor)] = (before, after);
        }

        private static uint Count(byte[] bytes, int descriptor)
        {
            if (descriptor < 0 || descriptor + ArrayField > bytes.Length || I64(bytes, descriptor) is < 0 or > uint.MaxValue)
                throw new InvalidDataException("A merge target array has no readable count.");
            return (uint)I64(bytes, descriptor);
        }
    }

    /** Classifies one edited installed entry as manifest, table or opaque, then finds its sites. */
    private static EntryPlan PlanInstalled(Context context, PackageEntry entry, byte[] built, bool manifest)
    {
        byte[] stock = context.Catalog.ReadOriginal(entry.TagHash);
        var plan = new EntryPlan(entry.TagHash, (uint)entry.EntryIndex, entry.Reference, built, stock);
        if (manifest && entry.Reference == PackageManifest.ClassId) plan.Kind = SidecarEntryKind.Manifest;
        else if (TableMergeRecipes.Tables.TryGetValue(entry.TagHash, out var recipe))
        {
            plan.Recipe = recipe.Name;
            try { ClassifyTable(context, plan, recipe); plan.Kind = SidecarEntryKind.Table; }
            catch (NotMergeable reason)
            {
                plan.Kind = SidecarEntryKind.Opaque; plan.Reason = reason.Message;
                plan.Diffs.Clear(); plan.Ops.Clear(); plan.Unreached = null;
                RecordGrowth(plan, recipe);
            }
        }
        else if (built.Length == stock.Length)
        {
            // A same-size edit ships its old bytes, so two mods' edits to other words both survive.
            plan.Kind = SidecarEntryKind.Table;
            var structural = I64(stock, 0) == stock.Length ? new List<(int, int)> { (0, TagView.SizeWordSize) } : [];
            plan.Diffs.AddRange(Diffs(stock, built, structural, [], insideOnly: false));
        }
        else
        {
            plan.Kind = SidecarEntryKind.Opaque;
            plan.Reason = "its size changed";
        }
        FindSites(context, plan);
        return plan;
    }

    /**
     * An opaque table that grew still names its growth, so the runtime turns the row layer off.
     * A shrunk array names no target; the runtime refuses a target whose count goes down.
     */
    private static void RecordGrowth(EntryPlan plan, MergeRecipe recipe)
    {
        foreach (var array in recipe.Arrays.Where(array => array.Kind == MergeOpKind.Append))
        {
            View? stock, built;
            try
            {
                stock = ReadView(plan.Stock, array.Descriptor, array.Row.Stride);
                built = ReadView(plan.Built, array.Descriptor, array.Row.Stride);
            }
            catch (NotMergeable reason) { throw new InvalidDataException($"Table {plan.Tag:X8} cannot be described: {reason.Message}."); }
            int stockCount = stock?.Count ?? 0, builtCount = built?.Count ?? 0;
            if (builtCount > stockCount)
                plan.Growth.Add(new(plan.Tag, (uint)array.Descriptor, (uint)stockCount, (uint)builtCount));
        }
    }

    /**
     * Refuses a build that adds a different row count to two installed members of one pair group.
     * A member this build does not edit adds 0; a member the install lacks is not checked.
     * @return Each group this build grows, with its row count and members.
     */
    private static List<object> CheckPairGroups(PackageCatalog catalog, IEnumerable<EntryPlan> plans)
    {
        var groups = new SortedDictionary<string, SortedDictionary<(uint Table, int Descriptor), uint>>(StringComparer.Ordinal);
        foreach (var recipe in TableMergeRecipes.Tables.Values.Where(recipe => catalog.TryGetEntry(recipe.Tag, out _)))
            foreach (var array in recipe.Arrays.Where(array => array.PairGroup is not null))
            {
                if (!groups.TryGetValue(array.PairGroup!, out var members)) groups.Add(array.PairGroup!, members = []);
                members[(recipe.Tag, array.Descriptor)] = 0;
            }
        foreach (var plan in plans)
        {
            if (plan.Kind == SidecarEntryKind.Table)
                foreach (var op in plan.Ops.Where(op => op.ParentOp == MergeSidecarFormat.TopLevelOp
                    && op.Kind == MergeOpKind.Append && op.PairGroup is not null))
                    groups[op.PairGroup!][(plan.Tag, op.Descriptor)] = op.ModCount - op.StockCount;
            foreach (var growth in plan.Growth)
                if (TableMergeRecipes.Tables[plan.Tag].Arrays.First(array => array.Descriptor == growth.Descriptor).PairGroup
                    is { } group)
                    groups[group][(plan.Tag, (int)growth.Descriptor)] = growth.ModCount - growth.StockCount;
        }
        var grown = new List<object>();
        foreach (var (group, members) in groups)
        {
            var first = members.First();
            foreach (var (member, rows) in members)
                if (rows != first.Value)
                    throw new InvalidDataException($"Pair group {group} adds {first.Value} rows to {first.Key.Table:X8} but {rows} "
                        + $"to {member.Table:X8}; the game merges these tables row for row.");
            if (first.Value > 0)
                grown.Add(new { group, addedRows = first.Value, tables = members.Keys.Select(member => member.Table.ToString("X8")) });
        }
        return grown;
    }

    /** Fills ops and diffs, or throws NotMergeable naming the first rule the entry breaks. */
    private static void ClassifyTable(Context context, EntryPlan plan, MergeRecipe recipe)
    {
        byte[] stock = plan.Stock, built = plan.Built;
        if (built.Length < stock.Length) throw new NotMergeable("it is smaller than the installed entry");
        bool sizeWord = I64(stock, 0) == stock.Length;
        if (sizeWord && I64(built, 0) != built.Length) throw new NotMergeable("its size word does not match its size");
        var spans = new TableSpans();
        if (sizeWord) spans.Structural.Add((0, TagView.SizeWordSize));
        var (stockViews, builtViews) = ReadViews(plan, recipe, spans);
        foreach (var array in recipe.Arrays)
        {
            ValidateRows(stock, stockViews, array, int.MaxValue);
            ValidateRows(built, builtViews, array, stock.Length);
        }
        BuildOps(context, plan, recipe, stockViews, builtViews);
        NestedOps(plan, recipe, stockViews, builtViews, spans);
        plan.Diffs.AddRange(Diffs(stock, built, spans.Structural, spans.Arrays, insideOnly: true));
        plan.Unreached = Unreached(plan, recipe, builtViews);
    }

    /** Stock ranges the runtime rebuilds: descriptors, header counts and the size word, then whole arrays. */
    private sealed class TableSpans
    {
        public List<(int Begin, int End)> Structural { get; } = [];
        public List<(int Begin, int End)> Arrays { get; } = [];
    }

    /** Reads every recipe array in both copies and records its stock spans. */
    private static (Dictionary<int, View> Stock, Dictionary<int, View> Built) ReadViews(EntryPlan plan, MergeRecipe recipe,
        TableSpans spans)
    {
        var stockViews = new Dictionary<int, View>();
        var builtViews = new Dictionary<int, View>();
        foreach (var array in recipe.Arrays)
        {
            int stride = array.Row.Stride;
            var before = Require(ReadView(plan.Stock, array.Descriptor, stride), $"its installed {array.Name} array is empty");
            var after = Require(ReadView(plan.Built, array.Descriptor, stride), $"its built {array.Name} array is empty");
            if (after.Count < before.Count) throw new NotMergeable($"rows were removed from {array.Name}");
            if (after.Count > array.Limit) throw new NotMergeable($"{array.Name} holds more than {array.Limit} rows");
            if (!plan.Built.AsSpan(after.Header + TagArray.ElementClassOffset, ElementWords)
                .SequenceEqual(plan.Stock.AsSpan(before.Header + TagArray.ElementClassOffset, ElementWords)))
                throw new NotMergeable($"the {array.Name} array changed its row class");
            stockViews[array.Descriptor] = before;
            builtViews[array.Descriptor] = after;
            spans.Structural.Add((array.Descriptor, array.Descriptor + ArrayField));
            spans.Structural.Add((before.Header, before.Header + HeaderCount));
            spans.Arrays.Add((before.Header - TagView.ClassWordSize, before.Rows + before.Count * stride));
        }
        return (stockViews, builtViews);
    }

    /** Adds one top-level op per recipe array, with the key flags the stock keys earn. */
    private static void BuildOps(Context context, EntryPlan plan, MergeRecipe recipe,
        IReadOnlyDictionary<int, View> stockViews, IReadOnlyDictionary<int, View> builtViews)
    {
        foreach (var array in recipe.Arrays)
        {
            var before = stockViews[array.Descriptor];
            var after = builtViews[array.Descriptor];
            byte flags = 0;
            if (array.KeyOffset >= 0)
            {
                var stockKeys = Keys(plan.Stock, before, array);
                bool ascending = StrictlyAscending(stockKeys);
                if (array.Kind == MergeOpKind.SortedMap)
                {
                    if (!ascending) throw new NotMergeable($"the installed {array.Name} keys are not sorted");
                    if (!StrictlyAscending(Keys(plan.Built, after, array))) throw new NotMergeable($"the built {array.Name} keys are not sorted");
                }
                else
                {
                    // A flag the stock keys earn must also hold for the built keys.
                    var builtKeys = Keys(plan.Built, after, array);
                    if (array.Unique && stockKeys.Distinct().Count() == stockKeys.Length)
                    {
                        if (builtKeys.Distinct().Count() != builtKeys.Length) throw new NotMergeable($"the built {array.Name} keys repeat");
                        flags |= MergeSidecarFormat.UniqueKey;
                    }
                    if (array.OrderedWhenStockAscending)
                    {
                        if (recipe.Tag == TableMergeRecipes.StringBankIndex) context.BankAscending = ascending;
                        if (ascending)
                        {
                            if (!StrictlyAscending(builtKeys)) throw new NotMergeable($"the built {array.Name} keys are not sorted");
                            flags |= MergeSidecarFormat.Ordered;
                        }
                    }
                }
            }
            plan.Ops.Add(new(array.Name, array.Kind, array.Descriptor, MergeSidecarFormat.TopLevelOp, 0, array.Row,
                (uint)before.Count, (uint)after.Count, array.Limit, array.PairGroup, flags, array.KeyOffset));
        }
    }

    /** Adds a nested op for each stock row array that grew; any other stock pointer must keep its target. */
    private static void NestedOps(EntryPlan plan, MergeRecipe recipe, IReadOnlyDictionary<int, View> stockViews,
        IReadOnlyDictionary<int, View> builtViews, TableSpans spans)
    {
        byte[] stock = plan.Stock, built = plan.Built;
        for (int parent = 0; parent < recipe.Arrays.Count; parent++)
        {
            var array = recipe.Arrays[parent];
            var before = stockViews[array.Descriptor];
            var after = builtViews[array.Descriptor];
            for (int row = 0; row < before.Count; row++)
                foreach (var field in array.Row.Fields)
                {
                    int stockAt = before.Rows + row * array.Row.Stride + field.Offset;
                    int builtAt = after.Rows + row * array.Row.Stride + field.Offset;
                    if (field.Kind == MergeFieldKind.RowPointer)
                    {
                        if (RowIndex(stock, stockAt, stockViews[field.TargetArray])
                            != RowIndex(built, builtAt, builtViews[field.TargetArray]))
                            throw new NotMergeable($"installed row {row} of {array.Name} points at another row");
                        continue;
                    }
                    if (field.Kind == MergeFieldKind.ObjectPointer)
                    {
                        if (Target(stock, stockAt) != Target(built, builtAt))
                            throw new NotMergeable($"installed row {row} of {array.Name} points at another object");
                        continue;
                    }
                    long stockCount = I64(stock, stockAt), builtCount = I64(built, builtAt);
                    long stockTarget = Target(stock, stockAt + ArrayRelative), builtTarget = Target(built, builtAt + ArrayRelative);
                    if (stockCount == builtCount && stockTarget == builtTarget) continue;
                    if (!field.Nestable || field.ElementStride <= 0)
                        throw new NotMergeable($"installed row {row} of {array.Name} changed its array at +{field.Offset}");
                    if (stockCount <= 0 || builtCount < stockCount || builtCount > TableMergeRecipes.EditorRowLimit)
                        throw new NotMergeable($"installed row {row} of {array.Name} changed an empty or shrunk array");
                    var child = new View((int)stockCount, (int)stockTarget, field.ElementStride);
                    if (!built.AsSpan((int)builtTarget + TagArray.ElementClassOffset, ElementWords)
                        .SequenceEqual(stock.AsSpan(child.Header + TagArray.ElementClassOffset, ElementWords)))
                        throw new NotMergeable($"installed row {row} of {array.Name} changed its array class");
                    spans.Arrays.Add((child.Header - TagView.ClassWordSize, child.Rows + child.Count * field.ElementStride));
                    spans.Structural.Add((child.Header, child.Header + HeaderCount));
                    plan.Ops.Add(new($"{array.Name}[{row}]+{field.Offset}", MergeOpKind.Append, field.Offset, (uint)parent,
                        (uint)row, new MergeShape(field.ElementStride, field.Element?.Fields ?? []), (uint)stockCount,
                        (uint)builtCount, TableMergeRecipes.EditorRowLimit, null, 0, -1));
                }
        }
    }

    private static View Require(View? view, string reason) => view ?? throw new NotMergeable(reason);

    /** Null for an empty array with no header; throws NotMergeable when the descriptor is broken. */
    private static View? ReadView(byte[] bytes, int descriptor, int stride)
    {
        if (descriptor < 0 || descriptor + ArrayField > bytes.Length) throw new NotMergeable($"it has no array descriptor at +{descriptor}");
        long count = I64(bytes, descriptor), relative = I64(bytes, descriptor + ArrayRelative);
        if (count == 0 && relative == 0) return null;
        long header = descriptor + ArrayRelative + relative;
        if (!ArrayHeaderValid(bytes, header, count, stride)) throw new NotMergeable($"its array at +{descriptor} is malformed");
        return new((int)count, (int)header, stride);
    }

    /** @return True when a typed array header repeats `count` behind its marker and, given a stride, its rows fit. */
    private static bool ArrayHeaderValid(byte[] bytes, long header, long count, int stride) =>
        count >= 0 && count <= TableMergeRecipes.EditorRowLimit && header >= 4 && header + TagArray.HeaderSize <= bytes.Length
        && TagArray.MarkerAt(bytes, header) && I64(bytes, (int)header) == count
        && (stride <= 0 || header + TagArray.HeaderSize + count * stride <= bytes.Length);

    /** Self-relative target of the i64 at `at`, or -1 for 0. */
    private static long Target(byte[] bytes, int at) => I64(bytes, at) is var relative && relative == 0 ? -1 : at + relative;

    /** Row number a row pointer names, -1 for none; ValidateFields has already placed it on a row start. */
    private static long RowIndex(byte[] bytes, int at, View view)
    {
        long value = I64(bytes, at);
        return value == 0 ? -1 : (at + value - view.Rows) / view.Stride;
    }

    /**
     * Checks every pointer field of every row and refuses a row word that points at a typed object the recipe
     * does not list. addedStart bounds the mod's added bytes.
     */
    private static void ValidateRows(byte[] bytes, IReadOnlyDictionary<int, View> views, MergeArray array, int addedStart)
    {
        var view = views[array.Descriptor];
        int stride = array.Row.Stride;
        for (int row = 0; row < view.Count; row++)
        {
            int start = view.Rows + row * stride;
            var declared = ValidateFields(bytes, views, array.Row, start, addedStart, $"{array.Name} row {row}");
            for (int word = (start + 7) & ~7; word + 8 <= start + stride; word += 8)
                if (!declared.Contains(word) && LooksLikePointer(bytes, word))
                    throw new NotMergeable($"{array.Name} row {row} holds an unlisted pointer at +{word - start}");
        }
    }

    /** @return The absolute offsets of the words this shape declares, so the pointer scan skips them. */
    private static HashSet<int> ValidateFields(byte[] bytes, IReadOnlyDictionary<int, View> views, MergeShape shape, int start,
        int addedStart, string where)
    {
        var declared = new HashSet<int>();
        foreach (var field in shape.Fields)
        {
            int at = start + field.Offset;
            if (field.Kind == MergeFieldKind.RowPointer)
            {
                declared.Add(at);
                long value = I64(bytes, at);
                if (value == 0) continue;
                var target = views[field.TargetArray];
                long offset = at + value - target.Rows;
                if (offset < 0 || offset % target.Stride != 0 || offset / target.Stride >= target.Count)
                    throw new NotMergeable($"{where} points outside its target rows");
                continue;
            }
            if (field.Kind == MergeFieldKind.ObjectPointer)
            {
                declared.Add(at);
                long target = I64(bytes, at);
                if (target != 0 && (at + target < 0 || at + target >= bytes.Length))
                    throw new NotMergeable($"{where} points outside the entry at +{field.Offset}");
                // An added object moves with the added bytes, so its own pointers are checked like a row's.
                if (target == 0 || field.Element is not { } owned || at + target < addedStart) continue;
                if (at + target + owned.Stride > bytes.Length) throw new NotMergeable($"{where} names an object past the entry end");
                ValidateFields(bytes, views, owned, (int)(at + target), addedStart, where);
                continue;
            }
            declared.Add(at); declared.Add(at + ArrayRelative);
            long relative = I64(bytes, at + ArrayRelative);
            long count = I64(bytes, at);
            if (count == 0)
            {
                if (relative != 0 && (at + ArrayRelative + relative <= 0 || at + ArrayRelative + relative > bytes.Length))
                    throw new NotMergeable($"{where} has an empty array pointing outside the entry");
                continue;
            }
            long header = at + ArrayRelative + relative;
            if (relative == 0 || !ArrayHeaderValid(bytes, header, count, field.ElementStride))
                throw new NotMergeable($"{where} has a malformed array at +{field.Offset}");
            if (header < addedStart) continue;
            var element = field.Element;
            if (element is null || field.ElementStride <= 0) continue;
            for (int index = 0; index < count; index++)
            {
                int elementStart = (int)header + TagArray.HeaderSize + index * field.ElementStride;
                ValidateFields(bytes, views, element, elementStart, addedStart, where);
                foreach (var local in element.Local) ValidateLocal(bytes, local, elementStart, addedStart, where);
            }
        }
        return declared;
    }

    /** An object an added element owns moves with the added bytes, so it and its arrays must sit there too. */
    private static void ValidateLocal(byte[] bytes, MergeLocalPointer local, int start, int addedStart, string where)
    {
        long target = Target(bytes, start + local.Offset);
        if (target < 0) return;
        if (target < addedStart || target + local.Target.Stride > bytes.Length)
            throw new NotMergeable($"{where} owns an object outside its added bytes");
        foreach (var field in local.Target.Fields)
        {
            int at = (int)target + field.Offset;
            long arrayTarget = Target(bytes, at + ArrayRelative);
            if (arrayTarget >= 0 && arrayTarget < addedStart) throw new NotMergeable($"{where} owns an array outside its added bytes");
        }
    }

    private static bool LooksLikePointer(byte[] bytes, int word)
    {
        long value = I64(bytes, word);
        if (value == 0) return false;
        long target = word + value;
        return target >= TagView.ClassWordSize && target < bytes.Length
            && (TagView.ClassAt(bytes, (int)target) & ClassWordMask) == ClassWordPrefix;
    }

    private static uint[] Keys(byte[] bytes, View view, MergeArray array) =>
        Enumerable.Range(0, view.Count).Select(row => U32(bytes, view.Rows + row * array.Row.Stride + array.KeyOffset)).ToArray();

    private static bool StrictlyAscending(uint[] keys)
    {
        for (int index = 1; index < keys.Length; index++) if (keys[index - 1] >= keys[index]) return false;
        return true;
    }

    /**
     * Every stock-region run where the build differs, split at span edges.
     * @param insideOnly A run outside every span throws; a recipe table may change only rows and structure words.
     */
    private static List<SidecarDiff> Diffs(byte[] stock, byte[] built, IReadOnlyList<(int Begin, int End)> structural,
        IReadOnlyList<(int Begin, int End)> arrays, bool insideOnly)
    {
        var spans = structural.Concat(arrays).ToArray();
        var edges = spans.SelectMany(span => new[] { span.Begin, span.End }).Where(edge => edge > 0 && edge < stock.Length)
            .Append(stock.Length).Distinct().Order().ToArray();
        var diffs = new List<SidecarDiff>();
        int position = 0;
        while (true)
        {
            int start = stock.AsSpan(position).CommonPrefixLength(built.AsSpan(position, stock.Length - position));
            position += start;
            if (position >= stock.Length) break;
            int end = position + 1;
            while (end < stock.Length && stock[end] != built[end]) end++;
            for (int begin = position; begin < end;)
            {
                int next = Math.Min(end, edges.First(edge => edge > begin));
                if (insideOnly && !spans.Any(span => span.Begin <= begin && next <= span.End))
                    throw new NotMergeable($"bytes at +0x{begin:X} outside its arrays changed");
                diffs.Add(new((uint)begin, stock.AsSpan(begin, next - begin).ToArray()));
                begin = next;
            }
            position = end;
        }
        return diffs;
    }

    /** Added bytes no op array and no walked owned data reaches; old copies of moved arrays land here. */
    private static long Unreached(EntryPlan plan, MergeRecipe recipe, IReadOnlyDictionary<int, View> views)
    {
        byte[] bytes = plan.Built;
        int added = plan.Stock.Length;
        var covered = new List<(long Begin, long End)>();
        var visited = new HashSet<long>();
        void Owned(long header, long count, int stride, MergeShape? element)
        {
            if (header < added || !visited.Add(header)) return;
            covered.Add((header - TagView.ClassWordSize, header + TagArray.HeaderSize + count * Math.Max(stride, 0)));
            if (element is null || stride <= 0) return;
            for (long index = 0; index < count; index++) Shape(header + TagArray.HeaderSize + index * stride, element);
        }
        void Shape(long start, MergeShape shape)
        {
            foreach (var field in shape.Fields.Where(field => field.Kind == MergeFieldKind.OwnedArray))
            {
                long count = I64(bytes, (int)(start + field.Offset));
                long target = Target(bytes, (int)(start + field.Offset + ArrayRelative));
                if (count > 0 && target >= 0) Owned(target, count, field.ElementStride, field.Element);
            }
            foreach (var field in shape.Fields.Where(field => field.Kind == MergeFieldKind.ObjectPointer && field.Element is not null))
            {
                long target = Target(bytes, (int)(start + field.Offset));
                if (target < added || !visited.Add(target)) continue;
                covered.Add((target, target + field.Element!.Stride));
                Shape(target, field.Element);
            }
            foreach (var local in shape.Local)
            {
                long target = Target(bytes, (int)(start + local.Offset));
                if (target < added || !visited.Add(target)) continue;
                covered.Add((target, target + local.Target.Stride));
                Shape(target, local.Target);
            }
        }
        foreach (var array in recipe.Arrays)
        {
            var view = views[array.Descriptor];
            covered.Add((view.Header - TagView.ClassWordSize, view.Rows + (long)view.Count * array.Row.Stride));
            for (int row = 0; row < view.Count; row++) Shape(view.Rows + (long)row * array.Row.Stride, array.Row);
        }
        long reached = 0, cursor = added;
        foreach (var (begin, end) in covered.OrderBy(span => span.Begin))
        {
            long from = Math.Max(begin, cursor), to = Math.Min(end, bytes.Length);
            if (to > from) { reached += to - from; cursor = to; }
        }
        return bytes.Length - added - reached;
    }

    /** Applies every site rule for the entry's table and class; keeps values in [stock count, mod count). */
    private static void FindSites(Context context, EntryPlan plan)
    {
        if (plan.Kind == SidecarEntryKind.Manifest) return;
        var rules = (TableMergeRecipes.ByTable.GetValueOrDefault(plan.Tag) ?? [])
            .Concat(TableMergeRecipes.ByClass.GetValueOrDefault(plan.Class) ?? []);
        byte[] bytes = plan.Built;
        foreach (var rule in rules)
        {
            uint stockCount, modCount;
            if (rule.OpLocal)
            {
                if (plan.Kind != SidecarEntryKind.Table || plan.Tag != rule.TargetTable) continue;
                var op = plan.Ops.FirstOrDefault(op => op.ParentOp == MergeSidecarFormat.TopLevelOp && op.Descriptor == rule.TargetArray);
                if (op is null) continue;
                (stockCount, modCount) = (op.StockCount, op.ModCount);
            }
            else (stockCount, modCount) = context.Counts(rule.TargetTable, rule.TargetArray);
            if (modCount <= stockCount) continue;
            foreach (int node in Walk(bytes, rule.Path, 0))
            {
                int at = node + rule.Field;
                if (at < 0 || at + rule.Width > bytes.Length) continue;
                long value = ReadValue(bytes.AsSpan(at), rule.Width, rule.Signed);
                if (value < stockCount || value >= modCount) continue;
                var hit = new SiteHit((uint)at, rule.Width, rule.Signed, rule.TargetTable, rule.TargetArray, rule.OpLocal, rule.Name);
                if (plan.Sites.TryGetValue(hit.Offset, out var existing))
                {
                    if (existing with { Rule = hit.Rule } != hit)
                        throw new InvalidDataException($"Entry {plan.Tag:X8} has two meanings for the field at +{at}.");
                    continue;
                }
                plan.Sites.Add(hit.Offset, hit);
            }
        }
        long end = 0;
        foreach (var site in plan.Sites.Values)
        {
            if (site.Offset < end) throw new InvalidDataException($"Entry {plan.Tag:X8} has overlapping row references.");
            end = site.Offset + site.Width;
        }
    }

    /** A site's row number, read at its width and sign. */
    internal static long ReadValue(ReadOnlySpan<byte> field, int width, bool signed) => width switch
    {
        1 => signed ? (sbyte)field[0] : field[0],
        2 => signed ? BinaryPrimitives.ReadInt16LittleEndian(field) : BinaryPrimitives.ReadUInt16LittleEndian(field),
        _ => signed ? BinaryPrimitives.ReadInt32LittleEndian(field) : BinaryPrimitives.ReadUInt32LittleEndian(field)
    };

    /** The nodes a site path reaches; a null pointer, another marker or an unreadable array ends a branch. */
    private static IEnumerable<int> Walk(byte[] bytes, IReadOnlyList<MergeStep> path, int node)
    {
        if (path.Count == 0) { yield return node; yield break; }
        var step = path[0];
        var rest = path.Skip(1).ToArray();
        IEnumerable<int> next = [];
        switch (step.Kind)
        {
            case MergeStepKind.Pointer:
            {
                int at = node + step.Offset;
                long target = at >= 0 && at + 8 <= bytes.Length ? Target(bytes, at) : -1;
                if (target >= 0 && target < bytes.Length
                    && (step.Marker == 0 || TagView.ClassAt(bytes, (int)target) == step.Marker))
                    next = [(int)target];
                break;
            }
            case MergeStepKind.Array:
                if (WalkView(bytes, node + step.Offset, step.Stride) is { } view)
                    next = Enumerable.Range(0, view.Count).Select(row => view.Rows + row * step.Stride);
                break;
            case MergeStepKind.Pool:
            {
                if (node + step.FirstOffset + 4 > bytes.Length || node + step.CountOffset >= bytes.Length) break;
                int first = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(node + step.FirstOffset));
                int count = bytes[node + step.CountOffset];
                if (WalkView(bytes, step.Offset, step.Stride) is { } pool && first >= 0 && first + count <= pool.Count)
                    next = Enumerable.Range(first, count).Select(row => pool.Rows + row * step.Stride);
                break;
            }
            default:
                if (node + step.Offset < bytes.Length && bytes[node + step.Offset] == step.Value) next = [node];
                break;
        }
        foreach (int child in next)
            foreach (int result in Walk(bytes, rest, child))
                yield return result;
    }

    private static View? WalkView(byte[] bytes, int descriptor, int stride)
    {
        try { return ReadView(bytes, descriptor, stride); }
        catch (NotMergeable) { return null; }
    }

    /** Builds the file: targets sorted by table and descriptor, shapes in op order, sites mapped to targets. */
    private static SidecarFile Assemble(Context context, PackageBuildArtifact artifact, byte[] digest, IReadOnlyList<EntryPlan> plans)
    {
        var targets = new SortedDictionary<(uint Table, uint Descriptor), SidecarTarget>();
        void AddTarget(SidecarTarget target)
        {
            if (targets.TryGetValue((target.Table, target.Descriptor), out var known) && known != target)
                throw new InvalidDataException($"Table {target.Table:X8} has two row counts in one build.");
            targets[(target.Table, target.Descriptor)] = target;
        }
        foreach (var plan in plans)
        {
            if (plan.Kind == SidecarEntryKind.Table)
                foreach (var op in plan.Ops.Where(op => op.ParentOp == MergeSidecarFormat.TopLevelOp && op.Kind == MergeOpKind.Append))
                    AddTarget(new(plan.Tag, (uint)op.Descriptor, op.StockCount, op.ModCount));
            foreach (var growth in plan.Growth) AddTarget(growth);
            foreach (var site in plan.Sites.Values.Where(site => !site.OpLocal))
            {
                var (stock, mod) = context.Counts(site.Table, site.Array);
                AddTarget(new(site.Table, (uint)site.Array, stock, mod));
            }
        }
        var targetIndex = targets.Keys.Select((key, index) => (key, index)).ToDictionary(pair => pair.key, pair => pair.index);
        var shapes = new List<SidecarShape>();
        var entries = new List<SidecarEntry>();
        foreach (var plan in plans.OrderBy(plan => plan.Index))
        {
            var opIndex = plan.Ops.Select((op, index) => (op, index))
                .Where(pair => pair.op.ParentOp == MergeSidecarFormat.TopLevelOp)
                .ToDictionary(pair => pair.op.Descriptor, pair => pair.index);
            // A shape's element shapes are added after it, so every element index is larger than its parent's.
            ushort AddShape(MergeShape shape)
            {
                int index = shapes.Count;
                if (index >= MergeSidecarFormat.NoElementShape) throw new InvalidDataException("The merge sidecar has too many shapes.");
                shapes.Add(null!);
                var fields = shape.Fields.Select(field => new SidecarShapeField((uint)field.Offset, field.Kind, Reference(field))).ToArray();
                shapes[index] = new((uint)shape.Stride, fields);
                return (ushort)index;
            }
            ushort Reference(MergeField field) =>
                field.Kind == MergeFieldKind.RowPointer ? (ushort)opIndex[field.TargetArray]
                : field.Kind == MergeFieldKind.ObjectPointer
                    ? field.Element is { Fields.Count: > 0 } target ? AddShape(target) : MergeSidecarFormat.NoElementShape
                : field.Element is { Fields.Count: > 0 } element && field.ElementStride > 0
                    ? AddShape(element with { Stride = field.ElementStride })
                    : MergeSidecarFormat.NoElementShape;
            var ops = plan.Ops.Select(op => new SidecarOp(op.Kind, op.Flags, (byte)(op.KeyOffset >= 0 ? 4 : 0),
                (uint)Math.Max(op.KeyOffset, 0), op.ParentOp, op.ParentRow, (uint)op.Descriptor, AddShape(op.Row),
                op.StockCount, op.ModCount, op.Limit, op.PairGroup is null ? 0 : TableMergeRecipes.PairGroupId(op.PairGroup))).ToArray();
            var sites = plan.Sites.Values.Select(site => new SidecarSite(site.Offset, (byte)site.Width,
                site.Signed ? MergeSidecarFormat.SignedSite : (byte)0,
                site.OpLocal ? (ushort)(MergeSidecarFormat.OpLocalTarget | opIndex[site.Array])
                    : (ushort)targetIndex[(site.Table, (uint)site.Array)])).ToArray();
            byte[] stockDigest = SHA256.HashData(plan.Stock);
            entries.Add(new(plan.Index, plan.Kind, (uint)plan.Stock.Length, (uint)plan.Built.Length, stockDigest, plan.Diffs, ops, sites));
        }
        return new(artifact.PackageId, artifact.PatchId, digest, targets.Values.ToArray(), shapes, entries);
    }

    private static object Describe(PackageBuildArtifact artifact, string path, SidecarFile file, IReadOnlyList<EntryPlan> plans) => new
    {
        package = Path.GetFileName(artifact.Path),
        sidecar = Path.GetFileName(path),
        packageId = artifact.PackageId.ToString("X4"),
        patchId = artifact.PatchId,
        targets = file.Targets.Select(target => new
        {
            table = target.Table.ToString("X8"), descriptor = target.Descriptor, stockCount = target.StockCount, modCount = target.ModCount
        }),
        entries = plans.OrderBy(plan => plan.Index).Select(plan => new
        {
            index = plan.Index,
            tag = plan.Tag.ToString("X8"),
            @class = plan.Class.ToString("X8"),
            kind = plan.Kind.ToString(),
            recipe = plan.Recipe,
            reason = plan.Reason,
            stockSize = plan.Stock.Length,
            builtSize = plan.Built.Length,
            diffs = plan.Diffs.Count,
            diffBytes = plan.Diffs.Sum(diff => diff.Old.Length),
            ops = plan.Ops.Select(op => new
            {
                name = op.Name, kind = op.Kind.ToString(), descriptor = op.Descriptor,
                parentOp = op.ParentOp == MergeSidecarFormat.TopLevelOp ? (long?)null : op.ParentOp, parentRow = op.ParentRow,
                stockCount = op.StockCount, modCount = op.ModCount, limit = op.Limit, pairGroup = op.PairGroup,
                uniqueKey = (op.Flags & MergeSidecarFormat.UniqueKey) != 0, ordered = (op.Flags & MergeSidecarFormat.Ordered) != 0
            }),
            sites = plan.Sites.Values.GroupBy(site => (site.Rule, site.Table, site.Array, site.OpLocal)).Select(group => new
            {
                rule = group.Key.Rule,
                target = group.Key.OpLocal ? $"op +{group.Key.Array}" : $"{group.Key.Table:X8} +{group.Key.Array}",
                count = group.Count()
            }),
            unreachedBytes = plan.Unreached
        })
    };

    private static long I64(byte[] bytes, int at) =>
        at >= 0 && at + 8 <= bytes.Length ? BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at)) : 0;
    private static uint U32(byte[] bytes, int at) =>
        at >= 0 && at + 4 <= bytes.Length ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)) : 0;
}
