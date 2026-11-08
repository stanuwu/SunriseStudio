using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Authoring;

internal static partial class OriginalConfigReferences
{
    /** Cached facts contain no borrowed source buffers or resolved overlay references. */
    private static void WriteNodes(BinaryWriter writer, IReadOnlyDictionary<uint, IsolationNode> nodes)
    {
        writer.Write(nodes.Count);
        foreach (var node in nodes.Values.OrderBy(node => node.Tag))
        {
            writer.Write(node.Tag); writer.Write(node.Class); writer.Write(node.Valid); writer.Write(node.Link);
            writer.Write(node.Placements.Length);
            foreach (var placement in node.Placements)
            {
                writer.Write(placement.Row); writer.Write(placement.Index);
                writer.Write(placement.Auxiliary); writer.Write(placement.Owner);
            }
            writer.Write(node.Maps.Length);
            foreach (uint map in node.Maps) writer.Write(map);
            writer.Write(node.Children.Length);
            foreach (var child in node.Children) { writer.Write(child.Tag); writer.Write(child.Wide); }
            writer.Write(node.OwnerCandidates.Length);
            foreach (uint owner in node.OwnerCandidates) writer.Write(owner);
        }
    }

    /** Offsets are checked against immutable entry sizes before cached placement facts are admitted. */
    private static Dictionary<uint, IsolationNode> ReadNodes(BinaryReader reader,
        IReadOnlyDictionary<uint, PackageEntry> entries, IReadOnlySet<uint> owners)
    {
        int count = Count(reader, entries.Count), facts = 0;
        var result = new Dictionary<uint, IsolationNode>();
        for (int index = 0; index < count; index++)
        {
            uint tag = reader.ReadUInt32(), type = reader.ReadUInt32();
            byte valid = reader.ReadByte(); uint link = reader.ReadUInt32();
            if (!entries.TryGetValue(tag, out var entry) || entry.Reference != type || result.ContainsKey(tag) || valid > 1)
                throw new InvalidDataException("The isolation cache node is invalid.");
            var placements = new InstanceIsolationScope.Placement[FactCount()];
            for (int row = 0; row < placements.Length; row++)
            {
                int offset = reader.ReadInt32(), ordinal = reader.ReadInt32(), auxiliary = reader.ReadInt32();
                uint owner = reader.ReadUInt32();
                if (type != SMapPlacements.Class || offset < 0 || offset > entry.Size - Unsafe.SizeOf<AuthoredPlacementEntry>()
                    || ordinal < 0 || auxiliary < InstanceIsolationScope.PayloadLead
                    || auxiliary > entry.Size - Unsafe.SizeOf<SStaticInstancesPayload>() || (auxiliary & 3) != 0)
                    throw new InvalidDataException("The isolation cache placement is invalid.");
                placements[row] = new(tag, offset, ordinal, auxiliary, owner);
            }
            uint[] maps = new uint[FactCount()];
            for (int row = 0; row < maps.Length; row++) maps[row] = reader.ReadUInt32();
            var children = new IsolationReference[FactCount()];
            for (int row = 0; row < children.Length; row++)
            {
                uint narrow = reader.ReadUInt32(); ulong wide = reader.ReadUInt64();
                if (narrow != uint.MaxValue && wide != 0) throw new InvalidDataException("The isolation cache reference is invalid.");
                children[row] = new(narrow, wide);
            }
            uint[] candidates = new uint[FactCount()];
            for (int row = 0; row < candidates.Length; row++)
            {
                candidates[row] = reader.ReadUInt32();
                if (!owners.Contains(candidates[row])) throw new InvalidDataException("The isolation cache owner candidate is invalid.");
            }
            if (valid == 0 && (link != 0 || placements.Length != 0 || maps.Length != 0 || children.Length != 0)
                || valid != 0 && candidates.Length != 0
                || type != SMapPlacements.Class && maps.Length != 0
                || type is SMapResourceLink.Class or SMapPlacements.Class or SBubbleParent.Class or SScenarioStateEntry.Class
                    && children.Length != 0
                || type is not (SMapResourceLink.Class or SBubbleParent.Class or SScenarioStateEntry.Class) && link != 0)
                throw new InvalidDataException("The isolation cache node fields do not match its class.");
            result.Add(tag, new(tag, type, valid != 0, link, placements, maps, children, candidates));
        }
        return result;

        int FactCount()
        {
            int length = Count(reader, MaximumLinks - facts); facts += length; return length;
        }
    }
}
