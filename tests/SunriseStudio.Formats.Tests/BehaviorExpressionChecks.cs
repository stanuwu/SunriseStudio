using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

internal static class BehaviorExpressionChecks
{
    internal static void Owned(string game, string corpus)
    {
        using var catalog = PackageCatalog.Open(game, cacheOptions: new() { Enabled = false });
        int roots = 0, expressions = 0, constants = 0, inputs = 0;
        foreach (var entry in catalog.Entries.Where(entry => entry.Reference == 0x8080941E))
        {
            byte[] bytes = catalog.ReadOriginal(entry.TagHash);
            string path = Path.Combine(corpus, $"{entry.TagHash:X8}.behavior.json");
            using var expected = JsonDocument.Parse(File.ReadAllBytes(path));
            var document = expected.RootElement;
            Check(Convert.ToHexString(SHA256.HashData(bytes)).Equals(
                document.GetProperty("source").GetProperty("raw_sha256").GetString(), StringComparison.OrdinalIgnoreCase),
                $"{entry.TagHash:X8}: installed behavior differs from the independent corpus source.");
            var graph = new BehaviorInspector().Inspect(new(entry.TagHash, entry.Reference, bytes));
            Check(graph.Diagnostics.Count == 0, $"{entry.TagHash:X8}: {string.Join("; ", graph.Diagnostics)}");
            foreach (var value in document.GetProperty("expressions").EnumerateArray())
            {
                int at = value.GetProperty("offset").GetInt32();
                var actual = BehaviorExpression.Read(bytes, at);
                Check(actual.NativeOverride == value.GetProperty("native_override").GetInt32()
                    && actual.Constants.Count == value.GetProperty("constants").GetArrayLength()
                    && actual.Inputs.Count == value.GetProperty("bindings").GetArrayLength(),
                    $"{entry.TagHash:X8}: expression {at} differs from the independent corpus.");
                Check(graph.Graph!.Nodes.Any(node => node.Id == $"expression:{at}"),
                    $"{entry.TagHash:X8}: expression {at} is missing from its graph.");
                foreach (var constant in value.GetProperty("constants").EnumerateArray())
                {
                    int index = constant.GetProperty("index").GetInt32();
                    var lanes = constant.GetProperty("bits").EnumerateArray().ToArray();
                    for (int lane = 0; lane < 4; lane++)
                        Check(BitConverter.SingleToUInt32Bits(actual.Constants[index].Value[lane])
                            == Convert.ToUInt32(lanes[lane].GetString()![2..], 16), "A native constant lane changed.");
                    constants++;
                }
                foreach (var input in value.GetProperty("bindings").EnumerateArray())
                {
                    int index = input.GetProperty("index").GetInt32();
                    Check(actual.Inputs[index].Selector == input.GetProperty("selector").GetByte()
                        && actual.Inputs[index].Hash == Convert.ToUInt32(input.GetProperty("id_hash").GetString()![2..], 16),
                        "A native expression input changed.");
                    inputs++;
                }
                expressions++;
            }
            roots++;
        }
        Check(roots == 1518 && expressions == 1380 && constants == 838 && inputs == 660,
            "The owned behavior corpus coverage changed.");
        Console.WriteLine($"behavior_owned roots={roots} expressions={expressions} constants={constants} inputs={inputs} graph=complete_for_corpus");
    }

    internal static void Run()
    {
        byte[] bytes = new byte[256];
        Array(bytes, 0, 80, 1, 0x8080941A);
        U(bytes, 100, 0xAABBCCDD);
        Expression(bytes, 0, 112, 160, [0x3C, 0, 0x34, 0, 3, 0x3E, 0], 100);
        var expression = BehaviorExpression.Read(bytes, 0, hash => hash == 0xAABBCCDD ? "progress" : null);
        Check(expression.Supported && expression.Output == "(object.progress * 100)"
            && expression.Constants.Single().Offset == 176 && expression.Inputs.Single().Offset == 96,
            "Behavior expression lost its native inputs, constants or order.");
        Check(expression.Instructions.Count == 4 && expression.Instructions[^1].Opcode == 0x3E,
            "Behavior expression did not consume the output operand.");
        Change(bytes, data => data[129] = 1, true);
        Change(bytes, data => data[131] = 1, true);
        Change(bytes, data => data[134] = 1, true);
        Change(bytes, data => U(data, 120, 0x80800006), true);
        Change(bytes, data => { Q(data, 16, 1); Q(data, 112, 1); data[128] = 0x34; }, true);
        Change(bytes, data => { Q(data, 16, 2); Q(data, 112, 2); data[128] = 0x34; data[129] = 0; }, true);
        Change(bytes, data => data[128] = 1, true);
        Change(bytes, data => Q(data, 0, 9), true);
        byte[] unknown = bytes.ToArray(); unknown[130] = 0xFF;
        Check(BehaviorExpression.Read(unknown, 0) is { Supported: false, Diagnostic: not null },
            "Unknown bytecode was presented as a supported expression.");
        byte[] native = bytes.ToArray(); U(native, 60, 1);
        Check(BehaviorExpression.Read(native, 0) is { Supported: false, NativeOverride: 1 },
            "A native evaluator was interpreted as fallback bytecode.");
        Graph();
        GraphLimits();
        TextLimits(bytes);
        SourceTextLimits();
        Console.WriteLine("behavior_expression inputs=pass constants=pass stack=checked native=opaque filters=visible nested_output=pass");
    }

    private static void Graph()
    {
        byte[] bytes = new byte[640];
        Array(bytes, 8, 32, 1, 0x80809420);
        Q(bytes, 56, 100 - 56); U(bytes, 96, 0x80804E3D);
        Array(bytes, 124, 224, 1, 0x80809316);
        Q(bytes, 248, 300 - 248); U(bytes, 296, 0x80804D7D);
        Expression(bytes, 308, 480, 512, [0x34, 0, 0x3E, 0], 1);
        Expression(bytes, 372, 560, 592, [0x34, 0, 0x3E, 0], 0);
        var result = new BehaviorInspector().Inspect(new(0x80A00000, 0x8080941E, bytes, "Behavior fixture"));
        Check(result.Diagnostics.Count == 0, string.Join("; ", result.Diagnostics));
        Check(result.Graph!.Nodes.Any(node => node.Label == "Filter")
            && result.Graph.Nodes.Any(node => node.Label == "Channel comparison")
            && result.Graph.Nodes.Any(node => node.Id == "expression:308" && node.Label == "1")
            && result.Graph.Nodes.Any(node => node.Id == "expression:372" && node.Label == "0"),
            "The behavior graph hid its condition or expression values.");
        Check(result.Graph.Edges.Any(edge => edge.SourceId == "node:100" && edge.TargetId == "node:300")
            && result.Graph.Edges.Count(edge => edge.SourceId == "node:300") == 2,
            "Behavior condition and expression ownership edges are missing.");
    }

    private static void GraphLimits()
    {
        static AssetInspection Inspect(byte[] data) => new BehaviorInspector().Inspect(new(0x80A00000, 0x8080941E, data));
        static void Reject(byte[] data)
        {
            try { _ = Inspect(data); }
            catch (InvalidDataException error) when (error.Message.Contains("view limit", StringComparison.Ordinal)) { return; }
            throw new InvalidOperationException("Behavior traversal accepted an excessive row count.");
        }
        static byte[] Rows(int count, bool shared)
        {
            byte[] data = new byte[48 + count * 16 + 8];
            Array(data, 8, 32, count, 0x80809420);
            if (shared)
            {
                int node = 48 + count * 16 + 4;
                U(data, node - 4, 0x80804D76);
                for (int index = 0; index < count; index++)
                {
                    int pointer = 56 + index * 16;
                    Q(data, pointer, node - pointer);
                }
            }
            return data;
        }
        _ = Inspect(Rows(8191, false));
        Reject(Rows(8193, false));
        Reject(Rows(8193, true));
        byte[] nested = new byte[132160];
        Array(nested, 8, 32, 2, 0x80809420);
        Q(nested, 56, 128 - 56); Q(nested, 72, 256 - 72);
        U(nested, 124, 0x80804E3D); U(nested, 252, 0x80804E3D);
        U(nested, 764, 0x80804D76);
        for (int owner = 0; owner < 2; owner++)
        {
            int header = 1024 + owner * 65568;
            Array(nested, (owner == 0 ? 128 : 256) + 24, header, 4096, 0x80809316);
            for (int index = 0; index < 4096; index++)
            {
                int pointer = header + 24 + index * 16;
                Q(nested, pointer, 768 - pointer);
            }
        }
        Reject(nested);

        const int comparisons = 4095, comparisonStride = 160;
        int comparisonStart = 64 + comparisons * 16;
        byte[] pairedExpressions = new byte[comparisonStart + comparisons * comparisonStride];
        Array(pairedExpressions, 8, 32, comparisons, 0x80809420);
        for (int index = 0; index < comparisons; index++)
        {
            int node = comparisonStart + index * comparisonStride + 4;
            U(pairedExpressions, node - 4, 0x80804D7D);
            Q(pairedExpressions, 56 + index * 16, node - (56 + index * 16));
        }
        Reject(pairedExpressions);

        const int counters = 40, counterStride = 96, constantCount = 256;
        int counterStart = 64 + counters * 16;
        int constantHeader = counterStart + counters * counterStride + 16;
        byte[] sharedConstants = new byte[constantHeader + 16 + constantCount * 16];
        Array(sharedConstants, 8, 32, counters, 0x80809420);
        for (int index = 0; index < counters; index++)
        {
            int node = counterStart + index * counterStride + 4;
            U(sharedConstants, node - 4, 0x80804E40);
            Q(sharedConstants, 56 + index * 16, node - (56 + index * 16));
            Array(sharedConstants, node + 48, constantHeader, constantCount, 0x80800090);
        }
        Reject(sharedConstants);
    }

    private static void TextLimits(byte[] source)
    {
        BehaviorExpression Read(byte[] program, int nameLength)
        {
            byte[] data = source.ToArray();
            Q(data, 16, program.Length); Q(data, 112, program.Length);
            program.CopyTo(data, 128);
            return BehaviorExpression.Read(data, 0, _ => new string('x', nameLength));
        }
        void Reject(byte[] program, int nameLength)
        {
            try { _ = Read(program, nameLength); }
            catch (InvalidDataException error) when (error.Message.Contains("text", StringComparison.Ordinal)) { return; }
            throw new InvalidOperationException("Behavior expression accepted text past its view limit.");
        }
        byte[] input = [0x3C, 0, 0x3E, 0];
        byte[] permuted = [0x3C, 0, 0x22, 0xE4, 0x3E, 0];
        Check(Read(input, 16377) is { Supported: true, Output.Length: 16384 }
            && Read(permuted, 16370) is { Supported: true, Output.Length: 16384 },
            "Behavior text exactly at the limit was refused.");
        Reject(input, 16378);
        Reject(permuted, 16371);
    }

    private static void SourceTextLimits()
    {
        static byte[] Sources(int count, int bytesPerSource, bool shared)
        {
            int nodes = 64 + count * 16, text = nodes + count * 96;
            byte[] data = new byte[text + (shared ? 1 : count) * (bytesPerSource + 1)];
            Array(data, 8, 32, count, 0x80809420);
            for (int index = 0; index < count; index++)
            {
                int node = nodes + index * 96 + 4;
                int source = text + (shared ? 0 : index) * (bytesPerSource + 1);
                U(data, node - 4, 0x80804E40);
                Q(data, 56 + index * 16, node - (56 + index * 16));
                Q(data, node + 8, source - (node + 8));
                data.AsSpan(source, bytesPerSource).Fill((byte)'x');
            }
            return data;
        }
        static AssetInspection Inspect(byte[] data) => new BehaviorInspector().Inspect(new(0x80A00000, 0x8080941E, data));
        static void Reject(byte[] data)
        {
            try { _ = Inspect(data); }
            catch (InvalidDataException error) when (error.Message.Contains("source text exceeds", StringComparison.Ordinal)) { return; }
            throw new InvalidOperationException("Behavior source strings exceeded their aggregate view limit.");
        }
        // Three nodes share one 600,000 byte source; budgeting it per node would pass the limit.
        Check(Inspect(Sources(3, 600_000, true)).Graph!.Nodes.Count(node => node.Label == "Counter") == 3,
            "Shared behavior source offsets were budgeted more than once.");
        Check(Inspect(Sources(1, 1_048_576, false)).Graph!.Nodes.Any(node => node.Label == "Counter"),
            "Behavior source text exactly at the byte limit was refused.");
        Reject(Sources(2, 600_000, false));
        Reject(Sources(1, 1_048_577, false));
    }

    private static void Expression(byte[] data, int at, int code, int constants, byte[] program, float value)
    {
        Array(data, at + 16, code, program.Length, 0x80800009);
        program.CopyTo(data, code + 16);
        Array(data, at + 32, constants, 1, 0x80800090);
        for (int lane = 0; lane < 4; lane++)
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(constants + 16 + lane * 4), value);
    }

    private static void Array(byte[] data, int at, int header, int count, uint type)
    {
        Q(data, at, count); Q(data, at + 8, header - at - 8);
        U(data, header - 4, 0x80809FBD); Q(data, header, count); U(data, header + 8, type);
    }

    private static void Change(byte[] original, Action<byte[]> edit, bool reject)
    {
        byte[] bytes = original.ToArray(); edit(bytes);
        try { _ = BehaviorExpression.Read(bytes, 0); }
        catch (InvalidDataException) { if (reject) return; throw; }
        if (reject) throw new InvalidOperationException("Malformed behavior expression was accepted.");
    }

    private static void U(byte[] data, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);
    private static void Q(byte[] data, int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(at), value);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
