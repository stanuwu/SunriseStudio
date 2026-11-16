using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

/** One model of a batch: a chunk of one static, with its tag and the largest position error its quantization gives. */
public sealed record BatchModel(string Name, uint ModelTag, int Vertices, int Triangles, float PositionError);

/** A batch's models and every entry they and their materials use; NextEntry is the first entry left free. */
public sealed record PreparedBatch(IReadOnlyList<BatchModel> Models, IReadOnlyList<ProjectAssetAddition> Assets, int NextEntry);

/**
 * The LOD files and switch distances of one static.
 * @param Levels glTF files, nearest first; one to three.
 * @param SwitchDistances Metres where each further level takes over; empty keeps the sphere radius rule.
 * @param DrawDistance Metres past which nothing draws; null keeps the radius rule.
 */
public sealed record StaticLodInput(IReadOnlyList<string> Levels, IReadOnlyList<float> SwitchDistances, float? DrawDistance = null);

/** Imports one static at map scale: its materials once, then its parts in chunks that keep snorm16 positions exact. */
public static class StaticBatchImporter
{
    /** One millimetre: the position error the batch keeps each model inside. */
    public const float DefaultMaxError = 0.001f;

    /**
     * Materials of every LOD file, then one model per chunk. Every chunk is in the static's own space,
     * so all its models take the static's placement.
     * @param geometry Changes each mesh before it splits; a null result drops the mesh.
     * @param share Materials shared with other statics of the same import.
     */
    public static PreparedBatch Prepare(PackageCatalog catalog, StaticLodInput input, string name, ushort packageId, int first,
        float maxError = DefaultMaxError, bool compress = true, bool skipUnsupported = false,
        Func<Formats.InterchangeMesh, Formats.InterchangeMesh?>? geometry = null, CancellationToken token = default,
        MaterialShare? share = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var categories = StaticLodCategory.ForLevels(input.Levels.Count);
        if (input.SwitchDistances.Count > input.Levels.Count - 1 || input.SwitchDistances.Any(d => !(d > 0) || !float.IsFinite(d))
            || input.DrawDistance is float draw && (!(draw > 0) || !float.IsFinite(draw)))
            throw new InvalidDataException("LOD distances must be positive, one fewer than the levels at most.");
        var assets = new List<ProjectAssetAddition>();
        var parts = new List<StaticPart>();
        int cursor = first;
        for (int level = 0; level < input.Levels.Count; level++)
        {
            string levelName = level == 0 ? name : $"{name}.lod{level}";
            var options = new OriginalAssetImportOptions(input.Levels[level], packageId, levelName, null, skipUnsupported) { CompressTextures = compress, Share = share };
            using var scene = OriginalAssetImporter.OriginalScene.Read(options, token);
            var materials = scene.PrepareMaterials(catalog, options, cursor, token);
            assets.AddRange(materials.Assets);
            cursor = materials.NextEntry;
            foreach (var part in scene.Parts(materials, categories[level]))
                if (geometry is null) parts.Add(part);
                else if (geometry(part.Mesh) is { } mesh) parts.Add(part with { Mesh = mesh });
        }
        // With two levels the one switch is level 2's; with three the first is level 1's.
        float? level1 = input.Levels.Count == 3 && input.SwitchDistances.Count > 0 ? input.SwitchDistances[0] : null;
        float? level2 = input.Levels.Count switch
        {
            2 when input.SwitchDistances.Count > 0 => input.SwitchDistances[0],
            3 when input.SwitchDistances.Count > 1 => input.SwitchDistances[1],
            _ => null
        };
        if (parts.Count == 0) throw new InvalidDataException($"{name} has no geometry left to import.");
        var models = new List<BatchModel>();
        var chunks = StaticChunks.Split(parts, maxError, token);
        for (int c = 0; c < chunks.Count; c++)
        {
            string chunkName = chunks.Count == 1 ? name : $"{name}.chunk{c}";
            var model = MeshAssetImporter.PrepareStatic(catalog, chunks[c].Parts, chunkName, packageId, cursor,
                radius => StaticLodCategory.Sizes(radius, level1, level2, input.DrawDistance), null, token);
            assets.AddRange(model.Assets);
            cursor = checked(cursor + model.Assets.Count);
            models.Add(new(chunkName, model.ModelTag, model.VertexCount, model.TriangleCount, chunks[c].PositionError));
        }
        return new(models, assets, cursor);
    }
}
