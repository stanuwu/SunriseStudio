using System.Collections.Concurrent;
using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

/** Cache part of StudioNameIndex: saves and restores the built name tables. */
public sealed partial class StudioNameIndex
{
    private sealed record CachedNames(Dictionary<uint, StudioNameCandidate[]> Tags,
        Dictionary<uint, StudioNameCandidate[]> Hashes, Dictionary<uint, string> Items,
        Dictionary<uint, string> Perks, Dictionary<uint, InvestmentItemFacts> Facts, Dictionary<uint, OwnerLink[]> Owners,
        string[] Diagnostics, int SdkFiles, GameNames Game, RenderNames Renderer);

    // Snapshot layout version; change it with the identity string whenever the layout changes.
    private const int NameCacheFormat = 4;

    private bool sceneLabelsAdded;
    private readonly long initialOverlayRevision;
    public bool CacheHit { get; private set; }
    public string CacheStatus { get; private set; } = "Not loaded";

    /**
     * Version of the naming rules the snapshot holds. The snapshot used to be keyed by the build ids of this assembly and
     * SunriseStudio.Packages, so every Solforge rebuild that touched either one threw the snapshot away and the next start
     * spent 2-4 minutes rebuilding it (entity components and asset owners walk about a million package entries). It is
     * now keyed by the installed data (packages, keys, SDK scripts) plus this number. BUMP IT whenever a change alters
     * which names are produced: StudioNameIndex*.cs, GameNames/ComponentIndex/ComponentSlots/LocalizedStrings/RenderNames
     * (SunriseStudio.Packages and SunriseStudio.Scene). Layout changes are caught anyway: NameCacheFormat and the
     * component cache version reject an old snapshot, which is then rebuilt.
     */
    private const int NameRulesVersion = 1;

    /** The installed data (packages, keys, SDK activity scripts and scenario packs) plus the naming rules version. */
    private string? NameCacheIdentity(CancellationToken token)
    {
        string sdk = Path.Combine(catalog.GameRoot, "bin", "x64", "Sunrise", "sdk");
        string activities = Path.Combine(sdk, "lua", "activities"), scenarios = Path.Combine(sdk, "scenarios");
        var files = (Directory.Exists(activities) ? Directory.EnumerateFiles(activities, "*.lua") : [])
            .Concat(Directory.Exists(scenarios) ? Directory.EnumerateFiles(scenarios, "*.pack") : []);
        return catalog.CreateCacheIdentity($"studio-names-v4:format{NameCacheFormat}:rules{NameRulesVersion}",
            files, token, codeVersioned: false);
    }

    /** Restore into private objects first, then publish the complete naming state together. */
    private bool TryReadNameCache(string? identity, long overlay, CancellationToken token, Action<string>? progress)
    {
        if (identity is null) return false;
        CachedNames? cached = null;
        Stage("Reading cached names", progress);
        bool valid = catalog.DiskCache.TryRead(identity, stream =>
        {
            var reader = new NameCacheReader(stream, token);
            if (reader.Data.ReadInt32() != NameCacheFormat || reader.Text() != identity)
                throw new InvalidDataException("Name snapshot identity differs.");
            StudioNameCandidate Candidate()
            {
                string text = reader.Text();
                var source = (StudioNameSource)reader.Data.ReadByte();
                var confidence = (StudioNameConfidence)reader.Data.ReadByte();
                if (!Enum.IsDefined(source) || !Enum.IsDefined(confidence))
                    throw new InvalidDataException("Name snapshot evidence is invalid.");
                return new(text, source, confidence, reader.Text(), reader.Data.ReadInt32(),
                    reader.Data.ReadUInt32(), reader.Data.ReadInt32());
            }
            var tags = reader.Map(() => reader.Array(Candidate));
            var hashes = reader.Map(() => reader.Array(Candidate));
            var items = reader.Map(reader.Text); var perks = reader.Map(reader.Text);
            var facts = reader.Map(() =>
            {
                ushort index = reader.Data.ReadUInt16();
                uint hash = reader.Data.ReadUInt32(), display = reader.Data.ReadUInt32();
                byte bucket = reader.Data.ReadByte(), tier = reader.Data.ReadByte();
                string line = reader.Text();
                byte blocks = reader.Data.ReadByte();
                return new InvestmentItemFacts(index, hash, display, bucket, tier, line,
                    (blocks & 1) != 0, (blocks & 2) != 0, (blocks & 4) != 0);
            });
            var owners = reader.Map(() => reader.Array(() =>
                new OwnerLink(reader.Data.ReadUInt32(), reader.Text(), reader.Data.ReadInt32())));
            var errors = reader.Array(reader.Text);
            int sdkFiles = reader.Count();
            var game = GameNames.ReadCache(reader, catalog, ResolveHash);
            var renderer = RenderNames.ReadCache(reader);
            cached = new(tags, hashes, items, perks, facts, owners, errors, sdkFiles, game, renderer);
        }, token);
        if (!valid || cached is null || catalog.OverlayRevision != overlay || NameCacheIdentity(token) != identity)
            return false;
        Replace(tagNames, cached.Tags); Replace(hashNames, cached.Hashes);
        Replace(itemNames, cached.Items); Replace(perkNames, cached.Perks); Replace(itemFacts, cached.Facts);
        Replace(owners, cached.Owners);
        diagnostics.Clear(); foreach (string error in cached.Diagnostics) diagnostics.Enqueue(error);
        sdkFiles = cached.SdkFiles;
        Volatile.Write(ref gameNames, cached.Game); Volatile.Write(ref renderNames, cached.Renderer);
        CacheHit = true; CacheStatus = "Loaded";
        Publish("Ready", true); Stage("Ready", progress);
        return true;
    }

    private static void Replace<T>(ConcurrentDictionary<uint, T> target, Dictionary<uint, T> source)
    {
        target.Clear();
        foreach (var pair in source) target[pair.Key] = pair.Value;
    }

    /** Project overlays and scene-specific labels never enter the installed naming snapshot. */
    private void WriteNameCache(string? identity, long overlay, CancellationToken token, Action<string>? progress)
    {
        if (identity is null || Volatile.Read(ref sceneLabelsAdded) || catalog.OverlayRevision != overlay
            || NameCacheIdentity(token) != identity) return;
        Stage("Saving name cache", progress);
        bool written = catalog.DiskCache.Write(identity, stream =>
        {
            var writer = new NameCacheWriter(stream, token);
            writer.Data.Write(NameCacheFormat); writer.Text(identity);
            void Candidate(StudioNameCandidate value)
            {
                writer.Text(value.Text); writer.Data.Write((byte)value.Source);
                writer.Data.Write((byte)value.Confidence); writer.Text(value.Detail);
                writer.Data.Write(value.Priority); writer.Data.Write(value.SourceTag); writer.Data.Write(value.SourceOffset);
            }
            writer.Map(tagNames, values => writer.Array(values, Candidate));
            writer.Map(hashNames, values => writer.Array(values, Candidate));
            writer.Map(itemNames, writer.Text); writer.Map(perkNames, writer.Text);
            writer.Map(itemFacts, facts =>
            {
                writer.Data.Write(facts.Index); writer.Data.Write(facts.Hash); writer.Data.Write(facts.DisplayTag);
                writer.Data.Write(facts.Bucket); writer.Data.Write(facts.Tier); writer.Text(facts.TypeLine);
                writer.Data.Write((byte)((facts.HasObjectives ? 1 : 0) | (facts.HasPlug ? 2 : 0) | (facts.HasSack ? 4 : 0)));
            });
            writer.Map(owners, values => writer.Array(values, owner =>
            { writer.Data.Write(owner.Tag); writer.Text(owner.Role); writer.Data.Write(owner.Ordinal); }));
            writer.Array(diagnostics.ToArray(), writer.Text); writer.Data.Write(sdkFiles);
            GameNames!.WriteCache(writer); RenderNames!.WriteCache(writer);
            writer.Data.Flush();
        }, token, () => !Volatile.Read(ref sceneLabelsAdded) && catalog.OverlayRevision == overlay
            && !catalog.HasOverlay);
        CacheStatus = written ? "Saved" : "Unavailable";
    }
}
