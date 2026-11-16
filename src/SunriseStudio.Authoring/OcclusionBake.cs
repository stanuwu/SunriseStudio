using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene.Collision;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;

namespace SunriseStudio.Authoring;

/** Adds new closed meshes as occluders to the tomes of a bubble's visibility maps. */
public static class OcclusionBake
{
    // Meshes above this many triangles are terrain-like and never tested for closure.
    private const int MaxOccluderTriangles = 200_000;

    /**
     * The closed meshes of a glb, three world positions per triangle. A mesh is closed when, with
     * vertices welded by position, every edge belongs to exactly two triangles.
     */
    public static List<IReadOnlyList<Vector3>> ClosedMeshes(string glb, Matrix4x4 world)
    {
        var result = new List<IReadOnlyList<Vector3>>();
        foreach (var mesh in GltfInterchange.Read(glb))
        {
            int triangles = mesh.Indices.Length / 3;
            if (triangles == 0 || triangles > MaxOccluderTriangles || !Closed(mesh.Positions, mesh.Indices)) continue;
            result.Add(mesh.Indices.Select(index => Vector3.Transform(mesh.Positions[index], world)).ToArray());
        }
        return result;
    }

    private static bool Closed(Vector3[] positions, uint[] indices)
    {
        var weld = new Dictionary<Vector3, int>();
        int Id(uint index) => weld.TryGetValue(positions[index], out int id) ? id : weld[positions[index]] = weld.Count;
        var edges = new Dictionary<(int, int), int>();
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int a = Id(indices[t]), b = Id(indices[t + 1]), c = Id(indices[t + 2]);
            foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
            {
                var key = p < q ? (p, q) : (q, p);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        }
        return edges.Count > 0 && edges.Values.All(count => count == 2);
    }

    /**
     * Closes the space each occluder encloses in every tome the bubble's visibility maps name, as
     * raw edits of those tomes. A tome the occluders miss, or one the editor cannot change, stays as it is.
     * @param skipped Receives a note per tome the editor refused.
     * @return The tomes changed.
     */
    public static IReadOnlyList<uint> AddOccluders(StudioProject project, PackageCatalog catalog, string destination, uint bubbleNameHash,
        IReadOnlyList<IReadOnlyList<Vector3>> occluders, List<string>? skipped = null)
    {
        if (occluders.Count == 0) return [];
        var world = WorldFiles.Load(project.ContentDirectory, destination);
        var maps = new SortedSet<uint>();
        foreach (string container in WorldContent.Containers(world, bubbleNameHash))
            foreach (var row in (JsonArray)world.Fields(container)["contents"]!)
            {
                if (WorldFiles.Text(row!["member_tag"]) is not { } file || world[file].SourceTag is not uint source) continue;
                byte[] list = catalog.Read(source);
                foreach (var payload in StaticCollisionReader.PlacedPayloads(list, source).Where(p => p.Marker == SPlacementVisibilityConstructor.Class))
                    maps.Add(TagView.Member<SPlacementVisibilityConstructor, uint>(list, payload.PayloadOffset,
                        nameof(SPlacementVisibilityConstructor.VisibilityMap)));
            }
        var changed = new List<uint>();
        foreach (uint map in maps)
        {
            if (!catalog.TryGetEntry(map, out var entry) || entry.Reference != SVisibilityTomeMap.Class) continue;
            ref readonly var view = ref TagView.Checked<SVisibilityTomeMap>(catalog.Read(map), 0);
            foreach (uint tag in new[] { view.FineTome, view.CoarseTome }.Distinct())
            {
                if (changed.Contains(tag) || !catalog.TryGetEntry(tag, out _)) continue;
                byte[] bytes = project.ReadAsset(tag, catalog);
                if (!TomeReader.IsTome(bytes)) continue;
                byte[] written;
                try
                {
                    var tome = TomeReader.Read(bytes);
                    TomeEditor.Apply(tome, new TomeOcclusionEdit { Added = occluders.ToList() });
                    written = TomeWriter.Write(tome);
                }
                catch (Exception error) when (error is NotSupportedException or InvalidDataException)
                {
                    // The tome keeps its stock occlusion; new instances are still culled by their own boxes.
                    skipped?.Add($"Occlusion tome {tag:X8} was left unchanged: {error.Message}");
                    continue;
                }
                if (written.AsSpan().SequenceEqual(bytes)) continue;
                project.ApplyEdit(tag, written, catalog);
                changed.Add(tag);
            }
        }
        return changed;
    }
}
