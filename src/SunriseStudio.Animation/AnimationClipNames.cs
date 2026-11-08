using System.Text;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Animation;

public sealed record AnimationClipNameEvidence(uint NameHash, int DefinitionRow, int ClipListIndex,
    uint AudioEventTag, int EventOffset, string Name, string AudioPath);

/** The animation definition's name rows: a hash and a clip slot each. */
public static class AnimationNameRows
{
    private const int MaximumRows = 65_536;

    /** Clip slots of every name row carrying hash; empty for a definition with no name rows. */
    public static IEnumerable<int> SlotsForHash(byte[] definition, uint hash) =>
        Read(definition).Where(row => row.Hash == hash).Select(row => row.Slot).ToArray();

    /** Every name row in order; empty for a definition too short to hold the array. */
    internal static (uint Hash, int Slot, byte Kind, int Row)[] Read(byte[] definition)
    {
        if (!TagView.FitsThrough<SAnimationDefinition>(definition, 0, nameof(SAnimationDefinition.MotionDefinitions))) return [];
        ref readonly var names = ref AnimationControlReader.Member<SAnimationDefinition, TagArray<SAnimationNameRow>>(
            definition, 0, nameof(SAnimationDefinition.MotionDefinitions));
        if (names.Count is < 0 or > MaximumRows)
            throw new InvalidDataException("Animation name row count is invalid.");
        var rows = AnimationControlReader.Rows<SAnimationNameRow>(definition, TagView.OffsetOf(definition, in names),
            SAnimationNameRow.Class);
        var result = new (uint Hash, int Slot, byte Kind, int Row)[rows.Length];
        for (int index = 0; index < result.Length; index++)
            result[index] = (rows[index].NameHash, rows[index].ClipSlot, rows[index].Kind, index);
        return result;
    }
}

/** Only a clip's typed audio-event paths may supply spellings for its owner's name hashes. */
internal static class AnimationClipNames
{
    internal sealed record EventPath(uint Tag, int Offset, string Path);
    internal sealed record Label(string? Name, IReadOnlyList<AnimationClipNameEvidence> Evidence);
    private const int MaximumRows = 65_536, MaximumPath = 4096, MaximumTokens = 64;

    /** Distinct spellings remain unresolved even when they share the same 32-bit hash. */
    internal static IReadOnlyDictionary<int, Label> Resolve(byte[] definition, uint[] clips,
        Func<uint, IReadOnlyList<EventPath>> paths)
    {
        var rows = AnimationNameRows.Read(definition);
        var repeatedHashes = rows.GroupBy(row => row.Hash).Where(group => group.Count() != 1)
            .Select(group => group.Key).ToHashSet();
        var evidence = new Dictionary<int, List<AnimationClipNameEvidence>>();
        foreach (var row in rows)
        {
            if (row.Slot < 0 || row.Slot >= clips.Length || row.Kind is not (1 or 2)
                || row.Hash is 0 or StringHash.Empty or uint.MaxValue || repeatedHashes.Contains(row.Hash)
                || clips[row.Slot] is 0 or uint.MaxValue) continue;
            foreach (var source in paths(clips[row.Slot]))
                foreach (string candidate in Candidates(source.Path))
                    if (Hash(candidate) == row.Hash)
                    {
                        if (!evidence.TryGetValue(row.Slot, out var matches)) evidence[row.Slot] = matches = [];
                        matches.Add(new(row.Hash, row.Row, row.Slot, source.Tag, source.Offset, candidate, source.Path));
                    }
        }
        return evidence.ToDictionary(pair => pair.Key, pair => new Label(
            Unique(pair.Value.Select(value => value.Name)), Array.AsReadOnly(pair.Value.Distinct().ToArray())));
    }

    /** Event pointers and string-bank bounds are checked before a path contributes a name. */
    internal static IReadOnlyList<EventPath> ReadPaths(byte[] clip, Func<uint, uint> classOf)
    {
        if (!TagView.Fits<SAnimationClip>(clip, 0)) return [];
        ref readonly var head = ref TagView.At<SAnimationClip>(clip);
        if (head.Events.Count is < 0 or > MaximumRows)
            throw new InvalidDataException("Animation event count is invalid.");
        var rows = AnimationControlReader.Rows<SAnimationEventSlot>(clip, TagView.OffsetOf(clip, in head.Events),
            SAnimationEventSlot.Class);
        if (rows.Length == 0) return [];
        var banks = TagBlock.Find(clip, SInlineString.Class)
            .Select(bank => (Start: bank.Start, End: bank.Start + bank.Length)).ToArray();
        var result = new List<EventPath>();
        foreach (ref readonly var row in rows)
        {
            if (row.FrameEvent.IsNull) continue;
            int value = TagView.TargetOffset(clip, in row.FrameEvent)!.Value;
            if (AnimationControlReader.ClassBefore(clip, value) != AnimationTagEvent.Class) continue;
            // Members are read alone and in this order, so an event that stops early needs no later bytes.
            if (AnimationControlReader.Member<AnimationTagEvent, byte>(clip, value, nameof(AnimationTagEvent.EventKind)) != 3) continue;
            uint eventTag = AnimationControlReader.Member<AnimationTagEvent, uint>(clip, value, nameof(AnimationTagEvent.EventTarget));
            if (eventTag is 0 or uint.MaxValue || classOf(eventTag) != SWwiseEvent.Class) continue;
            ref readonly var audioPath = ref AnimationControlReader.Member<AnimationTagEvent, RelativePointer<byte>>(
                clip, value, nameof(AnimationTagEvent.AudioPath));
            if (audioPath.IsNull) continue;
            int start = TagView.TargetOffset(clip, in audioPath)!.Value;
            var owners = banks.Where(bank => start >= bank.Start && start < bank.End
                && (start == bank.Start || clip[start - 1] == 0)).ToArray();
            if (owners.Length != 1) continue;
            var bytes = clip.AsSpan(start, Math.Min(MaximumPath + 1, owners[0].End - start));
            int length = bytes.IndexOf((byte)0);
            if (length is <= 0 or > MaximumPath) continue;
            bytes = bytes[..length];
            if (bytes.ContainsAnyExceptInRange((byte)32, (byte)126)) continue;
            string path = Encoding.ASCII.GetString(bytes);
            if (Candidates(path).Count != 0) result.Add(new(eventTag, value, path));
        }
        return result.AsReadOnly();
    }

    /** Only contiguous tokens from the referenced audio filename are candidates. */
    internal static IReadOnlySet<string> Candidates(string path)
    {
        const string prefix = "content/audio/wwise_events/", extension = ".wwise_event";
        string normalized = path.Replace('\\', '/').ToLowerInvariant();
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal)
            || !normalized.EndsWith(extension, StringComparison.Ordinal)) return new HashSet<string>();
        string stem = normalized[(normalized.LastIndexOf('/') + 1)..^extension.Length];
        if (stem.Length is 0 or > MaximumPath || stem.Any(value => value is not (>= 'a' and <= 'z'
            or >= '0' and <= '9' or '_'))) return new HashSet<string>();
        string[] tokens = stem.Split('_');
        if (tokens.Length > MaximumTokens || tokens.Any(value => value.Length == 0)) return new HashSet<string>();
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (int start = 0; start < tokens.Length; start++)
            for (int count = 1; count <= tokens.Length - start; count++)
                result.Add(string.Join("_", tokens, start, count));
        return result;
    }

    internal static string? Unique(IEnumerable<string?> names)
    {
        string[] values = names.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
            .Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return values.Length == 1 ? values[0] : null;
    }

    /** FNV-1 over UTF-16 code units, exact case; equal to the UTF-8 hash only for ASCII text. */
    internal static uint Hash(string text)
    {
        uint result = StringHash.Empty;
        foreach (char value in text) result = unchecked(result * 0x01000193) ^ value;
        return result;
    }
}
