using System.Collections.ObjectModel;
using System.Text;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

public enum RendererNameKind { Scope, Technique }

public sealed record RendererNameEntry(uint TagHash, uint ClassHash, string Name,
    RendererNameKind Kind, uint RootTag, int Ordinal, int RowOffset, int NameOffset);

public sealed record RendererNameConflict(uint TagHash, IReadOnlyList<string> Names,
    string? PackageName);

/** Renderer row names remain separate from package names and ambiguous tag aliases. */
public sealed partial class RenderNames
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Dictionary<uint, string> names = [];
    public IReadOnlyDictionary<uint, string> Names { get; }
    public IReadOnlyList<RendererNameEntry> Entries { get; }
    public IReadOnlyList<RendererNameConflict> Conflicts { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public int UnsetRowCount { get; }

    // Native stage rows use indexes 0 through 22 in this build.
    public static IReadOnlyList<string> StageNames { get; } = Array.AsReadOnly(new[]
    {
        "generate_gbuffer", "decals", "investment_decals", "shadow_generate", "lighting apply",
        "light probe apply", "decals_additive", "transparents", "distortion",
        "light_shaft_occlusion", "skin_prepass", "lens_flares", "depth_prepass",
        "water_reflection", "postprocess_transparent_stencil", "impulse", "reticle",
        "water_ripples", "mask_sun_light", "volumetrics", "cubemaps", "postprocess_screen",
        "world_forces"
    });

    public static string? GetStageName(int stage) =>
        (uint)stage < StageNames.Count ? StageNames[stage] : null;

    public string? GetTagName(uint tag) => names.GetValueOrDefault(tag);

    /** Each accepted row must target the live class required by its native table. */
    public RenderNames(PackageCatalog catalog, uint? rootTag = null, CancellationToken token = default)
    {
        var entries = new List<RendererNameEntry>();
        var diagnostics = new List<string>();
        var unset = 0;
        var roots = rootTag.HasValue ? [rootTag.Value]
            : catalog.Entries.Where(e => e.Reference == SRendererRoot.Class).Select(e => e.TagHash).ToArray();
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            if (!catalog.TryGetEntry(root, out var rootEntry) || rootEntry.Reference != SRendererRoot.Class)
                throw new InvalidDataException($"Renderer root 0x{root:X8} has the wrong live class.");
            var data = catalog.Read(root);
            var strings = StringRegions(data);
            foreach (var (kind, targetClass) in new[]
            {
                (RendererNameKind.Scope, SScope.Class),
                (RendererNameKind.Technique, STechnique.Class)
            })
            {
                var rows = NameRows(data, kind);
                for (var ordinal = 0; ordinal < rows.Length; ++ordinal)
                {
                    token.ThrowIfCancellationRequested();
                    var (row, tag) = rows[ordinal];
                    if (tag is 0 or uint.MaxValue) { unset++; continue; }
                    if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != targetClass)
                    {
                        diagnostics.Add($"Renderer {kind} row {ordinal} in 0x{root:X8} has an invalid tag 0x{tag:X8}.");
                        continue;
                    }
                    if (kind == RendererNameKind.Scope
                        && TagRead.U32(catalog.Read(tag), TagView.FieldOffset<SScope>("Common.ScopeIndex")) != ordinal)
                    {
                        diagnostics.Add($"Renderer scope 0x{tag:X8} disagrees with row index {ordinal}.");
                        continue;
                    }
                    var offset = NameOffset(data, kind, row);
                    var name = ReadName(data, strings, offset);
                    entries.Add(new RendererNameEntry(tag, targetClass, name, kind, root,
                        ordinal, row, offset));
                }
            }
        }
        var conflicts = new List<RendererNameConflict>();
        foreach (var group in entries.GroupBy(e => e.TagHash))
        {
            var aliases = group.Select(e => e.Name).Distinct(StringComparer.Ordinal).ToArray();
            var packageName = catalog.GetName(group.Key);
            if (aliases.Length != 1 || packageName != null && packageName != aliases[0])
                conflicts.Add(new RendererNameConflict(group.Key, Array.AsReadOnly(aliases), packageName));
            else names.Add(group.Key, aliases[0]);
        }
        Names = new ReadOnlyDictionary<uint, string>(names);
        Entries = entries.AsReadOnly();
        Conflicts = conflicts.AsReadOnly();
        Diagnostics = diagnostics.AsReadOnly();
        UnsetRowCount = unset;
    }

    /** Each row of one renderer name table: where it sits and the tag it names. */
    private static (int Row, uint Tag)[] NameRows(byte[] data, RendererNameKind kind)
    {
        if (kind == RendererNameKind.Scope)
        {
            ref readonly var scopeNames = ref TagView.At<TagArray<UnknownRendererScopeRow>>(data,
                TagView.FieldOffset<SRendererRoot>(nameof(SRendererRoot.ScopeNames)));
            var scopes = TagView.Rows(data, in scopeNames, UnknownRendererScopeRow.Class);
            var result = new (int, uint)[scopes.Length];
            for (var i = 0; i < scopes.Length; ++i) result[i] = (TagView.OffsetOf(data, in scopes[i]), scopes[i].Scope);
            return result;
        }
        ref readonly var techniqueNames = ref TagView.At<TagArray<UnknownRendererTechniqueRow>>(data,
            TagView.FieldOffset<SRendererRoot>(nameof(SRendererRoot.TechniqueNames)));
        var techniques = TagView.Rows(data, in techniqueNames, UnknownRendererTechniqueRow.Class);
        var rows = new (int, uint)[techniques.Length];
        for (var i = 0; i < techniques.Length; ++i) rows[i] = (TagView.OffsetOf(data, in techniques[i]), techniques[i].Technique);
        return rows;
    }

    /** Where a row's name pointer lands; a null pointer lands on the row itself. */
    private static int NameOffset(byte[] data, RendererNameKind kind, int row) => kind == RendererNameKind.Scope
        ? TagView.TargetOffset(data, in TagView.At<UnknownRendererScopeRow>(data, row).Name) ?? row
        : TagView.TargetOffset(data, in TagView.At<UnknownRendererTechniqueRow>(data, row).Name) ?? row;

    /** Name pointers must stay inside a bounded package string block. */
    private static (int Start, int Length)[] StringRegions(byte[] data) =>
        TagBlock.Find(data, SInlineString.Class).Select(block => (block.Start, block.Length)).ToArray();

    /** Authored pointers may share a string suffix but must end inside the owning block. */
    private static string ReadName(byte[] data, (int Start, int Length)[] strings, int offset)
    {
        var owners = strings.Where(s => offset >= s.Start && offset < s.Start + s.Length).ToArray();
        if (owners.Length != 1)
            throw new InvalidDataException("Renderer name pointer has no unique string block.");
        var tail = data.AsSpan(offset, owners[0].Start + owners[0].Length - offset);
        var end = tail.IndexOf((byte)0);
        if (end <= 0 || end > 1024)
            throw new InvalidDataException("Renderer name is empty or has no bounded terminator.");
        var name = StrictUtf8.GetString(tail[..end]);
        if (name.Any(char.IsControl)) throw new InvalidDataException("Renderer name has control bytes.");
        return name;
    }
}
