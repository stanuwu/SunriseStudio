using System.Buffers.Binary;
using System.Text;

namespace SunriseStudio.Authoring;

public sealed record ActivityDefinition(int Index, uint Hash, int Offset, string Name, byte Type, byte Destination);
public sealed record ActivityClientDefinition(uint Hash, int Offset, int DisplayIndex);
public sealed record ActivityTableAddition(uint TemplateHash, uint Hash, string Name, byte Type, byte Destination);

/** Native fixed-record relocation retains immutable policy at its installed offsets. */
public static class ActivityRegistrationTables
{
    public const uint DefinitionTag = 0x81327CF0, ClientTag = 0x81327D35;
    public const uint DefinitionClass = 0x808076F0, ClientClass = 0x80805E0A;
    private static readonly int[] DefinitionPointers = [16, 24, 32, 40, 48, 56, 64, 72, 80, 88, 104, 112, 128, 144, 168, 192, 208];
    private static readonly int[] ClientPointers = [0, 16, 32, 48, 72, 80, 96, 112, 120];

    public static IReadOnlyList<ActivityDefinition> ReadDefinitions(byte[] bytes)
    {
        var data = Sized(bytes, 136);
        int[] index = Rows(data, 8, 0x808076FC, 16); // activity definition index
        Require(index.Length is > 0 and <= 4095 && index[0] >= 156, "Invalid activity index framing.");
        var result = new List<ActivityDefinition>();
        var seen = new HashSet<uint>();
        int previous = 0;
        foreach (int at in index)
        {
            uint hash = data.U32(at);
            int pos = Relative(data, at + 8, 136);
            Require(hash is not (0 or uint.MaxValue) && seen.Add(hash) && data.U32(at + 4) == 0
                && pos >= 140 && pos > previous && pos <= bytes.Length - 232
                && (pos + 232 <= index[0] - 20 || pos - 4 >= index[^1] + 16)
                && (previous == 0 || previous + 232 <= pos - 4), "Invalid activity record extent or identity.");
            Require(data.U32(pos - 4) == 0x80807711 && data.U32(pos) == hash, "Activity record class or identity differs.");
            foreach (int field in DefinitionPointers) _ = Relative(data, pos + field, 136);
            int name = Relative(data, pos + 104, 136);
            int match = Relative(data, pos + 32, 136);
            Require(match != 0, "Activity matchmaking policy is absent.");
            uint tag = data.U32(match);
            Require((tag == uint.MaxValue || tag is >= 0x80800000 and <= 0x83FFFFFF)
                && bytes[pos + 218] < 54 && bytes[pos + 224] < 48, "Invalid activity policy.");
            result.Add(new(result.Count, hash, pos, name == 0 ? "" : Text(data, name, 256), bytes[pos + 218], bytes[pos + 224]));
            previous = pos;
        }
        return result;
    }

    public static IReadOnlyList<ActivityClientDefinition> ReadClients(byte[] bytes)
    {
        var data = Sized(bytes, 120);
        int[] index = Rows(data, 8, 0x80805E15, 16), display = Rows(data, 24, 0x80805E23, 36);
        Require(index.Length is > 0 and <= 4095 && display.Length > 0 && index[0] >= 140
            && display[0] > index[^1] + 16, "Invalid client activity framing.");
        var result = new List<ActivityClientDefinition>();
        var seen = new HashSet<uint>();
        int previous = 0;
        foreach (int at in index)
        {
            uint hash = data.U32(at);
            int pos = Relative(data, at + 8, 120);
            Require(hash is not (0 or uint.MaxValue) && seen.Add(hash) && data.U32(at + 4) == 0
                && pos % 8 == 0 && pos - 4 >= index[^1] + 16 && pos > previous
                && pos + 136 <= display[0] - 16 && (previous == 0 || previous + 136 <= pos - 4),
                "Invalid client activity extent or identity.");
            Require(data.U32(pos - 4) == 0x80805E18, "Client activity record class differs.");
            foreach (int field in ClientPointers) _ = Relative(data, pos + field, 120);
            int target = Relative(data, pos, 120), slot = Array.IndexOf(display, target);
            Require(slot >= 0, "Client activity display is outside its table.");
            result.Add(new(hash, pos, slot)); previous = pos;
        }
        return result;
    }

    public static (byte[] Definitions, byte[] Clients) Append(byte[] definitions, byte[] clients,
        IReadOnlyList<ActivityTableAddition> additions)
    {
        var original = ReadDefinitions(definitions); var client = ReadClients(clients);
        Require(original.Select(row => row.Hash).SequenceEqual(client.Select(row => row.Hash)), "Activity table indices differ.");
        Require(additions.Count > 0 && original.Count + additions.Count <= 4095, "Activity catalog capacity exceeded.");
        var hashes = original.Select(row => row.Hash).ToHashSet();
        var names = original.Select(row => row.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var item in additions)
        {
            Require(original.Any(row => row.Hash == item.TemplateHash), "Choose an installed template activity.");
            Require(item.Hash is not (0 or uint.MaxValue) && hashes.Add(item.Hash) && names.Add(item.Name)
                && System.Text.RegularExpressions.Regex.IsMatch(item.Name, "\\A[a-z][a-z0-9_]{0,38}\\z"), "Activity identity or name collides.");
            Require(item.Type < 54 && item.Destination < 48, "Invalid activity type or destination.");
        }
        var inv = new ActivityBytes(definitions);
        var positions = original.Select(row => row.Offset).ToList();
        foreach (var item in additions)
        {
            int old = original.Single(row => row.Hash == item.TemplateHash).Offset;
            int pos = inv.Object(0x80807711, new byte[232]);
            inv.Copy(pos, Relocate(definitions, old, pos, 232, DefinitionPointers, 136));
            inv.U32(pos, item.Hash);
            int name = inv.Add(Encoding.ASCII.GetBytes(item.Name + "\0")); inv.I64(pos + 104, name - pos - 104);
            inv.Byte(pos + 218, item.Type); inv.Byte(pos + 224, item.Destination);
            positions.Add(pos);
        }
        uint[] identities = [.. original.Select(row => row.Hash), .. additions.Select(row => row.Hash)];
        int[] indices = inv.Array(8, 0x808076FC, 16, new byte[positions.Count * 16]);
        for (int i = 0; i < indices.Length; i++) { inv.U32(indices[i], identities[i]); inv.I64(indices[i] + 8, positions[i] - indices[i] - 8); }
        byte[] result = inv.Finish(); _ = ReadDefinitions(result);
        Require(result.AsSpan(24, definitions.Length - 24).SequenceEqual(definitions.AsSpan(24)), "Installed activity bytes changed.");

        var cli = new ActivityBytes(clients);
        var sources = client.Concat(additions.Select(item => client.Single(row => row.Hash == item.TemplateHash))).ToArray();
        indices = cli.Array(8, 0x80805E15, 16, new byte[sources.Length * 16]); positions.Clear();
        foreach (var row in sources)
        {
            int pos = cli.Object(0x80805E18, new byte[136]);
            cli.Copy(pos, Relocate(clients, row.Offset, pos, 136, ClientPointers, 120)); positions.Add(pos);
        }
        int[] oldDisplay = Rows(new NativeRows(clients), 24, 0x80805E23, 36);
        int[] displays = cli.Array(24, 0x80805E23, 36, oldDisplay.SelectMany(at => clients.AsSpan(at, 36).ToArray()).ToArray());
        for (int i = 0; i < indices.Length; i++)
        {
            cli.U32(indices[i], identities[i]); cli.I64(indices[i] + 8, positions[i] - indices[i] - 8);
            cli.I64(positions[i], displays[sources[i].DisplayIndex] - positions[i]);
        }
        byte[] paired = cli.Finish(); _ = ReadClients(paired);
        Require(paired.AsSpan(40, clients.Length - 40).SequenceEqual(clients.AsSpan(40)), "Installed client bytes changed.");
        return (result, paired);
    }

    private static byte[] Relocate(byte[] bytes, int old, int next, int size, int[] fields, int minimum)
    {
        var data = new NativeRows(bytes); byte[] result = bytes.AsSpan(old, size).ToArray();
        foreach (int field in fields)
        {
            int target = Relative(data, old + field, minimum);
            if (target == 0) continue;
            if (target >= old && target < old + size) target += next - old;
            BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(field), target - next - field);
        }
        return result;
    }

    internal static NativeRows Sized(byte[] bytes, int minimum)
    {
        Require(bytes.Length >= minimum && bytes.Length <= 4 * 1024 * 1024, "Invalid activity resource size.");
        var data = new NativeRows(bytes); Require(data.U64(0) == (ulong)bytes.Length, "Activity resource size differs."); return data;
    }
    internal static int[] Rows(NativeRows data, int field, uint cls, int stride)
    {
        try { return data.Rows(field, cls, stride); }
        catch (OverflowException error) { throw new InvalidDataException("Activity array leaves its resource.", error); }
    }
    internal static int Relative(NativeRows data, int at, int minimum)
    {
        long relative = data.I64(at); if (relative == 0) return 0;
        Require(relative >= minimum - (long)at && relative < data.Bytes.Length - (long)at, "Activity pointer leaves its resource.");
        return (int)(at + relative);
    }
    internal static string Text(NativeRows data, int at, int capacity = 128)
    {
        int end = Array.IndexOf(data.Bytes, (byte)0, at, Math.Min(capacity, data.Bytes.Length - at));
        Require(end >= at && data.Bytes.AsSpan(at, end - at).IndexOfAnyInRange((byte)128, byte.MaxValue) < 0, "Invalid activity string.");
        return Encoding.ASCII.GetString(data.Bytes, at, end - at);
    }
    internal static void Require(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
}

/** Activity arrays use the Python oracle's padded layout; scenario and text arrays use tight native framing. */
internal sealed class ActivityBytes
{
    private readonly MemoryStream stream = new();
    internal ActivityBytes(byte[] bytes) => stream.Write(bytes);
    internal ActivityBytes(int size) : this(new byte[size]) { }
    internal int Length => checked((int)stream.Length);
    internal Span<byte> Span(int at) => stream.GetBuffer().AsSpan(at, Length - at);
    internal int Add(byte[] bytes) { int at = Length; stream.Position = at; stream.Write(bytes); return at; }
    internal void Copy(int at, byte[] bytes) => bytes.CopyTo(Span(at));
    internal void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Span(at), value);
    internal void U16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Span(at), value);
    internal void I64(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(Span(at), value);
    internal void Byte(int at, byte value) => Span(at)[0] = value;
    internal int Object(uint cls, byte[] bytes)
    {
        Add(new byte[(-(Length + 4)) & 7]); int marker = Add(new byte[4]); U32(marker, cls); return Add(bytes);
    }
    internal int[] Array(int field, uint cls, int stride, byte[] rows, bool tight = false)
    {
        if (rows.Length == 0) { I64(field, 0); I64(field + 8, 0); return []; }
        int count = rows.Length / stride;
        int first = tight ? (Length + 35) & ~15 : ((Length + 15) & ~15) + 32;
        Add(new byte[first - Length]); U32(first - 20, 0x80809FBD); I64(first - 16, count); U32(first - 8, cls);
        Add(rows); I64(field, count); I64(field + 8, first - field - 24);
        return Enumerable.Range(0, count).Select(i => first + i * stride).ToArray();
    }
    internal byte[] Finish() { I64(0, Length); ActivityRegistrationTables.Require(Length <= 4 * 1024 * 1024, "Activity resource exceeds capacity."); return stream.ToArray(); }
}
