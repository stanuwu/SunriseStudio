using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using SunriseStudio.Live;
using SunriseStudio.Packages;

/** Command protocol v2: spec constants, manifest bytes, and a fake game that answers every kind. */
static class CommandChecks
{
    public static void Run()
    {
        Constants();
        ManifestBytes();
        FakeGame();
        Console.WriteLine("live_command version=2 manifest=golden crc32=standard reload_tags=answered patch_tags=guarded reload_mod=v1_header reload_bubble=message");
    }

    private static void Constants()
    {
        Check(LiveCommandProtocol.Version == 2 && LiveCommandProtocol.OldestVersion == 1 && LiveCommandProtocol.MappingBytes == 320
            && LiveCommandProtocol.KindOffset == 32 && LiveCommandProtocol.StatusOffset == 36 && LiveCommandProtocol.GamePidOffset == 40
            && LiveCommandProtocol.ModIdOffset == 48 && LiveCommandProtocol.MessageOffset == 120
            && LiveCommandProtocol.ReloadTagsKind == 2 && LiveCommandProtocol.PatchTagsKind == 3 && LiveCommandProtocol.ReloadBubbleKind == 4
            && LiveCommandProtocol.QueryResidentKind == 5, "Command constants differ from the spec.");
        Check(LiveCommandProtocol.VersionFor(LiveCommandProtocol.ReloadModKind) == 1 && LiveCommandProtocol.VersionFor(LiveCommandProtocol.ReloadTagsKind) == 2,
            "Reload mod must keep the version 1 header.");
        Check(LiveReloadManifest.MappingName(12, 34) == @"Local\SunriseStudio.Manifest.12.34", "The manifest mapping name differs from the spec.");
        Check(Crc32.Compute("123456789"u8) == 0xCBF43926 && LiveReloadEntry.SpawnTime == 4, "The patch guard is not the standard CRC-32.");
    }

    /** One replace entry with one 3-byte patch range, laid out by hand from the spec. */
    private static void ManifestBytes()
    {
        var entry = new LiveReloadEntry(0x80A00001, 0x80809164, LiveEditKind.Replace, LiveReloadEntry.LayoutPreserving, 0x40, 0x0C00, 1,
            [new LivePatchRange(8, [1, 2, 3], 0xDEADBEEF)]);
        byte[] bytes = LiveReloadManifest.Encode(7, 3, [entry]);
        string expected = "53534D46" + "0100" + "0000" + "07000000" + "01000000" + "03000000" + "10000000"
            + "4000000000000000" + "6000000000000000" + "7000000000000000" + new string('0', 32)
            + "0100A080" + "64918080" + "02" + "01" + "0100" + "00000000" + "40000000" + "000C0000" + "01000000" + "00000000"
            + "08000000" + "03000000" + "EFBEADDE" + "01020300"
            + "0000000000000000";
        Check(Convert.ToHexString(bytes) == expected, "Manifest bytes differ from the spec layout:\n" + Convert.ToHexString(bytes));
        var decoded = LiveReloadManifest.Decode(bytes);
        Check(decoded.Request == 7 && decoded.Generation == 3 && decoded.Entries.Single() is { Tag: 0x80A00001, Kind: LiveEditKind.Replace } back
            && back.Patches.Single() is { Offset: 8, OldCrc32: 0xDEADBEEF } range && range.Bytes.SequenceEqual(new byte[] { 1, 2, 3 }),
            "The manifest does not decode to what was encoded.");
        LiveReloadManifest.WriteResult(bytes, decoded.ResultsOffset, 0, new(LiveReloadOutcome.Applied, 1, 0));
        Check(LiveReloadManifest.ReadResults(bytes).Single() is { Outcome: LiveReloadOutcome.Applied, Tier: 1 }, "A result does not read back.");
        byte[] broken = bytes.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(broken.AsSpan(0x28), 1 << 20);
        Throws(() => LiveReloadManifest.Decode(broken), "A manifest pointing outside itself was read.");
    }

    /**
     * A responder thread plays the game: it opens the command mapping by this process id, answers
     * each request once, and for a manifest kind writes one result per entry.
     */
    private static void FakeGame()
    {
        int self = Environment.ProcessId;
        var seen = new List<(uint Kind, uint Version, string Message)>();
        var resident = new Dictionary<uint, byte[]> { [0x80A00003] = [1, 2, 3, 4, 5, 6, 7, 8] };
        using var stop = new CancellationTokenSource();
        var responder = Task.Run(() =>
        {
            uint answered = 0;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var mapping = MemoryMappedFile.OpenExisting(LiveCommandProtocol.MappingName(self));
                    using var view = mapping.CreateViewAccessor(0, LiveCommandProtocol.MappingBytes);
                    if (view.ReadUInt32(0) != LiveCommandProtocol.Magic) { Thread.Sleep(5); continue; }
                    uint request = view.ReadUInt32(LiveCommandProtocol.RequestOffset);
                    if (request == 0 || request == answered) { Thread.Sleep(5); continue; }
                    uint kind = view.ReadUInt32(LiveCommandProtocol.KindOffset), version = view.ReadUInt32(4);
                    byte[] message = new byte[LiveCommandProtocol.MessageBytes];
                    view.ReadArray(LiveCommandProtocol.MessageOffset, message, 0, message.Length);
                    lock (seen) seen.Add((kind, version, System.Text.Encoding.UTF8.GetString(message).TrimEnd('\0')));
                    uint reloaded = 0;
                    if (kind is LiveCommandProtocol.ReloadTagsKind or LiveCommandProtocol.PatchTagsKind)
                    {
                        using var manifest = MemoryMappedFile.OpenExisting(LiveReloadManifest.MappingName(self, request));
                        using var stream = manifest.CreateViewStream();
                        byte[] bytes = new byte[stream.Length];
                        stream.ReadExactly(bytes);
                        var decoded = LiveReloadManifest.Decode(bytes);
                        for (int index = 0; index < decoded.Entries.Count; index++)
                            LiveReloadManifest.WriteResult(bytes, decoded.ResultsOffset, index, kind == LiveCommandProtocol.PatchTagsKind
                                ? Patch(resident, decoded.Entries[index]) : index == 0
                                ? new(LiveReloadOutcome.Applied, 1, 0) : new(LiveReloadOutcome.NeedsRestart, 4, 0));
                        stream.Position = 0;
                        stream.Write(bytes);
                        reloaded = (uint)decoded.Entries.Count;
                    }
                    view.Write(LiveCommandProtocol.StatusOffset, (uint)LiveCommandStatus.Ok);
                    view.Write(LiveCommandProtocol.GamePidOffset, (uint)self);
                    view.Write(LiveCommandProtocol.ReloadedOffset, reloaded);
                    Thread.MemoryBarrier();
                    view.Write(LiveCommandProtocol.ResponseOffset, request);
                    answered = request;
                }
                catch (FileNotFoundException) { Thread.Sleep(5); }
            }
        });
        try
        {
            using var link = new LiveCommandConnection(self);
            var entries = new[]
            {
                new LiveReloadEntry(0x80A00001, 0x80809164, LiveEditKind.Replace, 0, 0x40, 0x0C00, 1, []),
                new LiveReloadEntry(0x80A00002, 0x808071A7, LiveEditKind.Replace, 0, 0x80, 0x0C00, 2, []),
            };
            var tags = link.ReloadTagsAsync("test.mod", 2, entries, CancellationToken.None).GetAwaiter().GetResult();
            Check(tags.Reply.Status == LiveCommandStatus.Ok && tags.Reply.Reloaded == 2
                && tags.Results.Select(x => x.Outcome).SequenceEqual([LiveReloadOutcome.Applied, LiveReloadOutcome.NeedsRestart]),
                "Per-tag results did not come back.");
            var mod = link.ReloadModAsync("test.mod", CancellationToken.None).GetAwaiter().GetResult();
            Check(mod.Status == LiveCommandStatus.Ok, "Reload mod was not answered.");
            PatchTags(link, resident);
            link.ReloadBubbleAsync("test.mod", 0xAABBCCDD, CancellationToken.None).GetAwaiter().GetResult();
            lock (seen)
                Check(seen.Select(x => (x.Kind, x.Version)).SequenceEqual([(2u, 2u), (1u, 1u), (3u, 2u), (3u, 2u), (4u, 2u)]) && seen[4].Message == "AABBCCDD",
                    "The game saw the wrong kinds, versions or message: " + string.Join(", ", seen));
        }
        finally
        {
            stop.Cancel();
            responder.GetAwaiter().GetResult();
        }
    }

    /** A patch with the right guard lands; the same patch sent again finds the new bytes and is refused. */
    private static void PatchTags(LiveCommandConnection link, Dictionary<uint, byte[]> resident)
    {
        byte[] before = [3, 4];
        var entry = new LiveReloadEntry(0x80A00003, 0x8080713E, LiveEditKind.Patch,
            LiveReloadEntry.LayoutPreserving | LiveReloadEntry.PokeSafe, 8, 0x0C00, 3, [new LivePatchRange(2, [9, 9], Crc32.Compute(before))]);
        var first = link.PatchTagsAsync("test.mod", 3, [entry], CancellationToken.None).GetAwaiter().GetResult();
        Check(first.Results.Single() is { Outcome: LiveReloadOutcome.Applied, Tier: 2 }
            && resident[0x80A00003].SequenceEqual(new byte[] { 1, 2, 9, 9, 5, 6, 7, 8 }), "A guarded patch did not apply.");
        var second = link.PatchTagsAsync("test.mod", 4, [entry], CancellationToken.None).GetAwaiter().GetResult();
        Check(second.Results.Single().Outcome == LiveReloadOutcome.GuardMismatch
            && resident[0x80A00003].SequenceEqual(new byte[] { 1, 2, 9, 9, 5, 6, 7, 8 }), "A stale guard was not refused, or it wrote.");
    }

    /** The game's Tier 2 rule: every guard holds before any byte is written. */
    private static LiveReloadResult Patch(Dictionary<uint, byte[]> resident, LiveReloadEntry entry)
    {
        lock (resident)
        {
            if (!resident.TryGetValue(entry.Tag, out byte[]? bytes)) return new(LiveReloadOutcome.NotResident, 2, 0);
            if (entry.Patches.Any(range => range.Offset + range.Bytes.Length > bytes.Length
                || Crc32.Compute(bytes.AsSpan((int)range.Offset, range.Bytes.Length)) != range.OldCrc32))
                return new(LiveReloadOutcome.GuardMismatch, 2, 0);
            foreach (var range in entry.Patches) range.Bytes.CopyTo(bytes, (int)range.Offset);
            return new(LiveReloadOutcome.Applied, 2, 0);
        }
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception(message);
    }

    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
}
