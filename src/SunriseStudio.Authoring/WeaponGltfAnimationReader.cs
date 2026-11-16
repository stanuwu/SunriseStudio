using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using SunriseStudio.Animation;

namespace SunriseStudio.Authoring;

public sealed record WeaponSourceAnimation(string Name, float SourceDuration, int FrameCount,
    IReadOnlyDictionary<int, IReadOnlyList<BoneTransform>> Controls);

/** Source skin matrices preserve each authored pivot when mapped onto the weapon's native bind pose. */
public static class WeaponGltfAnimationReader
{
    private sealed record Channel(int Node, string Path, string Interpolation, float[] Times, float[][] Values);
    // Blender writes rest scales with float noise; larger gaps are a real non-uniform scale.
    private const float UniformScale = 1e-4f;

    /** Sampling keeps the caller's native frame count and records the source duration for review. */
    public static WeaponSourceAnimation Read(string path, string action,
        IReadOnlyDictionary<string, int> controls, SkeletonRig nativeRig, int frames, float metresPerUnit)
    {
        if (controls.Count != nativeRig.Bones.Count
            || !controls.Values.Order().SequenceEqual(Enumerable.Range(0, nativeRig.Bones.Count)))
            throw new InvalidDataException("Each native weapon bone needs one source control.");
        string[] joints = controls.OrderBy(pair => pair.Value).Select(pair => pair.Key).ToArray();
        var sampled = ReadSkins(path, action, joints, frames, metresPerUnit);
        var output = controls.Values.ToDictionary(index => index, _ => new List<BoneTransform>());
        foreach (var skins in sampled.Skins)
        {
            var native = new Matrix4x4[nativeRig.Bones.Count];
            for (int control = 0; control < native.Length; control++)
                native[control] = nativeRig.Bones[control].BindObject.Matrix * skins[control];
            foreach (var bone in nativeRig.Bones)
            {
                Matrix4x4 value = native[bone.Index];
                if (bone.ParentIndex >= 0)
                {
                    if (!Matrix4x4.Invert(native[bone.ParentIndex], out var inverse))
                        throw new InvalidDataException("Weapon action parent transform is singular.");
                    value *= inverse;
                }
                output[bone.Index].Add(BoneTransform.FromMatrix(value));
            }
        }
        return new(action, sampled.SourceDuration, frames,
            output.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<BoneTransform>)pair.Value.ToArray()));
    }

    /** Skin deltas of the named joints in weapon space, one row per frame, in the order given. */
    internal sealed record SkinSamples(float SourceDuration, Matrix4x4[][] Skins);

    /** The named actions of a GLB and their lengths in seconds, in file order. */
    internal static IReadOnlyList<(string Name, float Seconds)> Actions(string path)
    {
        using var file = new Document(path);
        var result = new List<(string, float)>();
        if (!file.Root.TryGetProperty("animations", out var animations)) return result;
        foreach (var animation in animations.EnumerateArray())
        {
            if (!animation.TryGetProperty("name", out var name) || name.GetString() is not { Length: > 0 } text) continue;
            var samplers = animation.GetProperty("samplers");
            float start = float.MaxValue, end = float.MinValue;
            foreach (var sampler in samplers.EnumerateArray())
            {
                var times = file.Values(sampler.GetProperty("input").GetInt32(), 1);
                if (times.Length == 0) continue;
                start = MathF.Min(start, times[0][0]); end = MathF.Max(end, times[^1][0]);
            }
            result.Add((text, end > start ? end - start : 0));
        }
        return result;
    }

    /**
     * The one skin must hold exactly the named joints; their parents may also animate.
     * @param space Scene space to model space; null gives weapon space at `metresPerUnit`.
     * @param meanScale Reads a non-uniform scale key as the mean of its axes; off refuses it.
     */
    internal static SkinSamples ReadSkins(string path, string action, IReadOnlyList<string> joints,
        int frames, float metresPerUnit, Matrix4x4? space = null, bool meanScale = false)
    {
        if (frames is < 2 or > short.MaxValue || !float.IsFinite(metresPerUnit) || metresPerUnit <= 0)
            throw new InvalidDataException("Weapon animation needs a finite scale and a positive frame range.");
        using var file = new Document(path);
        var root = file.Root;
        var nodes = root.GetProperty("nodes");
        int count = nodes.GetArrayLength();
        if (count > 16384) throw new InvalidDataException("Weapon scene has too many nodes.");
        var parents = Enumerable.Repeat(-1, count).ToArray();
        for (int node = 0; node < count; node++)
            if (nodes[node].TryGetProperty("children", out var children))
                foreach (var child in children.EnumerateArray())
                {
                    int index = child.GetInt32();
                    if (index < 0 || index >= count || parents[index] != -1)
                        throw new InvalidDataException("Weapon node hierarchy is not a tree.");
                    parents[index] = node;
                }
        var mapping = new Dictionary<int, int>();
        for (int joint = 0; joint < joints.Count; joint++)
        {
            int[] matches = Enumerable.Range(0, count).Where(index =>
                nodes[index].TryGetProperty("name", out var named) && named.GetString() == joints[joint]).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Weapon bone names must identify one exported joint.");
            mapping.Add(matches[0], joint);
        }
        var skins = root.GetProperty("skins");
        if (skins.GetArrayLength() != 1
            || !skins[0].GetProperty("joints").EnumerateArray().Select(value => value.GetInt32()).Order()
                .SequenceEqual(mapping.Keys.Order()))
            throw new NotSupportedException("Weapon animation needs one skin containing exactly the mapped joints.");
        var needed = mapping.Keys.ToHashSet();
        foreach (int node in mapping.Keys)
            for (int current = parents[node], depth = 0; current >= 0; current = parents[current])
            {
                if (++depth > count) throw new InvalidDataException("Weapon hierarchy has a parent cycle.");
                needed.Add(current);
            }
        var bind = Enumerable.Range(0, count).Select(index => needed.Contains(index)
            ? Transform(nodes[index]) : BoneTransform.Identity).ToArray();
        var inverseBind = new Dictionary<int, Matrix4x4>();
        int[] skinJoints = skins[0].GetProperty("joints").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        float[][]? inverseRows = skins[0].TryGetProperty("inverseBindMatrices", out var inverseAccessor)
            ? file.Values(inverseAccessor.GetInt32(), 16) : null;
        if (inverseRows is not null && inverseRows.Length != skinJoints.Length)
            throw new InvalidDataException("Weapon inverse binds do not match the joint list.");
        for (int index = 0; index < skinJoints.Length; index++)
        {
            Matrix4x4 inverse = inverseRows is null ? Matrix4x4.Identity : Matrix(inverseRows[index]);
            if (!Matrix4x4.Invert(inverse, out _))
                throw new InvalidDataException("Weapon joint bind transform is singular.");
            inverseBind.Add(skinJoints[index], inverse);
        }
        var animations = root.GetProperty("animations").EnumerateArray()
            .Where(value => value.TryGetProperty("name", out var name) && name.GetString() == action).ToArray();
        if (animations.Length != 1) throw new InvalidDataException("Choose one uniquely named exported weapon action.");
        var animation = animations[0];
        var samplers = animation.GetProperty("samplers");
        var channels = new List<Channel>();
        foreach (var channel in animation.GetProperty("channels").EnumerateArray())
        {
            var target = channel.GetProperty("target");
            int node = target.GetProperty("node").GetInt32();
            string property = target.GetProperty("path").GetString()!;
            if (!needed.Contains(node) || property is not ("rotation" or "translation" or "scale"))
                throw new NotSupportedException("Weapon actions may animate mapped joints and their parent transforms only.");
            var sampler = samplers[channel.GetProperty("sampler").GetInt32()];
            string interpolation = sampler.TryGetProperty("interpolation", out var method) ? method.GetString()! : "LINEAR";
            if (interpolation is not ("LINEAR" or "STEP"))
                throw new NotSupportedException("Weapon action interpolation must be LINEAR or STEP.");
            float[] times = file.Values(sampler.GetProperty("input").GetInt32(), 1).Select(value => value[0]).ToArray();
            float[][] values = file.Values(sampler.GetProperty("output").GetInt32(), property == "rotation" ? 4 : 3);
            if (times.Length == 0 || times.Length != values.Length || times.Any(time => time < 0)
                || times.Zip(times.Skip(1), (first, second) => first >= second).Any(value => value))
                throw new InvalidDataException("Weapon animation keys must have increasing finite times.");
            channels.Add(new(node, property, interpolation, times, values));
        }
        if (channels.Count == 0 || channels.GroupBy(channel => (channel.Node, channel.Path)).Any(group => group.Count() != 1))
            throw new InvalidDataException("Weapon action channels are empty or duplicated.");
        float start = channels.Min(channel => channel.Times[0]);
        float duration = channels.Max(channel => channel.Times[^1]) - start;
        var conversion = space ?? Conversion(metresPerUnit);
        Matrix4x4.Invert(conversion, out var inverseConversion);
        var output = new Matrix4x4[frames][];
        for (int frame = 0; frame < frames; frame++)
        {
            float time = start + duration * frame / (frames - 1);
            var poses = bind.ToArray();
            foreach (var channel in channels)
            {
                float[] value = Sample(channel, time);
                var pose = poses[channel.Node];
                poses[channel.Node] = channel.Path switch
                {
                    "rotation" => pose with { Rotation = Quaternion.Normalize(new(value[0], value[1], value[2], value[3])) },
                    "translation" => pose with { Translation = new(value[0], value[1], value[2]) },
                    "scale" when Math.Abs(value[0] - value[1]) < UniformScale && Math.Abs(value[0] - value[2]) < UniformScale
                        => pose with { Scale = value[0] },
                    // A bone takes one scale; a creature's squash and stretch keeps its average size.
                    "scale" when meanScale => pose with { Scale = (value[0] + value[1] + value[2]) / 3 },
                    _ => throw new NotSupportedException("Weapon joint animation needs uniform scale.")
                };
            }
            Matrix4x4[] world = World(poses, parents);
            output[frame] = new Matrix4x4[joints.Count];
            foreach (var (node, joint) in mapping)
                output[frame][joint] = inverseConversion * inverseBind[node] * world[node] * conversion;
        }
        return new(duration, output);
    }

    /** glTF Y-up scene space to weapon space: +X forward, +Y left, +Z up, in metres. */
    internal static Matrix4x4 Conversion(float metresPerUnit) => Matrix4x4.CreateRotationX(MathF.PI / 2)
        * Matrix4x4.CreateRotationZ(-MathF.PI / 2) * Matrix4x4.CreateScale(metresPerUnit);

    private static float[] Sample(Channel channel, float time)
    {
        int second = Array.FindIndex(channel.Times, value => value >= time);
        if (second <= 0) return channel.Values[second < 0 ? ^1 : 0];
        if (channel.Times[second] == time) return channel.Values[second];
        int first = second - 1;
        if (channel.Interpolation == "STEP") return channel.Values[first];
        float amount = (time - channel.Times[first]) / (channel.Times[second] - channel.Times[first]);
        float[] a = channel.Values[first], b = channel.Values[second];
        if (channel.Path == "rotation")
        {
            var rotation = Quaternion.Slerp(Quaternion.Normalize(new(a[0], a[1], a[2], a[3])),
                Quaternion.Normalize(new(b[0], b[1], b[2], b[3])), amount);
            return [rotation.X, rotation.Y, rotation.Z, rotation.W];
        }
        return a.Zip(b, (left, right) => left + (right - left) * amount).ToArray();
    }

    /** A parent cycle is rejected before composing any joint pose. */
    private static Matrix4x4[] World(BoneTransform[] local, int[] parents)
    {
        var output = new Matrix4x4[local.Length];
        var states = new byte[local.Length];
        void Visit(int index)
        {
            if (states[index] == 2) return;
            if (states[index] == 1) throw new InvalidDataException("Weapon hierarchy has a parent cycle.");
            states[index] = 1;
            int parent = parents[index];
            if (parent >= 0) Visit(parent);
            output[index] = parent < 0 ? local[index].Matrix : local[index].Matrix * output[parent];
            states[index] = 2;
        }
        for (int index = 0; index < local.Length; index++) Visit(index);
        return output;
    }

    private static BoneTransform Transform(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matrix))
        {
            float[] m = matrix.EnumerateArray().Select(value => value.GetSingle()).ToArray();
            if (m.Length != 16) throw new InvalidDataException("Weapon node matrix must have sixteen elements.");
            return BoneTransform.FromMatrix(Matrix(m));
        }
        float[] Value(string name, float[] fallback) => node.TryGetProperty(name, out var found)
            ? found.EnumerateArray().Select(value => value.GetSingle()).ToArray() : fallback;
        float[] t = Value("translation", [0, 0, 0]), r = Value("rotation", [0, 0, 0, 1]), s = Value("scale", [1, 1, 1]);
        if (t.Length != 3 || r.Length != 4 || s.Length != 3 || t.Concat(r).Concat(s).Any(value => !float.IsFinite(value))
            || s[0] <= 0 || Math.Abs(s[0] - s[1]) > UniformScale || Math.Abs(s[0] - s[2]) > UniformScale)
            throw new NotSupportedException("Weapon nodes need finite transforms and uniform positive scale.");
        var q = new Quaternion(r[0], r[1], r[2], r[3]);
        if (q.LengthSquared() < 1e-12f) throw new InvalidDataException("Weapon node rotation has no direction.");
        return new(Quaternion.Normalize(q), new(t[0], t[1], t[2]), s[0]);
    }

    private static Matrix4x4 Matrix(float[] m) => new(m[0], m[1], m[2], m[3],
        m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);

    /** Weapon animation accessors use embedded float streams without sparse patches. */
    internal sealed class Document : IDisposable
    {
        private readonly JsonDocument json;
        private readonly byte[] binary;
        public JsonElement Root => json.RootElement;

        public Document(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 28 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x46546C67
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != 2
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) != bytes.Length)
                throw new NotSupportedException("Weapon animation import needs a self-contained GLB 2 scene.");
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
            if (length > bytes.Length - 28 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)) != 0x4E4F534A)
                throw new InvalidDataException("Weapon GLB has an invalid JSON chunk.");
            json = JsonDocument.Parse(bytes.AsMemory(20, length));
            int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20 + length)));
            if (count > bytes.Length - 28 - length
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24 + length)) != 0x004E4942)
                throw new InvalidDataException("Weapon GLB has an invalid binary chunk.");
            binary = bytes.AsSpan(28 + length, count).ToArray();
        }

        public float[][] Values(int index, int width)
        {
            var accessor = Root.GetProperty("accessors")[index];
            string type = width switch { 1 => "SCALAR", 3 => "VEC3", 4 => "VEC4", 16 => "MAT4", _ => "" };
            if (accessor.GetProperty("componentType").GetInt32() != 5126 || accessor.GetProperty("type").GetString() != type
                || accessor.TryGetProperty("sparse", out _) || accessor.TryGetProperty("normalized", out var norm) && norm.GetBoolean())
                throw new NotSupportedException("Weapon action keys must use dense float accessors.");
            var view = Root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
            if (view.GetProperty("buffer").GetInt32() != 0)
                throw new NotSupportedException("Weapon action keys must use the embedded GLB buffer.");
            int count = accessor.GetProperty("count").GetInt32();
            int offset = Optional(view, "byteOffset") + Optional(accessor, "byteOffset");
            int stride = view.TryGetProperty("byteStride", out var stored) ? stored.GetInt32() : width * 4;
            if (count is < 0 or > 8_000_000 || stride < width * 4 || offset < 0
                || (long)offset + (long)Math.Max(0, count - 1) * stride + width * 4 > binary.Length)
                throw new InvalidDataException("Weapon animation accessor exceeds its buffer.");
            return Enumerable.Range(0, count).Select(row => Enumerable.Range(0, width).Select(axis =>
            {
                float value = BinaryPrimitives.ReadSingleLittleEndian(binary.AsSpan(offset + row * stride + axis * 4));
                return float.IsFinite(value) ? value : throw new InvalidDataException("Weapon animation key is not finite.");
            }).ToArray()).ToArray();
        }

        /** Rigid skin lanes use glTF's byte, short or float weight encoding. */
        public float[][] SkinValues(int index, bool weights)
        {
            var accessor = Root.GetProperty("accessors")[index];
            int type = accessor.GetProperty("componentType").GetInt32();
            bool normalized = accessor.TryGetProperty("normalized", out var norm) && norm.GetBoolean();
            if (accessor.GetProperty("type").GetString() != "VEC4" || accessor.TryGetProperty("sparse", out _)
                || (weights ? type is not (5121 or 5123 or 5126) || (type != 5126 && !normalized)
                    : type is not (5121 or 5123) || normalized))
                throw new NotSupportedException("Weapon skin lanes have an unsupported glTF encoding.");
            var view = Root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
            if (view.GetProperty("buffer").GetInt32() != 0)
                throw new NotSupportedException("Weapon skin data must use the embedded GLB buffer.");
            int count = accessor.GetProperty("count").GetInt32();
            int width = type == 5121 ? 1 : type == 5123 ? 2 : 4;
            int offset = Optional(view, "byteOffset") + Optional(accessor, "byteOffset");
            int stride = view.TryGetProperty("byteStride", out var stored) ? stored.GetInt32() : width * 4;
            if (count is < 0 or > 8_000_000 || stride < width * 4 || offset < 0
                || (long)offset + (long)Math.Max(0, count - 1) * stride + width * 4 > binary.Length)
                throw new InvalidDataException("Weapon skin accessor exceeds its buffer.");
            return Enumerable.Range(0, count).Select(row => Enumerable.Range(0, 4).Select(axis =>
            {
                int at = checked(offset + row * stride + axis * width);
                float value = type == 5121 ? binary[at] : type == 5123
                    ? BinaryPrimitives.ReadUInt16LittleEndian(binary.AsSpan(at))
                    : BinaryPrimitives.ReadSingleLittleEndian(binary.AsSpan(at));
                if (normalized) value /= type == 5121 ? byte.MaxValue : ushort.MaxValue;
                return float.IsFinite(value) ? value : throw new InvalidDataException("Weapon skin value is not finite.");
            }).ToArray()).ToArray();
        }

        private static int Optional(JsonElement value, string name) => value.TryGetProperty(name, out var found) ? found.GetInt32() : 0;
        public void Dispose() => json.Dispose();
    }
}
