using SunriseStudio.Formats.NewWorld;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Authoring;

/** Frames new-map collision the way installed packfiles are laid out, so HavokPackfile reads it. */
public static class NewWorldCollision
{
    // Class signatures in the installed 2012.2 class name tables.
    private const uint CompressedMeshSignature = 0x7A192C95, StaticCompoundSignature = 0xE198603F;
    // The packfile role: 1 is a per-mesh shape, 0 a compound.
    private const int MeshUserTag = 1, CompoundUserTag = 0;

    /** The object as the root of a retail packfile: reflection classes, then its own class. */
    public static byte[] Frame(HavokContents contents)
    {
        (uint signature, int userTag) = contents.RootClass == CompressedMesh.ClassName ? (CompressedMeshSignature, MeshUserTag)
            : contents.RootClass == StaticCompoundShape.ClassName ? (StaticCompoundSignature, CompoundUserTag)
            : throw new InvalidDataException($"New-map collision has no packfile framing for {contents.RootClass}.");
        var builder = new HavokPackfileBuilder();
        int root = builder.PlaceObject(contents.Data, contents.RootClass);
        foreach (var (source, target) in contents.Local) builder.Local(source, target);
        foreach (var (field, name) in contents.Imports) builder.Import(field, name);
        return builder.Build([.. CollisionPackfileWriter.ReflectionClasses, (signature, contents.RootClass)], root, userTag);
    }
}
