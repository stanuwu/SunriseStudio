using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace SunriseStudio.Scene;

/** Collision is drawn by its own overlay from decoded Havok shapes; no scene item carries it. */
public enum SceneLayer { Geometry, Terrain, Skybox, Distant, Objects, Volumes, Transitions, Spawns, Squads, Effects, Lights, Collision, Navigation }

[StructLayout(LayoutKind.Sequential)]
public struct SceneVertex(Vector3 position, Vector3 normal, Vector2 texCoord, Vector4 color)
{
    public Vector3 Position = position;
    public Vector3 Normal = normal;
    public Vector2 TexCoord = texCoord;
    public Vector4 Color = color;
}

public sealed class MeshData
{
    public uint TagHash { get; init; }
    public SceneVertex[] Vertices { get; init; } = [];
    public uint[] Indices { get; init; } = [];
    public uint MaterialTag { get; init; }
    public int PartIndex { get; init; }
    public int LogicalPartIndex { get; init; } = -1;
    /** Entity part word at +0x18; the game never draws a part with HiddenPartFlag set. */
    public ushort PartFlags { get; init; }
    public const ushort HiddenPartFlag = 0x40;
    public const string HiddenPartReason = "Part flag 0x40: never drawn";
    /** The part's bit is clear in the entity's initial bitset. */
    public const string InactiveInitialPartReason = "Inactive initial part";
    /** A cloth model part that draw set 0 of its cloth asset does not list; the cloth draw skips it. */
    public const string ClothSetPartReason = "Not in cloth draw set 0";
    public bool NeverDrawn => (PartFlags & HiddenPartFlag) != 0;
    /** 0 is the most detailed level; higher values are coarser alternates of the same mesh. */
    public int LodLevel { get; init; }
    /** The distance level inputs of a static mesh part; null for every other kind. */
    public StaticLodPart? StaticLod { get; init; }
    public uint[] SourceVertexIndices { get; init; } = [];
    public NativeMeshData? Native { get; init; }
}

public sealed class MeshInstance
{
    private Matrix4x4 transform = Matrix4x4.Identity;
    private ReadOnlyMemory<byte> nativeInstanceData;
    private bool? initiallyVisible;
    private string? initialVisibilityReason;
    public SceneObjectPlacement? ObjectPlacement { get; init; }
    public required MeshData Mesh { get; init; }
    public Matrix4x4 Transform { get => transform; init => transform = value; }
    /** Transform of the visit that placed this static instance or decorator record; it applies after the row's. */
    public Matrix4x4 InstanceParent { get; internal init; } = Matrix4x4.Identity;
    /** Runtime placement affects preview only; package authoring keeps Transform. */
    [JsonIgnore] public Matrix4x4? PreviewTransform { get; set; }
    [JsonIgnore] public Matrix4x4 RenderTransform => PreviewTransform ?? Transform;
    public string Name { get; set; } = "";
    public SceneLayer Layer { get; init; } = SceneLayer.Geometry;
    public uint BubbleHash { get; init; }
    public uint InstanceSourceTag { get; init; }
    public int InstanceSourceIndex { get; init; } = -1;
    public ReadOnlyMemory<byte> NativeInstanceData { get => nativeInstanceData; init => nativeInstanceData = value; }
    /** World box of a ground decorator cell, which draws many records; null uses the mesh bounds. */
    public (Vector3 Min, Vector3 Max)? CellBounds { get; init; }
    public uint ObjectClassTag { get; init; }
    public uint ModelSourceConfigTag { get; init; }
    public uint ModelSourceResourceClass { get; init; }
    /** Null keeps unresolved runtime state visible for inspection. */
    public bool? InitiallyVisible { get => initiallyVisible; init => initiallyVisible = value; }
    public string? InitialVisibilityReason { get => initialVisibilityReason; init => initialVisibilityReason = value; }
    public bool RequiresClothSimulation => ModelSourceResourceClass == 0x80807286;
    public uint PlacementSourceTag { get; init; }
    public int PlacementSourceOffset { get; init; } = -1;
    public ulong PlacementIdentity { get; init; }
    public IReadOnlyList<NativeOcclusionOwner> OcclusionOwners { get; set; } = [];
    public IReadOnlyList<MapContainerInfo> MapContainers { get; set; } = [];
    public AttachmentPreviewSource? AttachmentSource { get; init; }
    /** Coarse kind for filtering, for example "Terrain" or "Trigger volume". */
    public string Category { get; set; } = "";
    public IReadOnlyList<string> Tags { get; set; } = [];
    /** Scenario that placed the item; 0 for map content. */
    public uint ActivityTag { get; set; }
    public int StateOrdinal { get; set; } = -1;
    public string StateName { get; set; } = "";
    /** The label the name index last generated; null when the name is not generated. */
    internal string? GeneratedName;

    /** A copy of every field, with this initial visibility and reason. The copy's name counts as authored. */
    public MeshInstance WithVisibility(bool? visible, string? reason)
    {
        var copy = (MeshInstance)MemberwiseClone();
        copy.initiallyVisible = visible;
        copy.initialVisibilityReason = reason;
        copy.GeneratedName = null;
        return copy;
    }

    /** A placement update writes what a new decode of the same row would hold. */
    internal void SetAuthored(Matrix4x4 value, ReadOnlyMemory<byte> row)
    {
        transform = value;
        nativeInstanceData = row;
    }
}

public sealed class SceneMarker
{
    private Vector3 position, extents = Vector3.One;
    private Quaternion rotation = Quaternion.Identity;
    public SceneObjectPlacement? ObjectPlacement { get; init; }
    /** Transform of the visit that placed this 0x808099D6 row; it applies after the row's. */
    public Matrix4x4 PlacementParent { get; internal init; } = Matrix4x4.Identity;
    public IReadOnlyList<MapContainerInfo> MapContainers { get; set; } = [];
    public string Name { get; set; } = "";
    public uint NameHash { get; set; }
    public SceneNameEvidence NameEvidence { get; set; }
    public uint NameSourceTag { get; set; }
    public int NameSourceOffset { get; set; } = -1;
    public AuthoredSlotIdentity? NameSourceSlot { get; set; }
    public uint NameReferenceTag { get; set; }
    public int NameReferenceOffset { get; set; } = -1;
    public uint SourceTag { get; init; }
    public int SourceOffset { get; init; }
    public ulong PlacementIdentity { get; init; }
    public AuthoredSlotIdentity? Slot { get; set; }
    public string Semantic { get; set; } = "";
    public bool AssociationIsAmbiguous { get; set; }
    public uint TagHash { get; init; }
    public SceneLayer Layer { get; init; }
    public Vector3 Position { get => position; init => position = value; }
    public Vector3 Extents { get => extents; init => extents = value; }
    public Quaternion Rotation { get => rotation; init => rotation = value; }
    /** Runtime placement affects preview only; package authoring keeps Position and Rotation. */
    [JsonIgnore] public (Vector3 Position, Quaternion Rotation)? PreviewPose { get; set; }
    [JsonIgnore] public Vector3 RenderPosition => PreviewPose?.Position ?? Position;
    [JsonIgnore] public Quaternion RenderRotation => PreviewPose?.Rotation ?? Rotation;
    [JsonIgnore] public Vector3? PreviewExtents { get; set; }
    [JsonIgnore] public Vector3 RenderExtents => PreviewExtents ?? Extents;
    public Vector4 Color { get; init; } = Vector4.One;
    public uint BubbleHash { get; init; }
    /** Coarse kind for filtering, for example "Terrain" or "Trigger volume". */
    public string Category { get; set; } = "";
    public IReadOnlyList<string> Tags { get; set; } = [];
    /** Scenario that placed the item; 0 for map content. */
    public uint ActivityTag { get; set; }
    public int StateOrdinal { get; set; } = -1;
    public string StateName { get; set; } = "";
    /** Decoded particle node or lens flare; set only on Effects-layer markers. */
    public EffectDescription? Effect { get; init; }
    /** The label the name index last generated and the owner it used; null before the first. */
    internal string? GeneratedName, GeneratedBase;

    /** A placement update writes what a new decode of the same row would hold. */
    internal void SetAuthored(Vector3 newPosition, Quaternion newRotation, Vector3 newExtents)
    {
        position = newPosition;
        rotation = newRotation;
        extents = newExtents;
    }
}

/** A picker entry: the group's first row, its row count and the variant it selects (zero: none). */
public sealed record SpawnChoice(SpawnInput Input, int RowCount, uint Variant, string? Unresolved);

/**
 * One object channel of the opened entity's model: TFX channel Index, its name, the value the view uses,
 * and Rest, the value the game holds at rest, which the view uses until the user sets one.
 */
public sealed record SceneObjectChannel(int Index, uint NameHash, Vector4 Value, ObjectChannelRestValue Rest)
{
    /** The view reads a channel whose rest value is not settled as this value in every lane. */
    public const float ViewerDefault = 1;
}

public sealed record BubbleInfo(uint TagHash, uint NameHash, string Name);

/** A placed class-0x80807086 atmosphere resource; Settings are its bytes +0x10 to +0xF0. */
public sealed record PlacedAtmosphere(uint BubbleHash, uint PlacementListTag, int PlacementOffset, byte[] Settings);

/** A placed class-0x80806F61 time-of-day resource: +0x10 curve set tag, +0x14 day seconds, +0x18 start time. */
public sealed record PlacedTimeOfDay(uint BubbleHash, uint PlacementListTag, uint CurveSetTag, float DaySeconds, float StartTime);

public sealed class SceneDocument
{
    public string Name { get; set; } = "";
    public uint TagHash { get; set; }
    public int DecodeErrorCount { get; set; }
    public int UnsupportedCount { get; set; }
    public List<MeshInstance> Instances { get; } = [];
    public List<SceneMarker> Markers { get; } = [];
    public List<BubbleInfo> Bubbles { get; } = [];
    public List<string> Diagnostics { get; } = [];
    public List<EntityMaterialWarning> MaterialWarnings { get; } = [];
    public List<ScenarioStateInfo> ScenarioStates { get; } = [];
    public ScenarioInfo? Scenario { get; set; }
    public List<AuthoredObjectInfo> AuthoredObjects { get; } = [];
    public List<AuthoredSquadInfo> Squads { get; } = [];
    public List<NativeOcclusionOwner> OcclusionOwners { get; } = [];
    public List<PlacedAtmosphere> Atmospheres { get; } = [];
    public List<PlacedTimeOfDay> TimesOfDay { get; } = [];
    public List<PlacedNavigation> Navigation { get; } = [];
    public List<PlacedLightTable> LightTables { get; } = [];
    public List<PlacedCubemap> Cubemaps { get; } = [];
    public List<MapLayerInfo> MapLayers { get; } = [];
    public List<MapContainerInfo> MapContainers { get; } = [];
    public EntityAttachmentCatalog? Attachments { get; set; }
    public AttachmentPreviewSource? AttachmentPreview { get; set; }
    /** Variant row names of the opened entity; empty when it has no variant component. */
    public IReadOnlyList<uint> EntityVariants { get; set; } = [];
    /** Variant row the opened entity was read with; zero is the native constructor default. */
    public uint PreviewVariant { get; set; }
    /** Authored spawn inputs of the opened entity, ordered by first row. */
    public IReadOnlyList<SpawnChoice> SpawnChoices { get; set; } = [];
    /** Spawn row the opened entity was read with; null is the native default. */
    public SpawnInput? PreviewSpawn { get; set; }
    /** Variant parameters of the opened entity and the values the read used. */
    public IReadOnlyList<EntityVariantParameter> EntityParameters { get; set; } = [];
    /** Parameter values the user picked; they were applied after the spawn's own. */
    public IReadOnlyDictionary<uint, uint> PreviewParameters { get; set; } = new Dictionary<uint, uint>();
    /** Object channels of the opened entity's model, in TFX channel order. */
    public IReadOnlyList<SceneObjectChannel> ObjectChannels { get; set; } = [];
    /** Channel values the user set, by name, one value in every lane; the others use their rest value. */
    public IReadOnlyDictionary<uint, float> ChannelValues { get; set; } = new Dictionary<uint, float>();
    /** TFX object channel table of the opened entity; null when it has no channels. */
    public IReadOnlyList<Vector4>? ObjectChannelTable => ObjectChannels.Count == 0 ? null
        : ObjectChannels.OrderBy(channel => channel.Index).Select(channel => channel.Value).ToArray();
    /** Every tag the decoder read for this scene. A mesh cache hit reads no mesh tag. */
    public IReadOnlySet<uint> ReadTags => ReadTagSet;
    internal HashSet<uint> ReadTagSet { get; } = [];
    /** Bytes of each placement table as decoded; a placement update diffs against them. */
    internal Dictionary<uint, byte[]> PlacementSources { get; } = [];
    /** Placement rows whose position decides a squad anchor or its ambiguity. */
    internal HashSet<(uint Tag, int Offset)> SquadSourceRows { get; } = [];

    /** Bounds include transformed geometry and authored marker extents. */
    public (Vector3 Minimum, Vector3 Maximum) GetBounds(bool includeMarkers = true, bool includeSkybox = true)
    {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        var localBounds = new Dictionary<MeshData, (Vector3 Min, Vector3 Max)>();
        foreach (var instance in Instances)
        {
            if (!includeSkybox && instance.Layer is SceneLayer.Skybox or SceneLayer.Distant) continue;
            if (instance.Mesh.Vertices.Length == 0) continue;
            if (!localBounds.TryGetValue(instance.Mesh, out var local))
            {
                local = (new Vector3(float.PositiveInfinity), new Vector3(float.NegativeInfinity));
                foreach (var vertex in instance.Mesh.Vertices)
                {
                    local.Min = Vector3.Min(local.Min, vertex.Position);
                    local.Max = Vector3.Max(local.Max, vertex.Position);
                }
                localBounds.Add(instance.Mesh, local);
            }
            for (var corner = 0; corner < 8; ++corner)
            {
                var p = Vector3.Transform(new Vector3((corner & 1) == 0 ? local.Min.X : local.Max.X,
                    (corner & 2) == 0 ? local.Min.Y : local.Max.Y,
                    (corner & 4) == 0 ? local.Min.Z : local.Max.Z), instance.Transform);
                minimum = Vector3.Min(minimum, p);
                maximum = Vector3.Max(maximum, p);
            }
        }
        foreach (var marker in includeMarkers || !float.IsFinite(minimum.X) ? Markers : [])
        {
            minimum = Vector3.Min(minimum, marker.Position - marker.Extents);
            maximum = Vector3.Max(maximum, marker.Position + marker.Extents);
        }
        return float.IsFinite(minimum.X) ? (minimum, maximum) : (-Vector3.One, Vector3.One);
    }
}
