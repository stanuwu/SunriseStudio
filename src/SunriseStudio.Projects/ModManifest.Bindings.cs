using System.Runtime.CompilerServices;
using System.Text.Json;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Projects;

public static partial class ModManifest
{
    /** Throws when a compiled config site is malformed, undeclared or overlaps another site. */
    public static void RequireBindings(ModMetadata metadata, IReadOnlyList<ModConfigSite> bindings)
    {
        if (bindings.Count > ConfigBindingLimits.MaximumSites)
            throw new InvalidDataException("A mod can bind at most 256 config sites.");
        foreach (var binding in bindings)
        {
            if (binding is null || binding.Class != CuiResource.Class || !TagHashes.IsTag(binding.Tag)
                || TagHashes.Package(binding.Tag) > ConfigBindingLimits.MaximumPackageId
                || binding.Size < Unsafe.SizeOf<CuiResource>() || binding.Size > ConfigBindingLimits.MaximumEntryBytes
                || binding.Offset < 0
                || !Enum.IsDefined(binding.Type)) throw new InvalidDataException("Invalid compiled config binding.");
            StudioProject.ValidateHash(binding.SourceSha256);
            var setting = metadata.Config?.SingleOrDefault(value => value.Key == binding.Key);
            if (setting is null || setting.Type != binding.Type)
                throw new InvalidDataException("A compiled config binding has no matching declaration.");
            int width = binding.Type == ModConfigType.Bool ? 1 : 4;
            if (binding.Offset > binding.Size - width) throw new InvalidDataException("A config site leaves its entry.");
            if (binding.Profile == ConfigBindingLimits.PropertyProfile)
            {
                if (binding.PropertyOffset is not >= 0 || binding.PropertyOffset > binding.Size - Unsafe.SizeOf<CuiPropertyRow>()
                    || binding.BindingOffset is not null || binding.Type == ModConfigType.Int
                    || (binding.Type == ModConfigType.Bool ? binding.PoolOffset != BoolPool
                        : binding.PoolOffset != FloatPoolA && binding.PoolOffset != FloatPoolB))
                    throw new InvalidDataException("Invalid property config binding.");
            }
            else if (binding.Profile == ConfigBindingLimits.ComparisonProfile)
            {
                if (binding.BindingOffset is not >= 0 || binding.BindingOffset > binding.Size - Unsafe.SizeOf<CuiBindingRow>()
                    || binding.PropertyOffset is not null || binding.PoolOffset is not null || binding.Type == ModConfigType.Bool)
                    throw new InvalidDataException("Invalid comparison config binding.");
            }
            else throw new InvalidDataException("Unsupported config binding profile.");
        }
        foreach (var group in bindings.GroupBy(value => value.Tag))
        {
            var first = group.First(); int end = -1;
            foreach (var site in group.OrderBy(value => value.Offset))
            {
                if (site.Class != first.Class || site.Size != first.Size || site.SourceSha256 != first.SourceSha256
                    || site.Offset < end) throw new InvalidDataException("Config sites overlap or disagree about their source.");
                end = checked(site.Offset + (site.Type == ModConfigType.Bool ? 1 : 4));
            }
        }
    }

    // Value pools a property config site may use.
    private static readonly int BoolPool = TagView.FieldOffset<CuiResource>("ValuePool.BoolValues");
    private static readonly int FloatPoolA = TagView.FieldOffset<CuiResource>("ValuePool.PoolFloatsA");
    private static readonly int FloatPoolB = TagView.FieldOffset<CuiResource>("ValuePool.PoolFloatsBNormalized");

    /** Writes the configBindings array sorted by tag and offset; nothing when empty. */
    private static void WriteBindings(Utf8JsonWriter json, IReadOnlyList<ModConfigSite> bindings)
    {
        if (bindings.Count == 0) return;
        json.WriteStartArray("configBindings");
        foreach (var site in bindings.OrderBy(value => value.Tag).ThenBy(value => value.Offset))
        {
            json.WriteStartObject();
            json.WriteString("key", site.Key); json.WriteString("profile", site.Profile);
            json.WriteNumber("tag", site.Tag); json.WriteNumber("class", site.Class);
            json.WriteNumber("size", site.Size); json.WriteString("sourceSha256", site.SourceSha256);
            if (site.PropertyOffset is int property) json.WriteNumber("propertyOffset", property);
            if (site.PoolOffset is int pool) json.WriteNumber("poolOffset", pool);
            if (site.BindingOffset is int binding) json.WriteNumber("bindingOffset", binding);
            json.WriteNumber("offset", site.Offset);
            json.WriteString("type", site.Type switch { ModConfigType.Int => "int", ModConfigType.Float => "float", _ => "bool" });
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }
}
