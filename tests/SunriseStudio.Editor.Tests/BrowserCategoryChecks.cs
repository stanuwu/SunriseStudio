using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using SunriseStudio.Editor;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

internal static class BrowserCategoryChecks
{
    /** Browser menus retain entity roles after the complete component index is published. */
    internal static void Run(string root)
    {
        const uint first = 0x80A00000;
        string install = Path.Combine(root, "category-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        var strings = Strings(first + 7, "First prompt");
        byte[] model = Component(ComponentRoles.ModelComponent); U(model, 64, first + 8);
        byte[] prompt = Component(ComponentRoles.LightComponent); U(prompt, 64, 0x1234); U(prompt, 80, first + 6);
        byte[] combatant = Combatant(first + 10);
        byte[] record = new byte[16]; U(record, 8, 0x1234);
        var entries = new List<AuthoredPackageEntry>
        {
            new(0x80809C0F, 8 << 9, Entity(first + 1)), new(0x80809C36, 8 << 9, model),
            new(0x80809C0F, 8 << 9, Entity(first + 3)), new(0x80809C36, 8 << 9, Component(ComponentRoles.LightComponent)),
            new(0x80809C0F, 8 << 9, Entity(first + 5)), new(0x80809C36, 8 << 9, prompt),
            new(0x80809A88, 8 << 9, strings.Container), new(0x80809A8A, 8 << 9, strings.Data),
            new(0x8080941E, 8 << 9, Pattern("alpha")), new(0x80809C36, 8 << 9, combatant),
            new(0x8080815F, 8 << 9, record), new(0x80809C0F, 8 << 9, Entity(first + 9)),
            new(0x80800001, 8 << 9, new byte[32])
        };
        for (int i = 0; i < 256; i++) entries.Add(new(0x80809C36, 8 << 9, Component(ComponentRoles.LightComponent)));
        for (int i = 0; i < 10; i++)
        {
            byte[] unknown = Component(0xDEAD0001); U(unknown, 80, first + 6);
            entries.Add(new(0x80809C36, 8 << 9, unknown));
        }
        const uint unnamed = first + 279;
        entries.Add(new(0x80809C0F, 8 << 9, ShortEntity(unnamed + 1)));
        entries.Add(new(0x80809C36, 8 << 9, Component(ComponentRoles.CombatantComponent)));
        const uint otherRecord = unnamed + 2;
        byte[] other = new byte[16]; U(other, 8, 0x5678);
        entries.Add(new(0x8080815F, 8 << 9, other));
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_category_0100_0.pkg"), 0x100, entries);
        using var workspace = new StudioWorkspace
        { CacheOptions = new() { DirectoryPath = Path.Combine(root, "category-cache") } };
        workspace.OpenInstallation(install);
        var watch = Stopwatch.StartNew();
        while (workspace.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(15))
        { workspace.Poll(); Thread.Sleep(1); }
        Require(!workspace.IsBusy && workspace.Error.Length == 0, workspace.Error);
        Require(workspace.Assets.Single(row => row.Entry.TagHash == first).Kind == "Entity / Prop",
            "Model entities lost their Prop category.");
        Require(workspace.Assets.Single(row => row.Entry.TagHash == first + 2).Kind == "Entity / Light",
            "Light entities lost their Light category.");
        Require(new[] { "Entity / Prop", "Entity / Light" }.All(workspace.BrowserKinds.Contains),
            "Entity types are missing from the browser menu.");
        Require(workspace.Assets.Single(row => row.Entry.TagHash == unnamed).Kind == "Enemies and NPCs / Unnamed"
            && workspace.CategoryRows.TryGetValue(StudioCategories.EnemiesCategory, out var enemies)
            && enemies.Any(row => row.Entry.TagHash == unnamed),
            "An ActorGroup entity with no name or object type left the enemies category.");
        workspace.Select(first); Wait(workspace);
        workspace.CreateProject(Path.Combine(root, "category-project"), "Categories"); Wait(workspace);
        workspace.ApplyBytes(Entity(first + 3)); Wait(workspace);
        Require(workspace.Assets.Single(row => row.Entry.TagHash == first).Kind == "Entity / Light",
            "Entity category retained its original config after an overlay edit.");
        BrowserAsset Row(uint tag) => workspace.Assets.Single(row => row.Entry.TagHash == tag);
        void Kind(uint tag, string expected) => Require(Row(tag).Kind == expected, $"Wrong category for {tag:X8}: {Row(tag).Kind}");
        void Edit(uint tag, byte[] bytes)
        {
            workspace.Select(tag); Wait(workspace); workspace.ApplyBytes(bytes); Wait(workspace);
        }
        workspace.Undo(); Wait(workspace); Kind(first, "Entity / Prop");
        workspace.Redo(); Wait(workspace); Kind(first, "Entity / Light");
        workspace.Revert(first); Wait(workspace); Kind(first, "Entity / Prop");
        var untouched = Row(first + 2);
        Edit(first + 1, Component(ComponentRoles.LightComponent));
        Kind(first, "Entity / Light");
        Require(!workspace.BrowserKinds.Contains("Entity / Prop") && ReferenceEquals(untouched, Row(first + 2)),
            "Category counts or unrelated rows changed after a config edit.");
        workspace.Undo(); Wait(workspace); Kind(first, "Entity / Prop");
        Edit(first, Entity(first + 3));
        var detached = Row(first);
        Edit(first + 1, Component(ComponentRoles.TriggerShapeComponent));
        Require(ReferenceEquals(detached, Row(first)), "A detached config still invalidated its previous entity.");
        workspace.Undo(); Wait(workspace); workspace.Undo(); Wait(workspace);
        Require(Row(first).Name.Contains("Alpha", StringComparison.OrdinalIgnoreCase), "Initial behavior name missing.");
        Edit(first + 8, Pattern("beta"));
        Require(Row(first).SearchText.Contains("Beta", StringComparison.OrdinalIgnoreCase)
            && !Row(first).Name.Contains("Alpha", StringComparison.OrdinalIgnoreCase), "Edited behavior name stayed stale.");
        Edit(first + 1, Component(ComponentRoles.ModelComponent));
        Require(!Row(first).Name.Contains("Beta", StringComparison.OrdinalIgnoreCase), "Removed behavior name survived.");
        Edit(first + 7, Strings(first + 7, "Second prompt").Data);
        Require(Row(first + 4).Name.Contains("Second prompt") && Row(first + 11).Name.Contains("Second prompt"),
            "English data edits did not refresh scoped prompts and global combatant names.");
        Require(!Row(first + 4).SearchText.Contains("First prompt"), "Search kept a removed prompt.");
        Kind(first + 4, "Entity / Interactable"); Kind(first + 11, "Enemies and NPCs / Second prompt");
        workspace.Select(first + 11); Wait(workspace); workspace.LoadComponents(); Wait(workspace);
        var components = workspace.LoadedComponents!;
        Require(components.Sections[0].Primary == ComponentRoles.CombatantComponent,
            "The combatant config is not the first component section.");
        var definition = components.Sections[0].Fields.SingleOrDefault(field =>
            field.Name == StudioWorkspace.LocalDefinitionField && field.Offset == LocalDefinitionAt);
        Require(definition is not null, "The local definition field is missing from the combatant config.");
        try
        {
            workspace.ApplyComponentEdit(first + 9, definition!, $"0x{first + 8:X8}");
            Require(false, "A tag of the wrong class was accepted as a local definition.");
        }
        catch (InvalidDataException) { }
        workspace.ApplyComponentEdit(first + 9, definition!, $"0x{otherRecord:X8}"); Wait(workspace);
        Require(Row(first + 11).Kind.StartsWith("Enemies and NPCs / ", StringComparison.Ordinal)
            && Row(first + 11).Kind != "Enemies and NPCs / Second prompt", "A new local definition did not rename the enemy.");
        workspace.Undo(); Wait(workspace); Kind(first + 11, "Enemies and NPCs / Second prompt");
        int revision = workspace.AssetRevision;
        Edit(first + 12, [1, 2, 3, 4]);
        Require(workspace.AssetRevision == revision, "Unrelated payload edit refreshed component categories.");
        const uint inferred = first + 269;
        string derived = Row(inferred).Kind;
        Require(derived.Contains(" / uses "), "Derived component label fixture was not recognized.");
        workspace.ApplyAssetEdits(new Dictionary<uint, byte[]>
        { [inferred] = Component(0xDEAD0001), [inferred + 1] = Component(0xDEAD0001) }); Wait(workspace);
        Require(Row(inferred + 9).Kind == "Entity component / Other" && !workspace.BrowserKinds.Contains(derived),
            "Changed reference frequency did not refresh other configs of the same primary class.");
        workspace.Undo(); Wait(workspace);
        Require(Row(inferred + 9).Kind == derived, "Undo did not restore the shared derived component label.");
        string saved = workspace.Project!.ManifestPath;
        workspace.CloseProject(); Wait(workspace);
        Kind(first, "Entity / Prop");
        Require(Row(first).Name.Contains("Alpha", StringComparison.OrdinalIgnoreCase)
            && Row(first + 4).Name.Contains("First prompt"), "Closing a project retained overlay names.");
        workspace.OpenProject(saved); Wait(workspace);
        Require(Row(first + 4).Name.Contains("Second prompt"), "Opening a project did not refresh dependency names.");
        workspace.CloseProject(); Wait(workspace);
        using var cached = new StudioWorkspace { CacheOptions = workspace.CacheOptions };
        cached.OpenInstallation(install); Wait(cached);
        Require(cached.Names!.CacheHit, "Category fixture did not restore its installed name cache.");
        Require(cached.Assets.Single(row => row.Entry.TagHash == first + 11).Kind == "Enemies and NPCs / First prompt",
            "The name cache lost the ActorGroup config of an enemy.");
        cached.Select(first + 1); Wait(cached);
        cached.CreateProject(Path.Combine(root, "category-cached-project"), "Cached categories"); Wait(cached);
        cached.ApplyBytes(Component(ComponentRoles.LightComponent)); Wait(cached);
        Require(cached.Assets.Single(row => row.Entry.TagHash == first).Kind == "Entity / Light",
            "Cached dependency links did not invalidate the entity.");
        var catalog = cached.Catalog!;
        var index = cached.Names.GameNames!;
        long before = catalog.ReadStatistics.EntryReads;
        index.RefreshComponents([first + 1], out var affected);
        long reads = catalog.ReadStatistics.EntryReads - before;
        Require(reads <= 3 && affected.SetEquals([first, first + 1]), "A local config refresh reread unrelated payloads.");
        Console.WriteLine($"editor_categories overlay=pass config=pass dependencies=pass names=pass components=pass close=pass cache=pass reads={reads}");
    }

    private static void Wait(StudioWorkspace workspace)
    {
        var elapsed = Stopwatch.StartNew();
        while (workspace.IsBusy && elapsed.Elapsed < TimeSpan.FromSeconds(15))
        { workspace.Poll(); Thread.Sleep(1); }
        workspace.Poll();
        Require(!workspace.IsBusy && workspace.Error.Length == 0, workspace.Error);
    }

    private static byte[] Entity(uint component)
    {
        byte[] bytes = new byte[176];
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0x10), 1);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0x18), 128 - 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(124), 0x80809FBD);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(136), 0x80809C04);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(144), component);
        return bytes;
    }

    /** An entity too short for the object type byte at +150. */
    private static byte[] ShortEntity(uint component)
    {
        byte[] bytes = new byte[96];
        Q(bytes, 0x10, 1); Q(bytes, 0x18, 0x30 - 0x18);
        U(bytes, 0x2C, 0x80809FBD); U(bytes, 0x38, 0x80809C04); U(bytes, 0x40, component);
        return bytes;
    }

    private static byte[] Component(uint primary)
    {
        byte[] bytes = new byte[96];
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0x10), 0x30 - 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x2C), primary);
        return bytes;
    }

    // The ActorGroupSource object of a combatant fixture starts after the 96-byte config body.
    private const int SourceStart = 96;
    private static int LocalDefinitionAt => SourceStart + TagView.FieldOffset<ActorGroupSource>(nameof(ActorGroupSource.LocalDefinition));

    /** A combatant config as installed: Definition names an ActorGroupSource whose LocalDefinition is the record. */
    private static byte[] Combatant(uint record)
    {
        byte[] bytes = new byte[SourceStart + Unsafe.SizeOf<ActorGroupSource>()];
        Component(ComponentRoles.CombatantComponent).CopyTo(bytes, 0);
        int definition = TagView.FieldOffset<SEntityConfig>(nameof(SEntityConfig.Definition));
        Q(bytes, definition, SourceStart - definition);
        U(bytes, SourceStart - TagView.ClassWordSize, ActorGroupSource.Class);
        U(bytes, LocalDefinitionAt, record);
        return bytes;
    }

    private static byte[] Pattern(string name)
    {
        byte[] text = Encoding.UTF8.GetBytes($"content/{name}.pattern.tft");
        byte[] bytes = new byte[text.Length + 12]; U(bytes, 0, 0x80800065); Q(bytes, 4, text.Length);
        text.CopyTo(bytes, 12); return bytes;
    }

    private static (byte[] Container, byte[] Data) Strings(uint dataTag, string text)
    {
        byte[] container = new byte[80]; U(container, 24, dataTag); Array(container, 8, 48, 0x80800070); U(container, 64, 0x1234);
        byte[] data = new byte[288]; Array(data, 8, 112, 0x80809A90); Array(data, 72, 176, 0x80809A8E);
        Q(data, 136, 224 - 136); U(data, 144, 0x811C9DC5);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(148), (ushort)Encoding.UTF8.GetByteCount(text));
        Q(data, 192, 128 - 192); Q(data, 200, 1); Encoding.UTF8.GetBytes(text).CopyTo(data, 224);
        return (container, data);
    }

    private static void Array(byte[] bytes, int field, int header, uint type)
    { Q(bytes, field, 1); Q(bytes, field + 8, header - field - 8); U(bytes, header - 4, 0x80809FBD); Q(bytes, header, 1); U(bytes, header + 8, type); }
    private static void U(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Q(byte[] bytes, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), value);

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
