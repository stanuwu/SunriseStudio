using System.Buffers.Binary;
using System.Text.Json;

namespace SunriseStudio.Formats;

public static partial class GltfInterchange
{
    /** Geometry and skin readers share bounded accessor decoding. */
    private sealed class GltfAccessors(JsonElement document, List<byte[]> buffers)
    {
        // A 2M-vertex terrain grid has 12M indices; the cap only refuses counts no mesh reaches.
        private const int MaxAccessorCount = 1 << 26;
        private readonly JsonElement accessors = document.GetProperty("accessors");
        private readonly JsonElement views = document.GetProperty("bufferViews");
        private (byte[] Data, int Start, int Stride, int Count, int Type, bool Normalized) Accessor(int index, string shape)
        {
            var accessor = accessors[index];
            if (accessor.GetProperty("type").GetString() != shape || accessor.TryGetProperty("sparse", out _))
                throw new NotSupportedException("Sparse or mismatched glTF accessors are unsupported.");
            var view = views[accessor.GetProperty("bufferView").GetInt32()];
            byte[] bytes = buffers[view.GetProperty("buffer").GetInt32()];
            int type = accessor.GetProperty("componentType").GetInt32();
            int width = shape switch { "MAT4" => 16, "VEC4" => 4, "VEC3" => 3, "VEC2" => 2, _ => 1 };
            int size = type switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => 0 };
            if (size == 0) throw new NotSupportedException("glTF component format is unsupported.");
            int count = accessor.GetProperty("count").GetInt32(), viewStart = OptionalInt(view, "byteOffset");
            int start = checked(viewStart + OptionalInt(accessor, "byteOffset"));
            int stride = view.TryGetProperty("byteStride", out var strideValue) ? strideValue.GetInt32() : width * size;
            int viewLength = view.GetProperty("byteLength").GetInt32();
            bool normalized = accessor.TryGetProperty("normalized", out var normalization) && normalization.GetBoolean();
            if (count is < 0 or > MaxAccessorCount || viewStart < 0 || viewLength < 0 || viewStart > bytes.Length - viewLength
                || start < viewStart || start % size != 0 || OptionalInt(accessor, "byteOffset") % size != 0
                || stride < width * size || stride % size != 0 || normalized && type is 5125 or 5126
                || view.TryGetProperty("byteStride", out _) && (stride is < 4 or > 252 || stride % 4 != 0)
                || (long)start + Math.Max(0, count - 1L) * stride + (count == 0 ? 0 : width * size) > (long)viewStart + viewLength)
                throw new InvalidDataException("glTF accessor leaves its buffer view.");
            return (bytes, start, stride, count, type, normalized);
        }
        /** Lanes of a VEC3 or VEC4 accessor; any other shape throws. */
        public int Width(int index) => accessors[index].GetProperty("type").GetString() switch
        {
            "VEC3" => 3,
            "VEC4" => 4,
            _ => throw new NotSupportedException("glTF colors must be VEC3 or VEC4.")
        };
        public float[] Floats(int index, int width)
        {
            var a = Accessor(index, width switch { 16 => "MAT4", 4 => "VEC4", 3 => "VEC3", 2 => "VEC2", _ => "SCALAR" });
            int size = a.Type switch { 5120 or 5121 => 1, 5122 or 5123 => 2, _ => 4 };
            var values = new float[checked(a.Count * width)];
            for (int i = 0; i < a.Count; i++)
                for (int c = 0; c < width; c++)
                {
                    var span = a.Data.AsSpan(a.Start + i * a.Stride + c * size);
                    float value = a.Type switch
                    {
                        5126 => BinaryPrimitives.ReadSingleLittleEndian(span),
                        5120 => a.Normalized ? Math.Max((sbyte)span[0] / 127f, -1) : (sbyte)span[0],
                        5121 => a.Normalized ? span[0] / 255f : span[0],
                        5122 => a.Normalized ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(span) / 32767f, -1) : BinaryPrimitives.ReadInt16LittleEndian(span),
                        5123 => a.Normalized ? BinaryPrimitives.ReadUInt16LittleEndian(span) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(span),
                        _ => throw new NotSupportedException("glTF attribute component type is unsupported.")
                    };
                    if (!float.IsFinite(value)) throw new InvalidDataException("Non-finite glTF vertex.");
                    values[i * width + c] = value;
                }
            return values;
        }
        public uint[] Indices(int index, int width = 1)
        {
            var a = Accessor(index, width == 4 ? "VEC4" : "SCALAR");
            if (a.Normalized || a.Type is not (5121 or 5123 or 5125))
                throw new InvalidDataException("Invalid glTF index component type.");
            var output = new uint[checked(a.Count * width)];
            for (int i = 0; i < a.Count; i++)
            {
                int size = a.Type == 5121 ? 1 : a.Type == 5123 ? 2 : 4;
                for (int lane = 0; lane < width; lane++)
                {
                    var bytes = a.Data.AsSpan(a.Start + i * a.Stride + lane * size);
                    output[i * width + lane] = a.Type switch { 5121 => bytes[0], 5123 => BinaryPrimitives.ReadUInt16LittleEndian(bytes), _ => BinaryPrimitives.ReadUInt32LittleEndian(bytes) };
                }
            }
            return output;
        }
    }
}
