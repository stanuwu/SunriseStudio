using System.Numerics;

namespace SunriseStudio.Scene.Collision;

/** Havok QsTransform: scale, then rotate by the unit quaternion, then translate. */
public readonly record struct QsTransform(Vector3 Translation, Quaternion Rotation, Vector3 Scale)
{
    public static QsTransform Identity { get; } = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /** Row-vector matrix, the convention of the scene decoder. */
    public Matrix4x4 ToMatrix() => Matrix4x4.CreateScale(Scale)
        * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(Rotation)) * Matrix4x4.CreateTranslation(Translation);
}

/** One compound instance. The child is an in-file object offset or an import name `iN` or `tK`. */
public readonly record struct CompoundInstance(int Index, int Offset, QsTransform Transform, uint Flags,
    int? ChildObject, string? ChildImport)
{
    /** Index into the owning tag's import list, or -1 for an in-file child. */
    public int ImportIndex => ChildImport is ['i', .. var digits] && int.TryParse(digits, out int n) ? n : -1;
    /** Index into the owning tag's carrier list for an import `tK`, or -1. */
    public int CarrierIndex => ChildImport is ['t', .. var digits] && int.TryParse(digits, out int n) ? n : -1;
    /** Instance +0x38 collision filter info; 0 leaves the child's own filter. */
    public uint FilterInfo { get; init; }
}

/** A decoded hkpStaticCompoundShape: its instances and its baked tree. */
public sealed class StaticCompoundShape
{
    public const string ClassName = "hkpStaticCompoundShape";
    // The instance stride and the W lane base come from the hkClass layouts in the image.
    public const int InstanceSize = 0x50;
    public const uint FlagBase = 0x3F000000;
    public const uint ConvexFlag = 1, NotIdentityFlag = 2, ScaledFlag = 4, MirroredFlag = 8;

    public HavokPackfile File { get; }
    public IReadOnlyList<CompoundInstance> Instances { get; }
    /** Data offset of the first 6-byte tree node. */
    public int NodeData { get; }
    public int NodeCount { get; }
    /** Data offset of the tree domain min; the max follows 16 bytes later. */
    public int DomainOffset { get; }
    public Vector3 DomainMin => File.V3(DomainOffset);
    public Vector3 DomainMax => File.V3(DomainOffset + 16);

    private StaticCompoundShape(HavokPackfile file, int root)
    {
        File = file;
        if (file.ClassAt(root) != ClassName) throw new InvalidDataException("Packfile object is not a static compound shape.");
        var (count, data) = file.Array(root + 0x38);
        var instances = new CompoundInstance[count];
        for (int i = 0; i < count; i++)
        {
            int at = data + InstanceSize * i;
            if (file.U32(at + 0x2C) != FlagBase) throw new InvalidDataException("Compound instance scale W lane is not 0.5.");
            var transform = new QsTransform(file.V3(at), file.Q4(at + 0x10), file.V3(at + 0x20));
            instances[i] = new(i, at, transform, file.U32(at + 0x0C), file.Pointer(at + 0x30),
                file.Imports.GetValueOrDefault(at + 0x30)) { FilterInfo = file.U32(at + 0x38) };
        }
        Instances = instances;
        (NodeCount, NodeData) = file.Array(root + 0x70);
        DomainOffset = root + 0x80;
        if (count > 0 && NodeCount != 2 * count - 1) throw new InvalidDataException("Compound tree is not a full binary tree.");
    }

    public static StaticCompoundShape Parse(HavokPackfile file) => new(file, file.RootOffset);

    /** A compound that is not the packfile root, such as one inside a shape array. */
    public static StaticCompoundShape Parse(HavokPackfile file, int offset) => new(file, offset);

    /**
     * The flag bits of a translation W lane. Flag 1 is the child's convexity and is passed in.
     * @return Flags 2, 4 and 8 from the transform, or'd with the convex flag.
     */
    public static uint Flags(QsTransform transform, bool convex)
    {
        uint flags = convex ? ConvexFlag : 0;
        var (t, q, s) = (transform.Translation, transform.Rotation, transform.Scale);
        // Identity is approximate: stored instances at 5e-4 m and -q count as identity.
        bool identity = Near(t, Vector3.Zero) && Near(s, Vector3.One)
            && (Near(new Vector4(q.X, q.Y, q.Z, q.W), Vector4.UnitW) || Near(new Vector4(q.X, q.Y, q.Z, q.W), -Vector4.UnitW));
        if (!identity) flags |= NotIdentityFlag;
        if (s != Vector3.One) flags |= ScaledFlag;
        if (((s.X < 0 ? 1 : 0) + (s.Y < 0 ? 1 : 0) + (s.Z < 0 ? 1 : 0)) % 2 == 1) flags |= MirroredFlag;
        return flags;
    }

    // Per-component identity tolerance of flag 2. It reproduces every stored Leviathan flag.
    private const float IdentityTolerance = 1e-3f;

    private static bool Near(Vector3 a, Vector3 b) => Near(new Vector4(a, 0), new Vector4(b, 0));

    private static bool Near(Vector4 a, Vector4 b) => MathF.Abs(a.X - b.X) <= IdentityTolerance
        && MathF.Abs(a.Y - b.Y) <= IdentityTolerance && MathF.Abs(a.Z - b.Z) <= IdentityTolerance
        && MathF.Abs(a.W - b.W) <= IdentityTolerance;

    public byte[] ReadNodes() => File.Bytes.AsSpan(File.Absolute(NodeData), NodeCount * CompoundTree.NodeSize).ToArray();

    /** Each instance's leaf box, indexed by instance. */
    public CompoundBox[] LeafBoxes() =>
        CompoundTree.LeafBoxes(ReadNodes(), Instances.Count, CompoundBox.From(DomainMin, DomainMax));

    /** The tree refit to new leaf boxes; see CompoundTree.Refit. */
    public (byte[] Nodes, Vector3 DomainMin, Vector3 DomainMax) Refit(IReadOnlyList<CompoundBox> leaves) =>
        CompoundTree.Refit(ReadNodes(), Instances.Count, leaves);
}
