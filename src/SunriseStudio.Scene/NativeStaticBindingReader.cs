using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

public sealed partial class AssetSceneLoader
{
    private Dictionary<ulong, List<NativeOcclusionMapMatch>>? occlusionMaps;

    /** Shared static tables retain the AO containers reached by their own bubble route. */
    private void AssignOcclusionOwners()
    {
        var owners = scene.OcclusionOwners.GroupBy(o => o.BubbleHash)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<NativeOcclusionOwner>)g.Distinct().ToArray());
        foreach (var instance in scene.Instances)
        {
            cancellation.ThrowIfCancellationRequested();
            if (instance.Mesh.Native?.Kind is NativeMeshKind.Static or NativeMeshKind.Terrain)
                instance.OcclusionOwners = owners.GetValueOrDefault(instance.BubbleHash, []);
        }
    }

    /** AO candidates are exact key matches; render-state variants remain separate choices. */
    public IReadOnlyList<NativeOcclusionMapMatch> FindNativeOcclusionMaps(MeshInstance instance,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ulong key;
        if (instance.Mesh.Native?.Kind == NativeMeshKind.Terrain && instance.NativeInstanceData.Length == Unsafe.SizeOf<STerrainPayload>())
            key = TagView.At<STerrainPayload>(instance.NativeInstanceData.Span).AmbientOcclusionKey;
        else if (instance.Mesh.Native?.Kind != NativeMeshKind.Static || instance.InstanceSourceTag == 0
            || Class(instance.InstanceSourceTag) != SStaticMeshInstances.Class)
            throw new NotSupportedException("Native static shading needs an authored map instance.");
        else key = TagRead.U64(ReadBytes(instance.InstanceSourceTag),
            TagView.FieldOffset<SStaticMeshInstances>("Instances.AmbientOcclusionKey"));
        if (occlusionMaps == null)
        {
            var maps = new Dictionary<ulong, List<NativeOcclusionMapMatch>>();
            var entryIndex = 0;
            foreach (var entry in catalog.Entries)
            {
                if ((entryIndex++ & 0xFFF) == 0)
                {
                    token.ThrowIfCancellationRequested();
                    cancellation.ThrowIfCancellationRequested();
                }
                if (entry.Reference != SOcclusionMap.Class) continue;
                var data = ReadBytes(entry.TagHash);
                var buffer = TagRead.U32(data, TagView.FieldOffset<SOcclusionMap>(nameof(SOcclusionMap.ScalarBufferHeader)));
                ref readonly var offsets = ref TagView.At<TagArray<UnknownOcclusionMapInstance>>(data,
                    TagView.FieldOffset<SOcclusionMap>(nameof(SOcclusionMap.Offsets)));
                foreach (ref readonly var row in TagView.Rows(data, in offsets, UnknownOcclusionMapInstance.Class))
                {
                    token.ThrowIfCancellationRequested();
                    var itemKey = row.InstanceKey;
                    if (!maps.TryGetValue(itemKey, out var matches)) maps[itemKey] = matches = [];
                    matches.Add(new NativeOcclusionMapMatch(entry.TagHash, buffer,
                        TagRead.U32(data, TagView.FieldOffset<SOcclusionMap>(nameof(SOcclusionMap.Selector))), itemKey, row.BaseByteOffset));
                }
            }
            occlusionMaps = maps;
        }
        if (!occlusionMaps.TryGetValue(key, out var result)) return [];
        if (instance.OcclusionOwners.Count == 0)
            return instance.BubbleHash == 0 ? result : [];
        var ownedMaps = instance.OcclusionOwners.Select(o => o.MapTag).ToHashSet();
        return result.Where(m => ownedMaps.Contains(m.MapTag)).ToArray();
    }

    /**
     * Terrain CB11: header +0x30, group +0x20, then the AO byte offset as an integer in row 2 W.
     * Row 2 XYZ come from a frame vector the terrain VS does not read; they stay zero.
     */
    public NativeStaticBinding ReadNativeTerrainBinding(MeshInstance instance, CancellationToken token = default)
    {
        var native = instance.Mesh.Native;
        if (native is not { Kind: NativeMeshKind.Terrain })
            throw new NotSupportedException("Native terrain shading needs terrain geometry.");
        if (!IsIdentity(instance.Transform))
            throw new NotSupportedException("The terrain VS has no placement transform; this terrain is placed with one.");
        var choices = FindNativeOcclusionMaps(instance, token).DistinctBy(m => (m.BufferHeaderTag, m.BaseByteOffset)).ToArray();
        if (choices.Length != 1)
            throw new InvalidDataException(choices.Length == 0 ? "No authored AO buffer matches this terrain."
                : "Several authored AO maps match this terrain.");
        var selected = choices[0];
        var buffer = NativeStream(selected.BufferHeaderTag);
        if (buffer.Stride != 1 || buffer.Type != 0)
            throw new NotSupportedException("The authored AO buffer is not the verified R8_UNORM form.");
        if ((ulong)selected.BaseByteOffset + instance.Mesh.SourceVertexIndices.Max() >= (ulong)buffer.Data.Length)
            throw new InvalidDataException("The terrain AO fetch leaves its authored buffer.");
        return new NativeStaticBinding
        {
            Map = selected,
            ScalarBuffer = buffer,
            InstanceConstants =
            [
                native.TerrainOffset, native.UvTransform,
                new(0, 0, 0, BitConverter.UInt32BitsToSingle(selected.BaseByteOffset)), Vector4.Zero
            ],
            ScalarByteAddress = selected.BaseByteOffset
        };
    }

    /** rigid_model CB11: the instance matrix rows, model scale, offset and UV vectors, then (0,0,0,1). */
    public static Vector4[] ReadNativeEntityConstants(MeshInstance instance)
    {
        var native = instance.Mesh.Native;
        if (native is not { Kind: NativeMeshKind.Entity })
            throw new NotSupportedException("Native rigid-model shading needs entity geometry.");
        var m = instance.RenderTransform;
        var header = native.SourceHeader;
        return
        [
            new(m.M11, m.M12, m.M13, m.M14), new(m.M21, m.M22, m.M23, m.M24),
            new(m.M31, m.M32, m.M33, m.M34), new(m.M41, m.M42, m.M43, m.M44),
            EntityScale(header), PositionOffset(NativeMeshKind.Entity, header), UvTransform(NativeMeshKind.Entity, header),
            // The basic draw wrapper seeds vector 7 from a constant; other routes are unverified.
            new(0, 0, 0, 1)
        ];
    }

    private static bool IsIdentity(Matrix4x4 m) => Vector4.Distance(new(m.M11, m.M22, m.M33, m.M44), Vector4.One) < 1e-5f
        && MathF.Abs(m.M12) + MathF.Abs(m.M13) + MathF.Abs(m.M21) + MathF.Abs(m.M23) + MathF.Abs(m.M31) + MathF.Abs(m.M32)
            + MathF.Abs(m.M41) + MathF.Abs(m.M42) + MathF.Abs(m.M43) + MathF.Abs(m.M14) + MathF.Abs(m.M24) + MathF.Abs(m.M34) < 1e-4f;

    /**
     * Static CB11: three transposed model rows from the placement, then the UV and AO word row.
     * @param packedRow Row 3; it does not depend on the placement.
     */
    public static Vector4[] NativeStaticConstants(MeshInstance instance, Vector4 packedRow)
    {
        var native = instance.Mesh.Native ?? throw new NotSupportedException("Native static shading needs native geometry.");
        var model = Matrix4x4.CreateScale(native.PositionScale.X)
            * Matrix4x4.CreateTranslation(native.PositionOffset.X, native.PositionOffset.Y, native.PositionOffset.Z)
            * instance.Transform;
        return
        [
            new(model.M11, model.M21, model.M31, model.M41),
            new(model.M12, model.M22, model.M32, model.M42),
            new(model.M13, model.M23, model.M33, model.M43),
            packedRow
        ];
    }

    /** Uses the authored scalar bytes and preserves every packed bit consumed by the tested VS. */
    public NativeStaticBinding ReadNativeStaticBinding(MeshInstance instance,
        uint? occlusionMapTag = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var native = instance.Mesh.Native;
        if (native == null || native.Kind != NativeMeshKind.Static || instance.NativeInstanceData.Length != Unsafe.SizeOf<SStaticInstanceTransform>())
            throw new NotSupportedException("Native static shading needs the original 48-byte instance row.");
        var candidates = FindNativeOcclusionMaps(instance, token)
            .Where(m => occlusionMapTag == null || m.MapTag == occlusionMapTag.Value).ToArray();
        var choices = candidates.DistinctBy(m => (m.BufferHeaderTag, m.BaseByteOffset)).ToArray();
        if (choices.Length != 1)
            throw new InvalidDataException(choices.Length == 0 ? "No authored AO buffer matches this instance."
                : "Several authored AO maps match; select an AO map tag explicitly.");
        var selected = choices[0];
        var buffer = NativeStream(selected.BufferHeaderTag);
        if (buffer.Stride != 1 || buffer.Type != 0)
            throw new NotSupportedException("The authored AO buffer is not the verified R8_UNORM form.");
        ref readonly var row = ref TagView.At<SStaticInstanceTransform>(instance.NativeInstanceData.Span);
        var instanceOffset = row.AoInstanceByteOffset;
        var single = row.AoSingleScalar != 0;
        var byteOffset = unchecked(selected.BaseByteOffset + instanceOffset);
        if ((byteOffset & 3) != 0 || (native.BufferGroupWord0C & 3) != 0)
            throw new InvalidDataException("AO offsets do not satisfy native four-byte packing.");
        var packed = ((byteOffset >> 2) & 0x3DFFFFFF) | (single ? 0x02000000U : 0);
        if (!single) packed = unchecked(packed + (uint)(unchecked((int)native.BufferGroupWord0C) >> 2));
        var address = (packed & 0x01FFFFFF) << 2;
        var maximumVertex = single ? 0U : instance.Mesh.SourceVertexIndices.Max();
        if ((ulong)address + maximumVertex >= (ulong)buffer.Data.Length)
            throw new InvalidDataException("The native scalar fetch leaves its authored buffer.");
        var constants = NativeStaticConstants(instance, new(native.UvTransform.X, native.UvTransform.Z,
            native.UvTransform.W, BitConverter.UInt32BitsToSingle(packed)));
        return new NativeStaticBinding
        {
            Map = selected,
            ScalarBuffer = buffer,
            InstanceConstants = constants,
            PackedWord = packed,
            InstanceByteOffset = instanceOffset,
            BufferGroupByteOffset = native.BufferGroupWord0C,
            ScalarByteAddress = address,
            UseSingleScalar = single
        };
    }
}
