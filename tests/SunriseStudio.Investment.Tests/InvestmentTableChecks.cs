using System.Buffers.Binary;
using System.Text;
using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Live;
using SunriseStudio.Packages;
using Views = SunriseStudio.Tags.Generated;

/** Root-slot readers on a synthetic root with every table. */
internal static class InvestmentTableChecks
{
    private const uint First = 0x80A00000;
    // Fixture entry order; each tag is First plus its position in the package.
    private const int RootEntry = 0, ItemIndexEntry = 1, DisplayIndexEntry = 2, FlagSlotEntry = 3,
        ValueSlotEntry = 4, FlagMapEntry = 5,
        ValueMapEntry = 6, SharedEntry = 7, PoolEntry = 8, ProgressionEntry = 9, ObjectiveEntry = 10,
            FirstItemEntry = 11, ItemCount = 6,
        BucketEntry = FirstItemEntry + ItemCount, BankEntry = BucketEntry + 1, StringContainerEntry = BucketEntry + 2,
        StringDataEntry = BucketEntry + 3, ObjectiveTextEntry = BucketEntry + 4;
    // Sunrise reads the bucket table by its slot whatever its class, so the fixture gives it a class no reader names.
    private const uint AnyBucketClass = 0x80801717;
    // The Pursuits bucket id, which Sunrise names, and the fixture's invented first slot and slot count for it.
    private const byte PursuitsId = 40;
    private const int PursuitsFirst = 120, PursuitsSlots = 24;
    // String bank index, container, data and row classes of this build, and the variable hash a literal text part carries.
    private const uint BankIndexClass = 0x80805F98, StringContainerClass = 0x80809A88, StringDataClass = 0x80809A8A,
        StringHashClass = 0x80800070, StringPartClass = 0x80809A90, StringCombinationClass = 0x80809A8E, LiteralPart = 0x811C9DC5;
    // The objective display table's entry and row classes and its row size.
    private const uint ObjectiveDisplayClass = 0x808059EC, ObjectiveDisplayRowClass = 0x808059F0;
    private const int ObjectiveDisplayStride = 64;
    private const string FixtureObjectiveText = "Fixture targets defeated";
    // The reward site the fixture's pool entries name in root slot 84.
    private const ushort FixtureSite = 3;
    // A flag slot the account mapping table names (row 3), and item 0's maximum stack.
    private const ushort AccountFlagSlot = 4;
    private const uint ItemZeroStack = 3;

    /** A catalog with no root still loads; its tables report why they are empty. */
    public static void NoRoot(InvestmentCatalog catalog, uint itemTag, ReadOnlyMemory<byte> item)
    {
        var tables = catalog.Tables();
        Check(tables.Root is null && tables.Pools.Count == 0
            && tables.Diagnostics.Contains("This installation has no investment root."),
            "A catalog without a root did not report it.");
        Check(catalog.ReadItem(itemTag, item) is { Pursuit: null, Wrapper: null, Diagnostics.Count: 0 },
            "An item without blocks gained a pursuit.");
    }

    public static void Run(string parent)
    {
        string install = Path.Combine(parent, "tables-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        var items = Items();
        var strings = Strings(First + StringDataEntry, [FixtureObjectiveText]);
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_tables_fixture_0100_0.pkg"), 0x100,
        [
            new(Views.InvestmentRoot.Class, 8 << 9, Root()),
            new(0x80807BE5, 8 << 9,
                Index(Enumerable.Range(0,
                    ItemCount).Select(i => ((uint)(0x1000 + i), First + FirstItemEntry + (uint)i)).ToArray())),
            new(0x80805CDA, 8 << 9, Index([])),
            new(Views.UnlockFlagTable.Class, 8 << 9, FlagSlots()),
            new(Views.UnlockValueTable.Class, 8 << 9, ValueSlots()),
            new(Views.C80807d36.Class, 8 << 9, Maps(0x80807D48, [0, 5, 2, 4], [], [1, 5], [])),
            new(Views.C80807c80.Class, 8 << 9, Maps(0x80807C8D, [1], [0, 2, 3, 4], [], [])),
            new(Views.RewardExpressionTable.Class, 8 << 9, Shared()),
            new(Views.RewardPoolTable.Class, 8 << 9, Pools()),
            new(Views.C80807cd9.Class, 8 << 9, Progressions()),
            new(Views.ObjectiveTable.Class, 8 << 9, Objectives()),
            .. items.Select(item => new AuthoredPackageEntry(InvestmentCatalog.ItemClass, 8 << 9, item)),
            new(AnyBucketClass, 8 << 9,
                Buckets(3, (PursuitsId, PursuitsFirst, PursuitsSlots, 0, 0, 0), (10, 0, 50, 1, 1, 0), (12, 0, 6, 2, 0, 1))),
            new(BankIndexClass, 8 << 9, Banks(First + StringContainerEntry)),
            new(StringContainerClass, 8 << 9, strings.Container), new(StringDataClass, 8 << 9, strings.Data),
            new(ObjectiveDisplayClass, 8 << 9, ObjectiveDisplays())
        ]);
        using var packages = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        var sources = new InvestmentSources(Items: First + ItemIndexEntry, Displays: First + DisplayIndexEntry,
            StringBanks: First + BankEntry,
            ObjectiveDisplays: First + ObjectiveTextEntry);
        var catalog = InvestmentCatalog.Load(packages, sources: sources);
        Check(!catalog.TablesLoaded, "Load read the root-slot tables.");
        var tables = catalog.Tables();
        Check(catalog.TablesLoaded && ReferenceEquals(tables, catalog.Tables()), "Tables did not load once per catalog.");
        var root = tables.Root ?? throw new InvalidDataException("The root sweep missed the fixture root.");
        Check(root.Tag == First + RootEntry && root.SlotCount == 115, "The root sweep found another root.");
        Check(root.ReadSlot(packages, InvestmentRootSlots.Classes,
            Views.CharacterClassTable.Class).State == InvestmentSlotState.WrongClass
            && tables.Diagnostics.Contains("Unlock slots: Slot 12 has an unexpected class."), "A wrong slot class was accepted.");
        Check(tables.Slot84.State == InvestmentSlotState.Omitted, "Slot 84 was not omitted.");
        Expressions(tables);
        Bindings(tables);
        ProgressSources(tables);
        Pools(tables);
        Pass(tables);
        Pursuits(catalog, packages, tables);
        ObjectiveText(tables);
        ObjectiveTextRefusals(parent);
        ItemCatalogChecks.Run(catalog);
        Edits(catalog, packages, tables);
        RewardEdits(catalog, tables);
        BucketTable(tables);
        BucketRefusals(parent);
        Shapes(packages, tables);
        Progress(catalog, packages, tables);
        TruncatedItems(parent);
        Ambiguous(parent);
        Console.WriteLine($"investment_tables root=sweep slots={root.SlotCount} pools={tables.Pools.Count} "
            + $"progressions={tables.Progressions.Count} objectives={tables.Objectives.Count} diagnostics={tables.Diagnostics.Count}");
    }

    private static void Expressions(InvestmentTables tables)
    {
        var unlocks = tables.Unlocks;
        Check(unlocks.SharedCount == 50 && unlocks.Shared(5).IsEmpty
            && unlocks.Shared(9).Instructions.Count == 1, "Shared rows did not load.");
        Check(unlocks.GraphDiagnostics.Contains("1 shared expression references leave the table (first in row 4).")
            && unlocks.GraphDiagnostics.Contains("1 shared expression references name an empty row (first in row 6).")
            && unlocks.GraphDiagnostics.Any(row => row is "Shared expressions form a cycle: 2, 3." or "Shared expressions form a cycle: 3, 2."),
            "Shared graph problems were not all reported: " + string.Join(" | ", unlocks.GraphDiagnostics));
        InvestmentExpression Reference(uint row) => new(0, [new(InvestmentUnlocks.SharedOpcode, row, 0)]);
        Check(unlocks.Depth(Reference(1)) == 2 && unlocks.Depth(Reference(2)) == -1 && unlocks.Depth(Reference(4)) == -1
            && unlocks.Depth(Reference(5)) == -1 && unlocks.Depth(Reference(6)) == -1 && unlocks.Depth(Reference(99)) == -1,
            "Shared depths ignored a cycle or a broken reference.");
        Check(unlocks.Depth(Reference(8)) == 40 && unlocks.Problems(Reference(8))
            .SequenceEqual(["Sunrise refuses expressions nested deeper than 32 shared rows."]),
                "A 40-deep chain was refused or not noted.");
        Check(unlocks.Format(Reference(1)) == "shared #1" && unlocks.Format(unlocks.Shared(1)) == "shared #0 >= 1",
            "A balanced shared row did not print infix.");
        Check(unlocks.Format(unlocks.Shared(7)) == "op 19 (0), flag Account flag row 0, and"
            && unlocks.Problems(unlocks.Shared(7)).SequenceEqual(["Studio does not decode opcode 19."]),
            "An undecoded opcode printed infix.");
        Check(unlocks.Format(Reference(2)) == "shared #2 (missing, empty or cyclic)"
            && unlocks.Format(new InvestmentExpression(0, [])) == "None",
            "A cyclic reference printed as a value.");
        Check(unlocks.Format(new InvestmentExpression(0,
            [new(InvestmentUnlocks.SharedOpcode, 1, 0), new(2, 0, 0), new(2, 0, 0), new(4, 0, 0)]))
                == "shared #1, not, not, and", "A postfix listing did not name a shared row as infix does.");
    }

    private static void Bindings(InvestmentTables tables)
    {
        var unlocks = tables.Unlocks;
        Check(unlocks.FlagLabel(0) is { Text: "Account flag row 0", Proven: true }
            && unlocks.FlagLabel(1).Text == "Character flag row 0"
            && unlocks.ValueLabel(0).Text == "Character value row 0" && unlocks.ValueLabel(1).Text == "Account value row 0",
            "Agreeing slots lost their bank labels.");
        Check(tables.Diagnostics.Contains("2 unlock slots disagree with their mapping table; their labels show both rows.")
            && unlocks.FlagLabel(2) is { Proven: false, Text: "Account flag row 1" } label
            && label.Detail.EndsWith("names row 2", StringComparison.Ordinal),
            "A forced disagreement was resolved silently.");
        Check(unlocks.FlagLabel(3) is { Text: "Computed flag 0xABCD1234", Proven: false }
            && unlocks.ValueLabel(2) is { Text: "Item value (aggregation 2)", Proven: false }
            && unlocks.FlagLabel(40).Text == "Flag slot 40", "Unmapped kinds were given a bank.");
        LiveUnlockSource? Of(uint opcode, uint slot) => LiveUnlockSources.Of(unlocks, new(opcode, slot, 0));
        const uint flag = InvestmentUnlocks.FlagOpcode, value = InvestmentUnlocks.ValueOpcode;
        Check(Of(flag, 0) == new LiveUnlockSource(LiveUnlockKind.Flag, LiveUnlockBank.Account, 0)
            && Of(flag, 1) == new LiveUnlockSource(LiveUnlockKind.Flag, LiveUnlockBank.Character, 0)
            && Of(flag, 4) == new LiveUnlockSource(LiveUnlockKind.Flag, LiveUnlockBank.Account, 3)
            && Of(value, 0) == new LiveUnlockSource(LiveUnlockKind.Value, LiveUnlockBank.Character, 0)
            && Of(value, 1) == new LiveUnlockSource(LiveUnlockKind.Value, LiveUnlockBank.Account, 0),
            "Proven slots did not bind to their saved rows.");
        Check(Of(flag, 2) is null && Of(flag, 3) is null && Of(flag, 40) is null && Of(value, 2) is null
            && Of(InvestmentUnlocks.ConstantOpcode, 0) is null,
            "An unproven, computed or outside slot, or a constant, bound to a saved row.");
        // Slot 5's own row is character row 1, but the account table names it first, and Sunrise binds the first table naming a slot.
        Check(unlocks.FlagLabel(5).Proven && Of(flag, 5) is null,
            "A slot Sunrise binds to another bank bound to its label's row.");
    }

    /**
     * What an objective's progress reads once shared rows are inlined as Sunrise binds it: fixture objectives 0-7, then
     * expressions on objective 7's row through each kind of shared row. The fixture has no class table, so no class flag.
     */
    private static void ProgressSources(InvestmentTables tables)
    {
        const uint flag = InvestmentUnlocks.FlagOpcode, value = InvestmentUnlocks.ValueOpcode,
            shared = InvestmentUnlocks.SharedOpcode;
        InvestmentProgressSource Of(params (uint Opcode,
            uint Operand)[] instructions) => tables.ProgressSource(tables.Objectives[7] with
            { Source = new(0, [.. instructions.Select(row => new InvestmentUnlockInstruction(row.Opcode, row.Operand, 0))]) });
        var kinds = tables.Objectives.Select(objective => tables.ProgressSource(objective).Kind).ToArray();
        Check(kinds.SequenceEqual([InvestmentProgressKind.External, InvestmentProgressKind.Flag,
            InvestmentProgressKind.Unreadable,
                InvestmentProgressKind.Unreadable, InvestmentProgressKind.Value, InvestmentProgressKind.Value,
                    InvestmentProgressKind.Several,
                InvestmentProgressKind.Other]), "Fixture objectives were classified wrongly: " + string.Join(", ", kinds));
        Check(tables.ProgressSource(tables.Objectives[1]).Instruction is { Opcode: flag, Operand: 6 }
            && tables.ProgressSource(tables.Objectives[5]).Instruction is { Opcode: value, Operand: 4 },
            "A one-source objective did not name the instruction it binds to, through a shared row included.");
        Check(Of((shared, 0)) is { Kind: InvestmentProgressKind.Flag, Instruction.Operand: 0 }
            && Of((shared, 49)).Kind == InvestmentProgressKind.External && Of((shared, 48)).Kind == InvestmentProgressKind.Value
            && Of((shared, 1)).Kind == InvestmentProgressKind.Several
            && Of((value, 1), (value, 3), (13, 0)).Kind == InvestmentProgressKind.Several,
            "Shared rows were not inlined before counting instructions.");
        // The character value map names slot 2, so Sunrise reads it from the character bank. No map names slot 7, so Sunrise
        // binds it as an external input.
        Check(tables.Unlocks.BoundValue(2) && !tables.Unlocks.BoundValue(7) && Of((value, 2)).Kind == InvestmentProgressKind.Value
            && Of((value, 7)).Kind == InvestmentProgressKind.External, "A value slot was bound to the wrong source.");
        // Rows 17-47 nest 31 deep, which Sunrise binds; row 16 adds the 32nd level, which it refuses.
        Check(Of((shared, 17)).Kind == InvestmentProgressKind.Flag && Of((shared, 16)).Kind == InvestmentProgressKind.Unreadable,
            "The shared nesting limit differs from Sunrise's.");
        (string Name, (uint, uint)[] Program)[] unreadable =
        [
            ("a cycle", [(shared, 2)]), ("a row outside the table", [(shared, 99)]), ("an empty row", [(shared, 5)]),
            ("a row naming an empty row", [(shared, 6)]), ("an opcode Sunrise does not evaluate", [(shared, 7)]),
            ("a flag slot outside its table", [(flag, 40)]),
                ("a value slot outside its table", [(value, 8)]), ("no instruction", []),
            ("a shared row past the int range", [(shared, 0x80000000)])
        ];
        foreach (var (name, program) in unreadable)
            Check(Of(program).Kind == InvestmentProgressKind.Unreadable, $"An objective reading {name} was not unreadable.");
        Check(Of((InvestmentUnlocks.ConstantOpcode, 3)).Kind == InvestmentProgressKind.Other
            && Of((value, 7), (22, 0)).Kind == InvestmentProgressKind.Several,
            "A constant was a saved source, or an operator did not count as an instruction.");
    }

    /**
     * Set progress gating on fixture item 3 (objectives 0-7: an external value, a flag whose malformed controls read optional,
     * two unreadable, two saved values, several sources, and an optional constant), in the game's refusal order.
     */
    private static void Progress(InvestmentCatalog catalog, PackageCatalog packages, InvestmentTables tables)
    {
        var pursuit = catalog.ReadItem(First + FirstItemEntry + 3, packages.Read(First + FirstItemEntry + 3)).Pursuit!;
        Check(pursuit.Objectives.SequenceEqual(Enumerable.Range(0, 8).Select(index => (ushort)index))
            && tables.Objectives[7].Optional,
            "Fixture item 3 no longer lists objectives 0-7 with an optional last one.");
        const LivePursuitObjectiveFlags known = LivePursuitObjectiveFlags.Known;
        LivePursuitObjective Row(int ordinal,
            LivePursuitObjectiveFlags flags) => new(tables.Objectives[ordinal].Hash, 0, 10, flags);
        LiveHeldPursuit Held(LivePursuitFlags flags, params LivePursuitObjective[] rows) => new(0x4003,
            catalog.Items[3].Hash, 7, 0, flags, rows);
        var all = Held(LivePursuitFlags.ObjectivesKnown,
            [.. Enumerable.Range(0, InvestmentItemKinds.PursuitObjectiveCapacity).Select(ordinal => Row(ordinal, known))]);
        ProgressRefusal Set(int ordinal, LiveHeldPursuit? held, string link = "") => PursuitProgress.SetRefusal(tables,
            pursuit, ordinal, held, link);
        // Never hides the control: this server can never run it here. Anything else draws it, disabled with the text.
        bool Never(ProgressRefusal refusal, string text) => refusal == new ProgressRefusal(text, true);
        bool Waits(ProgressRefusal refusal, string text) => refusal == new ProgressRefusal(text, false);
        Check(Waits(Set(1, all, LiveGate.AttachSentence), LiveGate.AttachSentence) && Never(Set(1, null), PursuitProgress.NotHeld)
            && Never(Set(1, Held(LivePursuitFlags.None)), PursuitProgress.NoContract)
            && Never(Set(1, all with { Flags = LivePursuitFlags.Bounty }), PursuitProgress.BountyNotSaved),
            "Set progress did not refuse the link for now, then a missing copy, a contract the game cannot read and a bounty for good.");
        Check(Waits(Set(7, all), PursuitProgress.Differs)
            && Waits(Set(1, all with { Objectives = [Row(0, known), Row(2, known)] }), PursuitProgress.Differs),
            "Set progress allowed an objective the game does not report at that position, or hid it for good.");
        Check(Never(Set(0, all), PursuitProgress.NotSavedValue) && Never(Set(2, all), PursuitProgress.NotSavedValue)
            && Never(Set(6, all), PursuitProgress.NotSavedValue)
            && Waits(Set(4, all with { Objectives = [.. all.Objectives.Select((row, ordinal) =>
                ordinal == 4 ? row with { Flags = LivePursuitObjectiveFlags.None } : row)] }), PursuitProgress.Unreadable),
            "Set progress allowed a source that is not one saved value, or an unread value hid Set progress for good.");
        Check(Waits(Set(1, all), "") && Waits(Set(4, all), "") && Waits(Set(5, all), ""),
            "A saved flag or value could not be set.");
        // The game reports a skipped objective as known with value 0 whatever its saved row holds.
        Check(Waits(Set(4, all with { Objectives = [.. all.Objectives.Select((row, ordinal) =>
                ordinal == 4 ? row with { Flags = known | LivePursuitObjectiveFlags.Skipped } : row)] }),
                    PursuitProgress.Skipped),
            "Set progress allowed a skipped objective whose reported value is not its saved row.");
        Check(!PursuitProgress.CanSet(tables, pursuit, 1, null) && !PursuitProgress.CanSet(tables, pursuit, 0, all)
            && PursuitProgress.CanSet(tables, pursuit, 4,
            all with { Objectives = [.. all.Objectives.Select(row => row with { Flags = LivePursuitObjectiveFlags.None })] }),
            "Set progress was drawn where it can never run, or hidden while the game could not read a saved value.");
        Console.WriteLine("investment_progress_gate set=checked never=hidden transient=disabled");
    }

    private static void Pools(InvestmentTables tables)
    {
        var pools = tables.Pools;
        Check(pools.Count == 3 && pools[0].Entries.Count == 4
            && pools[2].Error == "Pool has no definition hash.", "Pool rows were dropped.");
        var entries = pools[0].Entries;
        Check(entries[0].Error == "Weight is negative or not finite." && entries[0].Weight == -1 && entries[0].AnyCategory,
            "A negative weight row was not kept with its error.");
        Check(tables.Unlocks.Format(entries[0].Condition) == "Account flag row 0 and (Account value row 0 >= 5)",
            "A pool condition did not print infix.");
        Check(entries[1].Error == "Socket override 0 has no socket type." && entries[1].Modifiers.Single().Value == 2
            && entries[1].Unknown28 == 0x5EED, "A bad socket override or modifier was misread.");
        Check(entries[2].Error == "Nested pool 7 is outside the pool table.", "An out-of-range nested pool was accepted.");
        var graph = tables.RewardGraph;
        Check(graph.Cycles.Single().SequenceEqual([1]) && graph.Depths.SequenceEqual([1, -1, 0]) && graph.MaximumDepth == 1
            && tables.Diagnostics.Contains("Reward pools contain themselves: 1."), "The automatic-wrapper cycle was missed.");
        Check(graph.WrappersOf(1).SequenceEqual([1]) && graph.WrappersOf(2).SequenceEqual([2])
            && graph.ParentsOf(2).SequenceEqual([(0, 3)]),
            "Pool references were not indexed.");
        Check(InvestmentRewardGraph.BaseTotal(pools[0], 0x22) == 3.5 + 1, "A selection total included another category.");
    }

    private static void Pass(InvestmentTables tables)
    {
        var pass = tables.PassCandidates.Single();
        Check(pass.Index == 1 && pass.Steps.Count == 3 && pass.Threshold(3) == 200 && pass.ScopeLabel == "Account"
            && tables.Progressions[0].ScopeLabel == "Character", "The pass candidate or its ladder was misread.");
        Check(pass.Rewards.Select(row => row.Index).SequenceEqual([0, 1, 2]) && pass.Rewards[0].Error == "Rank 300 is above 255."
            && pass.Rewards[1].Error == "Claim flag slot 5 is not an account flag." && pass.Rewards[2].Error.Length == 0,
            "Pass rows were reordered or validated wrongly.");
        Check(pass.Rewards[2].Conditions.Members.Count == 2
            && tables.Unlocks.Format(pass.Rewards[2].Conditions) == "(Account flag row 0) and (shared #1)",
            "Pass conditions were merged.");
    }

    private static void Pursuits(InvestmentCatalog catalog, PackageCatalog packages, InvestmentTables tables)
    {
        Check(tables.Objectives.Count == 8
            && tables.Objectives[1].Error == "The optional or counts-down byte is above 1, so Sunrise refuses the whole objective table."
            && tables.Objectives[0].Error.Length == 0 && tables.Objectives[2].Source.IsEmpty
            && tables.Objectives[3].Source.IsEmpty,
            "Objective rows were misread.");
        var detail = catalog.ReadItem(First + FirstItemEntry + 3, packages.Read(First + FirstItemEntry + 3));
        var pursuit = detail.Pursuit ?? throw new InvalidDataException("The pursuit block was not read.");
        Check(pursuit.Objectives.Count == 8 && pursuit.Diagnostics.Contains("Sunrise reads at most 7 objectives on a pursuit.")
            && pursuit.Diagnostics.Contains("Unused reward preview slot 1 is not empty."), "Pursuit rule breaks were not noted.");
        Check(pursuit is { RequireAll: false, RequireNonOptional: true }, "The completion controls were misread.");
        Check(pursuit.Previews.Single() is { ItemIndex: 0, Quantity: 3, ConditionRow: 1, Condition.Instructions.Count: 3 }
            && pursuit.LifetimeSeconds == 3600, "Previews or lifetime were misread.");
        Check(pursuit.PresenceFlags.SequenceEqual([(ushort)5]) && tables.ProgressLane(tables.Objectives[0], pursuit) == 0
            && tables.ProgressLane(tables.Objectives[1], pursuit) == -1, "The progress lane hint is wrong.");
        Check(pursuit.Chain is { OwnerItem: 4, Mode: 0, ModeDecoded: false, Error: "" } chain
            && chain.Steps.Select(step => (step.Value, step.ItemIndex)).SequenceEqual([(100, (ushort)3), (200, (ushort)5)]),
            "The quest set was misread.");
        Check(Enumerable.Range(0, catalog.Items.Count).Where(item => tables.QuestOwner(item) == 4).SequenceEqual([3, 5])
            && tables.ObjectiveUsers(0).SequenceEqual([3, 5]), "Item links were not captured.");
        var owned = catalog.ReadQuestSet(4);
        Check(owned.Steps.Select(step => step.ItemIndex).SequenceEqual(new ushort[] { 3, 5 }) && owned.Error.Length == 0,
            "The owner's quest set was not read.");
        try { catalog.ReadQuestSet(ItemCount); throw new InvalidDataException("A quest owner outside the item index was read."); }
        catch (ArgumentOutOfRangeException) { }
        var second = catalog.ReadItem(First + FirstItemEntry + 5, packages.Read(First + FirstItemEntry + 5)).Pursuit!;
        Check(second.LifetimeSeconds is null && second.Lifetime?.Instructions.Count == 2 && second.Diagnostics.Count == 0,
            "A computed lifetime was read as a constant.");
        // Item 3's lifetime is one constant row of 3600 seconds. Item 5's is a computed two-row expression, and item 0 has no
        // objective block.
        Check(tables.Expires(3) && !tables.Expires(5) && !tables.Expires(0) && !tables.Expires(-1) && !tables.Expires(ItemCount),
            "The installed lifetime does not mark the pursuit Sunrise reports as a bounty.");
        var wrapper = catalog.ReadItem(First + FirstItemEntry + 2, packages.Read(First + FirstItemEntry + 2)).Wrapper!;
        Check(wrapper is { Pool: 2, OpensOnAcquisition: false, Error: "More selections than Sunrise accepts (4)." }
            && wrapper.Selections.Count == 5,
            "Wrapper selections were capped or accepted.");
    }

    /** Display rows join objectives by index and hash; a row naming another hash gives no text and one diagnostic. */
    private static void ObjectiveText(InvestmentTables tables)
    {
        Check(tables.ObjectiveText(0) == FixtureObjectiveText && tables.ObjectiveText(7) == FixtureObjectiveText
            && tables.ObjectiveText(1) == "" && tables.ObjectiveText(2) == "" && tables.ObjectiveText(8) == ""
            && tables.ObjectiveText(-1) == "",
            "Objective descriptions were not joined by index and hash.");
        Check(tables.Diagnostics.Where(line => line.StartsWith("Objective text", StringComparison.Ordinal))
            .SequenceEqual(["Objective text: 1 objectives have no display row with their hash."]),
                "A mismatched display row was not one diagnostic.");
    }

    /** A display table of another class, or none, is a section diagnostic and leaves every description empty. */
    private static void ObjectiveTextRefusals(string parent)
    {
        foreach (var (name, displayClass,
            expected) in new[] { ("objective-text-class", AnyBucketClass, "Investment reference has an unexpected class."),
            ("objective-text-missing", 0u, "The objective display table is not installed.") })
        {
            string install = Path.Combine(parent, name);
            Directory.CreateDirectory(Path.Combine(install, "packages"));
            var root = new Blob(8 + 115 * 16);
            for (int slot = 0; slot < 115; slot++) root.U(8 + slot * 16, InvestmentRoot.OmittedSlot);
            root.U(8 + InvestmentRootSlots.Items * 16, First + 1); root.U(8 + InvestmentRootSlots.Objectives * 16, First + 3);
            AuthoredPackageEntry[] entries = [new(Views.InvestmentRoot.Class, 8 << 9, root.ToArray()),
                new(0x80807BE5, 8 << 9, Index([])),
                new(0x80805CDA, 8 << 9, Index([])), new(Views.ObjectiveTable.Class, 8 << 9, Objectives())];
            if (displayClass != 0) entries = [.. entries, new(displayClass, 8 << 9, ObjectiveDisplays())];
            PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_objective_text_fixture_0100_0.pkg"), 0x100, entries);
            using var packages = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
            var tables = InvestmentCatalog.Load(packages,
                sources: new(Items: First + 1, Displays: First + 2, StringBanks: uint.MaxValue,
                ObjectiveDisplays: First + 4)).Tables();
            Check(tables.Objectives.Count == 8 && tables.Diagnostics.Contains("Objective text: " + expected)
                && Enumerable.Range(0, 8).All(index => tables.ObjectiveText(index) == ""), $"The {name} display table was read.");
        }
    }

    private static void Edits(InvestmentCatalog catalog, PackageCatalog packages, InvestmentTables tables)
    {
        byte[] pools = packages.Read(First + PoolEntry), pass = packages.Read(First + ProgressionEntry),
            objectives = packages.Read(First + ObjectiveEntry);
        var entry = tables.Pools[0].Entries[1];
        var reward = tables.PassCandidates[0].Rewards[2];
        var objective = tables.Objectives[0];
        foreach (var (field, bytes, value) in new[] { (entry.WeightField, pools, "4.5"), (entry.QuantityField, pools, "9"),
            (reward.QuantityField, pass, "8"), (reward.ItemField, pass, "3"), (objective.CompletionField, objectives, "12") })
        {
            byte[] edited = catalog.EditTableField(field, bytes, value);
            Check(OnlyRange(bytes, edited, field.Property.Offset, 4) && field.Property.Editable,
                $"{field.Kind} changed bytes outside its field.");
            Reject(() => catalog.EditTableField(field, edited, value), "This field changed. Reopen its editor.");
        }
        Check(BinaryPrimitives.ReadSingleLittleEndian(catalog.EditTableField(entry.WeightField, pools,
            "4.5").AsSpan(entry.WeightField.Property.Offset)) == 4.5f,
            "The weight edit wrote the wrong value.");
        Check(reward.ItemField.Property.Offset == reward.Offset + 4 && BinaryPrimitives.ReadUInt32LittleEndian(
            catalog.EditTableField(reward.ItemField, pass, "3").AsSpan(reward.Offset + 4)) == 3,
                "The pass item edit wrote the wrong field.");
        const string itemRange = "Item must be an index of this installation's item table (0 to 5).";
        foreach (string item in new[] { "6", "65535", "-1",
            "x" }) Reject(() => catalog.EditTableField(reward.ItemField, pass, item), itemRange);
        Reject(() => catalog.EditTableField(entry.WeightField, pools, "NaN"), "Weight must be a finite number of zero or more.");
        Reject(() => catalog.EditTableField(entry.WeightField, pools, "-1"), "Weight must be a finite number of zero or more.");
        Reject(() => catalog.EditTableField(entry.QuantityField, pools, "0"), $"Quantity must be 1 to {int.MaxValue}.");
        Reject(() => catalog.EditTableField(objective.CompletionField, objectives, "-1"),
            "Completion value must be zero or more.");
        Reject(() => catalog.EditTableField(entry.WeightField with { OwnerTag = First + ProgressionEntry }, pools, "4"),
            "This table is no longer the investment root's reward pool table.");
        Reject(() => catalog.EditTableField(entry.WeightField with { Element = 9 }, pools, "4"),
            "This field changed. Reopen its editor.");
    }

    /**
     * A pass row's quantity and a pool entry's quantity follow Sunrise's reward refusals: an opening wrapper and an automatic perk
     * row take quantity 1 only, and an item granted as itself must fit its placement (fixture item 0 moved into each case).
     */
    private static void RewardEdits(InvestmentCatalog catalog, InvestmentTables tables)
    {
        Check(SeasonPassPage.RowRefusal(catalog, tables, 1, 3, false) == RewardRefusals.WrapperQuantity
            && SeasonPassPage.RowRefusal(catalog, tables, 1, 1, false).Length == 0
            && SeasonPassPage.RowRefusal(catalog, tables, 4, 2, false) == SeasonPassPage.PerkShapeRefusal
            && SeasonPassPage.RowRefusal(catalog, tables, 4, 1, false).Length == 0
            && SeasonPassPage.RowRefusal(catalog, tables, 0, 5, false).Length == 0,
            "A pass row quantity Sunrise refuses was accepted, or one it accepts was refused.");
        var item = catalog.Items[0];
        (InvestmentItem Item, long Quantity, bool Sockets, string Refusal)[] placements =
        [
            (item with { Bucket = RewardRefusals.PerkBucket }, 1, false, RewardRefusals.PerkAcquisition),
            (catalog.Items[4], 1, false, ""), (catalog.Items[4], 2, false, RewardRefusals.PerkAcquisition),
            (item with { Bucket = 12 }, 1, false, RewardRefusals.OtherInventory),
            (item with { Bucket = 10, Instanced = true }, 1, false, RewardRefusals.InstanceQuantity),
            (item with { Bucket = PursuitsId, Instanced = true }, 2, false, RewardRefusals.InstanceQuantity),
            (item with { Bucket = PursuitsId, Instanced = true }, 1, true, ""),
            (item with { Bucket = 10 }, 9, true, RewardRefusals.StackSockets), (item with { Bucket = 10 }, 9, false, "")
        ];
        foreach (var (shown, quantity, sockets, refusal) in placements)
            Check(RewardRefusals.Placement(tables, shown, quantity, sockets) == refusal,
                $"Placing item 0 in bucket {shown.Bucket} (instanced {shown.Instanced}) x{quantity} was not refused as \"{refusal}\".");
        // The pool entry popups refuse through the same rules: pool 1's entry is fixture item 1, which opens when acquired.
        using var workspace = new StudioWorkspace();
        var popup = new InvestmentFieldPopup();
        popup.Begin(workspace, tables.Pools[1].Entries[0].QuantityField, "");
        popup.Value = "2";
        string wrapped = InvestmentRewardTree.QuantityRefusal(catalog, tables, popup);
        popup.Value = "1";
        string single = InvestmentRewardTree.QuantityRefusal(catalog, tables, popup);
        popup.Begin(workspace, tables.Pools[0].Entries[1].QuantityField, "");
        popup.Value = "0x9";
        Check(wrapped == RewardRefusals.WrapperQuantity && single.Length == 0
            && InvestmentRewardTree.QuantityRefusal(catalog, tables, popup).Length == 0,
            "A pool entry quantity edit did not follow the wrapper rule.");
    }

    /** Two root entries never resolve to either; Load still succeeds. */
    private static void BucketTable(InvestmentTables tables)
    {
        var buckets = tables.Buckets;
        Check(buckets.Select(bucket => (bucket.Id, bucket.ArraySelector)).SequenceEqual([(PursuitsId, (byte)0),
            ((byte)10, (byte)1), ((byte)12, (byte)2)])
            && tables.BucketTableTag == First + BucketEntry && tables.BucketTableClass == AnyBucketClass,
            "The bucket table was not read by its slot whatever its class.");
        Check(tables.Bucket(PursuitsId) is { FirstSlot: PursuitsFirst, SlotCount: PursuitsSlots,
            LastSlot: PursuitsFirst + PursuitsSlots - 1, Fifo: 0 }
            && tables.Bucket(10) is { Fifo: 1, NoTransferOnEviction: 0 }
            && tables.Bucket(12) is { NoTransferOnEviction: 1, Raw.Length: 36 }
            && tables.Bucket(7) is null, "Bucket descriptors were misread.");
        Check(tables.BucketTableProblems.Count == 0 && buckets.All(bucket => bucket.Problem.Length == 0)
            && !tables.Diagnostics.Any(line => line.StartsWith("Buckets", StringComparison.Ordinal)),
            "A valid bucket table reported problems.");
        Check(tables.ItemsInBucket(PursuitsId).SequenceEqual([3, 5]) && tables.ItemsInBucket(37).SequenceEqual([4])
            && tables.ItemsInBucket(12).Count == 0, "Installed items were not indexed by bucket.");
    }

    /**
     * One table per refusal: Sunrise refuses the whole table for a repeated id, id 255, an unknown array selector or a FIFO or
     * no-transfer byte above 1, truncates a range, and fails a bad count.
     */
    private static void BucketRefusals(string parent)
    {
        const string refused = "Sunrise refuses the whole bucket table: ";
        var repeated = BucketTables(parent, "buckets-repeated",
            Buckets(3, (5, 0, 4, 0, 0, 0), (5, 4, 4, 0, 0, 0), (7, 8, 4, 0, 0, 0)));
        Check(repeated.BucketTableProblems.SequenceEqual([refused + "bucket 5 has more than one descriptor."])
            && repeated.Buckets.Count == 3 && repeated.Buckets.All(bucket => bucket.Problem.Length == 0),
            "A repeated bucket id was not a table problem.");
        var reserved = BucketTables(parent, "buckets-reserved", Buckets(2, (255, 0, 4, 0, 0, 0), (1, 4, 4, 0, 0, 0)));
        Check(reserved.BucketTableProblems.SequenceEqual([refused + "bucket 255 is reserved for no bucket."])
            && reserved.Buckets.All(bucket => bucket.Problem.Length == 0), "Bucket 255 was not a table problem.");
        var truncated = BucketTables(parent, "buckets-truncated",
            Buckets(2, (1, 0, ushort.MaxValue + 1, 0, 0, 0), (2, 0, 4, 3, 0, 0)));
        Check(truncated.BucketTableProblems.SequenceEqual([refused + "bucket 2's array selector 3 is not one Sunrise knows."])
            && truncated.Buckets[0].Problem.Contains("truncates", StringComparison.Ordinal)
            && truncated.Buckets[1].Problem.Length == 0,
            "A truncated range was not a row problem, or an unknown array selector not a table problem.");
        var controls = BucketTables(parent, "buckets-controls", Buckets(2, (1, 0, 4, 0, 2, 0), (2, 4, 4, 0, 1, 3)));
        Check(controls.BucketTableProblems.SequenceEqual([refused + "bucket 1's FIFO byte is 2, not 0 or 1.",
                refused + "bucket 2's no-transfer byte is 3, not 0 or 1."])
                    && controls.Buckets.All(bucket => bucket.Problem.Length == 0),
            "A FIFO or no-transfer byte above 1 was not a table problem.");
        foreach (int count in new[] { 0, InvestmentBuckets.Capacity + 1 })
        {
            var refusedTable = BucketTables(parent, $"buckets-count-{count}", Buckets(count));
            Check(refusedTable.Buckets.Count == 0
                && refusedTable.Diagnostics.Contains($"Buckets: the bucket table declares {count} descriptors."),
                $"A bucket table declaring {count} descriptors was read.");
        }
    }

    /** A root naming only the item index and the bucket table, in an installation of its own. */
    private static InvestmentTables BucketTables(string parent, string name, byte[] buckets)
    {
        string install = Path.Combine(parent, name);
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        var root = new Blob(8 + 115 * 16);
        for (int slot = 0; slot < 115; slot++) root.U(8 + slot * 16, InvestmentRoot.OmittedSlot);
        root.U(8 + InvestmentRootSlots.Items * 16, First + 1); root.U(8 + InvestmentRootSlots.Buckets * 16, First + 3);
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_buckets_fixture_0100_0.pkg"), 0x100,
        [
            new(Views.InvestmentRoot.Class, 8 << 9, root.ToArray()), new(0x80807BE5, 8 << 9, Index([])),
                new(0x80805CDA, 8 << 9, Index([])),
            new(AnyBucketClass, 8 << 9, buckets)
        ]);
        using var packages = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        return InvestmentCatalog.Load(packages,
            sources: new(Items: First + 1, Displays: First + 2, StringBanks: uint.MaxValue)).Tables();
    }

    /** A bucket table declaring count descriptors, followed by the given rows: id, first slot, slot count, selector, FIFO, no transfer. */
    private static byte[] Buckets(int count,
        params (byte Id, int First, int Slots, byte Selector, byte Fifo, byte NoTransfer)[] rows)
    {
        const int countOffset = 140, firstDescriptor = 144, size = 36;
        var blob = new Blob(firstDescriptor + rows.Length * size);
        blob.U(countOffset, (uint)count);
        for (int i = 0; i < rows.Length; i++)
        {
            int at = firstDescriptor + i * size;
            blob.B(at, rows[i].Id); blob.U(at + 4, (uint)rows[i].First); blob.U(at + 8, (uint)rows[i].Slots);
            blob.B(at + 24, rows[i].Selector); blob.B(at + 25, rows[i].Fifo); blob.B(at + 26, rows[i].NoTransfer);
        }
        return blob.ToArray();
    }

    private static void Ambiguous(string parent)
    {
        string install = Path.Combine(parent, "ambiguous-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_ambiguous_fixture_0100_0.pkg"), 0x100,
        [
            new(Views.InvestmentRoot.Class, 8 << 9, Root()), new(Views.InvestmentRoot.Class, 8 << 9, Root()),
            new(0x80807BE5, 8 << 9, Index([])), new(0x80805CDA, 8 << 9, Index([]))
        ]);
        using var packages = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        var catalog = InvestmentCatalog.Load(packages,
            sources: new(Items: First + 2, Displays: First + 3, StringBanks: uint.MaxValue));
        Check(catalog.Tables().Root is null && catalog.Tables().Diagnostics.Contains("The investment root is ambiguous."),
            "Two roots resolved to one.");
        var explicitRoot = InvestmentCatalog.Load(packages,
            sources: new(Items: First + 2, Displays: First + 3, StringBanks: uint.MaxValue, Root: First + 1));
        Check(explicitRoot.Tables().Root?.Tag == First + 1, "An explicit root tag was not used as-is.");
    }

    /** Pass rows by pool, quest owners, and the pursuit page each item is classified onto. */
    private static void Shapes(PackageCatalog packages, InvestmentTables tables)
    {
        Check(tables.PassRowsOfPool(1).SequenceEqual([(1, 2)]) && tables.PassRowsOfPool(2).Count == 0,
            "Pass rows by pool are wrong.");
        Check(tables.QuestOwner(3) == 4 && tables.QuestOwner(0) == -1 && tables.PursuitShape(3) == InvestmentPursuitShape.Quest
            && tables.PursuitShape(4) == InvestmentPursuitShape.Quest && tables.PursuitShape(0) == InvestmentPursuitShape.None,
            "Pursuit shapes or quest owners were misread.");
        byte[] step = packages.Read(First + FirstItemEntry + 5);
        Check(InvestmentPursuits.Classify(step) == (InvestmentPursuitShape.Quest, PursuitsId)
            && InvestmentPursuits.Classify(packages.Read(First + FirstItemEntry + 4)) == (InvestmentPursuitShape.Quest, (byte)37)
            && InvestmentPursuits.Classify(packages.Read(First + FirstItemEntry)) == (InvestmentPursuitShape.None, (byte)0),
            "An item was classified onto the wrong pursuit page.");
        // The same pursuit naming no quest owner is a single-step pursuit.
        int block = 0x30 + (int)BinaryPrimitives.ReadInt64LittleEndian(step.AsSpan(0x30));
        BinaryPrimitives.WriteUInt16LittleEndian(step.AsSpan(block + 0x1C), ushort.MaxValue);
        Check(InvestmentPursuits.Classify(step) == (InvestmentPursuitShape.Single, PursuitsId),
            "A pursuit outside a chain was not single-step.");
    }

    /** Every prefix of two rich items loads: link reading never throws, and only a cut through the fixed fields is reported. */
    private static void TruncatedItems(string parent)
    {
        string install = Path.Combine(parent, "truncated-items");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        byte[][] full = Items();
        // Lengths step by four bytes, so cuts land inside every block and field.
        const int step = 4;
        var cuts = new[] { full[0],
            full[3] }.SelectMany(item => Enumerable.Range(1, item.Length / step).Select(length => item[..(length * step)]))
            .ToArray();
        var root = new Blob(8 + 115 * 16);
        for (int slot = 0; slot < 115; slot++) root.U(8 + slot * 16, InvestmentRoot.OmittedSlot);
        root.U(8 + InvestmentRootSlots.Items * 16, First + 1);
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_truncated_fixture_0100_0.pkg"), 0x100,
        [
            new(Views.InvestmentRoot.Class, 8 << 9, root.ToArray()),
            new(0x80807BE5, 8 << 9, Index(cuts.Select((_, i) => ((uint)(0x2000 + i), First + 3 + (uint)i)).ToArray())),
            new(0x80805CDA, 8 << 9, Index([])),
            .. cuts.Select(item => new AuthoredPackageEntry(InvestmentCatalog.ItemClass, 8 << 9, item))
        ]);
        using var packages = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        var catalog = InvestmentCatalog.Load(packages,
            sources: new(Items: First + 1, Displays: First + 2, StringBanks: uint.MaxValue));
        var shapes = cuts.Select(item => InvestmentPursuits.Classify(item)).ToArray();
        Check(catalog.Items.Count == cuts.Length
            && catalog.Diagnostics.All(line => line.EndsWith(": Item fixed fields are truncated.", StringComparison.Ordinal))
            && catalog.Tables().QuestOwner(cuts.Length - 1) == 4,
            "A truncated item failed to load: " + string.Join(" | ", catalog.Diagnostics.Take(3)));
        Check(shapes[^1] == (InvestmentPursuitShape.Quest, PursuitsId)
            && shapes[0] == (InvestmentPursuitShape.None, byte.MaxValue),
            "A truncated item was classified from bytes it does not have.");
    }

    private static byte[] Root()
    {
        var root = new Blob(8 + 115 * 16);
        for (int slot = 0; slot < 115; slot++) root.U(8 + slot * 16, InvestmentRoot.OmittedSlot);
        void Slot(int slot, int entry) => root.U(8 + slot * 16, First + (uint)entry);
        Slot(InvestmentRootSlots.Classes, ItemIndexEntry); Slot(InvestmentRootSlots.Items, ItemIndexEntry);
        Slot(InvestmentRootSlots.FlagSlots, FlagSlotEntry); Slot(InvestmentRootSlots.ValueSlots, ValueSlotEntry);
        Slot(InvestmentRootSlots.FlagMaps, FlagMapEntry); Slot(InvestmentRootSlots.ValueMaps, ValueMapEntry);
        Slot(InvestmentRootSlots.SharedExpressions, SharedEntry); Slot(InvestmentRootSlots.Pools, PoolEntry);
        Slot(InvestmentRootSlots.Progressions, ProgressionEntry); Slot(InvestmentRootSlots.Objectives, ObjectiveEntry);
        Slot(InvestmentRootSlots.Buckets, BucketEntry);
        return root.ToArray();
    }

    // Flag slot 2 claims bank row 1, which feeds slot 5; the map names slot 2 at row 2. Slot 6 claims the same row as slot 2.
    private static byte[] FlagSlots() => Slots(0x80807D4F,
        [(0, 1, 0), (0, 3, 0), (0, 1, 1), (0xABCD1234, 6, 0xFFFF), (0, 1, 3), (0, 3, 1), (0, 1, 1)],
        (blob, at, row) => { blob.S(at + 4, (ushort)row.Kind); blob.S(at + 6, row.Row); });
    // Value slot kind byte, aggregation byte, then the bank row. No mapping table names slot 7, which objective 0 reads.
    private static byte[] ValueSlots() => Slots(0x80807C96,
        [(0, 2, 0), (0, 1, 0), (0, 4 | 2 << 8, 0xFFFF), (0, 0, 0xFFFF), (0, 0, 0xFFFF),
        (0, 0, 0xFFFF), (0, 0, 0xFFFF), (0, 4, 0xFFFF)], (blob, at, row) => { blob.S(at + 4, (ushort)row.Kind); blob.S(at + 6, row.Row); });

    private static byte[] Slots(uint rowClass, (uint Hash, int Kind, ushort Row)[] rows, Action<Blob, int,
        (uint Hash, int Kind, ushort Row)> write)
    {
        var blob = new Blob(24);
        int data = blob.Array(8, rowClass, rows.Length, 8);
        for (int i = 0; i < rows.Length; i++) { blob.U(data + i * 8, rows[i].Hash); write(blob, data + i * 8, rows[i]); }
        return blob.ToArray();
    }

    private static byte[] Maps(uint rowClass, params short[][] maps)
    {
        var blob = new Blob(72);
        for (int map = 0; map < maps.Length; map++)
        {
            if (maps[map].Length == 0) continue;
            int data = blob.Array(8 + map * 16, rowClass, maps[map].Length, 8);
            for (int row = 0; row < maps[map].Length; row++) blob.S(data + row * 8 + 4, (ushort)maps[map][row]);
        }
        return blob.ToArray();
    }

    /**
     * Rows: 0 flag; 1 shared 0 >= 1; 2/3 a cycle; 4 out of range; 5 empty; 6 names 5; 7 opcode 19; 8-47 a 40-deep chain; 48 a
     * saved value; 49 an external value.
     */
    private static byte[] Shared()
    {
        const int rows = 50;
        var blob = new Blob(24);
        int data = blob.Array(8, 0x80807C4F, rows, 24);
        (uint, uint)[] Body(int row) => row switch
        {
            0 => [(1, 0)], 1 => [(12, 0), (11, 1), (14, 0xFFFFFFFF)], 2 => [(12, 3)], 3 => [(12, 2)], 4 => [(12, 99)], 5 => [],
            6 => [(12, 5)], 7 => [(19, 0), (1, 0), (4, 0)], 47 => [(1, 0)], 48 => [(10, 4)],
                49 => [(10, 7)], _ => [(12, (uint)row + 1)]
        };
        for (int row = 0; row < rows; row++)
        {
            blob.U(data + row * 24, 0xE000 + (uint)row);
            var body = Body(row);
            if (body.Length != 0) blob.Expression(data + row * 24 + 8, body);
        }
        return blob.ToArray();
    }

    private static byte[] Pools()
    {
        var blob = new Blob(24);
        int pools = blob.Array(8, 0x8080748C, 3, 24);
        blob.U(pools, 0x11); blob.U(pools + 24, 0x12);
        int entries = blob.Array(pools + 8, 0x8080748E, 4, 80);
        void Entry(int at, ushort item, uint quantity, ushort nested, uint category, float weight)
        {
            blob.S(at, item); blob.U(at + 4, quantity); blob.S(at + 8, nested); blob.S(at + 10, 0xFFFF);
            blob.U(at + 20, category); blob.F(at + 24, weight);
        }
        Entry(entries, 0, 2, 0xFFFF, InvestmentRewards.AnyCategory, -1);
        blob.Expression(entries + 32, (1, 0), (10, 1), (11, 5), (14, 0xFFFFFFFF), (4, 0));
        Entry(entries + 80, 0, 1, 0xFFFF, 0x22, 3.5f); blob.U(entries + 80 + 28, 0x5EED);
        int modifier = blob.Array(entries + 80 + 48, 0x80807490, 1, 24);
        blob.Expression(modifier, (12, 1)); blob.F(modifier + 20, 2);
        int socket = blob.Array(entries + 80 + 64, 0x80803062, 1, 12);
        blob.S(socket, 0xFFFF); blob.S(socket + 2, 0xFFFF);
        Entry(entries + 160, 0xFFFF, 1, 7, 0x33, 1);
        Entry(entries + 240, 0xFFFF, 1, 2, 0x22, 1); blob.S(entries + 240 + 10, FixtureSite);
        int wrapped = blob.Array(pools + 24 + 8, 0x8080748E, 1, 80);
        Entry(wrapped, 1, 1, 0xFFFF, InvestmentRewards.AnyCategory, 2); blob.S(wrapped + 10, FixtureSite);
        return blob.ToArray();
    }

    private static byte[] Progressions()
    {
        var blob = new Blob(24);
        int rows = blob.Array(8, 0x80807CDD, 2, 88);
        blob.U(rows, 0x31); blob.B(rows + 4, 1);
        int pass = rows + 88;
        blob.U(pass, 0x32);
        int steps = blob.Array(pass + 24, 0x80807CED, 3, 8);
        blob.U(steps + 8, 100); blob.U(steps + 16, 100);
        int rewards = blob.Array(pass + 72, 0x80802FF7, 3, 56);
        void Reward(int row, uint rank, uint item, uint quantity, uint claim)
        { int at = rewards + row * 56; blob.U(at, rank); blob.U(at + 4, item); blob.U(at + 8, quantity); blob.U(at + 20, claim); }
        Reward(0, 300, 0, 1, 4); Reward(1, 2, 0, 5, 5); Reward(2, 3, 1, 7, 4);
        int conditions = blob.Array(rewards + 2 * 56 + 24, 0x80807D2F, 2, 16);
        blob.Expression(conditions, (1, 0)); blob.Expression(conditions + 16, (12, 1));
        return blob.ToArray();
    }

    /** Display rows for the fixture's objectives: 1 has no bank, 2 repeats another hash, every other row has the fixture text. */
    private static byte[] ObjectiveDisplays()
    {
        const int hash = 0, bank = 24, text = 28;
        var blob = new Blob(24);
        int rows = blob.Array(8, ObjectiveDisplayRowClass, 8, ObjectiveDisplayStride);
        for (int row = 0; row < 8; row++)
        {
            int at = rows + row * ObjectiveDisplayStride;
            blob.U(at + hash, row == 2 ? 0x99u : 0x41 + (uint)row);
            // The bank is a u16 followed by two zero bytes, as on the installed build.
            blob.S(at + bank, row == 1 ? ushort.MaxValue : (ushort)0);
            blob.U(at + text, row == 1 ? LiteralPart : 1);
        }
        return blob.ToArray();
    }

    /** A string bank index naming one container. */
    private static byte[] Banks(uint container)
    {
        var blob = new Blob(24);
        int rows = blob.Array(8, BankIndexClass, 1, 8);
        blob.U(rows + 4, container);
        return blob.ToArray();
    }

    /** One string bank: the container lists hashes 1 to n, and the data holds one literal part per text. */
    private static (byte[] Container, byte[] Data) Strings(uint dataTag, string[] texts)
    {
        const int englishData = 24, partsField = 8, combinationsField = 72, partStride = 32, combinationStride = 16;
        const int partText = 8, partVariable = 0x10, partLength = 0x14, partCount = 8;
        var container = new Blob(englishData + sizeof(uint));
        container.U(englishData, dataTag);
        int hashes = container.Array(8, StringHashClass, texts.Length, sizeof(uint));
        for (int i = 0; i < texts.Length; i++) container.U(hashes + i * sizeof(uint), (uint)(i + 1));
        var data = new Blob(combinationsField + 16);
        int parts = data.Array(partsField, StringPartClass, texts.Length, partStride);
        int combinations = data.Array(combinationsField, StringCombinationClass, texts.Length, combinationStride);
        for (int i = 0; i < texts.Length; i++)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(texts[i]);
            int part = parts + i * partStride, combination = combinations + i * combinationStride;
            data.Bytes(data.Block(part + partText, 0, bytes.Length), bytes);
            data.U(part + partVariable, LiteralPart); data.S(part + partLength, (ushort)bytes.Length);
            data.Pointer(combination, part); data.Q(combination + partCount, 1);
        }
        return (container.ToArray(), data.ToArray());
    }

    /** Objective 0 reads value slot 7; 1 has a non-boolean control byte and reads flags; 2 and 3 have empty sources in both encodings. */
    private static byte[] Objectives()
    {
        var blob = new Blob(24);
        int rows = blob.Array(8, 0x8080775F, 8, 0xA0);
        for (int row = 0; row < 8; row++) { blob.U(rows + row * 0xA0, 0x41 + (uint)row); blob.U(rows + row * 0xA0 + 0x30, 1); }
        blob.Expression(rows + 8, (10, 7)); blob.U(rows + 0x30, 10);
        blob.B(rows + 0xA0 + 0x28, 2);
        // Objective 1 reads flag slot 6 and adjusts its target by flag slot 2, which bind the same account row.
        blob.Expression(rows + 0xA0 + 8, (1, 6)); blob.Expression(rows + 0xA0 + 0x38, (1, 2));
        blob.Array(rows + 3 * 0xA0 + 8, 0x80807D31, 0, 8);
        // Objective 4 reads a saved value, 5 another through shared row 48, 6 two flags, and 7 is an optional constant.
        blob.Expression(rows + 4 * 0xA0 + 8, (10, 3));
        blob.Expression(rows + 5 * 0xA0 + 8, (12, 48));
        blob.Expression(rows + 6 * 0xA0 + 8, (1, 1), (1, 5), (4, 0));
        blob.Expression(rows + 7 * 0xA0 + 8, (11, 1)); blob.B(rows + 7 * 0xA0 + 0x28, 1);
        return blob.ToArray();
    }

    /**
     * Items 1 and 2 wrap pools 1 and 2, item 1 with an acquisition flag and opening when acquired; item 0 stacks to
     * ItemZeroStack; item 4 is a perk whose acquisition flag is an account flag; 3 and 5 are pursuits in item 4's quest set.
     */
    private static byte[][] Items()
    {
        var items = new byte[ItemCount][];
        for (int i = 0; i < ItemCount; i++)
        {
            var item = new Blob(512);
            item.S(0xDA, i switch { 1 => 2, 4 => AccountFlagSlot, _ => 0xFFFF });
            if (i == 0) item.U(180, ItemZeroStack);
            if (i is 1 or 2)
            {
                int wrapper = item.Block(0x58, 0x808077CC, 32);
                item.S(wrapper, (ushort)i); item.U(wrapper + 24, i == 1 ? 1u : 0u);
                int selections = item.Array(wrapper + 8, 0x808077CF, i == 2 ? 5 : 1, 12);
                item.U(selections + 4, 1);
            }
            if (i is 3 or 5)
            {
                item.B(184, 40);
                int block = item.Block(0x30, 0x808077EB, 0x20);
                int objectives = item.Array(block, 0x808087B1, i == 3 ? 8 : 1, 2);
                for (int k = 0; k < (i == 3 ? 8 : 1); k++) item.S(objectives + k * 2, (ushort)k);
                // Item 3 requires its non-optional objectives.
                if (i == 3) item.B(block + 0x18, 1);
                item.S(block + 0x1C, 4);
                if (i == 3) item.Expression(192, (11, 3600)); else item.Expression(192, (11, 5), (22, 0));
            }
            if (i == 3)
            {
                int preview = item.Block(0x98, 0x80807875, 4 + 6 * 12);
                item.U(preview, 1); item.U(preview + 4, 0); item.U(preview + 8, 3); item.U(preview + 12, 1);
                for (int slot = 1; slot < 6; slot++) { item.U(preview + 4 + slot * 12,
                    0xFFFF); item.U(preview + 12 + slot * 12, slot == 1 ? 0u : 0xFFFF); }
                int values = item.Block(0x90, 0x808077AB, 0x30);
                int presence = item.Array(values, 0x80807D4B, 1, 2); item.S(presence, 5);
                int instances = item.Array(values + 0x20, 0x808077AE, 1, 8); item.S(instances, 7);
            }
            if (i == 4)
            {
                item.B(184, 37);
                int set = item.Block(0x60, 0x808077C8, 0x20);
                item.S(set + 0x10, 0); item.B(set + 0x1C, 0);
                int members = item.Array(set, 0x808077CA, 2, 8);
                item.U(members, 100); item.S(members + 4, 3); item.U(members + 8, 200); item.S(members + 12, 5);
            }
            items[i] = item.ToArray();
        }
        return items;
    }

    private static byte[] Index((uint Hash, uint Tag)[] rows)
    {
        var blob = new Blob(24);
        int data = blob.Array(8, 0x80807BE8, rows.Length, 24);
        for (int i = 0; i < rows.Length; i++) { blob.U(data + i * 24, rows[i].Hash); blob.U(data + i * 24 + 16, rows[i].Tag); }
        return blob.ToArray();
    }

    /** A growing tag blob; typed arrays and class-tagged blocks are appended and pointed to from their fields. */
    private sealed class Blob(int size)
    {
        private byte[] bytes = new byte[Math.Max(size, 64)];
        private int length = size;

        /** Appends a typed array with its 0x80809FBD marker and points the descriptor at `field` to it; returns its data. */
        public int Array(int field, uint type, int count, int stride)
        {
            int header = Grow(4 + 16 + count * stride) + 4;
            U(header - 4, 0x80809FBD); Q(header, count); U(header + 8, type);
            Q(field, count); Q(field + 8, header - field - 8);
            return header + 16;
        }
        /** Appends a block after its four-byte class and points the relative field at it. */
        public int Block(int field, uint type, int size)
        {
            int at = Grow(4 + size) + 4;
            U(at - 4, type); Q(field, at - field);
            return at;
        }
        public void Expression(int field, params (uint Opcode, uint Operand)[] instructions)
        {
            int data = Array(field, Views.InvestmentExpressionRow.Class, instructions.Length, 8);
            for (int i = 0; i < instructions.Length; i++) { U(data + i * 8,
                instructions[i].Opcode); U(data + i * 8 + 4, instructions[i].Operand); }
        }
        public void B(int at, byte value) => bytes[at] = value;
        public void S(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
        public void U(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
        public void F(int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
        public void Q(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
        public void Bytes(int at, byte[] value) => value.CopyTo(bytes, at);
        /** Points the relative field at `target`. */
        public void Pointer(int field, int target) => Q(field, target - field);
        public byte[] ToArray() => bytes[..length];
        private int Grow(int count)
        {
            int start = (length + 7) & ~7;
            if (start + count > bytes.Length) System.Array.Resize(ref bytes, Math.Max(bytes.Length * 2, start + count));
            length = start + count;
            return start;
        }
    }

    private static bool OnlyRange(byte[] before, byte[] after, int offset, int size) => before.Length == after.Length
        && before.Take(offset).SequenceEqual(after.Take(offset))
        && before.Skip(offset + size).SequenceEqual(after.Skip(offset + size))
        && !before.Skip(offset).Take(size).SequenceEqual(after.Skip(offset).Take(size));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException error) when (error.Message == message) { return; }
        throw new Exception("Expected refusal: " + message);
    }
}
