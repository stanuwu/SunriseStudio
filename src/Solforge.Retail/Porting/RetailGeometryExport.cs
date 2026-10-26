/**
 * File: exports a retail bubble's static meshes and terrain as Wavefront OBJ plus instance placements.
 *
 * Why this matters: MeshAssetImporter reads .obj and writes a real 86657 static mesh
 * (EncodeGeometry + PackageWriter). So an OBJ export is the practical writer path for retail
 * geometry: decode here, then import into the project as a static prop / original asset.
 * Vertices stay in mesh-local space; placements.json carries the world transforms per mesh tag.
 */
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Solforge.Retail.Decoding;

namespace Solforge.Retail.Porting;

/** What one export produced. */
public sealed record RetailGeometryExportResult(uint Bubble, string Folder, IReadOnlyList<string> Files,
    int Meshes, int TerrainMeshes, int Triangles, int Instances, int Skipped, IReadOnlyList<string> Notes)
{
    /** The report.json body. */
    public JsonObject ToJson() => new()
    {
        ["bubble"] = RetailTag.Hex(Bubble), ["folder"] = Folder,
        ["files"] = new JsonArray(Files.Select(file => (JsonNode)file).ToArray()),
        ["meshes"] = Meshes, ["terrain_meshes"] = TerrainMeshes, ["triangles"] = Triangles,
        ["instances"] = Instances, ["skipped"] = Skipped,
        ["notes"] = new JsonArray(Notes.Select(note => (JsonNode)note).ToArray()),
        ["next"] = "Import each .obj with import_asset or start_creation (static prop / original asset): "
            + "MeshAssetImporter reads .obj and writes an 86657 static mesh. placements.json gives the world transforms, "
            + "so the map is authored from real retail geometry instead of guessed bounds."
    };
}

/** Decodes retail map geometry to OBJ. Read-only on retail data; writes only under the given root. */
public static class RetailGeometryExport
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /**
     * Exports the statics and/or terrain a bubble places.
     * @param root Project root; files go to retail_ports/geometry/&lt;bubble&gt;/.
     * @param maxMeshes Cap on unique meshes written (skipped meshes are reported, not dropped quietly).
     */
    public static RetailGeometryExportResult Export(RetailSource source, uint bubbleTag, string root,
        bool statics, bool terrain, int maxMeshes, CancellationToken cancel)
    {
        if (RetailMap.ReadBubble(source, bubbleTag, out string? problem) is not { } bubble)
            throw new InvalidDataException(problem ?? $"Bubble {RetailTag.Hex(bubbleTag)} did not decode.");

        var notes = new List<string>([.. bubble.Notes]);
        if (bubble.Truncated) notes.Add("The bubble's node list was truncated while decoding.");

        var instanceTags = new List<uint>();
        var terrainTags = new List<uint>();
        foreach (var node in bubble.Nodes)
        {
            cancel.ThrowIfCancellationRequested();
            foreach (var component in node.Components)
            {
                if (component.DataTag is not { } data) continue;
                if (statics && component.Kind == "statics" && !instanceTags.Contains(data)) instanceTags.Add(data);
                if (terrain && component.Kind == "terrain" && !terrainTags.Contains(data)) terrainTags.Add(data);
            }
        }

        var placements = new Dictionary<uint, List<Matrix4x4>>();
        int instances = 0;
        foreach (uint tag in instanceTags)
        {
            if (RetailGeometry.ReadStaticInstances(source, tag, out string? instanceProblem) is not { } set)
            {
                notes.Add($"Statics {RetailTag.Hex(tag)}: {instanceProblem}");
                continue;
            }
            foreach (var placement in set.Placements)
            {
                if (!placements.TryGetValue(placement.Static, out var list)) placements[placement.Static] = list = [];
                list.AddRange(placement.Transforms);
                instances += placement.Transforms.Count;
            }
            notes.AddRange(set.Notes);
        }

        string folder = Path.Combine(root, "retail_ports", "geometry", bubbleTag.ToString("x8", Invariant));
        Directory.CreateDirectory(folder);
        var files = new List<string>();
        int meshes = 0, terrainMeshes = 0, triangles = 0, skipped = 0;

        foreach (uint meshTag in placements.Keys.OrderBy(tag => tag))
        {
            cancel.ThrowIfCancellationRequested();
            if (meshes + terrainMeshes >= maxMeshes) { skipped++; continue; }
            if (RetailGeometry.ReadStaticMesh(source, meshTag, out string? meshProblem) is not { } mesh)
            { notes.Add($"Static {RetailTag.Hex(meshTag)}: {meshProblem}"); skipped++; continue; }
            files.Add(WriteObj(folder, mesh));
            meshes++;
            triangles += mesh.Indices.Length / 3;
            notes.AddRange(mesh.Notes.Select(note => $"{RetailTag.Hex(meshTag)}: {note}"));
        }

        foreach (uint meshTag in terrainTags.OrderBy(tag => tag))
        {
            cancel.ThrowIfCancellationRequested();
            if (meshes + terrainMeshes >= maxMeshes) { skipped++; continue; }
            if (RetailGeometry.ReadTerrain(source, meshTag, out string? terrainProblem) is not { } mesh)
            { notes.Add($"Terrain {RetailTag.Hex(meshTag)}: {terrainProblem}"); skipped++; continue; }
            files.Add(WriteObj(folder, mesh));
            terrainMeshes++;
            triangles += mesh.Indices.Length / 3;
            notes.AddRange(mesh.Notes.Select(note => $"{RetailTag.Hex(meshTag)}: {note}"));
        }

        string placementsPath = Path.Combine(folder, "placements.json");
        File.WriteAllText(placementsPath, Placements(placements).ToJsonString(Indented), new UTF8Encoding(false));
        files.Add(placementsPath);

        var result = new RetailGeometryExportResult(bubbleTag, folder, files, meshes, terrainMeshes, triangles,
            instances, skipped, notes.Distinct().Take(200).ToList());
        string reportPath = Path.Combine(folder, "report.json");
        File.WriteAllText(reportPath, result.ToJson().ToJsonString(Indented), new UTF8Encoding(false));
        return result with { Files = [.. files, reportPath] };
    }


    /** Mesh tag to its world transforms, as 16 floats in row-major order. */
    private static JsonObject Placements(Dictionary<uint, List<Matrix4x4>> placements)
    {
        var rows = new JsonObject();
        foreach (var (tag, list) in placements.OrderBy(pair => pair.Key))
            rows[RetailTag.Hex(tag)] = new JsonArray(list.Select(matrix => (JsonNode)new JsonArray(
                Float(matrix.M11), Float(matrix.M12), Float(matrix.M13), Float(matrix.M14),
                Float(matrix.M21), Float(matrix.M22), Float(matrix.M23), Float(matrix.M24),
                Float(matrix.M31), Float(matrix.M32), Float(matrix.M33), Float(matrix.M34),
                Float(matrix.M41), Float(matrix.M42), Float(matrix.M43), Float(matrix.M44))).ToArray());
        return rows;
    }

    private static JsonNode Float(float value) => JsonValue.Create(MathF.Round(value, 6));

    /** One OBJ per mesh; vertices are mesh-local and every face is a triangle. */
    private static string WriteObj(string folder, RetailMesh mesh)
    {
        string path = Path.Combine(folder, mesh.Tag.ToString("x8", Invariant) + ".obj");
        var text = new StringBuilder(64 * 1024);
        text.Append("# retail ").Append(mesh.Kind).Append(' ').Append(RetailTag.Hex(mesh.Tag)).Append('\n');
        text.Append("o ").Append(RetailTag.Hex(mesh.Tag)).Append('\n');
        foreach (var position in mesh.Positions)
            text.Append("v ").Append(Format(position.X)).Append(' ').Append(Format(position.Y)).Append(' ')
                .Append(Format(position.Z)).Append('\n');
        for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
            text.Append("f ").Append(mesh.Indices[i] + 1).Append(' ').Append(mesh.Indices[i + 1] + 1).Append(' ')
                .Append(mesh.Indices[i + 2] + 1).Append('\n');
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static string Format(float value) => value.ToString("0.######", Invariant);
}

