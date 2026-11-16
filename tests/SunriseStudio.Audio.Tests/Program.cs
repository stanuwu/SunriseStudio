using System.Buffers.Binary;
using System.Text;
using SunriseStudio.Audio;
using SunriseStudio.Packages;

string temporary = Path.Combine(Path.GetTempPath(), "sunrise-audio-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    // --music-only <game root> runs the music authoring checks alone.
    int musicOnly = Array.IndexOf(args, "--music-only");
    if (musicOnly >= 0)
    {
        MusicAuthoringChecks.Synthetic(temporary);
        using var musicGame = PackageCatalog.Open(args[musicOnly + 1]);
        MusicAuthoringChecks.Owned(musicGame, temporary);
        return;
    }
    DecoderChecks.Synthetic();
    HierarchyChecks.Synthetic();
    ComposerChecks.Synthetic(temporary);
    MusicChecks.Synthetic(temporary);
    MusicAuthoringChecks.Synthetic(temporary);
    CueChecks.Synthetic(temporary);
    DestinationChecks.Synthetic(temporary);
    PlaylistChecks.Synthetic(temporary);
    ParityChecks.Synthetic(temporary);
    SoundBankChecks.Synthetic();
    byte[] pcm = Wave();
    string wavePath = Path.Combine(temporary, "test.wav"); File.WriteAllBytes(wavePath, pcm);
    var wave = PcmWave.Read(wavePath);
    Check(wave.SampleRate == 48000 && wave.Channels == 2 && Math.Abs(wave.DurationSeconds - .1) < 1e-6, "PCM format changed.");
    var broken = pcm.ToArray(); Put32(broken, 4, 100); File.WriteAllBytes(wavePath, broken);
    Reject(() => PcmWave.Read(wavePath)); File.WriteAllBytes(wavePath, pcm);
    broken = pcm.ToArray(); Put32(broken, 28, 7); File.WriteAllBytes(wavePath, broken);
    Reject(() => PcmWave.Read(wavePath)); File.WriteAllBytes(wavePath, pcm);
    byte[] bank = Bank(42, 99, pcm);
    var parsed = AudioCatalog.ReadBank(1, bank);
    Check(parsed.Version == 113 && parsed.Id == 42 && parsed.Objects.Count == 2
        && parsed.EmbeddedMedia.Single().Id == 77, "Wwise chunks lost identities.");
    broken = bank.ToArray(); Put32(broken, 4, uint.MaxValue); Reject(() => AudioCatalog.ReadBank(1, broken));
    Reject(() => AudioCatalog.ReadBank(1, bank[..^1]));
    Reject(() => AudioCatalog.ReadBank(1, bank.Concat(bank).ToArray()));
    string game = Path.Combine(temporary, "installation"); Directory.CreateDirectory(Path.Combine(game, "packages"));
    const uint first = 0x80A00000;
    byte[] eventBytes = new byte[84]; Put64(eventBytes, 0, 84); Put32(eventBytes, 8, 99);
    Put32(eventBytes, 20, first + 1); Put64(eventBytes, 24, 1); Put64(eventBytes, 32, 32);
    Put32(eventBytes, 60, 0x80809FBD); Put64(eventBytes, 64, 1); Put32(eventBytes, 72, 0x80800014);
    Put32(eventBytes, 80, first + 2);
    PackageWriter.WriteNew(Path.Combine(game, "packages", "w64_audio_0100_0.pkg"), 0x100,
        [new(AudioCatalog.EventClass, 8 << 9, eventBytes), new(42, (26 << 9) | (5 << 6), bank),
        new(77, (26 << 9) | (6 << 6), pcm), new(13, (26 << 9) | (4 << 6), Bank(13, 12, null)),
        new(0, (26 << 9) | (7 << 6), [1, 2, 3])]);
    using (var catalog = PackageCatalog.Open(game))
    {
        var reader = new AudioCatalog(catalog);
        var document = reader.Read(first);
        Check(document.EventId == 99 && document.Bank?.Tag == first + 1 && document.Media.Single().Tag == first + 2,
            "Event did not keep its explicit media source.");
        var direct = reader.Read(first + 1);
        Check(direct.Media.Single().Tag == first + 2, "Embedded prefetch replaced the full external stream.");
        Check(catalog.TryGetEntry(first + 4, out var notAudio) && !AudioCatalog.Supports(notAudio), "Havok entry became audio.");
        using var session = new AudioSession(catalog, first, cacheDirectory: Path.Combine(temporary, "cache"));
        Check(!session.DirectoryPath.StartsWith(game, StringComparison.OrdinalIgnoreCase), "Extracted data entered the source folder.");
        Check(File.ReadAllBytes(session.MediaPath(77)).SequenceEqual(pcm), "Extracted stream differs.");
        Reject(() => session.ExportMedia(77, Path.Combine(game, "forbidden.wem"), game));
        string project = Path.Combine(temporary, "project"); Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "sunrise.project.json"), "{}");
        Reject(() => session.ExportMedia(77, Path.Combine(project, "forbidden.wem"), game));
        string output = Path.Combine(temporary, "external.wem"); session.ExportMedia(77, output, game);
        Check(File.ReadAllBytes(output).SequenceEqual(pcm), "Raw export changed bytes.");
        // A canceled request does no work.
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await RejectAsync(() => session.InspectMedia(77, canceled.Token));
            await RejectAsync(() => session.DecodeMedia(77, canceled.Token));
        }
        Check(!Directory.EnumerateFiles(session.DirectoryPath, "*.wav").Any(), "Canceled decode left a WAV behind.");
        var decoded = await session.DecodeMedia(77);
        Check(PcmWave.Read(decoded).Samples.SequenceEqual(pcm.AsSpan(44).ToArray()), "Decoder changed PCM samples.");
        await RenderCancellation(temporary);
    }
    await DecodeCancellation(temporary, eventBytes, first);
    Console.WriteLine("audio chunks=checked event_links=checked prefetch=external pcm=checked project_export=rejected cancel=managed");
    int root = Array.IndexOf(args, "--game-root");
    if (root >= 0)
    {
        using var owned = PackageCatalog.Open(args[root + 1]);
        int musicOutput = Array.IndexOf(args, "--music-output");
        await MusicChecks.Owned(owned, temporary, musicOutput >= 0 ? args[musicOutput + 1] : null);
        int transitionOutput = Array.IndexOf(args, "--transition-output");
        await TransitionChecks.Owned(owned, temporary, transitionOutput >= 0 ? args[transitionOutput + 1] : null);
        int automationOutput = Array.IndexOf(args, "--automation-output");
        await AutomationChecks.Owned(owned, temporary, automationOutput >= 0 ? args[automationOutput + 1] : null);
        int cueOutput = Array.IndexOf(args, "--cue-output");
        await CueChecks.Owned(owned, temporary, cueOutput >= 0 ? args[cueOutput + 1] : null);
        int destinationOutput = Array.IndexOf(args, "--destination-output");
        await DestinationChecks.Owned(owned, temporary, destinationOutput >= 0 ? args[destinationOutput + 1] : null);
        int playlistOutput = Array.IndexOf(args, "--playlist-output");
        await PlaylistChecks.Owned(owned, temporary, playlistOutput >= 0 ? args[playlistOutput + 1] : null);
        await ParityChecks.Owned(owned, temporary);
        MusicAuthoringChecks.Owned(owned, temporary);
        SoundBankChecks.Owned(owned);
        SoundBankChecks.OwnedOptions(owned);
        string directory, eventComposition;
        uint bankTag;
        using (var session = new AudioSession(owned, 0x80B4EEDC, cacheDirectory: Path.Combine(temporary, "cache")))
        {
            directory = session.DirectoryPath; bankTag = session.Document.Bank!.Tag;
            Check(session.Document.Bank is { Version: 113, Id: 4003848189 } && session.Document.Media.Count == 8
                && session.Document.Diagnostics.Count == 0, "Owned event links changed.");
            var item = session.Document.Media[0];
            var info = await session.InspectMedia(item.Id);
            Check(info.SampleRate == 48000 && info.Channels == 2 && info.Samples > 0, "Owned stream metadata failed.");
            var decoded = PcmWave.Read(await session.DecodeMedia(item.Id));
            Check(decoded.Samples.Any(value => value != 0), "Owned stream is silent.");
            await session.GenerateCompositions();
            static bool Layered(AudioEventNode node) => node is AudioEventGroup group
                && (group.Kind == AudioEventGroupKind.Layer && group.Items.Count > 1 || group.Items.Any(Layered));
            Check(session.Compositions.Count == 1 && Layered(session.Compositions[0].Plan.Root)
                && session.Compositions[0].Plan.Media().Count == 8, "Owned layered event composition was not generated.");
            eventComposition = session.Compositions[0].Name;
            var mixed = PcmWave.Read(await session.DecodeComposition(0));
            Check(mixed.Samples.Length > 0 && mixed.Samples.Any(value => value != 0), "Owned composition did not decode.");
            var inspected = await session.InspectComposition(0);
            Check(Math.Abs(inspected.DurationSeconds - mixed.DurationSeconds) < 1.0 / 48000, "Composition inspect length differs from its render.");
            string set = Path.Combine(temporary, "composition-set"); await session.ExportCompositionSet(set, owned.GameRoot);
            Check(Directory.GetFiles(Path.Combine(set, "compositions"), "0001 *.wav").Length == 1
                && File.ReadAllText(Path.Combine(set, "routing.txt")).StartsWith("== 0001 ", StringComparison.Ordinal)
                && File.Exists(Path.Combine(set, "4003848189.bnk")), "Composition set export is incomplete.");
            Console.WriteLine($"audio owned_event=80B4EEDC bank_objects={session.Document.Bank!.Objects.Count} media=8 compositions={session.Compositions.Count} stream_seconds={decoded.DurationSeconds:0.00} mix_seconds={mixed.DurationSeconds:0.00}");
        }
        Check(!Directory.Exists(directory), "Audio working directory survived disposal.");
        using (var bankSession = new AudioSession(owned, bankTag, cacheDirectory: Path.Combine(temporary, "cache")))
        {
            await bankSession.GenerateCompositions();
            Check(bankSession.Document.EventId is null && bankSession.Compositions.Any(value => value.Name == eventComposition),
                "Bank compositions do not list the bank's event.");
            Console.WriteLine($"audio owned_bank={bankTag:X8} compositions={bankSession.Compositions.Count}");
        }
    }
}
catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
finally
{
    if (Path.GetFullPath(temporary).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(temporary, true);
}

static byte[] Bank(uint id, uint eventId, byte[]? embedded)
{
    using var result = new MemoryStream();
    void Chunk(string name, byte[] value)
    { result.Write(Encoding.ASCII.GetBytes(name)); var size = new byte[4]; Put32(size, 0, (uint)value.Length); result.Write(size); result.Write(value); }
    byte[] header = new byte[16]; Put32(header, 0, 113); Put32(header, 4, id); Chunk("BKHD", header);
    byte[] hirc = new byte[31]; Put32(hirc, 0, 2);
    hirc[4] = 4; Put32(hirc, 5, 4); Put32(hirc, 9, eventId);
    hirc[13] = 2; Put32(hirc, 14, 13); Put32(hirc, 18, 500); Put32(hirc, 22, 0x00040001);
    hirc[26] = 2; Put32(hirc, 27, 77); Chunk("HIRC", hirc);
    if (embedded is not null)
    {
        byte[] index = new byte[12]; Put32(index, 0, 77); Put32(index, 8, (uint)embedded.Length);
        Chunk("DIDX", index); Chunk("DATA", embedded);
    }
    return result.ToArray();
}
static byte[] Wave()
{
    byte[] value = new byte[44 + 4800 * 4]; "RIFF"u8.CopyTo(value); Put32(value, 4, (uint)value.Length - 8);
    "WAVEfmt "u8.CopyTo(value.AsSpan(8)); Put32(value, 16, 16);
    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(22), 2);
    Put32(value, 24, 48000); Put32(value, 28, 192000);
    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(32), 4); BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(34), 16);
    "data"u8.CopyTo(value.AsSpan(36)); Put32(value, 40, (uint)value.Length - 44);
    for (int i = 44; i < value.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(value.AsSpan(i), (short)(i % 100));
    return value;
}
static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
static void Put64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
static void Reject(Action action)
{
    try { action(); } catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return; }
    throw new InvalidDataException("Invalid audio input or output was accepted.");
}
static async Task RejectAsync(Func<Task> action)
{
    try { await action(); } catch (OperationCanceledException) { return; }
    throw new InvalidDataException("Canceled audio work ran successfully.");
}
/** A five-minute render canceled in flight stops promptly and writes no output. */
static async Task RenderCancellation(string temporary)
{
    byte[] second = new byte[48000 * 2];
    for (int i = 0; i < second.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(second.AsSpan(i), 1000);
    var sources = new Dictionary<uint, PcmWave> { [1] = new(48000, 1, 16, second) };
    var plan = new AudioEventPlan(new AudioEventSound(1, "Sound 1", 0, 1, 0, 300), []);
    string output = Path.Combine(temporary, "canceled-render.wav");
    using var inFlight = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
    var clock = System.Diagnostics.Stopwatch.StartNew();
    await RejectAsync(() => Task.Run(() => AudioEventRender.Render(plan, sources, output, inFlight.Token)));
    Check(clock.Elapsed < TimeSpan.FromSeconds(5) && !File.Exists(output), "Canceled render did not stop promptly or left output.");
}
/** A downmix decode canceled mid-run stops promptly and leaves no cached WAV. */
static async Task DecodeCancellation(string temporary, byte[] eventBytes, uint first)
{
    byte[] wem = SurroundWem(60);
    _ = WemDecoder.Decode(wem, 2, CancellationToken.None);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    Check(WemDecoder.Decode(wem, 2, CancellationToken.None) is { Channels: 2 } full
        && full.Samples.Length == 48000 * 60 * 4, "Surround PCM did not mix down to stereo.");
    // Cancel a quarter of the way through a warm full decode.
    var delay = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond, clock.Elapsed.Ticks / 4));
    using (var inFlight = new CancellationTokenSource(delay))
    {
        clock.Restart();
        await RejectAsync(() => Task.Run(() => WemDecoder.Decode(wem, 2, inFlight.Token)));
        Check(clock.Elapsed < TimeSpan.FromSeconds(5), "Canceled decode did not stop promptly.");
    }
    string game = Path.Combine(temporary, "surround"); Directory.CreateDirectory(Path.Combine(game, "packages"));
    PackageWriter.WriteNew(Path.Combine(game, "packages", "w64_audio_0100_0.pkg"), 0x100,
        [new(AudioCatalog.EventClass, 8 << 9, eventBytes), new(42, (26 << 9) | (5 << 6), Bank(42, 99, null)),
        new(77, (26 << 9) | (6 << 6), wem)]);
    using var catalog = PackageCatalog.Open(game);
    using var session = new AudioSession(catalog, first, cacheDirectory: Path.Combine(temporary, "surround-cache"));
    using (var inFlight = new CancellationTokenSource(delay))
        await RejectAsync(() => session.DecodeMedia(77, inFlight.Token));
    Check(!Directory.EnumerateFiles(session.DirectoryPath, "*.wav").Any(), "Canceled decode left a WAV behind.");
}
/** 16-bit 5.1 PCM WEM with a speaker mask, so decoding takes the downmix path. */
static byte[] SurroundWem(int seconds)
{
    const int channels = 6, rate = 48000;
    byte[] value = new byte[52 + seconds * rate * channels * 2]; "RIFF"u8.CopyTo(value); Put32(value, 4, (uint)value.Length - 8);
    "WAVEfmt "u8.CopyTo(value.AsSpan(8)); Put32(value, 16, 0x18);
    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(20), 0xFFFE); BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(22), channels);
    Put32(value, 24, rate); Put32(value, 28, rate * channels * 2);
    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(32), channels * 2); BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(34), 16);
    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(36), 6); BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(38), 16);
    Put32(value, 40, 0x3F);
    "data"u8.CopyTo(value.AsSpan(44)); Put32(value, 48, (uint)value.Length - 52);
    for (int i = 52; i < value.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(value.AsSpan(i), (short)(i % 2000 - 1000));
    return value;
}
