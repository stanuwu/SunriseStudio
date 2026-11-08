using System.Diagnostics;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;

/** Whole-install checks: every installed tag whose class the registry knows parses and encodes to its own bytes. */
static class GameChecks
{
    private sealed class ClassStats
    {
        public int Tags, WithProblems;
        public long Bytes, Opaque;
        public string? FirstProblem;
        public uint FirstTag;
    }

    /**
     * @param limit Tags read per class; 0 reads every tag.
     * @param samples Tags out of depth-first order to print, block by block.
     */
    public static void Run(string gameRoot, string registryPath, int limit, int samples = 0)
    {
        var schema = new RegistryIndex(TagRegistry.Load(registryPath));
        using var catalog = PackageCatalog.Open(gameRoot);
        var clock = Stopwatch.StartNew();
        var stats = new Dictionary<uint, ClassStats>();
        var refused = new Dictionary<string, int>();
        var blocksPerTag = new SortedDictionary<int, long>();
        long blocks = 0, blockStrings = 0;
        int repackEqual = 0, repackDifferent = 0, differenceSamples = 5;
        int mismatches = 0, depthFirst = 0, breadthFirst = 0, stringsLast = 0, stringsLevel = 0, ordered = 0, skippedClasses = 0;
        foreach (var entry in catalog.Entries)
        {
            if (schema.Class(entry.Reference) is null) { skippedClasses++; continue; }
            if (!stats.TryGetValue(entry.Reference, out var classStats)) stats[entry.Reference] = classStats = new();
            if (limit > 0 && classStats.Tags >= limit) continue;
            byte[] blob = catalog.Read(entry.TagHash);
            var document = TagDocument.Parse(blob, entry.Reference, schema);
            classStats.Tags++;
            classStats.Bytes += blob.Length;
            classStats.Opaque += document.Opaque.Sum(x => (long)x.Length);
            if (!document.Encode().AsSpan().SequenceEqual(blob)) mismatches++;
            var stringBlocks = document.OutOfLine.Where(x => !x.Alias && x.Kind == TagNodeKind.Object && x.ClassId == SInlineString.Class).ToList();
            if (stringBlocks.Count > 0)
            {
                int bucket = Math.Min(stringBlocks.Count, 4);
                blocksPerTag[bucket] = blocksPerTag.GetValueOrDefault(bucket) + 1;
                blocks += stringBlocks.Count;
                blockStrings += document.OutOfLine.Count(x => x.Kind == TagNodeKind.String);
            }
            byte[]? repacked = CanonicalTagWriter.Repack(document, schema, out string? reason);
            if (repacked is null) refused[reason!] = refused.GetValueOrDefault(reason!) + 1;
            else if (repacked.AsSpan().SequenceEqual(blob)) repackEqual++;
            else
            {
                repackDifferent++;
                if (differenceSamples-- > 0)
                {
                    int first = 0;
                    while (first < Math.Min(blob.Length, repacked.Length) && blob[first] == repacked[first]) first++;
                    var owner = document.OutOfLine.FirstOrDefault(x => !x.Alias && first >= x.Offset && first < x.End);
                    Console.WriteLine($"  repack tag=0x{entry.TagHash:X8} class=0x{entry.Reference:X8} length={blob.Length} repacked={repacked.Length} "
                        + $"first_difference={first} in={owner?.Kind.ToString() ?? "root"} at={owner?.Offset}");
                }
            }
            if (document.Problems.Count > 0)
            {
                classStats.WithProblems++;
                if (classStats.FirstProblem is null) { classStats.FirstProblem = document.Problems[0]; classStats.FirstTag = entry.TagHash; }
                continue;
            }
            // Layout order: does the packer place out-of-line data depth first or breadth first?
            var owned = document.OutOfLine.Where(x => !x.Alias && x.Length > 0).ToList();
            if (owned.Count < 2) continue;
            ordered++;
            if (Increasing(owned)) depthFirst++;
            var pooled = owned.Where(x => x.Kind != TagNodeKind.String).Concat(owned.Where(x => x.Kind == TagNodeKind.String)).ToList();
            if (Increasing(pooled)) stringsLast++;
            var levelled = owned.Where(x => x.Kind != TagNodeKind.String)
                .Concat(BreadthFirst(document.Root).Where(x => x.Kind == TagNodeKind.String)).ToList();
            if (Increasing(levelled)) stringsLevel++;
            else if (samples-- > 0) PrintLayout(entry, levelled);
            if (Increasing(BreadthFirst(document.Root))) breadthFirst++;
        }
        long tags = stats.Values.Sum(x => x.Tags), bytes = stats.Values.Sum(x => x.Bytes), opaque = stats.Values.Sum(x => x.Opaque);
        Console.WriteLine($"game tags={tags} classes={stats.Count} entries_without_class={skippedClasses} encode_mismatches={mismatches} "
            + $"with_problems={stats.Values.Sum(x => x.WithProblems)} bytes={bytes} opaque_bytes={opaque} "
            + $"covered={(bytes == 0 ? 0 : 100.0 * (bytes - opaque) / bytes):F1}% seconds={clock.Elapsed.TotalSeconds:F0}");
        Console.WriteLine($"strings blocks={blocks} strings={blockStrings} tags_by_block_count="
            + string.Join(",", blocksPerTag.Select(x => $"{x.Key}{(x.Key == 4 ? "+" : "")}:{x.Value}")));
        Console.WriteLine($"repack equal={repackEqual} different={repackDifferent} refused="
            + string.Join(",", refused.OrderByDescending(x => x.Value).Select(x => $"{x.Key.Replace(' ', '_')}:{x.Value}")));
        Console.WriteLine($"layout tags_with_2+_blocks={ordered} depth_first={depthFirst} breadth_first={breadthFirst} "
            + $"strings_last={stringsLast} strings_last_by_level={stringsLevel}");
        foreach (var (classId, value) in stats.Where(x => x.Value.WithProblems > 0).OrderByDescending(x => x.Value.WithProblems).Take(20))
            Console.WriteLine($"  problems class=0x{classId:X8} tags={value.Tags} failing={value.WithProblems} "
                + $"first=0x{value.FirstTag:X8} {value.FirstProblem}");
        if (mismatches != 0) throw new InvalidOperationException($"{mismatches} tags did not encode to their own bytes.");
    }

    /** Blocks around the first one placed before its predecessor ends. */
    private static void PrintLayout(PackageEntry entry, List<TagNode> owned)
    {
        int inversion = Enumerable.Range(1, owned.Count - 1).First(i => owned[i].Offset < owned[i - 1].End);
        Console.WriteLine($"  layout tag=0x{entry.TagHash:X8} class=0x{entry.Reference:X8} blocks={owned.Count} inversion={inversion}");
        foreach (var node in owned.Skip(Math.Max(0, inversion - 4)).Take(8))
        {
            int depth = 0;
            for (var parent = node.Parent; parent is not null; parent = parent.Parent) depth++;
            Console.WriteLine($"    {node.Kind,-6} class=0x{node.ClassId:X8} field={node.Field?.Name} owner=0x{node.Parent?.ClassId:X8} "
                + $"depth={depth} at={node.Offset} end={node.End}");
        }
    }

    private static bool Increasing(IReadOnlyList<TagNode> nodes)
    {
        for (int index = 1; index < nodes.Count; index++)
            if (nodes[index].Offset < nodes[index - 1].End) return false;
        return true;
    }

    /** Owned out-of-line nodes level by level; each level in field order. */
    private static List<TagNode> BreadthFirst(TagNode root)
    {
        var result = new List<TagNode>();
        var queue = new Queue<TagNode>([root]);
        while (queue.TryDequeue(out var node))
        {
            foreach (var child in node.Children)
            {
                if (child.Kind is TagNodeKind.Array or TagNodeKind.Object or TagNodeKind.String && !child.Alias && child.Length > 0)
                    result.Add(child);
                queue.Enqueue(child);
            }
        }
        return result;
    }
}
