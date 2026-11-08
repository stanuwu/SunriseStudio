namespace SunriseStudio.Packages.Registry;

/** A registry with its classes keyed by id and its field type names resolved once. */
public sealed class RegistryIndex
{
    private readonly Dictionary<uint, RegistryClass> classes;
    private readonly string[] typeNames;

    public RegistryIndex(TagRegistry registry)
    {
        Registry = registry;
        classes = registry.Classes.ToDictionary(x => x.Class);
        typeNames = registry.FieldTypes.OrderBy(x => x.Id).Select(x => x.Name).ToArray();
    }

    public TagRegistry Registry { get; }

    public RegistryClass? Class(uint id) => classes.GetValueOrDefault(id);
    public string TypeName(RegistryField field) => typeNames[field.Type];
    /** @return The declared byte width of the field's type, from the type table. */
    public int TypeSize(RegistryField field) => Registry.FieldTypes[field.Type].Size;

    /** @return A field's byte width: its length, its nested class size, or its declared width; 0 when unknown. */
    public int FieldSize(RegistryField field) => field.Length ?? (TypeName(field) == "struct" && field.Struct is { } nested
        ? Class(nested)?.Size ?? 0
        : DeclaredSize(Registry, field));

    /**
     * The width a field states without a length or a nested class: a scalar inline array is count times
     * its element, flags and an enum take their element scalar, anything else its type size.
     */
    public static int DeclaredSize(TagRegistry registry, RegistryField field)
    {
        int Scalar() => field.ElementScalar is { } name && registry.FieldTypes.FirstOrDefault(x => x.Name == name) is { Size: > 0 } type
            ? type.Size
            : 0;
        return registry.FieldTypes[field.Type].Name switch
        {
            "inline_array" when field.Struct is null && field.Count is > 0 and int count => count * Scalar(),
            "flags" or "enum" => Scalar(),
            _ => registry.FieldTypes[field.Type].Size,
        };
    }
}
