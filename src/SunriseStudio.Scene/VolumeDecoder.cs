using System.Numerics;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

public sealed partial class AssetSceneLoader
{
    private readonly HashSet<(uint Tag, uint Bubble)> volumeConfigs = [];

    /** Trigger roots are reached through the exact placed-class construction rows. */
    private void ReadClassVolumes(uint classTag, uint bubble, string label)
    {
        if (Class(classTag) != SEntity.Class) return;
        byte[] definition = ReadBytes(classTag);
        // Only the component array must fit; short entities still list their builds.
        ref readonly var components = ref TagView.At<TagArray<SPatternComponent>>(definition,
            TagView.FieldOffset<SEntity>(nameof(SEntity.Components)));
        foreach (ref readonly var build in TagView.Rows(definition, in components, SPatternComponent.Class))
            ReadConfigVolumes(build.Config, bubble, label);
    }

    /** Config component rows name the trigger root through a self-relative i64 header link. */
    private void ReadConfigVolumes(uint configTag, uint bubble, string label)
    {
        if (Class(configTag) != SEntityConfig.Class || !volumeConfigs.Add((configTag, bubble))) return;
        byte[] config = ReadBytes(configTag);
        ref readonly var links = ref TagView.At<TagArray<PlacedConfigComponentRow>>(config,
            TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.ComponentLinks)));
        foreach (ref readonly var component in TagView.Rows(config, in links, PlacedConfigComponentRow.Class))
        {
            if (component.ComponentClass != STriggerComponent.Class) continue;
            long target = TagView.OffsetOf(config, in component.ComponentHeader) + component.ComponentHeader.Relative;
            if (component.ComponentHeader.IsNull || target is < 0 or > int.MaxValue
                || !TagView.Fits<TagOffsetPointer>(config, (int)target))
                throw new InvalidDataException("Trigger component header does not match its config.");
            int header = (int)target;
            var owner = TagView.At<TagOffsetPointer>(config, header);
            if (owner.Tag != configTag || owner.Word != STriggerComponentPeer.Class)
                throw new InvalidDataException("Trigger component header does not match its config.");
            var root = checked((int)(ulong)owner.Offset);
            // The peer may end before its view does; only its shape array must fit.
            ref readonly var shapes = ref TagView.At<TagArray<STriggerVolumeRow>>(config,
                root + TagView.FieldOffset<STriggerComponentPeer>(nameof(STriggerComponentPeer.TriggerShapes)));
            foreach (ref readonly var row in TagView.Rows(config, in shapes, STriggerVolumeRow.Class))
            {
                int rowOffset = TagView.OffsetOf(config, in row);
                var box = TriggerBox(in row);
                var name = $"{label} trigger {row.SlotOrdinal}";
                var rotation = row.SpawnEntry.Rotation;
                var position = row.SpawnEntry.Position;
                if (position.LengthSquared() > 1e-10f || MathF.Abs(rotation.W - 1) > 1e-5f
                    || new Vector3(rotation.X, rotation.Y, rotation.Z).LengthSquared() > 1e-10f)
                {
                    scene.UnsupportedCount++;
                    AddDiagnostic($"Trigger {name} has an unsupported non-identity placement.");
                    continue;
                }
                var objectPlacement = new SceneObjectPlacement(configTag, rowOffset, Matrix4x4.Identity);
                scene.Markers.Add(new SceneMarker
                {
                    ObjectPlacement = objectPlacement,
                    Name = name,
                    SourceTag = configTag,
                    SourceOffset = rowOffset,
                    Slot = new AuthoredSlotIdentity(row.Key, row.SlotType, row.SlotOrdinal),
                    Semantic = "trigger volume",
                    TagHash = configTag,
                    BubbleHash = bubble,
                    Layer = SceneLayer.Volumes,
                    Position = box.Position,
                    Extents = box.Extents,
                    Color = new Vector4(0.8f, 0.3f, 1, 0.7f)
                });
                var points = TagView.Rows(config, in row.Sector.Vertices, SVec4Point.Class);
                var triangles = TagView.Rows(config, in row.Sector.Triangles, TriggerVolumeTriangle.Class);
                var vertices = new SceneVertex[points.Length];
                for (int i = 0; i < points.Length; i++)
                    vertices[i] = new SceneVertex(points[i].Position, Vector3.UnitZ, Vector2.Zero, new Vector4(0.8f, 0.3f, 1, 0.35f));
                var corners = new List<uint>();
                foreach (ref readonly var triangle in triangles)
                    foreach (byte corner in (ReadOnlySpan<byte>)triangle.Indices) corners.Add(corner);
                var indices = corners.ToArray();
                if (indices.Any(i => i >= vertices.Length)) throw new InvalidDataException("Trigger triangle references an absent vertex.");
                if (indices.Length == 0) continue;
                var extrusion = row.Sector.ExtrusionHeight;
                if (!float.IsFinite(extrusion) || extrusion < 0) throw new InvalidDataException("Trigger extrusion is invalid.");
                Extrude(ref vertices, ref indices, extrusion);
                GenerateNormals(vertices, indices);
                scene.Instances.Add(new MeshInstance
                {
                    ObjectPlacement = objectPlacement,
                    Name = name,
                    BubbleHash = bubble,
                    Layer = SceneLayer.Volumes,
                    Mesh = new MeshData { TagHash = configTag, Vertices = vertices, Indices = indices, PartIndex = rowOffset }
                });
            }
        }
    }

    /**
     * Trigger bounds are an ordered min and max.
     * @return The marker's center and half size.
     */
    internal static (Vector3 Position, Vector3 Extents) TriggerBox(in STriggerVolumeRow row)
    {
        var min = row.Sector.BoundsMin;
        var max = row.Sector.BoundsMax;
        if (!Finite(min) || !Finite(max) || min.X > max.X || min.Y > max.Y || min.Z > max.Z)
            throw new InvalidDataException("Trigger bounds are invalid.");
        return ((min + max) * 0.5f, (max - min) * 0.5f);
    }

    /** Each boundary edge joins the authored base to its Z-extruded cap. */
    private static void Extrude(ref SceneVertex[] vertices, ref uint[] indices, float height)
    {
        if (height == 0) return;
        var count = (uint)vertices.Length;
        var source = vertices;
        vertices = new SceneVertex[count * 2];
        source.CopyTo(vertices, 0);
        source.CopyTo(vertices, (int)count);
        for (var i = (int)count; i < vertices.Length; ++i) vertices[i].Position.Z += height;
        var edges = new Dictionary<(uint Low, uint High), (uint A, uint B, int Count)>();
        var output = new List<uint>(indices.Length * 2);
        for (var i = 0; i < indices.Length; i += 3)
        {
            var a = indices[i]; var b = indices[i + 1]; var c = indices[i + 2];
            output.AddRange([c, b, a, a + count, b + count, c + count]);
            foreach (var edge in new[] { (a, b), (b, c), (c, a) })
            {
                var key = (Math.Min(edge.Item1, edge.Item2), Math.Max(edge.Item1, edge.Item2));
                var previous = edges.GetValueOrDefault(key);
                edges[key] = (edge.Item1, edge.Item2, previous.Count + 1);
            }
        }
        foreach (var edge in edges.Values.Where(e => e.Count == 1))
            output.AddRange([edge.A, edge.B, edge.B + count, edge.A, edge.B + count, edge.A + count]);
        indices = output.ToArray();
    }
}
