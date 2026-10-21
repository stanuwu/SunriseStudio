using System.Buffers.Binary;
using System.Text;

namespace SunriseStudio.Formats.NewWorld;

/** One scenario state per bubble: hash, enabled flag, public word (1 hosts the public link) and tail word. */
public sealed record ScenarioState(uint StateHash, bool Enabled, uint PublicWord, uint TailWord);

/** Installed player-service configs: type 18 lifetime_timer (80C01DA0), 17 lifetime (80F9FE17), 13 players[0] (80F9FE14). */
public sealed record PlayerServiceProfile(byte[] Timer, byte[] Lifetime, byte[] Players);

/**
 * The map-level scenario and activity: the 80809994 scenario with one state per bubble, its slices and registries,
 * the 80808AAE activity root, the null transition, the empty 80809440, the shared player-service object and the
 * localized area names (World Studio generated_scenario, generated_registry, generated_player_services,
 * generated_scenario_policy, localized_strings).
 */
public static class ScenarioWriter
{
    public const uint ScenarioClass = 0x80809994, SliceClass = 0x8080925B, ActivityClass = 0x80808AAE,
        TransitionClass = 0x80809BA3, ScheduleClass = 0x80809440, RegistryClass = 0x8080925E, ObjectClass = 0x80809462,
        IndirectClass = 0x80809468, RedirectClass = 0x80809B14, ConfigClass = 0x80809C36, ContainerClass = 0x80809A88,
        DataClass = 0x80809A8A;
    private const uint Sentinel = 0x811C9DC5, StringMarker = 0x80800065, PlayerRegistryKey = 0xC2DACAF7, HeaderTag = 0x80C70B90;
    /** Installed tags of the three player-service configs, in slot order 18, 17, 13. */
    public static readonly uint[] PlayerServiceSources = [0x80C01DA0, 0x80F9FE17, 0x80F9FE14];
    private static readonly int[] Kinds = [18, 17, 13];

    private static int Descriptor(int kind) => kind switch { 17 => 0x718, 13 => 0x878, _ => 0x238 };
    private static int[] SelfFields(int kind) => kind switch
    {
        17 => [0x70, 0xB8, 0x110, 0x170, 0x718],
        13 => [0x70, 0xA0, 0xB8, 0xF8, 0x110, 0x1F8, 0x210, 0x228, 0x878],
        _ => [0x70, 0xB8, 0x110, 0x238]
    };
    private static uint Component(int kind) => kind switch { 17 => 0x80809915, 13 => 0x80804F2D, _ => 0x80809917 };
    private static uint Sense(int kind) => kind == 13 ? 0x80804F2F : uint.MaxValue;
    private static uint Auth(int kind) => kind switch { 17 => 0x8080991A, 13 => 0x80804F30, _ => 0x80809919 };

    private static uint U32(byte[] raw, int at) => BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at));

    /** Checks an installed config's owner fields, header, descriptor and client references against its profile. */
    public static void CheckPlayerService(byte[] raw, int kind, uint origin)
    {
        int at = Descriptor(kind);
        if (raw.Length < at + 96 || BinaryPrimitives.ReadUInt64LittleEndian(raw) != (ulong)raw.Length)
            throw new InvalidDataException($"Installed {origin:X8} has an unknown player service layout.");
        var occurrences = Enumerable.Range(0, (raw.Length - 3 + 3) / 4).Select(i => 4 * i).Where(p => p + 4 <= raw.Length && U32(raw, p) == origin);
        var keys = Enumerable.Range(0, raw.Length / 4).Select(i => 4 * i).Where(p => U32(raw, p) == PlayerRegistryKey);
        int[] expectedKeys = kind == 17 ? [at + 48, at + 88] : [at + 48];
        if (!occurrences.SequenceEqual(SelfFields(kind)) || U32(raw, 0x44) != HeaderTag
            || U32(raw, at) != origin || U32(raw, at + 4) != Component(kind) || U32(raw, at + 8) != 0x70
            || U32(raw, at + 68) != Sense(kind) || U32(raw, at + 72) != Auth(kind) || !keys.SequenceEqual(expectedKeys)
            || kind == 17 && (BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at + 92)) != 18
                || BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at + 94)) != 2))
            throw new InvalidDataException($"Installed {origin:X8} differs from the pinned player service profile.");
    }

    private static byte[] PlayerConfig(byte[] installed, int kind, uint registryKey, int slot)
    {
        CheckPlayerService(installed, kind, PlayerServiceSources[slot]);
        var raw = installed.ToArray();
        int at = Descriptor(kind);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(at + 48), registryKey);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(at + 54), (ushort)slot);
        if (kind == 17)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(at + 88), registryKey);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(at + 92), 18);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(at + 94), 0);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x44), 0);
        foreach (int field in SelfFields(kind)) BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(field), 0);
        return raw;
    }

    /**
     * One global object with the native player, lifetime and lifetime-timer slots, each through a handle, a redirect
     * and its rebound installed config. Returns the object key.
     */
    public static string AddPlayerServices(GeneratedGraph graph, string prefix, PlayerServiceProfile profile, uint headerWord, uint registryKey)
    {
        if (registryKey is 0 or uint.MaxValue or Sentinel) throw new InvalidDataException("The player service registry key is absent.");
        var raw = new NativeBuffer(80);
        raw.Put32(8, headerWord); raw.Put32(12, registryKey);
        raw.Put64(24, 3);
        raw.Put32(48, uint.MaxValue);
        raw.Put64(72, 1);
        raw.AppendPadded(32, 0x80809B3A, 8,
        [
            NativeRow.U32s(18, MapIdentityWriter.NameHash("lifetime_timer")), NativeRow.U32s(17, MapIdentityWriter.NameHash("lifetime")),
            NativeRow.U32s(13, MapIdentityWriter.NameHash("players[0]"))
        ]);
        int block = raw.AppendPadded(56, 0x80809464, 24, [new byte[24]])[0];
        raw.PutI32(block, -1); raw.Put32(block + 4, Sentinel);
        var handles = raw.AppendPadded(block + 8, 0x80809466, 4, [new byte[4], new byte[4], new byte[4]]);
        raw.SetSize();
        string objectKey = prefix + "_object", header = prefix + "_header";
        graph.Add(objectKey, raw.ToArray(), ObjectClass);
        var headerRaw = new NativeBuffer(48);
        headerRaw.Put32(8, Sentinel); headerRaw.Put32(24, uint.MaxValue);
        headerRaw.SetSize();
        graph.Add(header, headerRaw.ToArray(), 0x80809BBB);
        byte[][] installed = [profile.Timer, profile.Lifetime, profile.Players];
        for (int index = 0; index < 3; index++)
        {
            int kind = Kinds[index];
            string config = $"{prefix}_config_{kind}", redirect = $"{prefix}_redirect_{kind}", handle = $"{prefix}_handle_{kind}";
            graph.Add(config, PlayerConfig(installed[index], kind, registryKey, index), ConfigClass);
            var redirectRaw = new NativeBuffer(80);
            redirectRaw.Put32(8, Sentinel); redirectRaw.Put32(20, Component(kind)); redirectRaw.Put32(24, 0x70);
            redirectRaw.SetSize();
            graph.Add(redirect, redirectRaw.ToArray(), RedirectClass);
            var handleRaw = new NativeBuffer(32);
            handleRaw.Put32(8, uint.MaxValue);
            int field = handleRaw.AppendPadded(16, 0x80809B13, 4, [new byte[4]])[0];
            handleRaw.SetSize();
            graph.Add(handle, handleRaw.ToArray(), IndirectClass);
            graph.Reference(objectKey, handles[index], handle);
            graph.Reference(handle, field, redirect);
            graph.Reference(redirect, 12, config);
            graph.Reference(redirect, 16, config);
            graph.Reference(config, 0x44, header);
            foreach (int at in SelfFields(kind)) graph.Reference(config, at, config);
        }
        return objectKey;
    }

    /** A slotless placed object naming a bare handle at +0xA0, which names the placement table at +8. */
    public static string AddPlacementObject(GeneratedGraph graph, string prefix, string placement, uint headerWord, uint registryKey, int ordinal)
    {
        if (registryKey is 0 or uint.MaxValue or Sentinel) throw new InvalidDataException("The object registry key is absent.");
        MapIdentityWriter.Require(graph, placement, MapIdentityWriter.TableClass, 8);
        var raw = new NativeBuffer(164);
        raw.Put32(8, headerWord); raw.Put32(12, registryKey);
        raw.Put32(48, uint.MaxValue);
        raw.Put64(56, 1); raw.PutI64(64, 32);
        raw.Put32(92, NativeBuffer.ArrayMarker); raw.Put64(96, 1); raw.Put32(104, 0x80809464);
        raw.PutI32(112, ordinal); raw.Put32(116, Sentinel); raw.Put64(120, 1); raw.PutI64(128, 16);
        raw.Put32(140, NativeBuffer.ArrayMarker); raw.Put64(144, 1); raw.Put32(152, 0x80809466);
        raw.SetSize();
        string objectKey = prefix + "_object", handle = prefix + "_handle";
        graph.Add(objectKey, raw.ToArray(), ObjectClass);
        var handleRaw = new NativeBuffer(32);
        handleRaw.SetSize();
        graph.Add(handle, handleRaw.ToArray(), IndirectClass);
        graph.Reference(objectKey, 160, handle);
        graph.Reference(handle, 8, placement);
        return objectKey;
    }

    /** Three ordered object arrays at +8, +24 and +40; a separate type-16 root. */
    public static string AddRegistry(GeneratedGraph graph, string key, IReadOnlyList<IReadOnlyList<string>> arrays)
    {
        if (arrays.Count != 3) throw new InvalidDataException("A registry has three arrays.");
        var raw = new NativeBuffer(56);
        var edges = new List<(int Offset, string Target)>();
        int[] descriptors = [8, 24, 40];
        for (int i = 0; i < 3; i++)
        {
            var keys = arrays[i];
            foreach (string item in keys) MapIdentityWriter.Require(graph, item, ObjectClass, 8);
            if (keys.Distinct().Count() != keys.Count) throw new InvalidDataException("A registry lists one object twice.");
            int start = raw.AppendNative(descriptors[i], 0x80809260, keys.Count, new byte[4 * keys.Count]);
            for (int k = 0; k < keys.Count; k++) edges.Add((start + 4 * k, keys[k]));
        }
        raw.SetSize();
        graph.Add(key, raw.ToArray(), RegistryClass, 16);
        foreach (var (offset, target) in edges) graph.Reference(key, offset, target);
        return key;
    }

    /** The installed null transition: no 80809BAB edges in its ten-word body. */
    public static byte[] EmptyTransition()
    {
        var raw = new NativeBuffer(48);
        for (int i = 0; i < 9; i++) raw.Put32(8 + 4 * i, uint.MaxValue);
        raw.SetSize();
        return raw.ToArray();
    }

    /** A 24-byte resource with one empty array: the empty 80809440 and the empty placement table share it. */
    public static byte[] EmptyArrayResource()
    {
        var raw = new NativeBuffer(24);
        raw.SetSize();
        return raw.ToArray();
    }

    private static int AppendString(NativeBuffer raw, string value)
    {
        byte[] encoded = [.. Encoding.ASCII.GetBytes(value), 0];
        raw.Add(NativeRow.U32s(StringMarker, (uint)encoded.Length, 0));
        return raw.Add(encoded);
    }

    /** Lowercase identity names the native lookups accept. */
    public static void RequireSymbol(string value, int limit)
    {
        if (value.Length == 0 || value.Length > limit || value[0] is < 'a' or > 'z'
            || value.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            throw new InvalidDataException($"{value} is not a lowercase identity name of up to {limit} characters.");
    }

    /**
     * The scenario, its slices and the activity root. Slice i names registry i; the scenario names the localized
     * text at +40, the lookup at +56, the 80809440 at +128 and each state's slice; the activity names the scenario at
     * +64 and the transition at +68. Returns the activity key.
     */
    public static string AddActivity(GeneratedGraph graph, string prefix, string name, uint word18, uint word24,
        IReadOnlyList<(uint NameHash, int GlobalIndex, byte[] Mask)> bubbles, IReadOnlyList<ScenarioState> states,
        string localized, string lookup, string schedule, IReadOnlyList<string> registries, string transition)
    {
        RequireSymbol(name, 39);
        if (("map:" + name + ":root").Length >= 64) throw new InvalidDataException("The map root name exceeds 63 characters.");
        if (bubbles.Count is < 1 or > 64 || states.Count != bubbles.Count || registries.Count != bubbles.Count)
            throw new InvalidDataException("Every bubble needs one state and one registry.");
        if (states.Select(s => s.StateHash).Distinct().Count() != states.Count || states.Any(s => s.PublicWord > 1))
            throw new InvalidDataException("Invalid or duplicate scenario state.");
        uint nameHash = MapIdentityWriter.NameHash(name);
        var raw = new NativeBuffer(140);
        raw.Put32(8, nameHash);
        raw.Put32(24, word18); raw.Put32(28, Sentinel); raw.Put32(32, nameHash); raw.Put32(36, word24);
        raw.Put32(120, uint.MaxValue);
        int symbol = raw.AppendNative(64, 0x8080924A, 1, new byte[16]);
        raw.Put32(symbol, nameHash);
        int rows = raw.AppendNative(80, 0x8080924D, bubbles.Count, new byte[24 * bubbles.Count]);
        var fields = new List<int>();
        for (int i = 0; i < bubbles.Count; i++)
        {
            int at = rows + 24 * i;
            raw.Put32(at, bubbles[i].NameHash);
            var state = new NativeBuffer(76);
            state.Put32(0, states[i].Enabled ? 1u : 0); state.Put32(4, states[i].StateHash); state.Put32(12, states[i].PublicWord);
            state.Put32(28, (uint)bubbles[i].GlobalIndex);
            state.PutBytes(32, bubbles[i].Mask);
            state.Put32(64, bubbles[i].NameHash); state.Put32(72, states[i].TailWord);
            fields.Add(raw.AppendNative(at + 8, 0x8080924F, 1, state.ToArray()) + 68);
        }
        int text = AppendString(raw, name);
        raw.PutI64(48, text - 48);
        raw.PutI64(symbol + 8, text - symbol - 8);
        raw.SetSize();
        var slices = Enumerable.Range(0, bubbles.Count).Select(i =>
        {
            var slice = new NativeBuffer(28);
            slice.Put32(16, (uint)i);
            int at = AppendString(slice, name);
            slice.PutI64(8, at - 8);
            slice.SetSize();
            return slice.ToArray();
        }).ToArray();
        var root = new NativeBuffer(32);
        root.Put32(8, nameHash);
        root.AppendNative(16, 0x80808491, 2, new byte[8]);
        root.SetSize();
        string scenario = prefix + "_scenario", activity = prefix + "_activity";
        graph.Add(scenario, raw.ToArray(), ScenarioClass, 16);
        graph.Add(activity, root.ToArray(), ActivityClass, 16);
        for (int i = 0; i < slices.Length; i++) graph.Add($"{prefix}_slice{i}", slices[i], SliceClass);
        graph.Reference(scenario, 40, localized);
        graph.Reference(scenario, 56, lookup);
        graph.Reference(scenario, 128, schedule);
        for (int i = 0; i < slices.Length; i++) graph.Reference(scenario, fields[i], $"{prefix}_slice{i}");
        for (int i = 0; i < slices.Length; i++) graph.Reference($"{prefix}_slice{i}", 20, registries[i]);
        graph.Reference(activity, 64, scenario);
        graph.Reference(activity, 68, transition);
        return activity;
    }

    /**
     * An 80809A88 container of ascending keys whose thirteen language slots all name one 80809A8A data. Each
     * string is one F400 part: code points shifted down by (smallest code point - 1), stored as UTF-8.
     */
    public static string AddLocalizedText(GeneratedGraph graph, string key, IReadOnlyDictionary<uint, string> entries)
    {
        var keys = entries.Keys.Order().ToArray();
        if (keys.Length == 0 || keys.Contains(Sentinel)) throw new InvalidDataException("Localized keys are missing or empty.");
        var container = new NativeBuffer(0x50);
        container.AppendNative(8, 0x80800070, keys.Length, NativeRow.U32s(keys));
        container.SetSize();
        var parts = keys.Select(k => TextPart(entries[k])).ToArray();
        var data = new NativeBuffer(0x58);
        int partRows = data.AppendNative(8, 0x80809A90, parts.Length, new byte[32 * parts.Length]);
        int units = data.AppendNative(0x28, 0x80800006, 1, new byte[2]);
        byte[] payload = [.. parts.SelectMany(p => p.Encoded), 0];
        int bytesAt = data.AppendNative(0x38, 0x80800005, payload.Length, payload);
        int stringRows = data.AppendNative(0x48, 0x80809A8E, parts.Length, new byte[16 * parts.Length]);
        _ = units;
        for (int i = 0; i < parts.Length; i++)
        {
            int at = partRows + 32 * i;
            data.PutI64(at + 8, bytesAt - at - 8);
            data.Put32(at + 16, Sentinel);
            data.Put16(at + 20, (ushort)parts[i].Encoded.Length);
            data.Put16(at + 22, (ushort)parts[i].Characters);
            data.Put16(at + 24, (ushort)parts[i].Shift);
            data.Put16(at + 26, 0xF400);
            bytesAt += parts[i].Encoded.Length;
            data.PutI64(stringRows + 16 * i, at - (stringRows + 16 * i));
            data.Put64(stringRows + 16 * i + 8, 1);
        }
        data.SetSize();
        graph.Add(key, container.ToArray(), ContainerClass);
        graph.Add(key + "_data", data.ToArray(), DataClass);
        for (int slot = 0; slot < 13; slot++) graph.Reference(key, 0x18 + 4 * slot, key + "_data");
        return key;
    }

    private static (byte[] Encoded, int Characters, int Shift) TextPart(string text)
    {
        var points = text.EnumerateRunes().Select(rune => rune.Value).ToArray();
        if (points.Length is 0 or >= 1 << 16 || points.Contains(0)) throw new InvalidDataException("Localized text must be a nonempty line.");
        int shift = points.Min() - 1;
        if (shift >= 1 << 16) throw new InvalidDataException("Localized text code points exceed the native shift.");
        try
        {
            var encoded = Encoding.UTF8.GetBytes(string.Concat(points.Select(p => new Rune(p - shift).ToString())));
            if (encoded.Length >= 1 << 16) throw new InvalidDataException("Localized text is too long.");
            return (encoded, points.Length, shift);
        }
        catch (ArgumentOutOfRangeException) { throw new InvalidDataException("Localized text cannot be shifted into UTF-8."); }
    }
}
