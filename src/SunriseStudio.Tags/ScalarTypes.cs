namespace SunriseStudio.Tags;

/** Registry field type names that read as one number or a run of floats. */
internal static class ScalarTypes
{
    public static readonly HashSet<string> Integers = ["u8", "i8", "u16", "i16", "u32", "i32", "u64", "i64", "bool8", "bool32"];
    public static readonly HashSet<string> Floats = ["f16", "f32", "f64"];
    public static readonly HashSet<string> Vectors = ["vec2", "vec3", "vec4", "quat", "mat3x4", "mat4x4", "aligned_vec3"];
}
