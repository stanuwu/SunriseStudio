using System.Buffers.Binary;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Editor;

/**
 * One config of the open entity. Fields are the known rows, Inspection the generic read.
 * Bytes are the payload both were read from; edits apply to them. Editable is true when any field can be edited.
 */
public sealed record ComponentSection(uint Config, uint Primary, string Role, string Name, byte[] Bytes,
    IReadOnlyList<AssetProperty> Fields, AssetInspection? Inspection, string Error = "", bool Editable = false);

/** Which config fields the Components page can write. */
internal static class ComponentFields
{
    public static bool IsEditable(AssetProperty property) => property is
        { Editable: true, Offset: >= 0, CuiOwner: null } && property.Encoding != ScalarEncoding.None;

    public static bool HasEditable(AssetNode node) =>
        node.Properties.Any(IsEditable) || (node.Children?.Any(HasEditable) ?? false);
}

/** The Components page's configs for one open document and overlay. */
public sealed record EntityComponentsState(StudioDocument Document, long Overlay, IReadOnlyList<ComponentSection> Sections);

public sealed partial class StudioWorkspace
{
    /** Name of the field that holds the combatant config's class-0x8080815F tag: display name and action table. */
    public const string LocalDefinitionField = "Local definition";
    private EntityComponentsState? loadedComponents;

    /** The open entity's configs; null until a read for this document and overlay lands. */
    public EntityComponentsState? LoadedComponents => loadedComponents is { } state
        && ReferenceEquals(state.Document, Document) && Catalog?.OverlayRevision == state.Overlay ? state : null;

    /** Reads every config of the open entity on a worker, combatant first; a result for an older overlay is dropped. */
    public void LoadComponents()
    {
        RequireIdle();
        var document = Document ?? throw new InvalidOperationException("Choose an entity.");
        var catalog = Catalog ?? throw new InvalidOperationException("Open your Sunrise installation first.");
        var game = Names?.GameNames;
        long overlay = catalog.OverlayRevision;
        string status = Status;
        BeginWork("Reading components", () =>
        {
            var sections = ComponentIndex.EntityConfigs(document.Bytes).Distinct()
                .Select(config => ReadComponent(catalog, game, config))
                .OrderBy(section => section.Primary == ComponentRoles.CombatantComponent ? 0 : 1).ToArray();
            return () =>
            {
                Status = status;
                if (ReferenceEquals(Catalog, catalog) && catalog.OverlayRevision == overlay)
                    loadedComponents = new(document, overlay, sections);
            };
        }, blocking: false, keepNotice: true);
    }

    /** One bad config keeps its row with the reason, so the other configs still show. */
    private ComponentSection ReadComponent(PackageCatalog catalog, GameNames? game, uint config)
    {
        OperationToken.ThrowIfCancellationRequested();
        string name = ResolveName(config) ?? $"{config:X8}";
        var index = game?.Components;
        uint primary = index?.PrimaryClass(config) ?? 0;
        try
        {
            if (!catalog.TryGetEntry(config, out var entry)) return new(config, 0, "Missing", name, [], [], null, "The config is not in the packages.");
            byte[] bytes = catalog.Read(config);
            if (primary == 0) primary = ComponentRoles.ReadConfig(catalog, bytes, null).Primary;
            string role = index?.ComponentLabel(primary) ?? ComponentRoles.Label(primary) ?? "Unknown component";
            var fields = new List<AssetProperty> { new("Component class", $"0x{primary:X8}") };
            if (index is not null && index.TryGetModelTag(config, out uint model) && model != 0)
                fields.Add(new("Model", $"{ResolveName(model) ?? $"{model:X8}"} (0x{model:X8})"));
            if (index?.Prompt(config) is { } prompt) fields.Add(new("Prompt", prompt));
            // The record's role is known only in the combatant config.
            if (primary == ComponentRoles.CombatantComponent && FindLocalDefinition(catalog, bytes) is { } record)
            {
                uint tag = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(record));
                string? text = ComponentRoles.CombatantName(catalog, tag, game?.Strings);
                fields.Add(new(LocalDefinitionField, $"0x{tag:X8}", record, ScalarEncoding.UInt32, true,
                    $"{(text is null ? "No display name" : $"Display name: {text}")}. Also sets the action table."));
            }
            var inspection = Inspectors.Inspect(new(config, entry.Reference, bytes, name, ResolveName, catalog.ContainsTag, catalog.Read));
            if (role.Length != 0) role = char.ToUpperInvariant(role[0]) + role[1..];
            bool editable = fields.Any(ComponentFields.IsEditable) || inspection.Sections.Any(ComponentFields.HasEditable);
            return new(config, primary, role, name, bytes, fields, inspection, Editable: editable);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or IndexOutOfRangeException
            or KeyNotFoundException or OverflowException or IOException)
        { return new(config, primary, "Unreadable", name, [], [], null, error.Message); }
    }

    /** Offset of LocalDefinition in the ActorGroupSource that Definition names; null unless it names a record. */
    private static int? FindLocalDefinition(PackageCatalog catalog, byte[] bytes)
    {
        int source = TagPointer.TryChecked(bytes, TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition)));
        if (source < 0 || TagView.ClassAt(bytes, source) != ActorGroupSource.Class
            || !TagView.FitsThrough<ActorGroupSource>(bytes, source, nameof(ActorGroupSource.LocalDefinition))) return null;
        int at = source + TagView.FieldOffset<ActorGroupSource>(nameof(ActorGroupSource.LocalDefinition));
        return catalog.TryGetEntry(TagRead.U32(bytes, at), out var entry) && AssetCategories.IsStructured(entry)
            && entry.Reference == ComponentRoles.CombatantNameRecord ? at : null;
    }

    /** Writes one config field as one undo step; the entity stays the open document. */
    public void ApplyComponentEdit(uint configTag, AssetProperty property, string value)
    {
        var state = LoadedComponents ?? throw new InvalidOperationException("The components are not read yet.");
        var section = state.Sections.FirstOrDefault(row => row.Config == configTag && row.Bytes.Length != 0)
            ?? throw new InvalidOperationException("The config is not part of this entity.");
        byte[] edited = AssetEdit.Apply(section.Bytes, property, value);
        if (property.Name == LocalDefinitionField)
        {
            uint tag = BinaryPrimitives.ReadUInt32LittleEndian(edited.AsSpan(property.Offset));
            if (Catalog is not { } catalog || !catalog.TryGetEntry(tag, out var entry) || !AssetCategories.IsStructured(entry)
                || entry.Reference != ComponentRoles.CombatantNameRecord)
                throw new InvalidDataException($"0x{tag:X8} is not a class-0x{ComponentRoles.CombatantNameRecord:X8} local definition.");
        }
        ApplyAssetEdits(new Dictionary<uint, byte[]> { [configTag] = edited }, $"Changed {property.Name}");
    }
}
