using System.Numerics;
using System.Text.RegularExpressions;
using SunriseStudio.Rendering;

namespace SunriseStudio.Interchange;

/** Only fully resolved shader expressions become Blender inputs. */
internal sealed class MaterialExpressionCompiler
{
    private readonly List<BundleShaderNode> nodes = [];
    private readonly Dictionary<string, int> cache = [];
    private Dictionary<string, int[]> registers = [];
    private readonly Stack<Branch> branches = [];
    private sealed record Branch(Dictionary<string, int[]> Before, int Condition)
    {
        public Dictionary<string, int[]>? First { get; set; }
    }
    private IReadOnlyList<Vector4>? constants;
    private IReadOnlyDictionary<int, TfxTerm> timeTerms = new Dictionary<int, TfxTerm>();
    // cb0 rows TFX writes; null when no row is a constant template copy.
    private IReadOnlySet<int>? writtenRows;
    private readonly Dictionary<TfxTerm, int> imported = new(ReferenceEqualityComparer.Instance);
    private IReadOnlySet<uint> textures = new HashSet<uint>();
    // 3D texture slots; a read whose coordinates resolve becomes a "volume" node, otherwise its stand-in.
    private IReadOnlySet<uint> volumes = new HashSet<uint>();
    private IReadOnlyDictionary<uint, Vector4[]> buffers = new Dictionary<uint, Vector4[]>();
    private int smoothness = -1, packedRadius = -1, visibility, emission = -1;
    private bool hasDiscard, unsupportedDiscard, resolvedBranchOnly, resolveNativeBasis, worldVaryings, uvPairs, detailUv;
    // Some loop left at an exit test that depends on the pixel.
    private bool approximateLoop;
    private float[][] immediate = [];
    private ShaderVaryings? varyings;
    // Fixed values for runtime-bound texture slots, the stand-ins Studio's viewport binds.
    private IReadOnlyDictionary<uint, Vector4> standIns = new Dictionary<uint, Vector4>();
    // dp3 results and their operand lanes, to find reflect(V, N).
    private readonly Dictionary<int, (int[] A, int[] B)> dots = [];
    // N of the first reflect(V, N) = N * 2 * dot(V, N) - V: the surface normal a forward shader lights with.
    private int[]? reflectNormal;
    private bool water;
    // Inputs and operations that left some lane unknown.
    private readonly SortedSet<string> unresolved = new(StringComparer.Ordinal);
    // The vertex stage run, whose outputs feed pixel inputs it computes; vertex inputs read only NORMAL0.
    private MaterialExpressionCompiler? vertex;
    private bool vertexStage, refactoring;
    private readonly Dictionary<int, int> vertexImports = [];
    // G-buffer o2.y packs emission as log2(intensity + 1/128) / 13 + 7/13.
    private const float EmissionScale = 1 / 13f, EmissionBias = 7 / 13f, EmissionFloor = 1 / 128f;
    // Pixel inputs the shader declares as SV_Position and SV_IsFrontFace.
    private string? screenRegister;
    private string frontFaceRegister = "v6";
    private bool frontFaceDeclared;
    // Frame scope at time zero, EV zero and no extra pre-exposure; its row 0 X is the clock.
    private static readonly NativeFrameScopeConstants Frame = NativeFrameScope.Create(new()
    { SimulationSeconds = 0, FrameFlag425B8 = false, FrameFlag425B9 = false, Extern1C = 1, Extern28 = 1 });

    /**
     * Compiles a pixel shader's surface outputs into Blender expression nodes.
     * @param approximateLoop Set when a loop stopped at an exit test that depends on the pixel.
     * @param terms Pixel constant lanes that change with time, keyed by vector * 4 + lane.
     * @param worldVaryings The vertex shader writes world N, T, B to v0..v2 and world position to v4.
     * @param uvPairs The vertex shader writes UV map 0 to v3.xy and a second UV pair to v3.zw.
     * @param detailUv The second pair is UV map 1; otherwise it is UV map 0 again.
     * @param buffers Other pixel constant buffers by slot, such as gear dye scopes.
     * @param varyings Pixel inputs the vertex shader copies from a vertex stream or computes; null when unread.
     * @param standIns Fixed sample values for runtime-bound texture slots.
     * @param water A water surface: its reflect() normal becomes the Normal output.
     * @param diagnostics Receives the inputs and operations that left some lane unknown.
     * @param volumes 3D texture slots the program may sample as "volume" nodes.
     * @param writtenRows cb0 rows TFX writes; a lane of any other row is tagged with its template lane.
     */
    internal static BundleMaterialProgram? Compile(string text, IReadOnlyList<Vector4>? constants, string blend,
        IReadOnlySet<uint> textures, out bool approximateLoop, bool nativeTangentInputs = false,
        IReadOnlyDictionary<int, TfxTerm>? terms = null, bool worldVaryings = false, bool uvPairs = false, bool detailUv = false,
        IReadOnlyDictionary<uint, Vector4[]>? buffers = null, ShaderVaryings? varyings = null,
        IReadOnlyDictionary<uint, Vector4>? standIns = null, bool water = false, ISet<string>? diagnostics = null,
        IReadOnlySet<uint>? volumes = null, IReadOnlySet<int>? writtenRows = null)
    {
        var compiler = new MaterialExpressionCompiler { constants = constants, textures = textures, worldVaryings = worldVaryings,
            writtenRows = writtenRows,
            volumes = volumes ?? new HashSet<uint>(),
            uvPairs = uvPairs, detailUv = detailUv, timeTerms = terms ?? new Dictionary<int, TfxTerm>(),
            buffers = buffers ?? new Dictionary<uint, Vector4[]>(), varyings = varyings,
            standIns = standIns ?? new Dictionary<uint, Vector4>(), water = water };
        if (varyings is { Vertex.Length: > 0 })
        {
            var stage = new MaterialExpressionCompiler { varyings = varyings, vertexStage = true };
            if (stage.Run(varyings.Vertex)) compiler.vertex = stage;
        }
        foreach (Match input in Regex.Matches(text,
            @"dcl_input_ps_s[gi]v\b[^\n]*?\b(?<reg>v\d+)\.[xyzw]+,\s*(?<name>position|is_front_face)\b"))
        {
            if (input.Groups["name"].Value == "position") compiler.screenRegister = input.Groups["reg"].Value;
            else (compiler.frontFaceRegister, compiler.frontFaceDeclared) = (input.Groups["reg"].Value, true);
        }
        var program = compiler.Read(text, blend, nativeTangentInputs);
        diagnostics?.UnionWith(compiler.unresolved);
        approximateLoop = compiler.approximateLoop;
        return program;
    }

    // Vertex inputs by register for a motion run: semantic name and index.
    private Dictionary<int, SignatureRow>? motionInputs;
    private int motionUv = -1;
    // CB11 row 5: the part's position offset in xyz and its scale in w.
    private Vector4 objectRow;
    private NativeEntityPalette palette;
    private bool fourRowPalette;

    /**
     * The object-space offset an entity vertex shader adds to each vertex, with every bone at rest.
     * @param constants Vertex cb0 as the material's TFX fills it.
     * @param terms Vertex cb0 lanes that change with time, keyed by vector * 4 + lane.
     * @param objectRow CB11 row 5: the part's position offset and scale.
     * @param diagnostics Receives the inputs and operations that left some lane unknown.
     * @param textures Vertex stage 2D texture slots the program may sample.
     * @return The "Offset" lanes x, y and z; null when no lane changes with time.
     */
    internal static BundleMaterialProgram? CompileMotion(string vertex, IReadOnlyList<Vector4> constants,
        IReadOnlyDictionary<int, TfxTerm> terms, Vector4 objectRow, ISet<string>? diagnostics = null, IReadOnlySet<uint>? textures = null)
    {
        var inputs = SignatureRows(vertex, "Input signature");
        if (SignatureRows(vertex, "Output signature").FirstOrDefault(row => row.Name == "SV_POSITION") is not { } clip
            || inputs.FirstOrDefault(row => row is { Name: "POSITION", Index: 0 }) is not { } position) return null;
        var compiler = new MaterialExpressionCompiler
        {
            constants = constants, timeTerms = terms, objectRow = objectRow, textures = textures ?? new HashSet<uint>(),
            motionInputs = inputs.ToDictionary(row => row.Register),
            motionUv = inputs.Where(row => row.Name == "TEXCOORD").Select(row => row.Index).DefaultIfEmpty(-1).Min(),
            palette = NativeShaderContracts.ClassifyPaletteIndex(vertex),
            fourRowPalette = Regex.IsMatch(vertex, @"^\s*ishl r\d+\.[xyzw]+, [rv]\d+\.[xyzw]+, l\((?:0, )*2(?:, 2)*\)\r?$", RegexOptions.Multiline)
        };
        bool ran = compiler.Run(vertex);
        diagnostics?.UnionWith(compiler.unresolved);
        if (!ran) return null;
        var offset = new int[3];
        for (int lane = 0; lane < 3; lane++)
        {
            int moved = Get(compiler.registers, $"o{clip.Register}", lane);
            int rest = compiler.Op("mad", compiler.MotionInput($"v{position.Register}", lane), compiler.Number(objectRow.W),
                compiler.Number(objectRow[lane]));
            if (moved < 0 || rest < 0) return null;
            offset[lane] = moved == rest ? compiler.Number(0) : compiler.Op("add", moved, compiler.Op("neg", rest));
        }
        return offset.All(lane => lane >= 0) && compiler.Reads(offset, "time")
            ? compiler.Prune(new() { ["Offset"] = offset }, false) : null;
    }

    /** One vertex input lane for a motion run: a stream the bundle carries, or bone inputs at rest. */
    private int MotionInput(string register, int lane)
    {
        if (!int.TryParse(register.AsSpan(1), out int number) || !motionInputs!.TryGetValue(number, out var input)) return -1;
        return (input.Name, input.Index) switch
        {
            ("POSITION", 0) => lane < 3 ? Add(new(ShaderVaryings.RawPosition, [], Channel: lane)) : Number(0),
            ("NORMAL", 0) => lane < 3 ? Add(new(ShaderVaryings.Normal, [], Channel: lane)) : Number(0),
            ("COLOR", 0) => Add(new(ShaderVaryings.Color, [], Channel: lane)),
            ("TEXCOORD", var index) when index == motionUv => Add(new(lane < 2 ? ShaderVaryings.Uv : ShaderVaryings.UvZw, [], Channel: lane & 1)),
            ("BLENDINDICES", _) => Number(input.Format is "uint" or "int" ? RestBones[lane] : RestBones[lane] / 255f),
            ("BLENDWEIGHT", _) => Number(RestWeights[lane]),
            _ => -1
        };
    }

    /**
     * CB11 and CB12 lanes for a motion run: placement, bones and view-projection are identity, the camera at the origin.
     * @return NaN for a lane with no rest value; null for any other register.
     */
    private float? MotionRow(string register, int lane)
    {
        var match = Regex.Match(register, @"^cb(?<slot>11|12)\[(?<row>\d+)\]$");
        if (!match.Success) return null;
        int row = int.Parse(match.Groups["row"].Value);
        float Unit(int axis) => axis == lane ? 1 : 0;
        // View row 13 is a clip-space offset, such as a sample jitter.
        if (match.Groups["slot"].Value == "12") return row < 4 ? Unit(row) : row is 7 or 13 ? 0 : float.NaN;
        if (row < 4) return Unit(row);
        if (row < 8) return row switch { 5 => objectRow[lane], 6 => lane < 2 ? 1 : 0, 7 => Unit(3), _ => float.NaN };
        int entry = row - 8;
        return palette switch
        {
            NativeEntityPalette.DualQuaternion => entry % 2 == 0 ? Unit(3) : 0,
            NativeEntityPalette.Matrix => Unit(entry % 3),
            NativeEntityPalette.None when fourRowPalette => entry % 4 < 3 ? Unit(entry % 4) : float.NaN,
            _ => float.NaN
        };
    }

    private sealed record SignatureRow(string Name, int Index, int Register, string Format);
    // Bones 1 and 2 with 8-bit weights 153 and 102, which sum to 255; four float weights that sum to 1.
    private static readonly int[] RestBones = [1, 2, 153, 102];
    private static readonly float[] RestWeights = [.4f, .3f, .2f, .1f];

    /** The rows of one signature table in the disassembly header. */
    private static List<SignatureRow> SignatureRows(string text, string title)
    {
        var rows = new List<SignatureRow>();
        int at = text.IndexOf("// " + title, StringComparison.Ordinal);
        if (at < 0) return rows;
        foreach (string line in text[at..].Split('\n').Skip(1))
        {
            // A bare comment line ends the table.
            if (!line.StartsWith("//", StringComparison.Ordinal) || rows.Count != 0 && line.Trim() == "//") break;
            var match = Regex.Match(line, @"^//\s+(?<name>\w+)\s+(?<index>\d+)\s+[xyzw]+\s+(?<register>\d+)\s+\w+\s+(?<format>\w+)");
            if (match.Success)
                rows.Add(new(match.Groups["name"].Value, int.Parse(match.Groups["index"].Value), int.Parse(match.Groups["register"].Value),
                    match.Groups["format"].Value));
        }
        return rows;
    }

    /** Separate visibility and verified normal passes cannot change the resolved surface outputs. */
    private BundleMaterialProgram? Read(string text, string blend, bool nativeTangentInputs)
    {
        bool deferred = Regex.IsMatch(text, @"\bdcl_output o2\.");
        if (!Run(text)) return null;
        var outputs = new Dictionary<string, int[]>();
        void Output(string role, params int[] values)
        {
            if (values.All(value => value >= 0 && (nodes[value].Operation != "constant" || float.IsFinite(nodes[value].Value))))
                outputs[role] = values;
        }
        Output("BaseColor", Get(registers, "o0", 0), Get(registers, "o0", 1), Get(registers, "o0", 2));
        if (deferred && blend == "Opaque")
        {
            Output("Metallic", Get(registers, "o2", 0));
            // The deferred pass reads smoothness as 8 * radius - 3, so a radius below 0.375 is rougher than rough.
            if (smoothness < 0 && PackRadius() is >= 0 and var radius) smoothness = Op("mad", radius, Number(8), Number(-3));
            Output("Roughness", Op("add", Number(1), Op("neg", smoothness)));
        }
        // An opaque draw ignores the alpha it writes.
        if (!deferred && blend != "Opaque" || blend is "Alpha" or "PremultipliedAlpha" or "InverseAlpha")
        {
            int alpha = Get(registers, "o0", 3);
            if (blend == "InverseAlpha") alpha = Op("add", Number(1), Op("neg", alpha));
            Output("Alpha", Saturate(alpha));
        }
        if (deferred && emission >= 0 && outputs.TryGetValue("BaseColor", out var color))
        {
            int intensity = Op("max", Op("add", emission, Number(-EmissionFloor)), Number(0));
            Output("Emission", color.Select(lane => Op("mul", lane, intensity)).ToArray());
        }
        // Blender's UV tangent with the exported bitangent sign matches the authored frame.
        if (!nativeTangentInputs && deferred && blend == "Opaque")
        {
            int[] normal = Enumerable.Range(0, 3).Select(lane => PackedNormal(Get(registers, "o1", lane))).ToArray();
            if (Reads(normal, ShaderVaryings.Normal) || Reads(normal, "surface")) Output("Normal", normal);
        }
        if (water && reflectNormal is { } surfaceNormal) Output("Normal", surfaceNormal);
        bool approximate = false;
        if (unsupportedDiscard)
        {
            resolvedBranchOnly = true;
            approximate = Run(text) && hasDiscard && !unsupportedDiscard;
        }
        if (hasDiscard && !unsupportedDiscard) Output("Visibility", visibility);
        if (nativeTangentInputs && deferred && blend == "Opaque")
        {
            resolveNativeBasis = true;
            resolvedBranchOnly = false;
            if (Run(text)) Output("Normal", Enumerable.Range(0, 3).Select(lane => PackedNormal(Get(registers, "o1", lane))).ToArray());
        }
        return outputs.Count == 0 ? null : Prune(outputs, approximate);
    }

    /** Only nodes an output reaches are kept; an input always precedes its node, so the order holds. */
    private BundleMaterialProgram Prune(Dictionary<string, int[]> outputs, bool approximate)
    {
        var used = new SortedSet<int>();
        var pending = new Stack<int>(outputs.Values.SelectMany(values => values));
        while (pending.TryPop(out int index))
            if (used.Add(index))
                foreach (int input in nodes[index].Inputs) pending.Push(input);
        var renumbered = new Dictionary<int, int>();
        var kept = new List<BundleShaderNode>();
        foreach (int index in used)
        {
            renumbered[index] = kept.Count;
            kept.Add(nodes[index] with { Inputs = nodes[index].Inputs.Select(input => renumbered[input]).ToArray() });
        }
        return new(kept, outputs.ToDictionary(pair => pair.Key, pair => pair.Value.Select(value => renumbered[value]).ToArray()))
        { ApproximateVisibility = approximate };
    }

    /** The output lanes all resolve and some lane reads a node with this operation. */
    private bool Reads(int[] roots, string operation)
    {
        if (roots.Any(root => root < 0)) return false;
        var seen = new HashSet<int>();
        var pending = new Stack<int>(roots);
        while (pending.TryPop(out int index))
        {
            if (!seen.Add(index)) continue;
            if (nodes[index].Operation == operation) return true;
            foreach (int input in nodes[index].Inputs) pending.Push(input);
        }
        return false;
    }

    /** Only the normal before the verified G-buffer packing becomes a world-space shader output. */
    private int PackedNormal(int index) => Packing(index) is var (normal, radius) && radius >= 0 && radius == PackRadius() ? normal : -1;

    /** The normal and radius of one o1 lane, saturate(normal * radius + 0.5); -1 for another shape. */
    private (int Normal, int Radius) Packing(int index)
    {
        bool Is(int value, string operation) => value >= 0 && nodes[value].Operation == operation;
        if (!Is(index, "min") || !IsNumber(nodes[index].Inputs[1], 1)) return (-1, -1);
        int maximum = nodes[index].Inputs[0];
        if (!Is(maximum, "max") || !IsNumber(nodes[maximum].Inputs[1], 0)) return (-1, -1);
        int mad = nodes[maximum].Inputs[0];
        if (!Is(mad, "mad") || !IsNumber(nodes[mad].Inputs[2], .5f)) return (-1, -1);
        return (nodes[mad].Inputs[0], nodes[mad].Inputs[1]);
    }

    /**
     * The radius all three o1 lanes pack with: the smoothness term, 0.125 to 0.5.
     * @return -1 when the lanes differ, or a fixed radius lies outside that range.
     */
    private int PackRadius()
    {
        var radii = Enumerable.Range(0, 3).Select(lane => Packing(Get(registers, "o1", lane)).Radius).Distinct().ToArray();
        if (radii is not [>= 0 and var radius]) return -1;
        return radius == packedRadius || nodes[radius].Operation != "constant" || nodes[radius].Value is >= .125f and <= .5f ? radius : -1;
    }

    /** Register writes use destination lanes; sample swizzles select source texture components. */
    private bool Run(string text)
    {
        registers = [];
        branches.Clear();
        smoothness = packedRadius = emission = -1;
        visibility = Number(1);
        hasDiscard = unsupportedDiscard = false;
        reflectNormal = null;
        dots.Clear();
        refactoring = Regex.IsMatch(text, @"^dcl_globalFlags\b.*\brefactoringAllowed\b", RegexOptions.Multiline);
        immediate = ImmediateRows(text);
        var code = Parse(text);
        // The matching endloop or endswitch of each loop or switch.
        var ends = new int[code.Count];
        var open = new Stack<int>();
        for (int index = 0; index < code.Count; index++)
            if (code[index].Opcode is "loop" or "switch") open.Push(index);
            else if (code[index].Opcode is "endloop" or "endswitch" && open.TryPop(out int start)) ends[start] = index;
        var flow = new Stack<object>();
        for (int pc = 0; pc < code.Count; pc++)
        {
            var (opcode, args) = code[pc];
            if (opcode is "call" or "callc" or "retc_nz" or "retc_z" or "continue" or "continuec_nz" or "continuec_z")
            {
                unresolved.Add(opcode);
                return false;
            }
            if (opcode == "loop") { flow.Push(new LoopScope(pc, ends[pc], branches.Count)); continue; }
            if (opcode == "endloop" && flow.TryPeek(out var last) && last is LoopScope again)
            {
                if (++again.Passes >= LoopPasses) { unresolved.Add("loop"); return false; }
                pc = again.Start;
                continue;
            }
            if (opcode is "break" or "breakc_nz" or "breakc_z" && flow.TryPeek(out var inner) && inner is LoopScope loop)
            {
                // An exit inside an if would need the branch condition as a loop predicate.
                if (branches.Count != loop.Depth) { unresolved.Add("loop"); return false; }
                bool? taken = opcode == "break" ? true : Truth(args[0]) is bool test ? test == (opcode == "breakc_nz") : null;
                // An exit test that depends on the pixel is taken: the loop keeps only the passes before it.
                if (taken is null) approximateLoop = true;
                if (taken != false) { flow.Pop(); pc = loop.End; }
                continue;
            }
            if (opcode == "switch") { flow.Push(new SwitchScope(branches.Count, Value(args[0], 0), Clone(registers))); continue; }
            if (flow.TryPeek(out var current) && current is SwitchScope choice)
            {
                if (opcode is "case" or "default")
                {
                    // A case body that falls into the next label is not a plain selection.
                    if (choice.Body) { unresolved.Add("switch"); return false; }
                    choice.Labels.Add(opcode == "case" ? Value(args[0], 0) : null);
                    registers = Clone(choice.Before);
                    continue;
                }
                if (opcode == "break")
                {
                    if (branches.Count != choice.Depth) { unresolved.Add("switch"); return false; }
                    choice.Cases.Add((choice.Labels, registers));
                    (choice.Labels, choice.Body) = ([], false);
                    continue;
                }
                if (opcode == "endswitch")
                {
                    flow.Pop();
                    if (choice.Labels.Count != 0) choice.Cases.Add((choice.Labels, registers));
                    registers = choice.Cases.FirstOrDefault(value => value.Labels.Contains(null)).Registers ?? choice.Before;
                    foreach (var (labels, state) in Enumerable.Reverse(choice.Cases).Where(value => !value.Labels.Contains(null)))
                    {
                        int condition = labels.Select(label => Op("eq", choice.Selector, label!.Value)).Aggregate((a, b) => Op("max", a, b));
                        var rest = registers;
                        registers = state.Keys.Union(rest.Keys).ToDictionary(key => key, key =>
                            Enumerable.Range(0, 4).Select(lane => Merge(condition, Get(state, key, lane), Get(rest, key, lane))).ToArray());
                    }
                    continue;
                }
                choice.Body = true;
            }
            if (opcode.StartsWith("break", StringComparison.Ordinal)) { unresolved.Add(opcode); return false; }
            if (opcode is "if_nz" or "if_z")
            {
                int condition = Op("ne", Value(args[0], 0), Number(0));
                if (opcode == "if_z") condition = Op("add", Number(1), Op("neg", condition));
                branches.Push(new(Clone(registers), condition));
                continue;
            }
            if (opcode == "else" && branches.TryPeek(out var alternate))
            {
                alternate.First = Clone(registers);
                registers = Clone(alternate.Before);
                continue;
            }
            if (opcode == "endif" && branches.TryPop(out var branch))
            {
                var first = branch.First ?? registers;
                var second = branch.First is null ? branch.Before : registers;
                registers = first.Keys.Union(second.Keys).ToDictionary(key => key, key =>
                    Enumerable.Range(0, 4).Select(lane => Merge(branch.Condition,
                        Get(first, key, lane), Get(second, key, lane))).ToArray());
                continue;
            }
            if (opcode is "discard_nz" or "discard_z")
            {
                int kill = Op(opcode == "discard_z" ? "eq" : "ne", Value(args[0], 0), Number(0));
                foreach (var scope in branches)
                    kill = Op("mul", kill, scope.First is null ? scope.Condition : Op("add", Number(1), Op("neg", scope.Condition)));
                if (kill < 0) unsupportedDiscard = true;
                else { hasDiscard = true; visibility = Op("mul", visibility, Op("add", Number(1), Op("neg", kill))); }
                continue;
            }
            // sincos writes sin to its first destination and cos to its second; either may be null.
            if (opcode is "sincos" or "sincos_sat" && args.Length == 3)
            {
                var writes = new List<(string Name, int Lane, int Value)>();
                for (int target = 0; target < 2; target++)
                    if (Regex.Match(args[target], @"^(?<name>r\d+)(?:\.(?<mask>[xyzw]+))?$") is { Success: true } written)
                        foreach (char channel in written.Groups["mask"].Value is { Length: > 0 } lanes ? lanes : "xyzw")
                        {
                            int lane = "xyzw".IndexOf(channel), value = Op(target == 0 ? "sin" : "cos", Value(args[2], lane));
                            writes.Add((written.Groups["name"].Value, lane, opcode == "sincos_sat" ? Saturate(value) : value));
                        }
                // Both results read the source before either write.
                foreach (var (target, lane, value) in writes)
                {
                    int[] held = registers.TryGetValue(target, out var prior) ? prior.ToArray() : [-1, -1, -1, -1];
                    held[lane] = value;
                    registers[target] = held;
                }
                continue;
            }
            // imul and umul write the high half first; only a null high half keeps the low product.
            if (opcode is "imul" or "umul" && args.Length == 4 && args[0] == "null") args = args[1..];
            if (args.Length < 2) continue;
            var destination = Regex.Match(args[0], @"^(?<name>[ro]\d+)(?:\.(?<mask>[xyzw]+))?$");
            if (!destination.Success) continue;
            string name = destination.Groups["name"].Value, mask = destination.Groups["mask"].Value;
            if (mask.Length == 0) mask = "xyzw";
            int[] output = registers.TryGetValue(name, out var previous) ? previous.ToArray() : [-1, -1, -1, -1];
            string operation = opcode.EndsWith("_sat", StringComparison.Ordinal) ? opcode[..^4] : opcode;
            // Explicit LOD, bias and gradient samples read the same texels; Blender picks its own mip.
            // A cube read resolves only through a stand-in; a volume read falls back to one.
            if (Regex.Match(opcode, @"^(?<kind>sample|sample_l|sample_b|sample_d|ld)_indexable\((?<dimension>texture2d|texture3d|texturecube)\)") is { Success: true } read)
            {
                var texture = args.Skip(2).Select(value => Regex.Match(value, @"^t(?<slot>\d+)(?:\.(?<swizzle>[xyzw]+))?$"))
                    .FirstOrDefault(value => value.Success);
                var sampler = args.Skip(2).Select(value => Regex.Match(value, @"^s(?<slot>\d+)$"))
                    .FirstOrDefault(value => value.Success);
                uint? samplerSlot = sampler is null ? null : uint.Parse(sampler.Groups["slot"].Value);
                foreach (char channel in mask)
                {
                    int lane = "xyzw".IndexOf(channel), sample = -1;
                    if (texture is not null)
                    {
                        string swizzle = texture.Groups["swizzle"].Value;
                        if (swizzle.Length == 0) swizzle = "xyzw";
                        uint slot = uint.Parse(texture.Groups["slot"].Value);
                        int component = "xyzw".IndexOf(swizzle[swizzle.Length == 1 ? 0 : lane]);
                        if (read.Groups["dimension"].Value == "texture3d" && read.Groups["kind"].Value != "ld" && volumes.Contains(slot)
                            && Value(args[1], 0) is >= 0 and var x && Value(args[1], 1) is >= 0 and var y && Value(args[1], 2) is >= 0 and var z)
                        {
                            output[lane] = Add(new("volume", [x, y, z], Slot: slot, Channel: component) { SamplerSlot = samplerSlot });
                            continue;
                        }
                        if (standIns.TryGetValue(slot, out var fixedValue))
                        {
                            output[lane] = Number(fixedValue[component]);
                            continue;
                        }
                        int u = Value(args[1], 0), v = Value(args[1], 1);
                        if (read.Groups["kind"].Value == "ld" || !textures.Contains(slot)) unresolved.Add("t" + slot);
                        if (read.Groups["kind"].Value != "ld" && u >= 0 && v >= 0 && textures.Contains(slot))
                            sample = Add(new("sample", [u, v], Slot: slot, Channel: component) { SamplerSlot = samplerSlot });
                    }
                    output[lane] = sample;
                }
            }
            else
            {
                int dot = -1;
                if (operation is "dp2" or "dp3" or "dp4")
                {
                    dot = Number(0);
                    for (int lane = 0; lane < operation[^1] - '0'; lane++)
                        dot = Op("add", dot, Op("mul", Value(args[1], lane), Value(args[2], lane)));
                    if (operation == "dp3" && dot >= 0 && !opcode.EndsWith("_sat", StringComparison.Ordinal))
                        dots.TryAdd(dot, (Lanes(args[1]), Lanes(args[2])));
                }
                if (water && reflectNormal is null && operation == "mad" && mask.Length == 3 && args.Length == 4
                    && args[3].StartsWith('-') && Reflects(Lanes(args[1]), args[2], Lanes(args[3][1..])) is { } normal)
                    reflectNormal = normal;
                bool packsSmoothness = operation == "mad" && args.Length == 4 && IsNumber(Value(args[2], 0), .125f)
                    && IsNumber(Value(args[3], 0), .375f);
                if (packsSmoothness) smoothness = Value(args[1], "xyzw".IndexOf(mask[0]));
                if (operation == "mad" && args.Length == 4 && Near(Value(args[2], 0), EmissionScale) && Near(Value(args[3], 0), EmissionBias)
                    && Value(args[1], "xyzw".IndexOf(mask[0])) is >= 0 and var encoded && nodes[encoded].Operation == "log")
                    emission = nodes[encoded].Inputs[0];
                bool saturated = opcode.EndsWith("_sat", StringComparison.Ordinal);
                foreach (char channel in mask)
                {
                    int lane = "xyzw".IndexOf(channel);
                    int value = saturated && Unbounded(operation, args, lane) is float bounded ? Number(bounded)
                        : operation.StartsWith("deriv_rt", StringComparison.Ordinal)
                        ? Footprint(Value(args[1], lane), vertical: operation.StartsWith("deriv_rty", StringComparison.Ordinal))
                        : operation.StartsWith("dp", StringComparison.Ordinal) ? dot
                        : operation == "and" && args.Length == 3 ? And(args, lane)
                        : operation == "or" && args.Length == 3 ? Or(Value(args[1], lane), Value(args[2], lane))
                        : operation is "iadd" or "ieq" or "ine" or "ige" or "ilt" or "imax" or "imin" && args.Length == 3
                            ? Integer(operation, Value(args[1], lane), Value(args[2], lane))
                        : operation == "ineg" && args.Length == 2 && Value(args[1], lane) is var negated && Integral(negated) ? Op("neg", negated)
                        : Op(operation, args.Skip(1).Select(argument => Value(argument, lane)).ToArray());
                    output[lane] = saturated ? Saturate(value) : value;
                    if (packsSmoothness) packedRadius = output[lane];
                }
            }
            registers[name] = output;
            if (nodes.Count > NodeLimit) { unresolved.Add("size"); return false; }
        }
        return true;
    }

    // Policy: a loop that needs more passes than this stops with a note.
    private const int LoopPasses = 64;
    // Policy: a program past this many nodes is too large for a Blender material.
    private const int NodeLimit = 4096;
    private sealed record Instruction(string Opcode, string[] Args);

    /** An open loop; Depth is the branch depth it opened at. */
    private sealed class LoopScope(int start, int end, int depth)
    {
        public int Start { get; } = start;
        public int End { get; } = end;
        public int Depth { get; } = depth;
        public int Passes { get; set; }
    }

    /** An open switch: the labels of the case being read, and each finished case with its registers. */
    private sealed class SwitchScope(int depth, int selector, Dictionary<string, int[]> before)
    {
        public int Depth { get; } = depth;
        public int Selector { get; } = selector;
        public Dictionary<string, int[]> Before { get; } = before;
        // A null label is the default case.
        public List<int?> Labels { get; set; } = [];
        public bool Body { get; set; }
        public List<(List<int?> Labels, Dictionary<string, int[]> Registers)> Cases { get; } = [];
    }

    private static List<Instruction> Parse(string text)
    {
        var code = new List<Instruction>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            // Immediate constant buffer rows continue their dcl line.
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("dcl_", StringComparison.Ordinal)
                || line.StartsWith('{')) continue;
            int space = line.IndexOf(' ');
            code.Add(new(space < 0 ? line : line[..space], space < 0 ? [] : DxbcText.SplitOperands(line[(space + 1)..])));
        }
        return code;
    }

    /** Rows of the shader's immediate constant buffer; empty when it declares none. */
    private static float[][] ImmediateRows(string text)
    {
        var table = Regex.Match(text, @"dcl_immediateConstantBuffer\s*\{(?<rows>.*?\})\s*\}", RegexOptions.Singleline);
        return !table.Success ? [] : Regex.Matches(table.Groups["rows"].Value, @"\{(?<row>[^{}]*)\}")
            .Select(row => row.Groups["row"].Value.Split(',').Select(value => MaterialPreviewAnalyzer.ParseLiteral(value.Trim())).ToArray())
            .ToArray();
    }

    /** A loop exit or branch test that resolves to a constant; null when it depends on the pixel. */
    private bool? Truth(string operand)
    {
        int value = Value(operand, 0);
        return value >= 0 && nodes[value].Operation == "constant" ? nodes[value].Value != 0 : null;
    }

    // Finite stand-ins for an operand a nonfinite lane leaves free.
    private static readonly float[] FreeValues = [-1e30f, -1, -1e-6f, 0, 1e-6f, .5f, 1, 1e30f];

    /**
     * A saturated lane with an infinite or NaN constant operand, when every finite value of its other operands
     * gives one result. Saturate turns NaN into 0, as Direct3D does.
     */
    private float? Unbounded(string operation, string[] args, int lane)
    {
        if (operation is not ("add" or "mul" or "mad" or "min" or "max" or "div") || args.Length < 3) return null;
        var fixedValues = args.Skip(1).Select(argument => RawConstant(argument, lane)).ToArray();
        if (!fixedValues.Any(value => value is float number && !float.IsFinite(number))) return null;
        // Op already folds a product with a zero factor in a refactoring shader.
        if (refactoring && operation is "mul" or "mad" && (fixedValues[0] == 0 || fixedValues[1] == 0)) return null;
        float? result = null;
        foreach (var values in Choices(fixedValues, 0))
        {
            float a = values[0], b = values[1];
            // Direct3D min and max return the operand that is not NaN.
            float raw = operation switch
            {
                "add" => a + b, "mul" => a * b, "mad" => a * b + values[2], "div" => a / b,
                "min" => float.IsNaN(a) ? b : float.IsNaN(b) ? a : MathF.Min(a, b),
                _ => float.IsNaN(a) ? b : float.IsNaN(b) ? a : MathF.Max(a, b)
            };
            float saturated = float.IsNaN(raw) ? 0 : Math.Clamp(raw, 0, 1);
            if (result is null) result = saturated;
            else if (result != saturated) return null;
        }
        return result;
    }

    /** Every combination of the fixed values with each free one taken from FreeValues. */
    private static IEnumerable<float[]> Choices(float?[] fixedValues, int index)
    {
        if (index == fixedValues.Length) { yield return new float[fixedValues.Length]; yield break; }
        foreach (var rest in Choices(fixedValues, index + 1))
            foreach (float value in fixedValues[index] is float known ? [known] : FreeValues)
            {
                var values = rest.ToArray();
                values[index] = value;
                yield return values;
            }
    }

    /** A literal, cb0 lane or constant register lane, nonfinite values included; null for anything else. */
    private float? RawConstant(string operand, int lane)
    {
        operand = operand.Trim();
        bool negate = operand.StartsWith('-');
        if (negate) operand = operand[1..];
        bool absolute = operand.StartsWith('|') && operand.EndsWith('|');
        if (absolute) operand = operand[1..^1];
        float? value = null;
        if (operand.StartsWith("l(", StringComparison.Ordinal))
        {
            string[] components = operand[2..^1].Split(',');
            value = MaterialPreviewAnalyzer.ParseLiteral(components[Math.Min(lane, components.Length - 1)].Trim());
        }
        else if (Regex.Match(operand, @"^(?<reg>r\d+|cb0\[(?<row>\d+)\])(?:\.(?<swizzle>[xyzw]+))?$") is { Success: true } match)
        {
            string swizzle = match.Groups["swizzle"].Value;
            int component = swizzle.Length == 0 ? lane : "xyzw".IndexOf(swizzle[Math.Min(lane, swizzle.Length - 1)]);
            if (match.Groups["row"].Success)
            {
                int row = int.Parse(match.Groups["row"].Value);
                if (constants is not null && row < constants.Count && !timeTerms.ContainsKey(row * 4 + component)) value = constants[row][component];
            }
            else if (Get(registers, match.Groups["reg"].Value, component) is >= 0 and var node && nodes[node].Operation == "constant")
                value = nodes[node].Value;
        }
        if (value is not float result) return null;
        if (absolute) result = MathF.Abs(result);
        return negate ? -result : result;
    }

    /** In the Visibility pass a branch that does not resolve, such as a hashed dither, is dropped. */
    private int Merge(int condition, int first, int second)
    {
        int merged = Op("movc", condition, first, second);
        if (merged >= 0 || !resolvedBranchOnly) return merged;
        return first < 0 ? second : second < 0 ? first : -1;
    }

    /** Missing runtime inputs stay unknown rather than becoming zero-valued constants. */
    private int Value(string operand, int lane)
    {
        operand = operand.Trim();
        bool negate = operand.StartsWith('-');
        if (negate) operand = operand[1..];
        bool absolute = operand.StartsWith('|') && operand.EndsWith('|');
        if (absolute) operand = operand[1..^1];
        int value = -1;
        if (operand.StartsWith("l("))
        {
            string[] components = operand[2..^1].Split(',');
            string scalar = components[Math.Min(lane, components.Length - 1)].Trim();
            value = Number(MaterialPreviewAnalyzer.ParseLiteral(scalar));
        }
        else if (Regex.Match(operand, @"^icb\[(?:(?<index>r\d+\.[xyzw])\s*\+\s*)?(?<offset>\d+)\](?:\.(?<swizzle>[xyzw]+))?$")
            is { Success: true } table)
        {
            string swizzle = table.Groups["swizzle"].Value;
            int component = swizzle.Length == 0 ? lane : "xyzw".IndexOf(swizzle[Math.Min(lane, swizzle.Length - 1)]);
            int row = int.Parse(table.Groups["offset"].Value);
            int index = table.Groups["index"].Success ? Value(table.Groups["index"].Value, 0) : Number(0);
            if (index >= 0 && nodes[index] is { Operation: "constant" } held && held.Value == MathF.Round(held.Value)
                && row + (int)held.Value is var at && at >= 0 && at < immediate.Length && component < immediate[at].Length)
                value = Number(immediate[at][component]);
            else unresolved.Add("icb");
        }
        else
        {
            // A buffer index register that holds a whole constant reads that row.
            if (Regex.Match(operand, @"^cb(?<slot>\d+)\[(?<index>[rv]\d+\.[xyzw])\s*\+\s*(?<offset>\d+)\](?<swizzle>\.[xyzw]+)?$") is { Success: true } dynamic
                && Value(dynamic.Groups["index"].Value, 0) is >= 0 and var at && nodes[at] is { Operation: "constant" } index
                && index.Value == MathF.Round(index.Value) && index.Value is >= 0 and < 65536)
                operand = $"cb{dynamic.Groups["slot"].Value}[{int.Parse(dynamic.Groups["offset"].Value) + (int)index.Value}]{dynamic.Groups["swizzle"].Value}";
            var match = Regex.Match(operand, @"^(?<reg>[rvo]\d+|cb\d+\[\d+\])(?:\.(?<swizzle>[xyzw]+))?$");
            // An indexed temp or a dynamically indexed buffer has no expression here.
            if (!match.Success && !Regex.IsMatch(operand, @"^[tsu]\d+")) unresolved.Add(Regex.Match(operand, @"^[a-z]+\d*").Value + "[]");
            if (match.Success)
            {
                string register = match.Groups["reg"].Value, swizzle = match.Groups["swizzle"].Value;
                int component = swizzle.Length == 0 ? lane : "xyzw".IndexOf(swizzle[Math.Min(lane, swizzle.Length - 1)]);
                if (motionInputs is not null && register[0] == 'v') value = MotionInput(register, component);
                else if (motionInputs is not null && MotionRow(register, component) is float rest) value = Number(rest);
                else if (vertexStage && register[0] == 'v')
                    value = varyings!.TryStream(register, component, out string input) ? Add(new(input, [], Channel: component)) : -1;
                else if (register[0] == 'v' && varyings?.TryGet(register, component, out string stream, out int channel) == true)
                    value = Add(new(stream, [], Channel: channel));
                else if (register[0] == 'v' && varyings?.TryConstant(register, component, out float fixedValue) == true)
                    value = Number(fixedValue);
                // v3.zw is UV map 1 with detailUv, else UV map 0 again. A lane the vertex shader computes is not a UV.
                else if (register == "v3" && (component < 2 || uvPairs) && (uvPairs || varyings?.Writes(register, component) != true))
                    value = Add(new("uv", [], Channel: component < 2 || detailUv ? component : component - 2));
                // Screen position, normalized; y runs down as in Direct3D.
                else if (register == screenRegister && component < 2) value = Add(new("screen", [], Channel: component));
                // View row 12 is (width, height, 1/width, 1/height); screen reads a 1 x 1 target.
                else if (screenRegister is not null && register == "cb12[12]") value = Number(1);
                else if (resolveNativeBasis && register is "v0" or "v1" or "v2" && component < 3)
                    value = Add(new("basis", [], Slot: (uint)(register[1] - '0'), Channel: component));
                // An undeclared face register is only assumed for the pinned layouts.
                else if ((resolveNativeBasis || worldVaryings || frontFaceDeclared) && register == frontFaceRegister && component == 0)
                    value = Add(new("frontface", []));
                else if (worldVaryings && register is "v0" or "v1" or "v2" && component < 3)
                    value = Add(new("surface", [], Slot: (uint)(register[1] - '0'), Channel: component));
                else if (worldVaryings && register == "v4" && component < 3) value = Add(new("position", [], Channel: component));
                else if (register[0] == 'v' && varyings?.TryWorld(register, component, out int kind, out int vectorLane, out float sign) == true)
                    value = sign < 0 ? Op("neg", World(kind, vectorLane)) : World(kind, vectorLane);
                // View scope row 7 is the inverse view translation: the camera's world position.
                else if (register == "cb12[7]" && component < 3) value = Add(new("camera", [], Channel: component));
                // View scope rows 4 to 7 are the inverse view; row 6 is the view +Z axis, pointing back at the camera.
                else if (register == "cb12[6]" && component < 3) value = Add(new("view", [], Channel: component));
                else if (register.StartsWith("cb0[") && constants is not null
                    && int.TryParse(register.AsSpan(4, register.Length - 5), out int row) && row < constants.Count)
                    value = timeTerms.TryGetValue(row * 4 + component, out var term) ? Import(term)
                        : writtenRows?.Contains(row) == false && float.IsFinite(constants[row][component])
                        ? Add(new("constant", [], constants[row][component]) { TemplateLane = row * 4 + component })
                        : Number(constants[row][component]);
                else if (Regex.Match(register, @"^cb(?<slot>\d+)\[(?<row>\d+)\]$") is { Success: true } buffer
                    && buffers.TryGetValue(uint.Parse(buffer.Groups["slot"].Value), out var rows)
                    && int.Parse(buffer.Groups["row"].Value) is var bufferRow && bufferRow < rows.Length)
                    value = Number(rows[bufferRow][component]);
                else if (register.StartsWith("cb13[") && int.TryParse(register.AsSpan(5, register.Length - 6), out int frameRow)
                    && frameRow < Frame.Vectors.Length && Frame.KnownWordMasks[(uint)(frameRow * 4 + component)] == uint.MaxValue)
                    value = frameRow == 0 && component == 0 ? Import(TfxTerm.Time) : Number(Frame.Vectors[frameRow][component]);
                // Blender evaluates per pixel what the vertex shader evaluates per vertex.
                else if (register[0] == 'v' && vertex is not null && varyings!.TryOutput(register, component, out string output))
                    value = ImportVertex(Get(vertex.registers, output, component));
                else value = Get(registers, register, component);
                if (value < 0 && register[0] is 'v' or 'c') unresolved.Add(register);
            }
        }
        if (absolute) value = Op("abs", value);
        return negate ? Op("neg", value) : value;
    }

    // An infinite constant may fold back to a finite one, as exp(log(0)) does; Add keeps it out of other nodes.
    private int Number(float value) => !float.IsNaN(value) ? Add(new("constant", [], value)) : -1;

    /** One lane of a world vector the vertex shader writes; cross(N, T) and the camera offset are built here. */
    private int World(int kind, int lane)
    {
        int Surface(uint slot, int channel) => Add(new("surface", [], Slot: slot, Channel: channel));
        return kind switch
        {
            ShaderVaryings.WorldPosition => Add(new("position", [], Channel: lane)),
            ShaderVaryings.FromCamera => Op("add", Add(new("position", [], Channel: lane)), Op("neg", Add(new("camera", [], Channel: lane)))),
            ShaderVaryings.WorldCross => Op("add", Op("mul", Surface(0, (lane + 1) % 3), Surface(1, (lane + 2) % 3)),
                Op("neg", Op("mul", Surface(0, (lane + 2) % 3), Surface(1, (lane + 1) % 3)))),
            _ => Surface((uint)kind, lane)
        };
    }

    private int[] Lanes(string operand) => [Value(operand, 0), Value(operand, 1), Value(operand, 2)];

    /**
     * Matches N * K - V where K = d + d and d = dot(V, N), the expansion of reflect(V, N).
     * @return N, or null when the operands do not form a reflection or a lane is unknown.
     */
    private int[]? Reflects(int[] normal, string twice, int[] view)
    {
        int k = Value(twice, 0);
        if (normal.Any(value => value < 0) || view.Any(value => value < 0) || k < 0 || Value(twice, 1) != k
            || Value(twice, 2) != k || nodes[k] is not { Operation: "add" } sum || sum.Inputs[0] != sum.Inputs[1]
            || !dots.TryGetValue(sum.Inputs[0], out var pair)) return null;
        return pair.A.SequenceEqual(view) && pair.B.SequenceEqual(normal)
            || pair.A.SequenceEqual(normal) && pair.B.SequenceEqual(view) ? normal : null;
    }

    /** Shared subterms import once; a nonfinite constant leaves the lane unknown. */
    private int Import(TfxTerm term)
    {
        if (imported.TryGetValue(term, out int existing)) return existing;
        int value = term.Operation switch
        {
            "constant" => Number(term.Value),
            "time" => Add(new("time", [])),
            _ => Op(term.Operation, term.Inputs.Select(Import).ToArray())
        };
        imported[term] = value;
        return value;
    }

    /** Copies a vertex stage expression into this program; a lane the vertex stage does not resolve stays unknown. */
    private int ImportVertex(int index)
    {
        if (index < 0) return -1;
        if (vertexImports.TryGetValue(index, out int existing)) return existing;
        var node = vertex!.nodes[index];
        int value = Add(node with { Inputs = node.Inputs.Select(ImportVertex).ToArray() });
        vertexImports[index] = value;
        return value;
    }

    private bool IsNumber(int node, float value) => node >= 0 && nodes[node].Operation == "constant" && nodes[node].Value == value;
    // Disassembly prints six decimals.
    private bool Near(int node, float value) => node >= 0 && nodes[node].Operation == "constant" && Math.Abs(nodes[node].Value - value) < 1e-5f;

    /** A comparison gives an all-ones mask; AND with a mask keeps the other operand or gives zero. */
    private int And(int left, int right)
    {
        if (IsMask(left) && IsMask(right)) return Op("mul", left, right);
        if (left >= 0 && right >= 0 && !IsMask(left) && !IsMask(right) && Integral(left) && Integral(right)
            && nodes[left].Operation == "constant" && nodes[right].Operation == "constant")
            return Number((long)nodes[left].Value & (long)nodes[right].Value);
        if (IsMask(left) && right >= 0) return Op("movc", left, right, Number(0));
        if (IsMask(right) && left >= 0) return Op("movc", right, left, Number(0));
        if (left >= 0 && right >= 0) unresolved.Add("and");
        return -1;
    }

    /** AND with a whole-number literal keeps that integer where a compare mask is set. */
    private int And(string[] args, int lane)
    {
        int left = Value(args[1], lane), right = Value(args[2], lane);
        if (WholeLiteral(args[2], lane) && IsMask(left)) return Op("movc", left, right, Number(0));
        if (WholeLiteral(args[1], lane) && IsMask(right)) return Op("movc", right, left, Number(0));
        return And(left, right);
    }

    /** A decimal integer literal lane; float bit patterns print in hex or with a decimal point. */
    private static bool WholeLiteral(string operand, int lane)
    {
        operand = operand.Trim();
        if (!operand.StartsWith("l(", StringComparison.Ordinal)) return false;
        string[] components = operand[2..^1].Split(',');
        return Regex.IsMatch(components[Math.Min(lane, components.Length - 1)].Trim(), @"^\d+$");
    }

    /**
     * Integer sums and compares of masks, as a sign test builds them. A node holds the negated integer:
     * a set mask is integer -1 but node 1. Sums and equality keep that relation.
     */
    private int MaskSum(string operation, int left, int right) => IsMaskSum(left) && IsMaskSum(right)
        ? Op(operation switch { "iadd" => "add", "ieq" => "eq", _ => "ne" }, left, right) : Op(operation, left, right);

    /** Integer operations on masks, then on values held as their number. */
    private int Integer(string operation, int left, int right)
    {
        if (operation is "iadd" or "ieq" or "ine" && IsMaskSum(left) && IsMaskSum(right)) return MaskSum(operation, left, right);
        if (!Integral(left) || !Integral(right)) return Op(operation, left, right);
        return Op(operation switch
        {
            "iadd" => "add", "ieq" => "eq", "ine" => "ne", "ige" => "ge", "ilt" => "lt", "imax" => "max", _ => "min"
        }, left, right);
    }

    private bool IsMaskSum(int node) => IsMask(node) || node >= 0 && nodes[node] is { Operation: "neg" or "add" } value && value.Inputs.All(IsMaskSum);

    /** OR of two masks is set when either is. */
    private int Or(int left, int right) => IsMask(left) && IsMask(right) ? Op("max", left, right) : Op("or", left, right);

    /** Compared values fold to 1 or 0, so those constants are masks too. */
    private bool IsMask(int node) => node >= 0 && nodes[node] switch
    {
        { Operation: "lt" or "ge" or "eq" or "ne" } => true,
        { Operation: "constant", Value: 0 or 1 } => true,
        { Operation: "mul" or "max" } value => value.Inputs.All(IsMask),
        { Operation: "movc" } value => IsMask(value.Inputs[1]) && IsMask(value.Inputs[2]),
        _ => false
    };
    private int Saturate(int value) => Op("min", Op("max", value, Number(0)), Number(1));

    // Policy: a pixel spans 1 cm of world or 1/100 of UV. Derivatives here only pick a detail scale.
    private const float FootprintSize = .01f;

    /** A screen derivative of a UV or world position x or y, with screen x along x; unknown for other values. */
    private int Footprint(int value, bool vertical)
    {
        if (value >= 0 && nodes[value].Operation is "position" or "uv" && nodes[value].Channel < 2)
            return Number(nodes[value].Channel == (vertical ? 1 : 0) ? FootprintSize : 0);
        if (value >= 0) unresolved.Add("deriv");
        return -1;
    }

    /** A value built from integer literals or rounding holds its number, so itof keeps it; a comparison mask does not. */
    private bool Integral(int node)
    {
        if (node < 0) return false;
        if (integral.TryGetValue(node, out bool known)) return known;
        bool result = nodes[node] switch
        {
            { Operation: "constant", Value: var value } => value == MathF.Round(value) && Math.Abs(value) < 1 << 24,
            { Operation: "movc" } choice => Integral(choice.Inputs[1]) && Integral(choice.Inputs[2]),
            { Operation: "round_z" or "round_ni" or "round_pi" or "round_ne" } => true,
            { Operation: "add" or "mul" or "neg" or "min" or "max" } value => value.Inputs.All(Integral),
            _ => false
        };
        integral[node] = result;
        return result;
    }
    // Nodes never change, so Integral holds per node.
    private readonly Dictionary<int, bool> integral = [];

    /** Integer bit and product operations on whole constants, as bone index arithmetic forms them; null for others. */
    private float? WholeConstant(string operation, int[] values)
    {
        if (operation is not ("ishl" or "ishr" or "ushr" or "bfi" or "imul" or "umul" or "imad" or "umad" or "xor" or "ult" or "uge" or "umin" or "umax")
            || values.Any(value => value < 0 || nodes[value] is not { Operation: "constant" } held || held.Value != MathF.Round(held.Value)
                || Math.Abs(held.Value) >= 1 << 24)) return null;
        long[] a = values.Select(value => (long)nodes[value].Value).ToArray();
        long result = operation switch
        {
            "ishl" => a[0] << (int)(a[1] & 31), "ishr" or "ushr" => a[0] >> (int)(a[1] & 31),
            "bfi" => (a[2] << (int)(a[1] & 31) & ((1L << (int)(a[0] & 31)) - 1 << (int)(a[1] & 31)))
                | (a[3] & ~((1L << (int)(a[0] & 31)) - 1 << (int)(a[1] & 31))),
            "imul" or "umul" => a[0] * a[1], "imad" or "umad" => a[0] * a[1] + a[2], "xor" => a[0] ^ a[1],
            "ult" => a[0] < a[1] ? 1 : 0, "uge" => a[0] >= a[1] ? 1 : 0,
            "umin" => Math.Min(a[0], a[1]), _ => Math.Max(a[0], a[1])
        };
        return Math.Abs(result) < 1 << 24 ? result : null;
    }

    /** Adds one operation node, folding constants, settled selections and refactoring identities; -1 when unknown. */
    private int Op(string operation, params int[] values)
    {
        if (operation == "mov") return values[0];
        if (operation is "itof" or "utof")
        {
            if (Integral(values[0])) return values[0];
            // A sum of compare masks holds the negated integer.
            if (operation == "itof" && IsMaskSum(values[0]) && nodes[values[0]].Operation != "constant") return Op("neg", values[0]);
            if (values[0] >= 0) unresolved.Add(operation);
            return -1;
        }
        // An integer is held as its number; truncation matches for values inside the integer range.
        if (operation is "ftoi" or "ftou")
            return values[0] < 0 ? -1 : Op("round_z", operation == "ftou" ? Op("max", values[0], Number(0)) : values[0]);
        if (operation == "rcp") return Op("div", Number(1), values[0]);
        if (WholeConstant(operation, values) is float whole) return Number(whole);
        if (operation == "movc")
        {
            if (values[1] == values[2]) return values[1];
            if (values[0] >= 0 && nodes[values[0]].Operation == "constant") return values[nodes[values[0]].Value != 0 ? 1 : 2];
        }
        // A shader that allows refactoring may fold x * 0 to 0, even where x is unknown or infinite.
        if (refactoring && operation is "mul" or "mad" && (IsNumber(values[0], 0) || IsNumber(values[1], 0)))
            return operation == "mul" ? Number(0) : values[2];
        if (operation is not ("add" or "mul" or "div" or "mad" or "min" or "max" or "abs" or "neg"
            or "sqrt" or "rsq" or "exp" or "log" or "frc" or "round_ni" or "round_ne" or "round_pi" or "round_z"
            or "sin" or "cos" or "lt" or "ge" or "eq" or "ne" or "movc"))
        {
            unresolved.Add(operation.Split('(')[0]);
            return -1;
        }
        if (values.Any(value => value < 0)) return -1;
        if (values.All(value => nodes[value].Operation == "constant"))
        {
            float a = nodes[values[0]].Value, b = values.Length > 1 ? nodes[values[1]].Value : 0;
            float result = operation switch
            {
                "add" => a + b, "mul" => a * b, "div" => a / b,
                "mad" => a * b + nodes[values[2]].Value, "min" => Math.Min(a, b), "max" => Math.Max(a, b),
                "abs" => Math.Abs(a), "neg" => -a, "sqrt" => MathF.Sqrt(a), "rsq" => 1 / MathF.Sqrt(a),
                "exp" => MathF.Pow(2, a), "log" => MathF.Log2(a), "frc" => a - MathF.Floor(a),
                "round_ni" => MathF.Floor(a), "round_ne" => MathF.Round(a, MidpointRounding.ToEven),
                "round_pi" => MathF.Ceiling(a), "round_z" => MathF.Truncate(a), "sin" => MathF.Sin(a), "cos" => MathF.Cos(a),
                "lt" => a < b ? 1 : 0, "ge" => a >= b ? 1 : 0,
                "eq" => a == b ? 1 : 0, "ne" => a != b ? 1 : 0, "movc" => nodes[values[a != 0 ? 1 : 2]].Value,
                _ => float.NaN
            };
            if (float.IsNaN(result)) unresolved.Add("nonfinite");
            return Number(result);
        }
        bool Is(int input, float value) => IsNumber(values[input], value);
        if (refactoring) switch (operation)
        {
            case "add" when Is(0, 0): return values[1];
            case "add" when Is(1, 0): return values[0];
            case "mul" when Is(0, 1): return values[1];
            case "mul" when Is(1, 1): return values[0];
            case "div" when Is(1, 1): return values[0];
            case "mad" when Is(0, 1): return Op("add", values[1], values[2]);
            case "mad" when Is(1, 1): return Op("add", values[0], values[2]);
            case "mad" when Is(2, 0): return Op("mul", values[0], values[1]);
        }
        return Add(new(operation, values));
    }

    private int Add(BundleShaderNode value)
    {
        // Blender nodes and the bundle JSON take only finite constants.
        if (value.Inputs.Any(input => nodes[input] is { Operation: "constant" } held && !float.IsFinite(held.Value)))
        {
            unresolved.Add("nonfinite");
            return -1;
        }
        string key = $"{value.Operation}:{value.Value:R}:{value.Slot}:{value.Channel}:{value.SamplerSlot}:{value.TemplateLane}:{string.Join(',', value.Inputs)}";
        if (cache.TryGetValue(key, out int existing)) return existing;
        int index = nodes.Count; nodes.Add(value); cache.Add(key, index); return index;
    }
    private static int Get(Dictionary<string, int[]> values, string name, int lane) => values.TryGetValue(name, out var vector) ? vector[lane] : -1;
    private static Dictionary<string, int[]> Clone(Dictionary<string, int[]> values) => values.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
}
