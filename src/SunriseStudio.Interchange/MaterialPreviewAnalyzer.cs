using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;

namespace SunriseStudio.Interchange;

/** Preview roles follow shader component flow; slot numbers alone never assign texture roles. */
public static class MaterialPreviewAnalyzer
{
    // This vertex shader supplies world N, T and cross(N,T)*handedness in TEXCOORD0..2.
    private const string NativeBasisVertexShader = "D67606C0E9766D10759890F5A32A555662887C25533D81A2951E2D4DEACD0D66";
    // Extern 86 vector 0 holds the part's gear-dye bit; the material reads it to pick its dye pair.
    public static readonly TfxExternKey GearDyeBit = new(86, 0);
    private static readonly Lazy<NativeRenderStateCatalog> States = new(() => new());
    public sealed record TextureInput(uint Slot, uint Tag, int Format);
    private sealed record Source(uint Slot, char Channel, bool DirectUv);
    private sealed record Value(HashSet<Source> Sources, string? Input = null, float? Constant = null)
    {
        internal static Value Unknown => new([]);
        internal static Value Number(float value) => new([], Constant: float.IsFinite(value) ? value : null);
    }

    /**
     * A forced stage byte beats the material override, which beats the stage pass byte.
     * @param forcedBlend Blend byte a decal dye forces over all of them; its low 7 bits are the blend index.
     */
    public static string ResolveBlendMode(uint stateOverrides, int renderStage, byte? forcedBlend = null)
    {
        int index = forcedBlend is { } forced ? forced & 0x7F : NativeStagePassStates.Resolve(stateOverrides, renderStage).Blend;
        return NativeStagePassStates.ClassifyBlend(States.Value.GetBlend(index).RenderTarget[0]);
    }

    /** True when the stage culls clockwise triangles; Blender treats those as back faces. */
    public static bool CullsClockwise(uint stateOverrides, int renderStage)
    {
        var selection = NativeStagePassStates.Resolve(stateOverrides, renderStage);
        var rasterizer = States.Value.GetRasterizer(selection.Rasterizer, selection.Bias);
        bool counterClockwise = rasterizer.FrontCounterClockwise;
        return rasterizer.CullMode == Vortice.Direct3D11.CullMode.Back ? counterClockwise
            : rasterizer.CullMode == Vortice.Direct3D11.CullMode.Front && !counterClockwise;
    }

    public static BundleMaterialPreview Read(PackageCatalog catalog, uint materialTag, TfxInputs? inspectionInputs = null,
        IReadOnlyCollection<int>? renderStages = null) => Read(catalog, materialTag, inspectionInputs, renderStages, null);

    /**
     * Resolved draw textures replace material slots before shader dataflow assigns their roles.
     * @param constantBuffers Pixel constant buffers the draw binds besides cb0, by slot.
     * @param forcedBlend Blend byte the part's decal dye forces in every stage; null keeps the resolved blend.
     */
    public static BundleMaterialPreview Read(PackageCatalog catalog, uint materialTag, TfxInputs? inspectionInputs,
        IReadOnlyCollection<int>? renderStages, IReadOnlyList<TextureInput>? textureOverrides,
        IReadOnlyDictionary<uint, Vector4[]>? constantBuffers = null, byte? forcedBlend = null)
    {
        var reader = new GameMaterialReader(catalog);
        var material = reader.Read(materialTag);
        var stages = renderStages?.Where(stage => stage >= 0).ToArray() ?? [];
        var stageBlends = stages.Distinct().Order().ToDictionary(stage => stage,
            stage => ResolveBlendMode(material.StateOverrides, stage, forcedBlend));
        // Parts that write no colour are hidden, so a visible blend is the editable base.
        string baseBlend = stages.Length == 0 ? ResolveBlendMode(material.StateOverrides, -1, forcedBlend)
            : stages.GroupBy(stage => stageBlends[stage]).OrderBy(group => group.Key == "NoColor")
                .ThenByDescending(group => group.Count()).ThenBy(group => group.Min()).First().Key;
        if (material.PixelShader is not { } shader)
            return new("No pixel shader", 0, "", [], [1, 1, 1, 1], 0, .5f, 1, [0, 0, 0], ["Native pixel shader is unavailable."])
            { BlendMode = baseBlend, StageBlendModes = stageBlends.Count == 0 ? null : stageBlends };
        var inputs = new List<TextureInput>();
        foreach (var texture in material.Textures.Where(value => value.Stage == GameShaderStage.Pixel
            && value.ArraySize == 1 && value.Depth == 1 && value.ShaderDimension == "texture2d"))
            inputs.Add(new(texture.Slot, texture.TextureTag, (int)texture.Format));
        if (textureOverrides is not null)
            foreach (var texture in textureOverrides)
            {
                inputs.RemoveAll(value => value.Slot == texture.Slot);
                inputs.Add(texture);
            }
        Vector4[]? constants = null;
        IReadOnlyDictionary<int, TfxTerm>? terms = null;
        IReadOnlySet<int>? writtenRows = null;
        IReadOnlyDictionary<uint, BundleMaterialSampler>? samplers = null;
        IReadOnlyList<string> missing = [];
        (int Scale, int Offset)? decoratorRows = null;
        IReadOnlyList<Vector4>? vertexConstants = null;
        MaterialStageData? vertexStage = null;
        bool water = false;
        IReadOnlyDictionary<uint, string> runtimeSlots = new Dictionary<uint, string>();
        try
        {
            var execution = reader.ReadExecution(materialTag);
            vertexStage = execution.Vertex;
            var tfx = inspectionInputs ?? CreateInputs(catalog);
            if (material.VertexShader is { } recordReader && DecoratorRecordInput.IsMatch(recordReader.Disassembly))
                decoratorRows = DecoratorRows(execution.Vertex, tfx.Parameters);
            var evaluated = EvaluateWithUnknowns(execution.Pixel, tfx, out missing);
            water = new[] { execution.Pixel, execution.Vertex }.Any(stage =>
                TfxProgramInspection.Read(stage.TfxBytecode).Dependencies.Any(value => value.Extern == WaterExtern));
            if (evaluated is null) throw new NotSupportedException("TFX does not run with unknown inputs.");
            runtimeSlots = evaluated.ResourceBindings.Where(pair => pair.Key.Stage == GameShaderStage.Pixel)
                .ToDictionary(pair => pair.Key.Slot, pair => pair.Value.Name);
            samplers = evaluated.Samplers.Where(pair => pair.Key.Stage == GameShaderStage.Pixel).ToDictionary(
                pair => pair.Key.Slot, pair =>
                {
                    var value = pair.Value.Description;
                    return new BundleMaterialSampler(value.Filter.ToString(), value.AddressU.ToString(), value.AddressV.ToString(),
                        value.AddressW.ToString(), value.MipLODBias, value.MinLOD, value.MaxLOD, value.MaxAnisotropy);
                });
            constants = evaluated.Constants; terms = evaluated.Terms;
            // A constant-buffer tag replaces the inline template, so no cb0 lane is an editable field.
            if (execution.Pixel.ConstantSourceTag == 0) writtenRows = evaluated.WrittenRows;
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException
            or IndexOutOfRangeException or OverflowException) { }
        // The entity vertex run reads vertex cb0 as the viewport binds it.
        try { if (vertexStage is not null) vertexConstants = EvaluateWithUnknowns(vertexStage, inspectionInputs ?? CreateInputs(catalog), out _)?.Constants; }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException
            or IndexOutOfRangeException or OverflowException) { }
        string shaderHash = Convert.ToHexString(SHA256.HashData(reader.ReadShaderBytecode(shader)));
        string? vertexHash = null;
        try
        {
            if (material.VertexShader is { } vertexShader)
                vertexHash = Convert.ToHexString(SHA256.HashData(reader.ReadShaderBytecode(vertexShader)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or KeyNotFoundException) { }
        var varyings = material.VertexShader is { } vertex ? ShaderVaryings.Read(vertex.Disassembly, shader.Disassembly, decoratorRows, vertexConstants) : null;
        var standIns = runtimeSlots.Where(pair => RuntimeStandIns.ContainsKey(pair.Value))
            .ToDictionary(pair => pair.Key, pair => RuntimeStandIns[pair.Value]);
        // Blender has no cube sampler, so a cube texture reads as its mean texel; a volume read falls back to it.
        var meanTexels = new Dictionary<uint, string>();
        foreach (var texture in material.Textures.Where(value => value.Stage == GameShaderStage.Pixel && value.DeclaredByShader
            && value.ShaderDimension is "texture3d" or "texturecube" && !standIns.ContainsKey(value.Slot)))
            if (MeanTexel(catalog, texture.TextureTag, texture.IsSrgb) is { } mean) (standIns[texture.Slot], meanTexels[texture.Slot]) = (mean, texture.ShaderDimension!);
        var volumes = material.Textures.Where(value => value.Stage == GameShaderStage.Pixel && value.DeclaredByShader
                && value.ShaderDimension == "texture3d" && value.ArraySize == 1 && value.Depth > 1 && !runtimeSlots.ContainsKey(value.Slot))
            .ToDictionary(value => value.Slot, value => new BundleVolumeTexture(value.TextureTag, value.Width, value.Height, value.Depth,
                (int)Math.Ceiling(Math.Sqrt(value.Depth)), (int)value.Format)
                { Mean = standIns.TryGetValue(value.Slot, out var mean) ? [mean.X, mean.Y, mean.Z, mean.W] : null });
        // An empty texture row binds a null view, which reads zero, as in Studio's viewport.
        foreach (var (stage, slot) in material.EmptyTextureSlots)
            if (stage == GameShaderStage.Pixel && inputs.All(value => value.Slot != slot) && !runtimeSlots.ContainsKey(slot))
                standIns.TryAdd(slot, Vector4.Zero);
        // A soft-depth fade then saturates, so a forward shield or glow shows instead of fading to nothing.
        if (inputs.All(value => value.Slot != SceneDepthSlot) && !runtimeSlots.ContainsKey(SceneDepthSlot)
            && SceneDepthTexture.IsMatch(shader.Disassembly) && SoftDepthBuffer.IsMatch(shader.Disassembly)
            && constantBuffers?.ContainsKey(2) != true)
        {
            standIns[SceneDepthSlot] = Vector4.Zero;
            constantBuffers = new Dictionary<uint, Vector4[]>(constantBuffers ?? new Dictionary<uint, Vector4[]>()) { [2] = FarSceneDepth };
        }
        BundleMaterialPreview Build(string blendMode)
        {
            var preview = Analyze(shader.Disassembly, shader.HeaderTag, shaderHash, inputs, constants, blendMode,
                vertexHash == NativeBasisVertexShader, terms, constantBuffers: constantBuffers, varyings: varyings, standIns: standIns, water: water,
                volumes: volumes, writtenRows: writtenRows);
            preview = preview with { TextureFormats = inputs.ToDictionary(value => value.Slot, value => value.Format)
                    .Concat((preview.VolumeTextures ?? new Dictionary<uint, BundleVolumeTexture>()).Select(pair => KeyValuePair.Create(pair.Key, pair.Value.Format)))
                    .ToDictionary(), Samplers = samplers, ReadsFrameTextures = runtimeSlots.Count != 0 || constants is null };
            if (water) preview = MaterialPreviewRecipes.Water(preview);
            if (preview.Program?.Outputs.ContainsKey("BaseColor") == true && meanTexels.ContainsValue("texturecube"))
                preview = preview with { Notes = preview.Notes.Append("Cube textures read as their mean colour.").ToArray() };
            if (preview.Program?.Outputs.ContainsKey("BaseColor") == true
                && meanTexels.Any(pair => pair.Value == "texture3d" && preview.VolumeTextures?.ContainsKey(pair.Key) != true))
                preview = preview with { Notes = preview.Notes.Append("Volume textures read as their mean colour where their coordinates do not resolve.").ToArray() };
            if (samplers?.Values.Any(value => value.MipLodBias != 0 || value.MinLod != 0 || value.MaxLod < float.MaxValue) == true)
                preview = preview with { Notes = preview.Notes.Append("Blender chooses its own mip level; native mip bias and LOD limits are retained as metadata.").ToArray() };
            if (samplers?.Values.Any(value => value.AddressU != value.AddressV || value.AddressU is not ("Wrap" or "Clamp")) == true)
                preview = preview with { Notes = preview.Notes.Append("Blender expression samples approximate unsupported or mixed-axis sampler addressing.").ToArray() };
            if (samplers?.Values.Any(value => value.Filter is not ("MinMagMipLinear" or "MinMagMipPoint")) == true)
                preview = preview with { Notes = preview.Notes.Append("Blender expression samples approximate the native sampler filter.").ToArray() };
            preview = HideUnresolvedBlend(MaterialPreviewRecipes.Apply(preview));
            return missing.Count == 0 ? preview : preview with { Notes = preview.Notes.Concat(missing).ToArray() };
        }
        var result = Build(baseBlend);
        if (stageBlends.Count == 0) return result;
        var variants = stageBlends.Values.Distinct().Where(blend => blend != baseBlend && blend != "NoColor")
            .ToDictionary(blend => blend, Build);
        return result with { StageBlendModes = stageBlends, BlendVariants = variants.Count == 0 ? null : variants };
    }

    /**
     * The mean of a texture's smallest mip that still holds whole 4 x 4 blocks, over every face and depth slice.
     * @param srgb The sampler returns linear values, so texels are linearized before the mean.
     * @return Null when the texture is missing or its format has no decoder.
     */
    private static Vector4? MeanTexel(PackageCatalog catalog, uint tag, bool srgb)
    {
        try
        {
            var texture = new TextureDataReader(catalog).Read(tag);
            var mips = texture.Subresources.Where(value => value.ArraySlice == 0).ToList();
            int mip = mips.Where(value => value.Width >= 4 && value.Height >= 4).Select(value => value.MipLevel).DefaultIfEmpty(0).Max();
            var sum = Vector4.Zero; long count = 0;
            foreach (var sub in texture.Subresources.Where(value => value.MipLevel == mip))
                for (int depth = 0; depth < sub.Depth; depth++)
                {
                    byte[] pixels = BundleTextureCodec.Decode(texture, mip, sub.ArraySlice, depth).Pixels;
                    for (int at = 0; at + 3 < pixels.Length; at += 4, count++)
                    {
                        float Lane(int index) => pixels[at + index] / 255f is var value && srgb && index < 3
                            ? value <= .04045f ? value / 12.92f : MathF.Pow((value + .055f) / 1.055f, 2.4f) : value;
                        sum += new Vector4(Lane(0), Lane(1), Lane(2), Lane(3));
                    }
                }
            return count == 0 ? null : sum / count;
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException
            or KeyNotFoundException or InvalidOperationException or IOException) { return null; }
    }

    /** A volume texture's mip 0 depth slices in rows of Columns tiles, slice 0 at the top left. */
    public static RgbaImage VolumeAtlas(PackageCatalog catalog, BundleVolumeTexture volume)
    {
        var texture = new TextureDataReader(catalog).Read(volume.TextureTag);
        var sub = texture.Subresources.Single(value => value.MipLevel == 0 && value.ArraySlice == 0);
        if (sub.Width != volume.Width || sub.Height != volume.Height || sub.Depth != volume.Depth || volume.Columns < 1)
            throw new InvalidDataException("Volume texture size differs from its material binding.");
        int rows = (volume.Depth + volume.Columns - 1) / volume.Columns, stride = volume.Columns * volume.Width * 4;
        var pixels = new byte[stride * rows * volume.Height];
        for (int depth = 0; depth < volume.Depth; depth++)
        {
            var slice = BundleTextureCodec.Decode(texture, 0, 0, depth);
            int left = depth % volume.Columns * volume.Width, top = depth / volume.Columns * volume.Height;
            for (int y = 0; y < volume.Height; y++)
                slice.Pixels.AsSpan(y * volume.Width * 4, volume.Width * 4).CopyTo(pixels.AsSpan((top + y) * stride + left * 4));
        }
        return new(volume.Columns * volume.Width, rows * volume.Height, pixels);
    }

    /**
     * Missing render globals remain unavailable in small synthetic installations.
     * Gear dye bit 0 and exposure 0 match the viewer's defaults; set GearDyeBit for a part on bit 1.
     */
    public static TfxInputs CreateInputs(PackageCatalog catalog)
    {
        TfxParameterLookup? parameters = null;
        if (catalog.ContainsTag(NativeInspectionMaterialInputs.RenderGlobalsTag))
            parameters = new GameMaterialReader(catalog).ReadTfxParameterDefaults(
                NativeInspectionMaterialInputs.RenderGlobalsTag).CreateSnapshot();
        var inputs = NativeInspectionMaterialInputs.Create(0, parameters);
        NativeFrameInputs.ApplyTimeTerms(inputs);
        inputs.Vectors[GearDyeBit] = Vector4.Zero;
        // Extern 1 float 7 is 2^EV and float 8 is EV.
        inputs.Floats[new(1, 7)] = 1;
        inputs.Floats[new(1, 8)] = 0;
        return inputs;
    }

    // A decorator vertex shader reads its record bytes 12 to 15 as TEXCOORD7.
    private static readonly Regex DecoratorRecordInput = new(@"^// TEXCOORD\s+7\s", RegexOptions.Multiline);
    private static readonly Regex MissingExtern = new(@"extern (?<extern>\d+) (?<kind>float|vector|matrix|resource) \D*(?<element>\d+)");
    private static readonly Regex MissingChannel = new(@"object channel \d+");

    // Extern 67 is the water object block; a material whose TFX reads it is a water surface.
    private const byte WaterExtern = 67;
    // Policy: at most this many unknown inputs; each evaluation fills one.
    private const int UnknownInputLimit = 64;
    // Studio's viewport binds these for water: flat ripples, no shadow, black reflection and environment.
    private static readonly Dictionary<string, Vector4> RuntimeStandIns = new()
    {
        ["extern 67 resource 5"] = new(128 / 255f, 128 / 255f, 128 / 255f, 1),
        ["extern 67 resource 3"] = Vector4.One,
        ["extern 67 resource 6"] = Vector4.Zero,
        ["extern 3 resource 19"] = Vector4.Zero
    };
    // The transparent scope binds scene depth at pixel t10; Blender has none, so it reads as zero.
    private const uint SceneDepthSlot = 10;
    // A one-row CB2 beside scene depth linearizes it as 1 / (depth * y + x).
    private static readonly Regex SoftDepthBuffer = new(@"^dcl_constantbuffer CB2\[1\]", RegexOptions.Multiline);
    private static readonly Regex SceneDepthTexture = new(@"^dcl_resource_texture2d \S+ t10$", RegexOptions.Multiline);
    // This x puts zero depth far away.
    private static readonly Vector4[] FarSceneDepth = [new(1e-6f, 0, 0, 0)];

    /**
     * Runs TFX with each input the defaults lack as unknown: NaN numbers and named placeholder resources.
     * @param missing Receives the inputs that were unknown, in the order the program asked for them.
     * @return Null when the program still does not complete.
     */
    private static TfxEvaluation? EvaluateWithUnknowns(MaterialStageData stage, TfxInputs inputs, out IReadOnlyList<string> missing)
    {
        var result = TfxEvaluator.Evaluate(stage, inputs);
        missing = result.MissingRequirements;
        if (result.IsComplete) return result;
        var probe = new TfxInputs { Parameters = inputs.Parameters, ObjectChannels = inputs.ObjectChannels,
            ObjectChannelDefault = inputs.ObjectChannelDefault };
        foreach (var (key, value) in inputs.Floats) probe.Floats[key] = value;
        foreach (var (key, value) in inputs.FloatTerms) probe.FloatTerms[key] = value;
        foreach (var (key, value) in inputs.Vectors) probe.Vectors[key] = value;
        foreach (var (key, value) in inputs.Matrices) probe.Matrices[key] = value;
        foreach (var (key, value) in inputs.Resources) probe.Resources[key] = value;
        var unknown = new List<string>();
        for (int attempt = 0; attempt < UnknownInputLimit && !result.IsComplete; attempt++)
        {
            string need = result.MissingRequirements[0];
            unknown.Add(need);
            if (MissingChannel.IsMatch(need)) probe.ObjectChannelDefault = new Vector4(float.NaN);
            else if (MissingExtern.Match(need) is { Success: true } match)
            {
                var key = new TfxExternKey(byte.Parse(match.Groups["extern"].Value, CultureInfo.InvariantCulture),
                    byte.Parse(match.Groups["element"].Value, CultureInfo.InvariantCulture));
                switch (match.Groups["kind"].Value)
                {
                    case "float": probe.Floats[key] = float.NaN; break;
                    case "vector": probe.Vectors[key] = new Vector4(float.NaN); break;
                    case "matrix": const float n = float.NaN; probe.Matrices[key] = new(n, n, n, n, n, n, n, n, n, n, n, n, n, n, n, n); break;
                    default: probe.Resources[key] = new($"extern {key.Extern} resource {key.Element}",
                        Vortice.Direct3D.ShaderResourceViewDimension.Texture2D); break;
                }
            }
            else return null;
            result = TfxEvaluator.Evaluate(stage, probe);
        }
        missing = unknown;
        return result.IsComplete ? result : null;
    }

    /**
     * The vertex cb0 rows that copy extern 72 vectors 6 and 7, a decorator's TEXCOORD7 scale and offset.
     * Every input reads a distinct marker, so only an unchanged copy matches.
     * @return Null when either row is absent or the program does not run.
     */
    internal static (int Scale, int Offset)? DecoratorRows(MaterialStageData vertex, TfxParameterLookup? parameters)
    {
        // Markers 0 to 7 are extern 72; a float marker fills all four lanes, so it never equals a vector one.
        static Vector4 Marker(int index) => new Vector4(-1_000_000 - 4 * index) - new Vector4(0, 1, 2, 3);
        var probe = new TfxInputs { Parameters = parameters };
        int next = 8;
        try
        {
            for (int attempt = 0; attempt < 64; attempt++)
            {
                var result = TfxEvaluator.Evaluate(vertex, probe);
                if (result.IsComplete)
                {
                    int Row(int element) => Array.IndexOf(result.Constants, Marker(element)) is var row and >= 0
                        && Array.LastIndexOf(result.Constants, Marker(element)) == row ? row : -1;
                    return (Row(6), Row(7)) is ( >= 0, >= 0) and var rows ? rows : null;
                }
                string need = result.MissingRequirements[0];
                if (MissingChannel.IsMatch(need)) { probe.ObjectChannelDefault = Marker(next++); continue; }
                if (MissingExtern.Match(need) is not { Success: true } match) return null;
                var key = new TfxExternKey(byte.Parse(match.Groups["extern"].Value, CultureInfo.InvariantCulture),
                    byte.Parse(match.Groups["element"].Value, CultureInfo.InvariantCulture));
                var marker = Marker(key.Extern == 72 && key.Element < 8 ? key.Element : next++);
                switch (match.Groups["kind"].Value)
                {
                    case "float": probe.Floats[key] = marker.X; break;
                    case "vector": probe.Vectors[key] = marker; break;
                    case "matrix": probe.Matrices[key] = new(marker.X, marker.Y, marker.Z, marker.W, marker.X - 4, marker.Y - 4,
                        marker.Z - 4, marker.W - 4, marker.X - 8, marker.Y - 8, marker.Z - 8, marker.W - 8, 0, 0, 0, 1); next += 2; break;
                    default: probe.Resources[key] = new("decorator row probe", Vortice.Direct3D.ShaderResourceViewDimension.Texture2D); break;
                }
            }
        }
        catch (Exception error) when (error is ArgumentException or IndexOutOfRangeException or OverflowException or FormatException) { }
        return null;
    }

    /**
     * Three-target material outputs use colour, packed normal/smoothness, and metal channels.
     * @param timeTerms Constant lanes that change with time; the program reads them as time expressions.
     * @param worldVaryings The vertex shader writes world N, T, B to v0..v2 and world position to v4.
     * @param uvPairs The vertex shader writes UV map 0 to v3.xy and a second UV pair to v3.zw.
     * @param constantBuffers Pixel constant buffers besides cb0, by slot.
     * @param detailUv The second UV pair is UV map 1, the detail UVs; otherwise it is UV map 0 again.
     * @param varyings Pixel inputs the vertex shader copies from a vertex stream; null when unread.
     * @param standIns Fixed sample values for runtime-bound texture slots.
     * @param water A water surface: the compiler keeps its reflect() normal.
     * @param volumes 3D textures by slot; the program samples them as "volume" nodes.
     * @param writtenRows cb0 rows TFX writes; null when no cb0 lane is a constant template copy.
     */
    public static BundleMaterialPreview Analyze(string text, uint shaderTag, string shaderHash,
        IReadOnlyList<TextureInput> textures, IReadOnlyList<Vector4>? constants = null, string blendMode = "Opaque",
        bool nativeTangentInputs = false, IReadOnlyDictionary<int, TfxTerm>? timeTerms = null, bool worldVaryings = false,
        bool uvPairs = false, IReadOnlyDictionary<uint, Vector4[]>? constantBuffers = null, bool detailUv = false,
        ShaderVaryings? varyings = null, IReadOnlyDictionary<uint, Vector4>? standIns = null, bool water = false,
        IReadOnlyDictionary<uint, BundleVolumeTexture>? volumes = null, IReadOnlySet<int>? writtenRows = null)
    {
        var registers = new Dictionary<string, Value[]>(StringComparer.Ordinal);
        var branchStarts = new Stack<Dictionary<string, Value[]>>();
        var branchFirst = new Stack<Dictionary<string, Value[]>?>();
        var branchConditions = new Stack<bool?>();
        var normals = new List<(Value X, Value Y)>(); var smoothness = new List<Value>();
        bool deferred = Regex.IsMatch(text, @"\bdcl_output o2\.");
        bool branched = false, discarded = false;
        // Textures a branch tests are layer weights, such as terrain dye maps, not surface values.
        var weights = new HashSet<uint>();
        Value Read(string operand, int lane)
        {
            operand = operand.Trim();
            bool negate = operand.StartsWith('-'); if (negate) operand = operand[1..];
            bool absolute = operand.StartsWith('|') && operand.EndsWith('|'); if (absolute) operand = operand[1..^1];
            Value value;
            if (operand.StartsWith("l(", StringComparison.Ordinal))
            {
                string[] values = operand[2..^1].Split(',');
                value = Value.Number(ParseLiteral(values[Math.Min(lane, values.Length - 1)].Trim()));
            }
            else
            {
                var match = Regex.Match(operand, @"^(?<reg>[rvo]\d+|cb\d+\[\d+\])(?:\.(?<swizzle>[xyzw]+))?$");
                if (!match.Success) return Value.Unknown;
                string register = match.Groups["reg"].Value, swizzle = match.Groups["swizzle"].Value;
                int component = swizzle.Length == 0 ? lane : "xyzw".IndexOf(swizzle[Math.Min(lane, swizzle.Length - 1)]);
                if (register.StartsWith('v')) value = new([], $"{register}.{"xyzw"[component]}");
                else if (register.StartsWith("cb0[", StringComparison.Ordinal) && constants is not null
                    && int.TryParse(register.AsSpan(4, register.Length - 5), out int row) && row < constants.Count)
                    value = Value.Number(constants[row][component]);
                else if (constantBuffers is not null && Regex.Match(register, @"^cb(?<slot>\d+)\[(?<row>\d+)\]$") is { Success: true } buffer
                    && constantBuffers.TryGetValue(uint.Parse(buffer.Groups["slot"].Value, CultureInfo.InvariantCulture), out var rows)
                    && int.Parse(buffer.Groups["row"].Value, CultureInfo.InvariantCulture) is var bufferRow && bufferRow < rows.Length)
                    value = Value.Number(rows[bufferRow][component]);
                else value = registers.TryGetValue(register, out var stored) ? stored[component] : Value.Unknown;
            }
            return absolute || negate ? value with { Input = null, Constant = value.Constant is float evaluatedConstant
                ? (negate ? -1 : 1) * (absolute ? Math.Abs(evaluatedConstant) : evaluatedConstant) : null } : value;
        }
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("dcl_", StringComparison.Ordinal)) continue;
            int space = line.IndexOf(' '); string opcode = space < 0 ? line : line[..space];
            string[] operands = space < 0 ? [] : DxbcText.SplitOperands(line[(space + 1)..]);
            if (opcode.StartsWith("if", StringComparison.Ordinal))
            {
                if (operands.Length != 0) weights.UnionWith(Read(operands[0], 0).Sources.Select(source => source.Slot));
                branched = true; branchStarts.Push(Clone(registers)); branchFirst.Push(null);
                branchConditions.Push(operands.Length != 0 && Read(operands[0], 0).Constant is float condition ? condition != 0 : null);
                continue;
            }
            if (opcode == "else" && branchStarts.Count != 0)
            { branchFirst.Pop(); branchFirst.Push(Clone(registers)); registers = Clone(branchStarts.Peek()); continue; }
            if (opcode == "endif" && branchStarts.Count != 0)
            {
                var first = branchFirst.Pop(); var start = branchStarts.Pop(); bool? condition = branchConditions.Pop();
                registers = condition.HasValue ? condition.Value ? first ?? registers : first is null ? start : registers
                    : Merge(registers, first ?? start); continue;
            }
            if (opcode.StartsWith("discard", StringComparison.Ordinal)) { discarded = true; continue; }
            if (operands.Length < 2) continue;
            var destination = Regex.Match(operands[0], @"^(?<reg>[ro]\d+)(?:\.(?<mask>[xyzw]+))?$");
            if (!destination.Success) continue;
            string name = destination.Groups["reg"].Value, mask = destination.Groups["mask"].Value;
            if (mask.Length == 0) mask = "xyzw";
            Value[] output = registers.TryGetValue(name, out var prior) ? prior.ToArray() : Enumerable.Range(0, 4).Select(_ => Value.Unknown).ToArray();
            if (opcode.StartsWith("sample", StringComparison.Ordinal) || opcode.StartsWith("ld_", StringComparison.Ordinal))
            {
                string? texture = operands.Skip(2).FirstOrDefault(value => Regex.IsMatch(value, @"^t\d+(?:\.[xyzw]+)?$"));
                if (texture is null) continue;
                var match = Regex.Match(texture, @"^t(?<slot>\d+)(?:\.(?<swizzle>[xyzw]+))?$");
                uint slot = uint.Parse(match.Groups["slot"].Value, CultureInfo.InvariantCulture);
                string swizzle = match.Groups["swizzle"].Value; if (swizzle.Length == 0) swizzle = "xyzw";
                var u = Read(operands[1], 0); var v = Read(operands[1], 1);
                bool direct = u.Input is { } input && input.EndsWith(".x", StringComparison.Ordinal) && v.Input == input[..^1] + "y";
                foreach (char channel in mask)
                { int lane = "xyzw".IndexOf(channel); output[lane] = new([new(slot, swizzle[swizzle.Length == 1 ? 0 : lane], direct)]); }
            }
            else
            {
                string operation = opcode.Split('_')[0]; bool saturate = opcode.EndsWith("_sat", StringComparison.Ordinal);
                Value? reduced = null;
                if (operation is "dp2" or "dp3" or "dp4")
                {
                    int count = operation[^1] - '0';
                    var terms = Enumerable.Range(0, count).SelectMany(lane => operands.Skip(1).Select(value => Read(value, lane))).ToArray();
                    reduced = Combine(terms);
                    if (operation == "dp2") normals.Add((Read(operands[1], 0), Read(operands[1], 1)));
                }
                if (operation == "mad" && operands.Length == 4 && Literal(operands[2], .125f) && Literal(operands[3], .375f))
                    smoothness.Add(Read(operands[1], "xyzw".IndexOf(mask[0])));
                foreach (char channel in mask)
                {
                    int lane = "xyzw".IndexOf(channel);
                    var values = operands.Skip(1).Select(value => Read(value, lane)).ToArray();
                    Value value = reduced ?? (operation == "mov" ? values[0] : Combine(values));
                    float? computed = reduced is null ? Evaluate(operation, values) : null;
                    if (computed is float constant) value = value with { Constant = saturate ? Math.Clamp(constant, 0, 1) : constant };
                    output[lane] = value;
                }
            }
            registers[name] = output;
        }
        Value[] Result(string name) => registers.GetValueOrDefault(name) ?? Enumerable.Range(0, 4).Select(_ => Value.Unknown).ToArray();
        var color = Result("o0"); var third = Result("o2");
        var inputs = textures.DistinctBy(value => value.Slot).ToDictionary(value => value.Slot);
        var bindings = new List<BundleMaterialBinding>(); var notes = new List<string>();
        bool Srgb(TextureInput value) => value.Format is 29 or 72 or 75 or 78 or 91 or 93 or 99;
        int ColorScore(TextureInput texture)
        {
            bool diagonal = Enumerable.Range(0, 3).All(lane => color[lane].Sources.Any(source => source.Slot == texture.Slot && source.Channel == "xyz"[lane]));
            if (!diagonal) return -1;
            int cross = Enumerable.Range(0, 3).Sum(lane => color[lane].Sources.Count(source => source.Slot == texture.Slot && source.Channel != "xyz"[lane]));
            if (cross >= 6 && !Srgb(texture)) return -1;
            bool direct = color.Take(3).SelectMany(value => value.Sources).Any(source => source.Slot == texture.Slot && source.DirectUv);
            return 30 - cross + (Srgb(texture) ? 30 : 0) + (direct ? 10 : 0);
        }
        var baseTexture = inputs.Values.Where(value => ColorScore(value) >= 0).OrderByDescending(ColorScore).ThenBy(value => value.Slot).FirstOrDefault();
        if (baseTexture is not null) bindings.Add(new("BaseColor", baseTexture.Slot, baseTexture.Tag, "rgb", Srgb: Srgb(baseTexture)));
        var normalSources = Combine(normals.SelectMany(value => new[] { value.X, value.Y }).ToArray()).Sources;
        bool Has(Value value, uint slot, char channel) => value.Sources.Any(source => source.Slot == slot && source.Channel == channel);
        var normalTexture = inputs.Values.Where(texture => normals.Any(value => Has(value.X, texture.Slot, 'x')
            && Has(value.Y, texture.Slot, 'y') && !Has(value.X, texture.Slot, 'y') && !Has(value.Y, texture.Slot, 'x')))
            .OrderByDescending(texture => normalSources.Any(source => source.Slot == texture.Slot && source.DirectUv))
            .ThenBy(texture => Srgb(texture)).ThenBy(texture => texture.Slot).FirstOrDefault();
        if (deferred && normalTexture is not null)
            bindings.Add(new("Normal", normalTexture.Slot, normalTexture.Tag, "rg", ReconstructNormalZ: true));
        if (shaderHash == "9E97A5ACE9FCD8D9C743E287D1C0682C94F0B9E0EB037EA32593DDF53DCE5E13"
            && constants?.Count >= 3 && bindings.FindIndex(value => value.Role == "Normal" && value.Slot == 2) is >= 0 and var normalIndex
            && Components(constants[2]).All(float.IsFinite))
        {
            bindings[normalIndex] = bindings[normalIndex] with
            { UnpackScale = constants[2].X, UnpackBias = constants[2].Y };
            bindings.Add(new("RoughnessFloor", 2, bindings[normalIndex].TextureTag, "b", -1, 1 - constants[2].Z));
        }
        if (deferred)
        {
            var scalars = inputs.Where(pair => !weights.Contains(pair.Key)).ToDictionary();
            var metal = SelectScalar(third[0], scalars, normalTexture?.Slot, baseTexture?.Slot);
            if (metal is not null) bindings.Add(new("Metallic", metal.Slot, inputs[metal.Slot].Tag, Channel(metal.Channel)));
            var smooth = SelectScalar(Combine(smoothness.ToArray()), scalars, normalTexture?.Slot, baseTexture?.Slot,
                metal?.Slot, preferredChannel: 'y');
            if (smooth is not null) bindings.Add(new("Roughness", smooth.Slot, inputs[smooth.Slot].Tag, Channel(smooth.Channel), -1, 1));
        }
        if (!deferred || blendMode is "InverseAlpha" or "PremultipliedAlpha" or "Alpha")
        {
            var opacity = SelectScalar(color[3], inputs, null, null, baseTexture?.Slot, 'w');
            if (opacity is not null && opacity.Channel == 'w') bindings.Add(new("Alpha", opacity.Slot, inputs[opacity.Slot].Tag, "a"));
        }
        float[] baseColor = baseTexture is null && color.Take(3).All(value => value.Constant is >= 0 and <= 1)
            ? [color[0].Constant!.Value, color[1].Constant!.Value, color[2].Constant!.Value, 1] : [1, 1, 1, 1];
        if (baseTexture is null && blendMode is "InverseAlpha" or "PremultipliedAlpha" or "Additive" && color.Take(3).Any(value => value.Constant is null))
            baseColor = [0, 0, 0, 1];
        float metallic = bindings.Any(value => value.Role == "Metallic") ? 1 : Math.Clamp(third[0].Constant ?? 0, 0, 1);
        float roughness = bindings.Any(value => value.Role == "Roughness") ? 1 : .5f;
        notes.Add("Base texture roles follow native shader dataflow. Dye, detail, runtime lighting and view terms are approximated.");
        if (baseTexture is null && color.Take(3).Any(value => value.Constant is null)) notes.Add("Base colour uses unresolved shader palette or runtime values.");
        if (branched) notes.Add("Shader branches contribute possible texture sources.");
        if (discarded) notes.Add("Native alpha testing is not reproduced by this preview profile.");
        // An unresolved premultiplied alpha adds its colour; it must not cover what is behind with black.
        float alpha = blendMode == "InverseAlpha" ? Math.Clamp(1 - (color[3].Constant ?? 1), 0, 1)
            : blendMode == "Additive" ? 0 : blendMode == "PremultipliedAlpha" ? Math.Clamp(color[3].Constant ?? 0, 0, 1)
            : !deferred && color[3].Constant is float alphaValue ? Math.Clamp(alphaValue, 0, 1) : 1;
        if (bindings.Any(binding => binding.Role == "Alpha")) alpha = 1;
        if (blendMode is "InverseAlpha" or "PremultipliedAlpha" or "Additive")
        {
            bindings.RemoveAll(binding => binding.Role is "Normal" or "Metallic" or "Roughness" or "RoughnessFloor");
            metallic = 0; roughness = .5f;
        }
        BundleMaterialPalette? palette = null;
        if (shaderHash == "337BF122BD8891F0D800B81C099468B6470F4DABA5924D832041C33CC87F4E44" && constants?.Count >= 12
            && constants.Take(9).SelectMany(Components).All(float.IsFinite))
        {
            palette = new("StaticPalette815B8187", 0, 1, Components(constants[0]),
                constants.Skip(1).Take(7).Select(Components).ToArray(), [constants[8].X, constants[8].Y]);
            bindings.RemoveAll(value => value.Role == "BaseColor");
            baseColor = [1, 1, 1, 1];
            notes.RemoveAll(value => value.StartsWith("Base colour uses", StringComparison.Ordinal));
        }
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        var program = MaterialExpressionCompiler.Compile(text, constants, blendMode,
            textures.Select(texture => texture.Slot).ToHashSet(), out bool approximateLoop, nativeTangentInputs, timeTerms, worldVaryings,
            uvPairs, detailUv, constantBuffers, varyings, standIns, water, unresolved, volumes?.Keys.ToHashSet(), writtenRows);
        if (program?.Outputs.ContainsKey("BaseColor") != true && unresolved.Count != 0)
            notes.Add("Shader expressions stop at unresolved inputs or operations: " + string.Join(", ", unresolved) + ".");
        if (program is not null && approximateLoop) notes.Add(ApproximateLoopNote);
        if (program?.Nodes.Any(node => node.Operation == "screen") == true)
            notes.Add("Screen-space terms use Blender's window coordinates.");
        if (program?.Nodes.Any(node => node.Operation == "time") == true)
            notes.Add("Animated constants follow the Blender frame at the scene frame rate.");
        if (program?.Nodes.Any(node => node.Operation == ShaderVaryings.Instance) == true)
            notes.Add("Decorator terms read each placement's record and AO inputs.");
        if (program?.Nodes.Any(node => node.Operation == "surface") == true)
            notes.Add("Shader tangent and bitangent use Blender's UV tangent; the authored bitangent sign is not exported.");
        if (program?.Outputs.ContainsKey("BaseColor") == true)
            notes.RemoveAll(value => value.StartsWith("Base colour uses", StringComparison.Ordinal));
        if (program?.Outputs.ContainsKey("Visibility") == true)
            notes.RemoveAll(value => value.StartsWith("Native alpha testing", StringComparison.Ordinal));
        if (program?.ApproximateVisibility == true)
            notes.Add("Alpha test skips a shader branch it cannot resolve, such as a hashed dither threshold.");
        return new(program is null ? "Shader dataflow approximation" : "Shader expressions", shaderTag, shaderHash, bindings, baseColor,
            metallic, roughness, alpha, [0, 0, 0], notes)
        { BlendMode = blendMode, Palette = palette, Program = program, Deferred = deferred,
            VolumeTextures = program?.Nodes.Where(node => node.Operation == "volume").Select(node => node.Slot).Distinct()
                .ToDictionary(slot => slot, slot => volumes![slot]) is { Count: > 0 } sampled ? sampled : null,
            PixelConstants = constants?.Select(value => Components(value).Select(component => float.IsFinite(component) ? component : 0).ToArray()).ToArray(),
            PixelConstantMasks = constants?.Select(value => Components(value).Select((component, index) => float.IsFinite(component) ? 1u << index : 0u).Aggregate(0u, (left, right) => left | right)).ToArray() };
    }

    public const string ApproximateLoopNote = "Approximate: a shader loop stops at its first exit test that depends on the pixel, "
        + "so parallax and ray-march offsets are left out.";

    public const string UnresolvedBlendNote = "Blended colour needs runtime inputs, such as scene depth; the preview leaves it out.";

    /** Unresolved forward premultiplied or additive colour draws nothing, not a black or untinted cover. */
    private static BundleMaterialPreview HideUnresolvedBlend(BundleMaterialPreview preview) =>
        preview.Deferred || preview.Recipe is not null || preview.Palette is not null
            || preview.BlendMode is not ("PremultipliedAlpha" or "Additive" or "InverseAlpha")
            || preview.Program?.Outputs.ContainsKey("BaseColor") == true ? preview
            : preview with { Bindings = [], BaseColor = [0, 0, 0, 1], Alpha = 0, Program = null,
                Notes = preview.Notes.Append(UnresolvedBlendNote).ToArray() };

    private static Source? SelectScalar(Value value, IReadOnlyDictionary<uint, TextureInput> inputs,
        uint? normal, uint? color, uint? preferred = null, char preferredChannel = 'x') => value.Sources
        .Where(source => inputs.ContainsKey(source.Slot) && source.Slot != normal && source.Slot != color)
        .OrderByDescending(source => source.Slot == preferred).ThenByDescending(source => source.DirectUv)
        .ThenByDescending(source => source.Channel == preferredChannel).ThenBy(source => source.Slot).FirstOrDefault();
    private static string Channel(char value) => "rgba"["xyzw".IndexOf(value)].ToString();
    private static Value Combine(params Value[] values) => new(values.SelectMany(value => value.Sources).ToHashSet());
    private static Dictionary<string, Value[]> Clone(Dictionary<string, Value[]> input) => input.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    private static Dictionary<string, Value[]> Merge(Dictionary<string, Value[]> a, Dictionary<string, Value[]> b) =>
        a.Keys.Union(b.Keys).ToDictionary(key => key, key => Enumerable.Range(0, 4).Select(index =>
            Combine(a.GetValueOrDefault(key)?[index] ?? Value.Unknown, b.GetValueOrDefault(key)?[index] ?? Value.Unknown)).ToArray());
    private static bool Literal(string operand, float expected) => operand.StartsWith("l(", StringComparison.Ordinal)
        && operand[2..^1].Split(',').All(value => float.TryParse(value.Trim(), CultureInfo.InvariantCulture, out float result) && Math.Abs(result - expected) < .00001f);
    private static float? Evaluate(string operation, Value[] values)
    {
        if (operation == "movc" && values.Length == 3 && values[0].Constant is float condition)
            return values[condition != 0 ? 1 : 2].Constant;
        if (values.Any(value => value.Constant is null)) return null;
        float a = values[0].Constant!.Value, b = values.Length > 1 ? values[1].Constant!.Value : 0;
        float result = operation switch
        {
            "mov" => a, "add" => a + b, "mul" => a * b, "mad" => a * b + values[2].Constant!.Value,
            "div" => a / b, "min" => Math.Min(a, b), "max" => Math.Max(a, b), "sqrt" => MathF.Sqrt(a),
            "rsq" => 1 / MathF.Sqrt(a), "exp" => MathF.Pow(2, a), "log" => MathF.Log2(a),
            "lt" => a < b ? 1 : 0, "ge" => a >= b ? 1 : 0, "eq" => a == b ? 1 : 0, "ne" => a != b ? 1 : 0,
            _ => float.NaN
        };
        return float.IsFinite(result) ? result : null;
    }
    private static float[] Components(Vector4 value) => [value.X, value.Y, value.Z, value.W];
    /** The DXBC disassembler spells nonfinite literals with the legacy Microsoft float syntax. */
    internal static float ParseLiteral(string value)
    {
        if (value.Contains("#INF", StringComparison.OrdinalIgnoreCase))
            return value.StartsWith('-') ? float.NegativeInfinity : float.PositiveInfinity;
        if (value.Contains("#", StringComparison.Ordinal)) return float.NaN;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bits))
            return BitConverter.UInt32BitsToSingle(bits);
        if (float.TryParse(value, CultureInfo.InvariantCulture, out float number)) return number;
        throw new InvalidDataException("Shader contains an invalid numeric literal.");
    }
}
