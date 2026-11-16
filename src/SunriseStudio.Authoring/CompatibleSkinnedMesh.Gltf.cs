using SunriseStudio.Animation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

namespace SunriseStudio.Authoring;

public static partial class CompatibleSkinnedMesh
{
    /** Existing rig identities remain authoritative after mapping the file's joint order. */
    public static PreparedCompatibleSkin PrepareGltf(PackageCatalog catalog, string path,
        CompatibleSkinReplacementOptions options, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Prepare(catalog, ReadGltf(catalog, path, options.EntityTag).Input, options, token);
    }

    /**
     * A new model from a glb skinned to an installed entity's skeleton, for a copy of that entity.
     * The file's joints must match the entity's bind pose; the model is added, not replaced.
     * @param entityTag The installed entity whose skeleton the file was exported with.
     * @param options The mesh template, as PrepareNewRig takes it.
     */
    public static PreparedCompatibleSkin PrepareNewRigGltf(PackageCatalog catalog, string path, uint entityTag,
        CompatibleSkinReplacementOptions options, NewRigPart? part = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var (input, rig) = ReadGltf(catalog, path, entityTag);
        ValidateBones(input.Bones, rig);
        return PrepareNewRig(catalog, input.Geometry, input.Influences, rig.Bones.Count, options, token: token, part: part);
    }

    /** Reads a skinned glb and puts its joints and influences in the entity's package bone order. */
    private static (CompatibleSkinnedMeshInput Input, SkeletonRig Rig) ReadGltf(PackageCatalog catalog, string path, uint entityTag)
    {
        var file = GltfInterchange.ReadSkinned(path);
        var rig = new SkeletonReader(catalog).Read(entityTag);
        if (file.Joints.Count != rig.Bones.Count)
            throw new InvalidDataException("Export every joint of the selected entity's skeleton.");
        var mapping = new int[file.Joints.Count];
        var assigned = new HashSet<int>();
        for (int index = 0; index < file.Joints.Count; index++)
        {
            var joint = file.Joints[index];
            if (joint.SourceEntityTag is uint owner && owner != entityTag)
                throw new InvalidDataException("The file's bone properties name a different entity.");
            int mapped;
            if (joint.SourceBoneIndex is int ordinal && joint.SourceEntityTag == entityTag)
                mapped = ordinal;
            else
            {
                var candidates = rig.Bones.Where(bone => joint.Name == bone.Name
                    || joint.Name == $"bone_{bone.Index:D4}_{bone.Name}"
                    || joint.Name == $"bone_{bone.NameHash:X8}").Select(bone => bone.Index).Distinct().ToArray();
                if (candidates.Length != 1)
                    throw new InvalidDataException($"Bone {joint.Name} needs a unique template name or its Sunrise properties.");
                mapped = candidates[0];
            }
            if ((uint)mapped >= rig.Bones.Count || !assigned.Add(mapped))
                throw new InvalidDataException("Imported bone identities are invalid or duplicated.");
            mapping[index] = mapped;
        }
        var bones = new CompatibleSkinBone[rig.Bones.Count];
        for (int index = 0; index < file.Joints.Count; index++)
        {
            var joint = file.Joints[index];
            int mapped = mapping[index];
            bones[mapped] = new(rig.Bones[mapped].NameHash, joint.ParentIndex < 0 ? -1 : mapping[joint.ParentIndex],
                joint.BindObject, joint.InverseBindObject);
        }
        var influences = file.Influences.Select(value => new VertexSkinWeights(mapping[value.Bone0],
            mapping[value.Bone1], mapping[value.Bone2], mapping[value.Bone3], value.Weights)).ToArray();
        return (new(file.Geometry, bones, influences), rig);
    }
}
