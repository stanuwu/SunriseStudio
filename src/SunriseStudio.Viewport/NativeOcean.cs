using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using Vortice.Direct3D;

namespace SunriseStudio.Viewport;

/**
 * One water settings tag, class 0x80806D49: the wave block the ocean programs read as extern 91.
 * @param WaveNoise 3D noise at +0x08, extern 91 resource 0.
 * @param DetailNoise 3D noise at +0x0C, extern 91 resource 1.
 * @param Floats Extern 91 float elements 4 and 6 to 12, from +0x14 and +0x18 to +0x30.
 */
public sealed record NativeOceanSettings(uint Tag, uint WaveNoise, uint DetailNoise, IReadOnlyDictionary<byte, float> Floats)
{
    // fnv1 name the map's name table files the settings under; 0x7FF742E1BE10.
    public const uint NameHash = 0x1C03307A;
    private const uint MapRootClass = SMapRoot.Class, NameMapClass = SRuntimeNameMap.Class, NamePairClass = SRuntimeNameRow.Class;
    // Extern 91 float elements 6 to 12 are the settings' seven listed floats, in order.
    private const int FirstListedElement = 6;

    /** Reads a settings tag; null when either noise texture is unset, as 0x7FF742E1BE10 rejects it. */
    public static NativeOceanSettings? Read(PackageCatalog catalog, uint tag)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != UnknownWaterSettings.Class) return null;
        var bytes = catalog.Read(tag);
        if (!TagView.FitsThrough<UnknownWaterSettings>(bytes, 0, nameof(UnknownWaterSettings.Extern91Floats612)))
            throw new InvalidDataException("Water settings are shorter than 0x34 bytes.");
        uint wave = TagView.Member<UnknownWaterSettings, uint>(bytes, 0, nameof(UnknownWaterSettings.WaveNoise3d));
        uint detail = TagView.Member<UnknownWaterSettings, uint>(bytes, 0, nameof(UnknownWaterSettings.DetailNoise3d));
        if (wave == uint.MaxValue || detail == uint.MaxValue) return null;
        var floats = new Dictionary<byte, float> { [4] = TagView.Member<UnknownWaterSettings, float>(bytes, 0, nameof(UnknownWaterSettings.Extern91Float4)) };
        ref readonly var listed = ref TagView.Member<UnknownWaterSettings, Inline7<float>>(bytes, 0, nameof(UnknownWaterSettings.Extern91Floats612));
        // Element 5 is a frame value no ocean program reads; it is left unset.
        for (byte element = FirstListedElement; element <= 12; element++) floats[element] = listed[element - FirstListedElement];
        return new(tag, wave, detail, floats);
    }

    /**
     * The settings the game draws a map with: the map's name table entry, else the render globals default.
     * @param mapTag A map root, class 0x808091DE; any other tag takes the default.
     * @return Null when neither names usable settings, as in an installation without render globals.
     */
    public static NativeOceanSettings? Resolve(PackageCatalog catalog, uint mapTag, uint renderGlobalsTag)
    {
        if (catalog.TryGetEntry(mapTag, out var map) && map.Reference == MapRootClass
            && catalog.Read(mapTag) is { } root && TagView.FitsThrough<SMapRoot>(root, 0, nameof(SMapRoot.RootNameTable))
            && NamedSettings(catalog, TagView.Member<SMapRoot, uint>(root, 0, nameof(SMapRoot.RootNameTable))) is { } named
            && Read(catalog, named) is { } settings)
            return settings;
        if (!catalog.TryGetEntry(renderGlobalsTag, out _)) return null;
        var globals = catalog.Read(renderGlobalsTag);
        return TagView.FitsThrough<SRendererRoot>(globals, 0, nameof(SRendererRoot.DefaultWaterSettings))
            ? Read(catalog, TagView.Member<SRendererRoot, uint>(globals, 0, nameof(SRendererRoot.DefaultWaterSettings))) : null;
    }

    /** The tag filed under NameHash in a name table, class 0x80809780; pairs are {fnv1 name, tag}. */
    private static uint? NamedSettings(PackageCatalog catalog, uint table)
    {
        if (!catalog.TryGetEntry(table, out var entry) || entry.Reference != NameMapClass) return null;
        var bytes = catalog.Read(table);
        ref readonly var entries = ref TagView.At<TagArray<SRuntimeNameRow>>(bytes,
            TagView.FieldOffset<SRuntimeNameMap>(nameof(SRuntimeNameMap.TagEntries)));
        ReadOnlySpan<SRuntimeNameRow> rows;
        try { rows = TagView.CheckedRows(bytes, in entries, NamePairClass); }
        catch (Exception error) when (error is InvalidDataException or OverflowException) { return null; }
        foreach (ref readonly var pair in rows)
            if (pair.NameHash == NameHash) return pair.ProgramTag;
        return null;
    }

}

/** Extern 91, the ocean wave block that 0x7FF742E1F940 pushes before each water draw. */
public static class NativeOcean
{
    public const byte Extern = 91;
    public static readonly TfxResource WaveNoise = new("ocean wave noise", ShaderResourceViewDimension.Texture3D);
    public static readonly TfxResource DetailNoise = new("ocean detail noise", ShaderResourceViewDimension.Texture3D);

    // The renderer builds its frame inputs from a plan's parameter snapshot, so the settings are keyed by it.
    private static readonly ConditionalWeakTable<TfxParameterLookup, NativeOceanSettings> Settings = new();

    public static void Apply(TfxInputs inputs, NativeOceanSettings settings)
    {
        inputs.Resources[new(Extern, 0)] = WaveNoise;
        inputs.Resources[new(Extern, 1)] = DetailNoise;
        foreach (var (element, value) in settings.Floats) inputs.Floats[new(Extern, element)] = value;
    }

    /** Adds extern 91 when the plan that owns these parameters resolved ocean settings. */
    public static void ApplyRegistered(TfxInputs inputs, TfxParameterLookup parameters)
    {
        if (Settings.TryGetValue(parameters, out var settings)) Apply(inputs, settings);
    }

    /**
     * Resolves the map's ocean settings and keys them to the plan's parameter snapshot.
     * @return Null when the settings are unset; extern 91 then stays missing and ocean parts are skipped.
     */
    public static NativeOceanSettings? Register(PackageCatalog catalog, uint mapTag, TfxParameterLookup parameters)
    {
        var settings = NativeOceanSettings.Resolve(catalog, mapTag, NativeScenePlan.RenderGlobalsTag);
        if (settings is not null) Settings.AddOrUpdate(parameters, settings);
        return settings;
    }

    /** True when a vertex or pixel program reads extern 91. */
    public static bool Reads(MaterialExecutionData execution) =>
        new[] { execution.Vertex, execution.Pixel }.Any(stage =>
            TfxProgramInspection.Read(stage.TfxBytecode).Dependencies.Any(d => d.Extern == Extern));

    /** Decodes both noise textures; each must be a 3D texture, as the programs sample them. */
    public static IEnumerable<(uint Tag, TextureData Data)> ReadTextures(TextureDataReader reader, NativeOceanSettings settings)
    {
        foreach (uint tag in new[] { settings.WaveNoise, settings.DetailNoise }.Distinct())
        {
            var data = reader.Read(tag);
            if (!data.IsVolume) throw new InvalidDataException($"Ocean noise 0x{tag:X8} is not a 3D texture.");
            yield return (tag, data);
        }
    }

    /** The views extern 91 binds; empty until both noise textures are on the GPU. */
    public static IEnumerable<(TfxResource Resource, T View)> Views<T>(NativeOceanSettings? settings, Func<uint, T?> view)
        where T : class
    {
        if (settings is null || view(settings.WaveNoise) is not { } wave || view(settings.DetailNoise) is not { } detail) yield break;
        yield return (WaveNoise, wave);
        yield return (DetailNoise, detail);
    }
}
