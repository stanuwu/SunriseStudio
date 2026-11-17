using System.Text;
using static SunriseStudio.Authoring.ActivityRegistrationTables;

namespace SunriseStudio.Authoring;

/** Scenario symbols and map stems use FNV-1, with the native suffix-sharing string pool. */
public static class ActivityRegistrationIdentity
{
    public static uint Hash(string text)
    {
        uint value = 0x811C9DC5;
        foreach (byte item in Encoding.UTF8.GetBytes(text)) value = unchecked(value * 0x01000193) ^ item;
        return value;
    }

    public static byte[] RebindScenario(byte[] original, string sourceName, string name, string stem)
    {
        var data = Sized(original, 140);
        Require(data.U32(8) == Hash(sourceName) && data.U32(12) == 0, "Scenario identity differs from its activity.");
        int[] symbols = Rows(data, 64, 0x8080924A, 16);
        Require(symbols.Length is > 0 and <= 8192, "Scenario symbols are absent.");
        var fields = new List<int> { 48 };
        var seen = new HashSet<uint>();
        int primary = -1;
        foreach (int at in symbols)
        {
            string text = Text(data, Relative(data, at + 8, 96));
            Require(data.U32(at) == Hash(text) && data.U32(at + 4) == 0 && data.U32(at) != Hash(name)
                && seen.Add(data.U32(at)), "Scenario symbol identity collides.");
            if (text == sourceName) { Require(primary < 0, "Duplicate primary scenario symbol."); primary = at; }
            fields.Add(at + 8);
        }
        Require(primary >= 0, "Scenario primary symbol is absent.");
        foreach (int at in Rows(data, 96, 0x8080924B, 24)) // sequence references
        {
            Require(data.U32(at + 20) == 0 && (data.I64(at + 8) == 0) == (data.U32(at + 16) == uint.MaxValue),
                "SCENARIO_STRING_PROFILE: unsupported sequence string.");
            if (data.I64(at + 8) != 0) fields.Add(at + 8);
        }
        fields.Sort();
        int[] targets = fields.Select(field => Relative(data, field, 96)).ToArray();
        int tail = targets[0];
        Require(tail >= 12 && data.U32(tail - 12) == 0x80800065 && data.U64(tail - 8) == (ulong)(original.Length - tail),
            "SCENARIO_STRING_PROFILE: unsupported scenario string pool.");
        string[] texts = targets.Select(target => Text(data, target, original.Length - target)).ToArray();
        var before = Pool(texts);
        Require(before.Bytes.AsSpan().SequenceEqual(original.AsSpan(tail)) && before.Starts.Select(at => at + tail).SequenceEqual(targets),
            "SCENARIO_STRING_PROFILE: scenario pool is not canonical.");
        Require(data.U32(32) == Hash(texts[0]), "Scenario stem hash differs.");
        texts[fields.IndexOf(48)] = stem; texts[fields.IndexOf(primary + 8)] = name;
        var pool = Pool(texts); var output = new ActivityBytes(original[..tail]); output.Add(pool.Bytes);
        for (int i = 0; i < fields.Count; i++) output.I64(fields[i], tail + pool.Starts[i] - fields[i]);
        output.I64(tail - 8, pool.Bytes.Length); output.U32(8, Hash(name)); output.U32(32, Hash(stem)); output.U32(primary, Hash(name));
        return output.Finish();
    }

    private static (byte[] Bytes, int[] Starts) Pool(IEnumerable<string> texts)
    {
        var bytes = new List<byte>(); var placed = new List<(int At, string Text)>(); var starts = new List<int>();
        foreach (string text in texts)
        {
            Require(text.Length > 0 && !text.Contains('\0') && text.All(c => c < 128), "Invalid scenario pool text.");
            int at = -1;
            foreach (var previous in placed)
                if (previous.Text.EndsWith(text, StringComparison.Ordinal)) { at = previous.At + previous.Text.Length - text.Length; break; }
            if (at < 0) { at = bytes.Count; placed.Add((at, text)); bytes.AddRange(Encoding.ASCII.GetBytes(text + "\0")); }
            starts.Add(at);
        }
        return (bytes.ToArray(), starts.ToArray());
    }

    public static byte[] RebindSlice(byte[] original, string stem, uint registry)
    {
        var data = Sized(original, 42);
        Require(data.I64(8) == 32 && data.U32(24) == 0 && data.U32(28) == 0x80800065
            && data.U64(32) == (ulong)(original.Length - 40) && original[^1] == 0
            && !original.AsSpan(40, original.Length - 41).Contains((byte)0), "Unsupported scenario slice layout.");
        var output = new ActivityBytes(original[..40]); output.Add(Encoding.ASCII.GetBytes(stem + "\0"));
        output.I64(32, stem.Length + 1); output.U32(20, registry); return output.Finish();
    }

    /** Thirteen language slots share the authored text, matching localized_strings.build. */
    public static (byte[] Container, byte[] Data) LocalizedText(IReadOnlyDictionary<uint, string> entries, uint dataTag)
    {
        Require(entries.Count is > 0 and <= 65535 && !entries.ContainsKey(0x811C9DC5), "Invalid localized keys.");
        var sorted = entries.OrderBy(pair => pair.Key).ToArray();
        var container = new ActivityBytes(80);
        for (int i = 0; i < 13; i++) container.U32(24 + i * 4, dataTag);
        int[] keys = container.Array(8, 0x80800070, 4, new byte[sorted.Length * 4], true);
        for (int i = 0; i < keys.Length; i++) container.U32(keys[i], sorted[i].Key);
        var data = new ActivityBytes(88);
        int[] parts = data.Array(8, 0x80809A90, 32, new byte[sorted.Length * 32], true);
        var encoded = new List<byte[]>(); var shifts = new List<ushort>(); var counts = new List<int>();
        foreach (var (_, text) in sorted)
        {
            try { _ = new UTF8Encoding(false, true).GetByteCount(text); }
            catch (EncoderFallbackException error) { throw new InvalidDataException("Localized text contains an invalid surrogate.", error); }
            int[] points = text.EnumerateRunes().Select(rune => rune.Value).ToArray();
            Require(points.Length is > 0 and < 65536 && !points.Contains(0), "Invalid localized text.");
            int shift = points.Min() - 1;
            Require(shift < 65536, "Localized text exceeds native shift.");
            byte[] bytes = Encoding.UTF8.GetBytes(string.Concat(points.Select(point => char.ConvertFromUtf32(point - shift))));
            Require(bytes.Length < 65536, "Localized text is too long.");
            encoded.Add(bytes); shifts.Add((ushort)shift); counts.Add(points.Length);
        }
        data.Array(40, 0x80800006, 2, new byte[2], true);
        int payload = data.Array(56, 0x80800005, 1, [.. encoded.SelectMany(bytes => bytes), 0], true)[0];
        int[] strings = data.Array(72, 0x80809A8E, 16, new byte[sorted.Length * 16], true);
        for (int i = 0; i < parts.Length; i++)
        {
            int at = parts[i]; data.I64(at + 8, payload - at - 8); data.U32(at + 16, 0x811C9DC5);
            data.U16(at + 20, (ushort)encoded[i].Length); data.U16(at + 22, (ushort)counts[i]);
            data.U16(at + 24, shifts[i]); data.U16(at + 26, 0xF400);
            data.I64(strings[i], at - strings[i]); data.I64(strings[i] + 8, 1); payload += encoded[i].Length;
        }
        return (container.Finish(), data.Finish());
    }
}
