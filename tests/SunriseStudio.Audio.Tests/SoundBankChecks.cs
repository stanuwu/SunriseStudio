using System.Buffers.Binary;
using SunriseStudio.Audio;
using SunriseStudio.Packages;

/** Authored weapon banks keep the template hierarchy and replace only leaf sources and fire-rate curves. */
internal static class SoundBankChecks
{
    private const uint TemplateBank = 500, Mixer = 10, Loop = 20, ShotA = 30, ShotB = 31, Tail = 50;
    private const uint PlayLoop = 60, StopLoop = 61, PlayTail = 62, Start = 70, Stop = 71;
    private const uint RateOfFire = 0x6379397C, Pcm = 0x00010001;

    public static void Synthetic()
    {
        var template = AudioCatalog.ReadBank(1, Template());
        var wave = Wave(1); var release = Wave(2);
        var built = WwiseSoundBank.Build(template, Start, Stop, "test/fire", wave, release);
        var bank = AudioCatalog.ReadBank(2, built.Bytes);
        Check(bank.Objects.Select(item => item.Type).SequenceEqual(template.Objects.Select(item => item.Type)), "Object order or types changed.");
        Check(built.EventId == WwiseSoundBank.Hash("test/fire") && built.RelatedEventId != Stop, "Event IDs were not renamed.");
        var nodes = WwiseSoundBank.Nodes(bank);
        var old = new HashSet<uint> { Loop, ShotA, ShotB, Tail };
        Check(nodes.Count == 4 && nodes.All(node => !old.Contains(node.Id)), "A sound or container kept a template ID.");
        var loop = nodes.Single(node => node.Type == 5);
        Check(loop.Parent == Mixer && loop.TransitionMode == 5, "The loop lost its parent or trigger rate.");
        var shots = nodes.Where(node => node.Type == 2 && node.Parent == loop.Id).ToList();
        var tail = nodes.Single(node => node.Type == 2 && node.Parent == Mixer);
        Check(shots.Count == 2 && loop.Children.OrderBy(id => id).SequenceEqual(shots.Select(node => node.Id).OrderBy(id => id)),
            "The loop's children were not renamed with its leaves.");
        var media = bank.EmbeddedMedia.ToDictionary(item => item.Id);
        Check(media.Count == 2 && shots.All(node => node.SourcePlugin == Pcm && media[node.SourceId].Embedded!.SequenceEqual(WwiseSoundBank.PcmWem(wave)))
            && tail.SourcePlugin == Pcm && media[tail.SourceId].Embedded!.SequenceEqual(WwiseSoundBank.PcmWem(release)),
            "Leaves do not play the authored wave and release.");
        var curve = loop.RateOfFireTransitions.Single();
        Check(Math.Abs(At(curve, 20) - 0.05) < 1e-6 && Math.Abs(At(curve, 10) - 0.1) < 1e-6, "The loop period does not follow the fire rate.");
        var play = bank.Objects.Where(item => item.Type == 3 && item.Data[1] == 0x04).ToList();
        Check(play.Count == 2 && play.All(item => U32(item.Data, item.Data.Length - 4) == built.BankId), "Play actions still name the template bank.");

        var single = AudioCatalog.ReadBank(3, WwiseSoundBank.Build(template, Start, Stop, "test/single", wave).Bytes);
        Check(single.EmbeddedMedia.Count == 1 && WwiseSoundBank.Nodes(single).Where(node => node.Type == 2)
            .All(node => node.SourceId == single.EmbeddedMedia[0].Id), "Without a release every leaf plays the wave.");

        byte[] cut = Template(truncate: true);
        Reject(() => WwiseSoundBank.Build(AudioCatalog.ReadBank(4, cut), Start, Stop, "test/cut", wave));
        Reject(() => WwiseSoundBank.Build(template, 99, Stop, "test/missing", wave));

        // A variation: the loop's two shots play two waves, and the tail still plays the release.
        var other = Wave(3);
        var varied = AudioCatalog.ReadBank(5, WwiseSoundBank.Build(template, Start, Stop, "test/vary", wave, release,
            new(Variations: [other])).Bytes);
        var variedNodes = WwiseSoundBank.Nodes(varied);
        var variedLoop = variedNodes.Single(node => node.Type == 5);
        var variedMedia = varied.EmbeddedMedia.ToDictionary(item => item.Id);
        var played = variedLoop.Children.Select(id => variedMedia[variedNodes.Single(node => node.Id == id).SourceId].Embedded!).ToList();
        Check(varied.EmbeddedMedia.Count == 3 && played.Count == 2 && played[0].SequenceEqual(WwiseSoundBank.PcmWem(wave))
            && played[1].SequenceEqual(WwiseSoundBank.PcmWem(other))
            && variedMedia[variedNodes.Single(node => node.Type == 2 && node.Parent == Mixer).SourceId].Embedded!.SequenceEqual(WwiseSoundBank.PcmWem(release)),
            "A variation did not go round the loop's sounds.");
        // The template has no switch container and no attenuation, so those choices are refused.
        Reject(() => WwiseSoundBank.Build(template, Start, Stop, "test/switch", wave, null, new(Switches: new Dictionary<uint, PcmWave> { [7] = other })));
        Reject(() => WwiseSoundBank.Build(template, Start, Stop, "test/far", wave, null, new(MaxDistance: 30)));
        Console.WriteLine("audio sound_bank=synthetic nodes=renamed leaves=pcm release=paired rate_of_fire=period variation=round no_switch=refused no_attenuation=refused");
    }

    /** Options on installed banks: variations and a farther distance on a door sound, and one switch branch. */
    public static void OwnedOptions(PackageCatalog catalog)
    {
        const uint doorEvent = 0x80F5D86D;
        byte[] header = catalog.Read(doorEvent);
        var template = AudioCatalog.ReadBank(U32(header, 20), catalog.Read(U32(header, 20)));
        PcmWave first = Wave(1), second = Wave(2), third = Wave(3);
        float before = WwiseSoundBank.Attenuations(template).Max(item => item.MaxDistance);
        var built = WwiseSoundBank.Build(template, U32(header, 8), U32(header, 12), "test/door", first, null,
            new(Variations: [second, third], MaxDistance: before / 2));
        var bank = AudioCatalog.ReadBank(0, built.Bytes);
        var nodes = WwiseSoundBank.Nodes(bank);
        var byId = nodes.ToDictionary(node => node.Id);
        var randoms = nodes.Where(node => node.Type == 5 && node.Children.Count == 3).ToList();
        Check(randoms.Count == 2 && bank.EmbeddedMedia.Count == 3
            && randoms.All(node => node.Children.Select(id => byId[id].SourceId).Distinct().Count() == 3),
            "The door's random containers do not play three waves each.");
        var oldIds = WwiseSoundBank.Attenuations(template).Select(item => item.Id).ToHashSet();
        var stretched = WwiseSoundBank.Attenuations(bank).Where(item => !oldIds.Contains(item.Id)).ToList();
        var kept = WwiseSoundBank.Attenuations(bank).Where(item => oldIds.Contains(item.Id)).ToList();
        // The door's top nodes took the mixer's positioning; each now names the stretched copy itself.
        var positioned = nodes.Where(node => node.Attenuation != 0).ToList();
        Check(stretched.Count == 1 && Math.Abs(stretched[0].MaxDistance - before / 2) < 0.001f
            && kept.Count == oldIds.Count && kept.Max(item => item.MaxDistance) == before
            && positioned.Count > 0 && positioned.All(node => node.Attenuation == stretched[0].Id)
            && positioned.All(node => !byId.ContainsKey(node.Parent)),
            "The door's distance curve was not stretched in a copy its top nodes name.");

        // Every installed attenuation and node of a sample of banks reads to its last byte.
        int banks = 0, attenuations = 0, index = 0;
        AudioBank? branched = null;
        WwiseNode? container = null;
        uint value = 0;
        foreach (var entry in catalog.Entries.Where(entry => entry is { Type: 26, SubType: 5 } && catalog.ContainsInstalledPackage(entry.PackageId)))
        {
            if (index++ % 40 != 0 && branched is not null) continue;
            var sample = AudioCatalog.ReadBank(entry.TagHash, catalog.ReadOriginal(entry.TagHash));
            if (index % 40 == 1) { banks++; attenuations += WwiseSoundBank.Attenuations(sample).Count; }
            if (branched is not null || !sample.Objects.Any(item => item.Type == 4) || !sample.Objects.Any(item => item.Type == 6)) continue;
            // The first bank with a switch branch that ends in a sound, and that the builder can copy.
            var source = WwiseSoundBank.Nodes(sample);
            bool Leaf(uint id) => source.Any(leaf => leaf.Id == id && leaf.Type == 2 && (leaf.SourcePlugin & 0xF) == 1);
            if (source.FirstOrDefault(node => node.SwitchValues.Count >= 2 && node.SwitchValues.Keys.Any(Leaf)) is not { } found) continue;
            uint branch = found.SwitchValues[found.SwitchValues.Keys.First(Leaf)];
            try
            {
                branched = AudioCatalog.ReadBank(0, WwiseSoundBank.Build(sample, sample.Objects.First(item => item.Type == 4).Id, StringHash.Empty,
                    "test/switched", first, null, new(Switches: new Dictionary<uint, PcmWave> { [branch] = second })).Bytes);
                (container, value) = (found, branch);
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException) { }
        }
        Check(banks > 100 && attenuations > 100, "No installed attenuations were read.");
        if (branched is null || container is null) throw new InvalidDataException("No installed switch container took a branch wave.");
        var branchedNodes = WwiseSoundBank.Nodes(branched);
        var branchedContainer = branchedNodes.Single(node => node.SwitchGroup == container.SwitchGroup && node.Children.Count == container.Children.Count
            && node.SwitchValues.Count == container.SwitchValues.Count && node.Parent == container.Parent);
        var branchMedia = branched.EmbeddedMedia.ToDictionary(item => item.Id);
        var onBranch = branchedContainer.SwitchValues.Where(pair => pair.Value == value).Select(pair => branchedNodes.Single(node => node.Id == pair.Key))
            .Where(node => node.Type == 2).ToList();
        Check(branched.EmbeddedMedia.Count == 2 && onBranch.Count > 0
            && onBranch.All(node => branchMedia[node.SourceId].Embedded!.SequenceEqual(WwiseSoundBank.PcmWem(second))),
            "The switch branch does not play its own wave.");
        Console.WriteLine($"audio sound_options door_randoms={randoms.Count} waves=3 distance={before:0.#}->{before / 2:0.#} positioned={positioned.Count} "
            + $"sampled_banks={banks} attenuations={attenuations} switch_group={container.SwitchGroup:X8} branch={value:X8} leaves={onBranch.Count}");
    }

    /** Tommy's Matchbook's fire event: 144 leaves, a stop tail and three trigger-rate loops. */
    public static void Owned(PackageCatalog catalog)
    {
        const uint fireEvent = 0x80FF7609;
        byte[] header = catalog.Read(fireEvent);
        var template = AudioCatalog.ReadBank(U32(header, 20), catalog.Read(U32(header, 20)));
        var built = WwiseSoundBank.Build(template, U32(header, 8), U32(header, 12), "test/tommy", Wave(1), Wave(2));
        var bank = AudioCatalog.ReadBank(0, built.Bytes);
        var before = WwiseSoundBank.Nodes(template); var after = WwiseSoundBank.Nodes(bank);
        var ids = bank.Objects.Select(item => item.Id).ToHashSet();
        Check(bank.Objects.Count == template.Objects.Count && after.Count == before.Count
            && after.All(node => node.Parent == 0 || ids.Contains(node.Parent)), "The Tommy hierarchy did not survive.");
        var leaves = after.Where(node => node.Type == 2).ToList();
        Check(leaves.All(node => node.SourcePlugin == Pcm) && leaves.Count(node => node.SourceId == bank.EmbeddedMedia[0].Id) == 15
            && leaves.Count(node => node.SourceId == bank.EmbeddedMedia[1].Id) == 129, "Tommy leaves are not split into shots and tail.");
        var loops = after.Where(node => node.RateOfFireTransitions.Count > 0).ToList();
        Check(loops.Count == 3 && loops.All(node => node.TransitionMode == 5 && Math.Abs(At(node.RateOfFireTransitions[0], 20) - 0.05) < 1e-6),
            "Tommy loops do not play 1200 RPM at 20 rounds per second.");
        Console.WriteLine($"audio sound_bank=owned template={fireEvent:X8} objects={bank.Objects.Count} shots=15 tail=129 loops={loops.Count}");
    }

    private static double At(IReadOnlyList<(float X, float Y)> curve, double x)
    {
        if (x <= curve[0].X) return curve[0].Y;
        for (int index = 1; index < curve.Count; index++)
            if (x <= curve[index].X)
                return curve[index - 1].Y + (curve[index].Y - curve[index - 1].Y) * (x - curve[index - 1].X) / (curve[index].X - curve[index - 1].X);
        return curve[^1].Y;
    }

    private static PcmWave Wave(short value)
    {
        byte[] samples = new byte[480 * 4];
        for (int at = 0; at < samples.Length; at += 2) BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(at), (short)(value * (at % 50)));
        return new PcmWave(48000, 2, 16, samples);
    }

    /** A version-113 bank: mixer -> trigger-rate loop -> two shots, a tail under the mixer, and start and stop events. */
    private static byte[] Template(bool truncate = false)
    {
        var objects = new List<(byte Type, uint Id, byte[] Data)>
        {
            (7, Mixer, [.. Node(0), .. Children(Loop, Tail)]),
            (5, Loop, [.. Node(Mixer, RateCurve()), .. Container()]),
            (2, ShotA, [.. Source(900), .. Node(Loop)]),
            (2, ShotB, [.. Source(901), .. Node(Loop)]),
            (2, Tail, [.. Source(902), .. Node(Mixer)]),
            (3, PlayLoop, ActionData(0x0403, Loop, true)),
            (3, StopLoop, ActionData(0x0103, Loop, false)),
            (3, PlayTail, ActionData(0x0403, Tail, true)),
            (4, Start, EventData(PlayLoop)),
            (4, Stop, EventData(StopLoop, PlayTail))
        };
        if (truncate) objects[2] = (2, ShotA, objects[2].Data[..^1]);
        using var stream = new MemoryStream();
        void Chunk(string kind, byte[] data) { stream.Write(System.Text.Encoding.ASCII.GetBytes(kind)); stream.Write(BitConverter.GetBytes(data.Length)); stream.Write(data); }
        byte[] header = new byte[16]; Put(header, 0, 113); Put(header, 4, TemplateBank); Chunk("BKHD", header);
        using var table = new MemoryStream();
        table.Write(BitConverter.GetBytes(objects.Count));
        foreach (var (type, id, data) in objects)
        { table.WriteByte(type); table.Write(BitConverter.GetBytes(data.Length + 4)); table.Write(BitConverter.GetBytes(id)); table.Write(data); }
        Chunk("HIRC", table.ToArray());
        return stream.ToArray();
    }

    /** Shared node parameters with nothing overridden, then the given RTPC curves. */
    private static byte[] Node(uint parent, params byte[][] curves)
    {
        var bytes = new List<byte> { 0, 0, 0 };
        bytes.AddRange(BitConverter.GetBytes(0u)); bytes.AddRange(BitConverter.GetBytes(parent));
        bytes.AddRange(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        bytes.AddRange(BitConverter.GetBytes(0u));
        bytes.AddRange(BitConverter.GetBytes((ushort)curves.Length));
        foreach (byte[] curve in curves) bytes.AddRange(curve);
        return [.. bytes];
    }

    private static byte[] RateCurve()
    {
        var bytes = new List<byte>(BitConverter.GetBytes(RateOfFire)) { 0, 1, 0x07 };
        bytes.AddRange(BitConverter.GetBytes(77u)); bytes.Add(0); bytes.AddRange(BitConverter.GetBytes((ushort)2));
        foreach (var (x, y) in new[] { (10f, 0.1f), (15f, 0.06f) })
        { bytes.AddRange(BitConverter.GetBytes(x)); bytes.AddRange(BitConverter.GetBytes(y)); bytes.AddRange(BitConverter.GetBytes(4u)); }
        return [.. bytes];
    }

    /** Endless continuous random container at trigger rate with both shots in its playlist. */
    private static byte[] Container()
    {
        var bytes = new List<byte>(new byte[6]);
        bytes.AddRange(new byte[12]); bytes.AddRange(BitConverter.GetBytes((ushort)1));
        bytes.AddRange(new byte[] { 5, 0, 0, 0x08 });
        bytes.AddRange(Children(ShotA, ShotB));
        bytes.AddRange(BitConverter.GetBytes((ushort)2));
        foreach (uint id in new[] { ShotA, ShotB }) { bytes.AddRange(BitConverter.GetBytes(id)); bytes.AddRange(BitConverter.GetBytes(50000)); }
        return [.. bytes];
    }

    private static byte[] Children(params uint[] ids) => [.. BitConverter.GetBytes(ids.Length), .. ids.SelectMany(BitConverter.GetBytes)];

    /** A streamed Vorbis source. */
    private static byte[] Source(uint media) =>
        [.. BitConverter.GetBytes(0x00040001u), 2, .. BitConverter.GetBytes(media), .. BitConverter.GetBytes(100u), 0];

    private static byte[] ActionData(ushort type, uint target, bool play) =>
        [.. BitConverter.GetBytes(type), .. BitConverter.GetBytes(target), 0, 0, 0, 4, .. (play ? BitConverter.GetBytes(TemplateBank) : BitConverter.GetBytes(0u))];

    private static byte[] EventData(params uint[] actions) => [.. BitConverter.GetBytes(actions.Length), .. actions.SelectMany(BitConverter.GetBytes)];

    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static void Put(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidDataException("A broken sound bank template was accepted.");
    }
}
