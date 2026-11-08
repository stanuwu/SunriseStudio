using System.Buffers.Binary;
using System.Text.RegularExpressions;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Editing;
using SunriseStudio.Tags.Views;
using ManifestView = SunriseStudio.Tags.Generated.PackageManifest;

namespace SunriseStudio.Projects;

/**
 * Finds every location in a build that holds one of the mod's own tags or package ids. An own tag
 * in bytes the schema cannot type fails the build: a loader moving the mod would miss it.
 */
public static partial class ReferenceMapBuilder
{
    /**
     * @param own The new package ids the build writes.
     * @param files Built package files; each one's misc region and merge sidecar are read.
     * @throws InvalidDataException naming the first own tag the schema cannot type.
     */
    public static ReferenceMap Build(string modId, IReadOnlyCollection<ushort> own, ProjectBuildContents contents,
        IReadOnlyList<(string Path, ushort PackageId)> files, PackageCatalog catalog, RegistryIndex schema)
    {
        var rows = new List<ReferenceRow>();
        var unresolved = new List<string>();
        foreach (var (tag, bytes) in contents.InstalledEdits.OrderBy(pair => pair.Key))
        {
            if (!catalog.TryGetEntry(tag, out var entry)) throw new InvalidDataException($"Edited tag {tag:X8} is not installed.");
            Entry(entry.PackageId, entry.EntryIndex, entry.Reference, bytes, own, schema, rows, unresolved);
        }
        foreach (var entry in contents.NewEntries.OrderBy(entry => entry.PackageId).ThenBy(entry => entry.EntryIndex))
            Entry(entry.PackageId, entry.EntryIndex, entry.Reference, entry.Data, own, schema, rows, unresolved);
        if (unresolved.Count > 0)
            throw new InvalidDataException("Unresolved own-hash occurrence at " + string.Join(", ", unresolved.Take(8))
                + ". Give the field a tag reference type in the registry, or change the value.");
        foreach (var (file, package) in files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
        {
            byte[] misc = PackageWriter.ReadMisc(file);
            foreach (int offset in PackageMisc.TagFieldOffsets(misc).Where(offset => IsOwn(U32(misc, offset), own)))
                rows.Add(new(package, ReferenceMap.MiscEntry, (uint)offset, ReferenceKind.TagRef));
            foreach (int offset in PackageDependencyTable.GroupPackageOffsets(misc).Where(offset => own.Contains(U16(misc, offset))))
                rows.Add(new(package, ReferenceMap.MiscEntry, (uint)offset, ReferenceKind.PackageId));
            string sidecar = Path.ChangeExtension(file, MergeSidecarFormat.Extension);
            if (!File.Exists(sidecar)) continue;
            byte[] data = File.ReadAllBytes(sidecar);
            var parsed = MergeSidecarFormat.Read(data);
            for (int target = 0; target < parsed.Targets.Count; target++)
                if (IsOwn(parsed.Targets[target].Table, own))
                    rows.Add(new(package, ReferenceMap.SidecarEntry, (uint)(MergeSidecarFormat.HeaderSize + target * MergeSidecarFormat.TargetSize),
                        ReferenceKind.Sidecar));
        }
        return new(modId, own.Order().ToArray(), [], rows);
    }

    /**
     * Adds the hex literals in mission scripts and SDK modules that name an own tag.
     * @param sources Each file's folder in the mod, `scripts` or `sdk`, with its files.
     */
    public static ReferenceMap WithLua(ReferenceMap map, IEnumerable<(string Folder, ModScriptFile File)> sources)
    {
        var own = map.Packages.ToHashSet();
        var files = map.Files.ToList();
        var rows = map.Rows.ToList();
        foreach (var (folder, source) in sources.OrderBy(x => x.Folder, StringComparer.Ordinal).ThenBy(x => x.File.RelativePath, StringComparer.Ordinal))
        {
            string[] lines = System.Text.Encoding.UTF8.GetString(source.Data).Split('\n');
            int index = -1;
            for (int line = 0; line < lines.Length; line++)
                foreach (Match match in HexLiteral().Matches(lines[line]))
                {
                    if (!IsOwn(Convert.ToUInt32(match.Value[2..], 16), own)) continue;
                    if (index < 0) { index = files.Count; files.Add(folder + "/" + source.RelativePath); }
                    rows.Add(new((uint)index, (uint)line + 1, (uint)match.Index, ReferenceKind.LuaConstant));
                }
        }
        return map with { Files = files, Rows = rows };
    }

    private static readonly int[] ManifestTags =
        [TagView.FieldOffset<ManifestView>(nameof(ManifestView.SelfTag)), TagView.FieldOffset<ManifestView>(nameof(ManifestView.RootTag))];

    /** Typed tag references become rows; any other own value in a typed tag is unresolved. */
    private static void Entry(ushort package, int index, uint classId, byte[] bytes, IReadOnlyCollection<ushort> own, RegistryIndex schema,
        List<ReferenceRow> rows, List<string> unresolved)
    {
        if (classId == PackageManifest.ClassId)
        {
            foreach (int offset in PackageManifest.GroupPackageOffsets(bytes).Where(offset => own.Contains(U16(bytes, offset))))
                rows.Add(new(package, (uint)index, (uint)offset, ReferenceKind.PackageId));
            foreach (int offset in ManifestTags.Where(offset => IsOwn(U32(bytes, offset), own)))
                rows.Add(new(package, (uint)index, (uint)offset, ReferenceKind.TagRef));
            return;
        }
        // A class with no fields is raw data, such as texture or vertex bytes, and holds no references.
        if (schema.Class(classId) is not { Fields.Count: > 0 }) return;
        TagFieldRow tree;
        try { tree = TagFieldTree.Build(TagDocument.Parse(bytes, classId, schema), schema); }
        catch (InvalidDataException)
        {
            foreach (int offset in Aligned(0, bytes.Length).Where(offset => IsOwn(U32(bytes, offset), own)))
                unresolved.Add($"tag {TagHashes.Make(package, index):X8} +0x{offset:X} (the tag does not parse)");
            return;
        }
        foreach (var row in tree.Walk().Where(row => row.Children.Count == 0 && row.Kind is TagFieldKind.Value or TagFieldKind.Unknown))
        {
            if (row.Edit == TagValueEdit.TagRef)
            {
                if (IsOwn(U32(bytes, row.Offset), own)) rows.Add(new(package, (uint)index, (uint)row.Offset, ReferenceKind.TagRef));
                continue;
            }
            if (row.Edit == TagValueEdit.WideRef) continue;
            foreach (int offset in Aligned(row.Offset, row.Offset + row.Size).Where(offset => IsOwn(U32(bytes, offset), own)))
                unresolved.Add($"tag {TagHashes.Make(package, index):X8} +0x{offset:X} ({row.Path})");
        }
    }

    private static IEnumerable<int> Aligned(int start, int end)
    {
        for (int offset = (start + 3) & ~3; offset + 4 <= end; offset += 4) yield return offset;
    }

    /** @return True for a tag hash in one of the mod's own packages. */
    private static bool IsOwn(uint value, IReadOnlyCollection<ushort> own)
    {
        if (!TagHashes.IsTag(value)) return false;
        uint package = TagHashes.Package(value);
        return package <= ushort.MaxValue && own.Contains((ushort)package);
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));

    [GeneratedRegex(@"\b0[xX][0-9A-Fa-f]{8}\b")]
    private static partial Regex HexLiteral();
}
