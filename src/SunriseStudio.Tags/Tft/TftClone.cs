using System.Buffers.Binary;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Graph;
using SunriseStudio.Packages.Registry;

namespace SunriseStudio.Tags.Tft;

/**
 * Installed tags as new `.tft` files: a reference to another cloned tag becomes its project name,
 * by tag or by wide hash; any other one stays `#0x` or `##0x`.
 */
public static class TftClone
{
    /**
     * @param members The installed tags to clone, each to its own file.
     * @param name Project tag name per installed tag; it must end with the tag type.
     */
    public static IReadOnlyList<TftFile> Clone(IReadOnlyCollection<uint> members, ITagSource source, RegistryIndex schema, Func<uint, string> name)
    {
        var entries = source.Entries.Where(x => members.Contains(x.TagHash)).ToDictionary(x => x.TagHash);
        var names = members.ToDictionary(x => x, name);
        var files = new List<TftFile>();
        foreach (uint tag in members.Order())
        {
            var entry = entries.GetValueOrDefault(tag) ?? throw new InvalidDataException($"0x{tag:X8} is not installed.");
            byte[] installed = source.Read(tag), data = installed.ToArray();
            var document = TagDocument.Parse(installed, entry.Reference, schema);
            if (document.Problems.Count > 0) throw new InvalidDataException($"0x{tag:X8}: {string.Join("; ", document.Problems)}.");
            var references = new Dictionary<int, TftReference>();
            foreach (var reference in document.References.Where(x => !x.IsNull && !references.ContainsKey(x.Offset)))
            {
                if (reference.Kind == TagReferenceKind.WideRef)
                {
                    references[reference.Offset] = new(reference.Offset, TftReferenceKind.WideRef, 0,
                        source.ResolveWide(reference.Value) is { } wide && names.TryGetValue(wide, out string? member) ? member
                            : TftNames.InstalledWide(reference.Value));
                    data.AsSpan(reference.Offset, 8).Clear();
                    continue;
                }
                uint target = (uint)reference.Value;
                references[reference.Offset] = new(reference.Offset, TftReferenceKind.TagRef, 0,
                    names.TryGetValue(target, out string? project) ? project : TftNames.Installed(target));
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(reference.Offset), TftFile.TagRefPlaceholder);
            }
            string tagName = names[tag];
            files.Add(new TftFile
            {
                Name = tagName, TagType = TftNames.TagTypeOf(tagName), ClassId = entry.Reference, TypeInfo = entry.TypeInfo,
                BuildHash = TftFile.HashBuildId(schema.Registry.BuildId), Layout = TftLayout.Extract(schema, data, entry.Reference),
                Data = data, References = references.Values.OrderBy(x => x.Offset).ToList(), Info = TftFile.CloneInfo(tag),
            });
        }
        return files;
    }
}
