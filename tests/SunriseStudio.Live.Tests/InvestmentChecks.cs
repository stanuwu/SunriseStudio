using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using SunriseStudio.Live;
using P = SunriseStudio.Live.LiveInvestmentProtocol;

/**
 * The investment link mirrors the native SSIV layout; this process plays the game on its own mapping. Payload fixtures are
 * written at the native header's literal offsets, so a reader that moves a field fails here.
 */
internal static class InvestmentChecks
{
    private const ulong Account = 0x4000000000000002, Character = 0x4000000000000003, OtherCharacter = 0x4000000000000004;
    private const ulong Content = 0x1F2E3D4C5B6A7988, Started = 0x01DB1234ABCD5678;
    private const uint ItemHash = 0x80A10001, StackHash = 0x80A10003;
    private static readonly LiveInvestmentContext Bound = new(Account, Character, Content);
    // Saved rows of each kind and bank the game reads: account flag 11, profile flag 2, character flag 7, then values.
    private static readonly LiveUnlockSource[] Sources =
    [
        new(LiveUnlockKind.Flag, LiveUnlockBank.Account, 11), new(LiveUnlockKind.Flag, LiveUnlockBank.Profile, 2),
        new(LiveUnlockKind.Flag, LiveUnlockBank.Character, 7), new(LiveUnlockKind.Value, LiveUnlockBank.Account, 5866),
        new(LiveUnlockKind.Value, LiveUnlockBank.Character, 0xFFFF)
    ];

    public static async Task Run()
    {
        Offsets();
        Readers();
        Rejections();
        Unlocks();
        PursuitEdits();
        await Connection();
        Console.WriteLine("live investment_offsets=checked investment_readers=checked investment_rejects=checked investment_unlocks=checked "
            + "investment_pursuit_edits=checked investment_connection=checked");
    }

    /** Every offset, size and capacity beside the native header's value (studio_investment_wire.h version 3). */
    private static void Offsets()
    {
        (string Name, int Actual, int Expected)[] offsets =
        [
            (nameof(P.ControlBytes), P.ControlBytes, 48), (nameof(P.HeartbeatOffset), P.HeartbeatOffset, 16),
            (nameof(P.GameVersionOffset), P.GameVersionOffset, 24), (nameof(P.GamePidOffset), P.GamePidOffset, 28),
            (nameof(P.GameStartedOffset), P.GameStartedOffset, 32), (nameof(P.RequestOffset), P.RequestOffset, 40),
            (nameof(P.ResponseOffset), P.ResponseOffset, 44), (nameof(P.RequestBlockOffset), P.RequestBlockOffset, 48),
            (nameof(P.OpcodeOffset), P.OpcodeOffset, 48), (nameof(P.RequestPayloadBytesOffset), P.RequestPayloadBytesOffset, 52),
            (nameof(P.OperationOffset), P.OperationOffset, 56), (nameof(P.ExpectedOffset), P.ExpectedOffset, 64),
            (nameof(P.RequestPayloadOffset), P.RequestPayloadOffset, 88), (nameof(P.RequestBlockBytes), P.RequestBlockBytes, 80),
            (nameof(P.ResponseBlockOffset), P.ResponseBlockOffset, 128), (nameof(P.StatusOffset), P.StatusOffset, 128),
            (nameof(P.ResponseReservedOffset), P.ResponseReservedOffset, 132),
            (nameof(P.ResponseOperationOffset), P.ResponseOperationOffset, 136),
            (nameof(P.ResponseOpcodeOffset), P.ResponseOpcodeOffset, 144),
            (nameof(P.ResponsePayloadBytesOffset), P.ResponsePayloadBytesOffset, 148),
            (nameof(P.ContextOffset), P.ContextOffset, 152), (nameof(P.ReasonOffset), P.ReasonOffset, 176),
            (nameof(P.ReasonBytes), P.ReasonBytes, 64), (nameof(P.ResponsePayloadOffset), P.ResponsePayloadOffset, 240),
            (nameof(P.ResponseHeaderBytes), P.ResponseHeaderBytes, 112), (nameof(P.MappingBytes), P.MappingBytes, 45536),
            (nameof(P.RequestPayloadBytes), P.RequestPayloadBytes, 40),
                (nameof(P.ResponsePayloadBytes), P.ResponsePayloadBytes, 45296),
            (nameof(P.GrantItemBytes), P.GrantItemBytes, 8), (nameof(P.GrantQuantityOffset), P.GrantQuantityOffset, 4),
            (nameof(P.SetQuantityBytes), P.SetQuantityBytes, 32), (nameof(P.SetInstanceOffset), P.SetInstanceOffset, 8),
            (nameof(P.SetHashOffset), P.SetHashOffset, 16), (nameof(P.SetExpectedSerialOffset), P.SetExpectedSerialOffset, 20),
            (nameof(P.SetExpectedQuantityOffset), P.SetExpectedQuantityOffset, 24),
                (nameof(P.SetQuantityOffset), P.SetQuantityOffset, 28),
            (nameof(P.SetSeasonalExperienceBytes), P.SetSeasonalExperienceBytes, 8),
            (nameof(P.ExperienceReplacementOffset), P.ExperienceReplacementOffset, 4),
            (nameof(P.SeasonPassRowBytes), P.SeasonPassRowBytes, 8), (nameof(P.PassHashOffset), P.PassHashOffset, 4),
            (nameof(P.UnlockValuesRequestBytes), P.UnlockValuesRequestBytes, 40),
                (nameof(P.UnlockSourcesOffset), P.UnlockSourcesOffset, 4),
            (nameof(P.UnlockSourceBytes), P.UnlockSourceBytes, 4), (nameof(P.UnlockBankOffset), P.UnlockBankOffset, 1),
            (nameof(P.UnlockIndexOffset), P.UnlockIndexOffset, 2),
            (nameof(P.SetObjectiveBytes), P.SetObjectiveBytes, 32),
                (nameof(P.CompleteObjectivesBytes), P.CompleteObjectivesBytes, 16),
            (nameof(P.PursuitHashOffset), P.PursuitHashOffset, 8), (nameof(P.PursuitSerialOffset), P.PursuitSerialOffset, 12),
            (nameof(P.ObjectiveRequestHashOffset), P.ObjectiveRequestHashOffset, 16),
            (nameof(P.ObjectiveOrdinalOffset), P.ObjectiveOrdinalOffset, 20),
            (nameof(P.ObjectiveExpectedOffset), P.ObjectiveExpectedOffset, 24),
            (nameof(P.ObjectiveReplacementOffset), P.ObjectiveReplacementOffset, 28),
            (nameof(P.HelloBytes), P.HelloBytes, 56), (nameof(P.HelloCharacterCountOffset), P.HelloCharacterCountOffset, 4),
            (nameof(P.HelloPeerOffset), P.HelloPeerOffset, 5), (nameof(P.HelloReservedOffset), P.HelloReservedOffset, 6),
            (nameof(P.HelloCharactersOffset), P.HelloCharactersOffset, 8), (nameof(P.CharacterBytes), P.CharacterBytes, 16),
            (nameof(P.CharacterClassOffset), P.CharacterClassOffset, 8),
                (nameof(P.CharacterSelectedOffset), P.CharacterSelectedOffset, 9),
            (nameof(P.CharacterReservedOffset), P.CharacterReservedOffset, 10),
            (nameof(P.InventoryBytes), P.InventoryBytes, 31584), (nameof(P.ProfileCountOffset), P.ProfileCountOffset, 0),
            (nameof(P.ItemCountOffset), P.ItemCountOffset, 2), (nameof(P.StackCountOffset), P.StackCountOffset, 4),
            (nameof(P.BucketCountOffset), P.BucketCountOffset, 6), (nameof(P.ProfileOffset), P.ProfileOffset, 8),
            (nameof(P.ItemsOffset), P.ItemsOffset, 16832), (nameof(P.StacksOffset), P.StacksOffset, 25232),
            (nameof(P.BucketsOffset), P.BucketsOffset, 30560), (nameof(P.InventoryTailOffset), P.InventoryTailOffset, 31580),
            (nameof(P.ProfileRowBytes), P.ProfileRowBytes, 24), (nameof(P.ItemBytes), P.ItemBytes, 24),
            (nameof(P.StackBytes), P.StackBytes, 16), (nameof(P.BucketBytes), P.BucketBytes, 4),
                (nameof(P.RowHashOffset), P.RowHashOffset, 8),
            (nameof(P.RowQuantityOffset), P.RowQuantityOffset, 12), (nameof(P.RowSerialOffset), P.RowSerialOffset, 16),
            (nameof(P.RowBucketOffset), P.RowBucketOffset, 20), (nameof(P.ProfileReservedOffset), P.ProfileReservedOffset, 21),
            (nameof(P.ItemEquipmentSlotOffset), P.ItemEquipmentSlotOffset, 21),
                (nameof(P.ItemReservedOffset), P.ItemReservedOffset, 22),
            (nameof(P.StackQuantityOffset), P.StackQuantityOffset, 4), (nameof(P.StackSerialOffset), P.StackSerialOffset, 8),
            (nameof(P.StackBucketOffset), P.StackBucketOffset, 12), (nameof(P.StackReservedOffset), P.StackReservedOffset, 13),
            (nameof(P.BucketEquipmentSlotOffset), P.BucketEquipmentSlotOffset, 1),
            (nameof(P.BucketSlotCountOffset), P.BucketSlotCountOffset, 2),
            (nameof(P.SeasonPassBytes), P.SeasonPassBytes, 2056), (nameof(P.PassRankOffset), P.PassRankOffset, 4),
            (nameof(P.PassRowCountOffset), P.PassRowCountOffset, 6), (nameof(P.PassRowsOffset), P.PassRowsOffset, 8),
            (nameof(P.PassRowBytes), P.PassRowBytes, 8), (nameof(P.RowRankOffset), P.RowRankOffset, 4),
            (nameof(P.RowReservedOffset), P.RowReservedOffset, 5), (nameof(P.RowFlagsOffset), P.RowFlagsOffset, 6),
            (nameof(P.PursuitBytes), P.PursuitBytes, 45296), (nameof(P.PursuitReservedOffset), P.PursuitReservedOffset, 2),
            (nameof(P.HeldOffset), P.HeldOffset, 8), (nameof(P.HeldBytes), P.HeldBytes, 136),
                (nameof(P.HeldHashOffset), P.HeldHashOffset, 8),
            (nameof(P.HeldSerialOffset), P.HeldSerialOffset, 12), (nameof(P.HeldItemFlagsOffset), P.HeldItemFlagsOffset, 16),
            (nameof(P.HeldFlagsOffset), P.HeldFlagsOffset, 20),
                (nameof(P.HeldObjectiveCountOffset), P.HeldObjectiveCountOffset, 21),
            (nameof(P.HeldReservedOffset), P.HeldReservedOffset, 22),
                (nameof(P.HeldObjectivesOffset), P.HeldObjectivesOffset, 24),
            (nameof(P.ObjectiveBytes), P.ObjectiveBytes, 16), (nameof(P.ObjectiveValueOffset), P.ObjectiveValueOffset, 4),
            (nameof(P.ObjectiveTargetOffset), P.ObjectiveTargetOffset, 8),
                (nameof(P.ObjectiveFlagsOffset), P.ObjectiveFlagsOffset, 12),
            (nameof(P.ObjectiveReservedOffset), P.ObjectiveReservedOffset, 13),
            (nameof(P.ReceiptBytes), P.ReceiptBytes, 8), (nameof(P.ReceiptWaitOffset), P.ReceiptWaitOffset, 4),
            (nameof(P.UnlockValuesBytes), P.UnlockValuesBytes, 72), (nameof(P.UnlockValueBytes), P.UnlockValueBytes, 8),
            (nameof(P.UnlockStateReservedOffset), P.UnlockStateReservedOffset, 1),
                (nameof(P.UnlockValueOffset), P.UnlockValueOffset, 4),
            (nameof(P.CharacterCapacity), P.CharacterCapacity, 3), (nameof(P.ProfileRowCapacity), P.ProfileRowCapacity, 701),
            (nameof(P.CharacterItemCapacity), P.CharacterItemCapacity, 350),
            (nameof(P.CharacterStackCapacity), P.CharacterStackCapacity, 333), (nameof(P.BucketCapacity), P.BucketCapacity, 255),
            (nameof(P.SeasonPassRowCapacity), P.SeasonPassRowCapacity, 256),
                (nameof(P.HeldPursuitCapacity), P.HeldPursuitCapacity, 333),
            (nameof(P.PursuitObjectiveCapacity), P.PursuitObjectiveCapacity, 7),
                (nameof(P.UnlockReadCapacity), P.UnlockReadCapacity, 9)
        ];
        var differing = offsets.Where(row => row.Actual != row.Expected).Select(row => $"{row.Name} {row.Actual} (header {row.Expected})").ToArray();
        Check(differing.Length == 0, "Investment offsets differ from the native header: " + string.Join(", ", differing));
        Check(P.Magic == 0x56495353 && P.Version == 3 && P.MappingName(42) == @"Local\SunriseStudio.Investment.42",
            "Investment magic, version or mapping name differs.");
        // Each request and answer size, as the game declares it.
        (LiveInvestmentOpcode Opcode, int Request, int Answer)[] sizes =
        [
            (LiveInvestmentOpcode.Hello, 0, 56), (LiveInvestmentOpcode.InventorySnapshot, 0, 31584),
            (LiveInvestmentOpcode.SeasonPassSnapshot, 0, 2056), (LiveInvestmentOpcode.PursuitSnapshot, 0, 45296),
            (LiveInvestmentOpcode.GrantItem, 8, 8), (LiveInvestmentOpcode.SetQuantity, 32, 8),
                (LiveInvestmentOpcode.SetSeasonalExperience, 8, 8),
            (LiveInvestmentOpcode.ClaimSeasonPassReward, 8, 8), (LiveInvestmentOpcode.ResetSeasonPassClaim, 8, 8),
            (LiveInvestmentOpcode.ReadReceipt, 0, 8), (LiveInvestmentOpcode.AcknowledgeReceipt, 0, 8),
            (LiveInvestmentOpcode.UnlockValues, 40, 72), (LiveInvestmentOpcode.SetPursuitObjective, 32, 8),
            (LiveInvestmentOpcode.CompletePursuitObjectives, 16, 8)
        ];
        foreach (var (opcode, request, answer) in sizes)
            Check(P.RequestBytes(opcode) == request && P.ResponseBytes(opcode) == answer,
                $"{opcode} is sized differently from the native header.");
        Check((int)LiveInvestmentOpcode.ReadReceipt == 10 && (int)LiveInvestmentOpcode.AcknowledgeReceipt == 11
            && (int)LiveInvestmentOpcode.UnlockValues == 12 && (int)LiveInvestmentOpcode.SetPursuitObjective == 13
            && (int)LiveInvestmentOpcode.CompletePursuitObjectives == 14 && (int)LiveInvestmentStatus.Refused == 3
            && (int)LiveReceiptState.CommittedUnpublished == 5 && (int)LiveInvestmentWait.Catalog == 3
            && (byte)LivePursuitFlags.ObjectivesComplete == 4 && (byte)LivePursuitObjectiveFlags.Skipped == 4
            && (ushort)LiveSeasonPassRowFlags.ConditionMet == 64 && (byte)LiveUnlockKind.Value == 2
            && (byte)LiveUnlockBank.Character == 3,
            "Investment enum values differ from the native header.");
    }

    /** Readers decode only the counted prefix and keep every field where the game put it. */
    private static void Readers()
    {
        var hello = P.ReadHello(Reply(LiveInvestmentOpcode.Hello, Bound, HelloPayload()));
        Check(hello is { Readiness: LiveInvestmentReadiness.Items | LiveInvestmentReadiness.Buckets,
            GameStarted: Started, PeerOwnsAccount: true }
            && hello.Characters.Count == 2 && hello.Characters[0] is { Soid: Character, CharacterClass: 2, Selected: true }
            && hello.Characters[1] is { Soid: OtherCharacter, Selected: false }, "The hello layout differs.");

        var inventory = P.ReadInventory(Reply(LiveInvestmentOpcode.InventorySnapshot, Bound, InventoryPayload()));
        Check(inventory is { Buckets.Count: 2, Profile.Count: 1, Items.Count: 2, Stacks.Count: 1 }
            && inventory.Buckets[0] is { BucketId: 2, SlotCount: 10, EquipmentSlot: -1 }
            && inventory.Buckets[1] is { BucketId: 3, SlotCount: 9, EquipmentSlot: 16 }
            && inventory.Profile[0] is { Instance: 0x5000000000000001, DefinitionHash: StackHash, Quantity: 25,
            MutationSerial: 4, BucketId: 2 }
            && inventory.Items[0] is { Instance: 0x4000000000000200, Equipped: true, EquipmentSlot: 0,
            BucketId: 3, MutationSerial: 6 }
            && inventory.Items[1] is { Equipped: false, BucketId: P.NoBucket }
            && inventory.Stacks[0] is { DefinitionHash: StackHash, Quantity: 3, MutationSerial: 8, BucketId: 5 },
            "The inventory layout differs.");

        var pass = new byte[2056];
        I32(pass, 0, 125000); U16(pass, 4, 12); U16(pass, 6, 3);
        PassRow(pass, 0, ItemHash, 1, LiveSeasonPassRowFlags.Available | LiveSeasonPassRowFlags.Claimed);
        PassRow(pass, 2, StackHash, 4, LiveSeasonPassRowFlags.Available | LiveSeasonPassRowFlags.Automatic);
        var season = P.ReadSeasonPass(Reply(LiveInvestmentOpcode.SeasonPassSnapshot, Bound, pass));
        Check(season is { Experience: 125000, Rank: 12, Rows.Count: 3 }
            && season.Rows[1] is { ItemHash: 0, Flags: LiveSeasonPassRowFlags.None }
            && season.Rows[2] is { Index: 2, ItemHash: StackHash, RequiredRank: 4 }
            && season.Rows[2].Flags == (LiveSeasonPassRowFlags.Available | LiveSeasonPassRowFlags.Automatic),
            "The season pass layout differs.");

        var held = P.ReadPursuits(Reply(LiveInvestmentOpcode.PursuitSnapshot, Bound, PursuitPayload())).Held.Single();
        Check(held is { Instance: 0x4000000000000100, ItemHash: ItemHash, MutationSerial: 9, ItemFlags: 2, Objectives.Count: 2 }
            && held.Flags == (LivePursuitFlags.Bounty | LivePursuitFlags.ObjectivesKnown)
            && held.Objectives[1] is { ObjectiveHash: 0x1234, Value: 2, Target: 5, Flags: LivePursuitObjectiveFlags.Known },
            "The pursuit layout differs.");

        var receipt = P.ReadReceipt(Reply(LiveInvestmentOpcode.ReadReceipt, Bound,
            Receipt(LiveReceiptState.Deferred, LiveInvestmentWait.Catalog), 7));
        Check(receipt is { OperationId: 7, State: LiveReceiptState.Deferred, Wait: LiveInvestmentWait.Catalog, Terminal: false }
            && receipt.Context == Bound, "The receipt layout differs.");
    }

    /** Every count, reserved field and unused slot is checked; nothing out of range reaches the editor. */
    private static void Rejections()
    {
        void Inventory(byte[] payload, string message) =>
            Reject(() => P.ReadInventory(Reply(LiveInvestmentOpcode.InventorySnapshot, Bound, payload)), message);
        Inventory(Changed(InventoryPayload(), 8 + 24 + 3, 1), "A value in an unused profile slot was accepted.");
        Inventory(Changed8(InventoryPayload(), 16832 + 22, 1), "A nonzero character item reserved byte was accepted.");
        Inventory(Changed8(InventoryPayload(), 16832 + 21, 17), "An equipment slot past the native slots was accepted.");
        Inventory(Changed16(InventoryPayload(), 2, 351), "An item count past capacity was accepted.");
        Inventory(Changed(InventoryPayload(), 31580, 1), "A nonzero inventory tail was accepted.");
        Reject(() => P.ReadHello(Reply(LiveInvestmentOpcode.Hello,
            Bound with { CharacterSoid = OtherCharacter }, HelloPayload())),
            "A hello whose selected character differs from its context was accepted.");
        Reject(() => P.ReadHello(Reply(LiveInvestmentOpcode.Hello, Bound, Changed8(HelloPayload(), 5, 2))),
            "A client state of 2 was accepted.");
        Reject(() => P.ReadHello(Reply(LiveInvestmentOpcode.Hello, Bound, Changed8(HelloPayload(), 4, 4))),
            "A character count past capacity was accepted.");
        Reject(() => P.ReadPursuits(Reply(LiveInvestmentOpcode.PursuitSnapshot, Bound, Changed8(PursuitPayload(), 8 + 20, 8))),
            "An unknown pursuit flag was accepted.");
        Reject(() => P.ReadPursuits(Reply(LiveInvestmentOpcode.PursuitSnapshot, Bound, Changed8(PursuitPayload(), 8 + 22, 1))),
            "A nonzero held pursuit reserved byte was accepted.");
        Reject(() => P.ReadReceipt(Reply(LiveInvestmentOpcode.ReadReceipt, Bound, Receipt(LiveReceiptState.Refused), 1)),
            "A refused receipt without its label was accepted.");
        Reject(() => P.ReadReceipt(Reply(LiveInvestmentOpcode.ReadReceipt, Bound, Receipt(LiveReceiptState.Deferred), 1)),
            "A deferred receipt without its wait was accepted.");
        Reject(() => P.ReadReceipt(Reply(LiveInvestmentOpcode.ReadReceipt, Bound, Receipt(LiveReceiptState.Unknown), 1)),
            "An unknown receipt naming a target was accepted.");

        byte[] control = new byte[48], response = new byte[112 + 56];
        U32(control, 0, P.Magic); U32(control, 4, 3); U32(control, 8, 45536); U32(control, 24, 3); U32(control, 28, 7);
        U64(control, 32, Started); U32(control, 44, 5);
        void Header(LiveInvestmentStatus status, uint payload)
        {
            U32(response, 0, (uint)status); U32(response, 16, (uint)LiveInvestmentOpcode.Hello); U32(response, 20, payload);
        }
        LiveInvestmentReply? Read(uint request = 5, ulong started = Started) =>
            P.Read(control, response, 7, started, request, LiveInvestmentOpcode.Hello, 0);
        Header(LiveInvestmentStatus.Ok, 56);
        Check(Read() is { Status: LiveInvestmentStatus.Ok, Reason: "", Payload.Length: 56,
            GameStarted: Started } && Read(request: 4) is null,
            "A reply to the current id was not read.");
        Reject(() => Read(started: Started + 1), "A reply from another game instance was accepted.");
        Header(LiveInvestmentStatus.Refused + 1, 56);
        Reject(() => Read(), "A status past Refused was accepted.");
        Header(LiveInvestmentStatus.Ok, 55);
        Reject(() => Read(), "A reply with a partial payload was accepted.");
        Header(LiveInvestmentStatus.Ok, 0);
        Reject(() => Read(), "An ok reply without its payload was accepted.");
        Encoding.ASCII.GetBytes("account_state", response.AsSpan(48));
        Header(LiveInvestmentStatus.Refused, 56);
        Reject(() => Read(), "A refused reply carrying a payload was accepted.");
        Header(LiveInvestmentStatus.Refused, 0);
        Check(Read() is { Status: LiveInvestmentStatus.Refused, Reason: "account_state",
            Payload.Length: 0 }, "A refusal was not read.");
        response[48 + "account_state".Length + 1] = (byte)'x';
        Reject(() => Read(), "A reason with bytes after its terminator was accepted.");
        response.AsSpan(48, 64).Fill((byte)'x');
        Reject(() => Read(), "An unterminated reason was accepted.");
        response.AsSpan(48, 64).Clear();
        Reject(() => Read(), "A refusal without a reason was accepted.");
        Header(LiveInvestmentStatus.Ok, 56);
        U32(response, 4, 1);
        Reject(() => Read(), "A reply with a nonzero reserved field was accepted.");
        U32(response, 4, 0);
        U32(control, 24, 2);
        Reject(() => Read(), "A reply across a version mismatch was accepted.");

        RejectArgument(() => P.GrantItem(ItemHash, 0), "A zero grant quantity was encoded.");
        RejectArgument(() => P.SetQuantity(LiveInventoryScope.CharacterItem, 0, ItemHash, 1, 1, 0),
            "An instanced item without its instance was encoded.");
        RejectArgument(() => P.SeasonPassRow(256, ItemHash), "A row past the pass capacity was encoded.");
        RejectArgument(() => LiveInvestmentOperation.Create(Bound with { ContentHash = 0 }, Started),
            "An operation without the game's catalog was created.");
    }

    /** The unlockValues request and answer: sources and values at the native offsets, every state and spare byte checked. */
    private static void Unlocks()
    {
        var request = P.UnlockValues(Sources);
        Check(request.Length == 40 && BinaryPrimitives.ReadUInt16LittleEndian(request) == Sources.Length
            && request.AsSpan(2, 2).IndexOfAnyExcept((byte)0) < 0
            && request[4] == (byte)LiveUnlockKind.Flag && request[5] == (byte)LiveUnlockBank.Account
            && BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(6)) == 11
            && request[4 + 16] == (byte)LiveUnlockKind.Value && request[4 + 16 + 1] == (byte)LiveUnlockBank.Character
            && BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(4 + 16 + 2)) == 0xFFFF
            && request.AsSpan(4 + Sources.Length * 4).IndexOfAnyExcept((byte)0) < 0, "The unlock request layout differs.");
        var block = new byte[80];
        P.EncodeRequest(block, LiveInvestmentOpcode.UnlockValues, 0, default, request);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(block) == 12
            && BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(4)) == 40
            && block.AsSpan(8, 32).IndexOfAnyExcept((byte)0) < 0 && block.AsSpan(40).SequenceEqual(request),
            "The unlock request block differs.");
        RejectArgument(() => P.EncodeRequest(block, LiveInvestmentOpcode.UnlockValues, 0, Bound, request),
            "An unlock read carried a context.");
        RejectArgument(() => P.EncodeRequest(block, LiveInvestmentOpcode.UnlockValues, 1, default, request),
            "An unlock read carried an operation id.");
        RejectArgument(() => P.UnlockValues([]), "An unlock read of no source was encoded.");
        RejectArgument(() => P.UnlockValues([.. Enumerable.Repeat(Sources[0], 10)]), "An unlock read past capacity was encoded.");
        RejectArgument(() => P.UnlockValues([new(LiveUnlockKind.Value, LiveUnlockBank.Profile, 1)]),
            "A profile value was encoded.");
        RejectArgument(() => P.UnlockValues([default]), "An all-zero source was encoded.");

        var values = P.ReadUnlockValues(Reply(LiveInvestmentOpcode.UnlockValues, Bound, UnlockPayload()), Sources);
        Check(values is { Values.Count: 5 } && values.Context == Bound
            && values.Values.Select(value => value.Source).SequenceEqual(Sources)
            && values.Values[0] is { Known: true, Value: 1 } && values.Values[1] is { Known: true, Value: 0 }
            && values.Values[3] is { Known: true, Value: -40 } && values.Values[4] is { Known: false, Value: 0 },
            "The unlock values layout differs.");
        void Refuse(byte[] payload, string message) =>
            Reject(() => P.ReadUnlockValues(Reply(LiveInvestmentOpcode.UnlockValues, Bound, payload), Sources), message);
        Refuse(Changed8(UnlockPayload(), 1, 1), "A nonzero value reserved byte was accepted.");
        Refuse(Changed8(UnlockPayload(), 2, 1), "A nonzero second value reserved field was accepted.");
        Refuse(Changed8(UnlockPayload(), 8, 2), "An unknown unlock read state was accepted.");
        Refuse(Changed(UnlockPayload(), 4, 2), "A flag reading 2 was accepted.");
        Refuse(Changed(UnlockPayload(), 4 * 8 + 4, 1), "An invalid row with a value was accepted.");
        Refuse(Changed(UnlockPayload(), Sources.Length * 8 + 4, 1), "A value past the requested sources was accepted.");
    }

    /** setPursuitObjective and completePursuitObjectives at the native offsets, with the whole int32 range for a value row. */
    private static void PursuitEdits()
    {
        const ulong instance = 0x4000000000000100;
        const uint objective = 0x80A1C0DE;
        foreach (int value in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
        {
            var set = P.SetPursuitObjective(instance, ItemHash, -7, objective, 6, value, ~value);
            Check(set.Length == 32 && BinaryPrimitives.ReadUInt64LittleEndian(set) == instance
                && BinaryPrimitives.ReadUInt32LittleEndian(set.AsSpan(8)) == ItemHash
                && BinaryPrimitives.ReadInt32LittleEndian(set.AsSpan(12)) == -7
                && BinaryPrimitives.ReadUInt32LittleEndian(set.AsSpan(16)) == objective && set[20] == 6
                && set.AsSpan(21, 3).IndexOfAnyExcept((byte)0) < 0
                && BinaryPrimitives.ReadInt32LittleEndian(set.AsSpan(24)) == value
                && BinaryPrimitives.ReadInt32LittleEndian(set.AsSpan(28)) == ~value,
                $"The set objective request for {value} differs.");
        }
        var complete = P.CompletePursuitObjectives(instance, ItemHash, int.MinValue);
        Check(complete.Length == 16 && BinaryPrimitives.ReadUInt64LittleEndian(complete) == instance
            && BinaryPrimitives.ReadUInt32LittleEndian(complete.AsSpan(8)) == ItemHash
            && BinaryPrimitives.ReadInt32LittleEndian(complete.AsSpan(12)) == int.MinValue,
            "The complete objectives request differs.");
        RejectArgument(() => P.SetPursuitObjective(0, ItemHash, 0, objective, 0, 0, 1),
            "A set objective without an instance was encoded.");
        RejectArgument(() => P.SetPursuitObjective(instance, ItemHash, 0, objective, 7, 0, 1),
            "An ordinal past the objective capacity was encoded.");
        RejectArgument(() => P.CompletePursuitObjectives(instance, 0, 0), "A completion without an item hash was encoded.");

        var block = new byte[80];
        P.EncodeRequest(block, LiveInvestmentOpcode.CompletePursuitObjectives, 5, Bound, complete);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(block) == 14
            && BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(4)) == 16
            && BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(8)) == 5
            && BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(24)) == Character
            && block.AsSpan(40).StartsWith(complete) && block.AsSpan(56).IndexOfAnyExcept((byte)0) < 0,
            "The complete objectives request block differs.");
        RejectArgument(() => P.EncodeRequest(block, LiveInvestmentOpcode.CompletePursuitObjectives, 0, Bound, complete),
            "A completion without an operation id was encoded.");
        RejectArgument(() => P.EncodeRequest(block, LiveInvestmentOpcode.CompletePursuitObjectives + 1, 5, Bound,
            []), "Opcode 15 was encoded.");
    }

    /** One persistent mapping: hello, snapshots, a receipt's whole life, stale answers, timeouts and a version change. */
    private static async Task Connection()
    {
        int pid = Environment.ProcessId;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = limit.Token;
        using var connection = new LiveInvestmentConnection(pid);
        using var mapping = await OpenAsync(pid, token);
        using var view = mapping.CreateViewAccessor();
        Check(view.ReadUInt32(0) == P.Magic && view.ReadUInt32(4) == 3 && view.ReadUInt32(8) == 45536 && view.ReadInt32(12) == pid
            && view.ReadInt64(16) != 0, "The investment mapping header was not written.");
        Check(connection.State == LiveInvestmentLinkState.Opening, "The link connected before the game wrote its version.");
        view.Write(28, (uint)pid); view.Write(32, Started); view.Write(24, 3u);
        await UntilAsync(() => connection.State == LiveInvestmentLinkState.Connected, token);
        var game = new FakeGame(view);

        var helloTask = connection.HelloAsync(token);
        uint request = await game.NextAsync(token);
        Check(game.Opcode == LiveInvestmentOpcode.Hello && game.Block().AsSpan(4).IndexOfAnyExcept((byte)0) < 0,
            "The hello request carries a payload, an operation or a context.");
        // An answer under the previous request id selects no character. Had the connection taken it, its hello would fail.
        game.Answer(request, LiveInvestmentStatus.Ok, Bound, Changed8(HelloPayload(), 8 + 9, 0), answerId: request - 1);
        await game.ServicedAsync(token);
        game.Answer(request, LiveInvestmentStatus.Ok, Bound, HelloPayload());
        var hello = await helloTask.WaitAsync(token);
        Check(hello.Characters[0].Selected, "An answer to an older request id was taken.");
        // The connection sends only operations made from the game's last hello, so one from another game start never goes out.
        Check(hello.Context == Bound
            && connection.GrantItemAsync(LiveInvestmentOperation.Create(hello.Context, hello.GameStarted + 1),
            ItemHash, 1, token).IsFaulted, "The hello was not adopted.");
        // A written mutation stays unresolved, and is refused before it is sent again, until the game refuses it, forgets it or
        // reports it never arrived. A resolved one goes out again, which the game refuses as reused.
        void Unresolved(LiveInvestmentOperation sent, string message) => Check(connection.GrantItemAsync(sent, ItemHash, 1, token)
            is { IsFaulted: true, Exception.InnerException: InvalidOperationException }, message);
        async Task Resolved(LiveInvestmentOperation sent, string message)
        {
            var again = connection.GrantItemAsync(sent, ItemHash, 1, token);
            Check(!again.IsCompleted, message);
            game.Answer(await game.NextAsync(token), LiveInvestmentStatus.Refused, default, [], "operation_reused");
            await Rejected<InvalidOperationException>(again, message);
        }

        var inventoryTask = connection.InventoryAsync(token);
        game.Answer(await game.NextAsync(token), LiveInvestmentStatus.Ok, Bound, Changed(InventoryPayload(), 25232 + 16, 1));
        await Rejected<InvalidDataException>(inventoryTask, "A snapshot with nonzero padding was accepted.");
        inventoryTask = connection.InventoryAsync(token);
        game.Answer(await game.NextAsync(token), LiveInvestmentStatus.Ok, Bound, InventoryPayload());
        Check((await inventoryTask.WaitAsync(token)).Items.Count == 2, "A partial inventory was not read.");

        // A read of saved rows carries its sources and nothing else, and its answer is matched to them.
        var unlocks = connection.UnlockValuesAsync(Sources, token);
        request = await game.NextAsync(token);
        Check(game.Opcode == LiveInvestmentOpcode.UnlockValues && game.Operation == 0 && game.Expected == default
            && game.Block().AsSpan(40).SequenceEqual(P.UnlockValues(Sources)), "The unlock read was not sent as encoded.");
        game.Answer(request, LiveInvestmentStatus.Ok, Bound, UnlockPayload());
        Check((await unlocks.WaitAsync(token)).Values[3].Value == -40, "The unlock values were not read.");
        unlocks = connection.UnlockValuesAsync(Sources, token);
        game.Answer(await game.NextAsync(token), LiveInvestmentStatus.Refused, default, [], "account_state");
        await Rejected<InvalidOperationException>(unlocks, "A refused unlock read was accepted.");

        var operation = LiveInvestmentOperation.Create(hello.Context, hello.GameStarted);
        var grant = connection.GrantItemAsync(operation, ItemHash, 3, token);
        request = await game.NextAsync(token);
        var block = game.Block();
        Check(game.Opcode == LiveInvestmentOpcode.GrantItem && game.Operation == operation.Id && game.Expected == Bound
            && BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(4)) == 8
            && BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(40)) == ItemHash
            && BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(44)) == 3 && block.AsSpan(48).IndexOfAnyExcept((byte)0) < 0,
            "The grant request layout differs.");
        Unresolved(operation, "A written mutation was not unresolved.");
        game.Answer(request, LiveInvestmentStatus.Ok, Bound, Receipt(LiveReceiptState.Queued));
        Check(await grant.WaitAsync(token) is { State: LiveReceiptState.Queued, Terminal: false },
            "The queued receipt was not read.");
        await Rejected<InvalidOperationException>(connection.GrantItemAsync(operation, ItemHash, 3, token),
            "An unresolved operation was sent again.");

        var read = connection.ReadReceiptAsync(operation.Id, token);
        request = await game.NextAsync(token);
        Check(game.Opcode == LiveInvestmentOpcode.ReadReceipt && game.Operation == operation.Id && game.Expected == default
            && BinaryPrimitives.ReadUInt32LittleEndian(game.Block().AsSpan(4)) == 0, "The receipt request layout differs.");
        game.Answer(request, LiveInvestmentStatus.Ok, Bound, Receipt(LiveReceiptState.Published));
        Check(await read.WaitAsync(token) is { State: LiveReceiptState.Published, Terminal: true },
            "The published receipt was not read.");
        Unresolved(operation, "Reading a published receipt resolved its operation.");
        var acknowledge = connection.AcknowledgeAsync(operation.Id, token);
        game.Answer(await game.NextAsync(token), LiveInvestmentStatus.Ok, Bound, Receipt(LiveReceiptState.Published));
        await acknowledge.WaitAsync(token);
        await Resolved(operation, "An acknowledged operation stayed unresolved.");

        // A refused mutation arrives as its receipt, with the label in the reply's reason.
        var completion = LiveInvestmentOperation.Create(hello.Context, hello.GameStarted);
        var completeTask = connection.CompletePursuitObjectivesAsync(completion, 0x4000000000000100, ItemHash, 4, token);
        request = await game.NextAsync(token);
        Check(game.Opcode == LiveInvestmentOpcode.CompletePursuitObjectives, "The complete objectives request was not sent.");
        game.Answer(request, LiveInvestmentStatus.Ok, Bound, Receipt(LiveReceiptState.Refused), "objectives_complete");
        Check(await completeTask.WaitAsync(token) is { State: LiveReceiptState.Refused, Reason: "objectives_complete" },
            "A refused completion receipt was not read.");
        var dropped = connection.AcknowledgeAsync(completion.Id, token);
        game.Answer(await game.NextAsync(token), LiveInvestmentStatus.Ok, default, new byte[8]);
        await dropped.WaitAsync(token);
        await Resolved(completion, "An acknowledged completion stayed unresolved.");

        // A pending receipt refuses its acknowledgement, which leaves the operation unresolved.
        var pending = LiveInvestmentOperation.Create(hello.Context, hello.GameStarted);
        var queued = connection.SetSeasonalExperienceAsync(pending, 10, 20, token);
        game.Answer(await game.NextAsync(token), LiveInvestmentStatus.Ok, Bound, Receipt(LiveReceiptState.Queued));
        await queued.WaitAsync(token);
        var early = connection.AcknowledgeAsync(pending.Id, token);
        game.Answer(await game.NextAsync(token), LiveInvestmentStatus.Refused, default, [], "receipt_pending");
        await Rejected<InvalidOperationException>(early, "Acknowledging a pending receipt was accepted.");
        Unresolved(pending, "A refused acknowledgement resolved its operation.");

        var reused = LiveInvestmentOperation.Create(hello.Context, hello.GameStarted);
        var quantity = connection.SetQuantityAsync(reused, LiveInventoryScope.CharacterItem,
            0x4000000000000100, ItemHash, 4, 1, 0, token);
        request = await game.NextAsync(token);
        block = game.Block();
        Check(game.Expected == Bound && block[40] == (byte)LiveInventoryScope.CharacterItem
            && BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(48)) == 0x4000000000000100
            && BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(56)) == ItemHash
            && BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(60)) == 4
            && BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(64)) == 1
            && BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(68)) == 0
            && block.AsSpan(41, 7).IndexOfAnyExcept((byte)0) < 0, "The set quantity request layout differs.");
        game.Answer(request, LiveInvestmentStatus.Refused, default, [], "operation_reused");
        await Rejected<InvalidOperationException>(quantity, "A submission the game did not retain was accepted.");
        await Resolved(reused, "A submission the game did not retain stayed unresolved.");

        var lost = LiveInvestmentOperation.Create(hello.Context, hello.GameStarted);
        var silent = connection.SetQuantityAsync(lost, LiveInventoryScope.CharacterStack, 0, StackHash, 2, 3, 5, token);
        uint unanswered = await game.NextAsync(token);
        game.Skip(unanswered);
        try { await silent.WaitAsync(token); throw new InvalidDataException("A mutation without an answer completed."); }
        catch (LiveInvestmentOutcomeUnknownException error) { Check(error.Operation == lost, "The unknown outcome names another operation."); }
        Unresolved(lost, "A timed-out mutation was not kept unresolved.");

        var check = connection.ReadReceiptAsync(lost.Id, token);
        request = await game.NextAsync(token);
        // The late answer is a queued receipt and the real one unknown, so the receipt read shows which one was taken.
        game.Answer(unanswered, LiveInvestmentStatus.Ok, Bound, Receipt(LiveReceiptState.Queued), answerId: unanswered);
        await game.ServicedAsync(token);
        game.Answer(request, LiveInvestmentStatus.Ok, default, new byte[8]);
        Check((await check.WaitAsync(token)).State == LiveReceiptState.Unknown,
            "A late answer to a timed-out request was taken.");
        await Resolved(lost, "A receipt the game never received did not resolve the operation.");

        view.Write(24, 4u);
        await UntilAsync(() => connection.State == LiveInvestmentLinkState.VersionDiffers, token);
        Check(connection.Diagnostic.Contains("differs"), "A version mismatch has no note.");
        // A mutation that cannot be written fails as not sent, so the editor knows nothing changed.
        var unsent = LiveInvestmentOperation.Create(hello.Context, hello.GameStarted);
        await Rejected<LiveInvestmentNotSentException>(connection.GrantItemAsync(unsent, ItemHash, 1, token),
            "A mutation across a version mismatch was not reported as not sent.");
        await Rejected<InvalidOperationException>(connection.HelloAsync(token), "A request was sent across a version mismatch.");
        connection.Dispose();
        Check(view.ReadInt64(16) == 0, "Dispose did not tell the game to drop the link.");
    }

    /** Studio creates the mapping on its own task; the game only opens it. */
    private static async Task<MemoryMappedFile> OpenAsync(int pid, CancellationToken token)
    {
        while (true)
        {
            try { return MemoryMappedFile.OpenExisting(P.MappingName(pid)); }
            catch (FileNotFoundException) { await Task.Delay(5, token); }
        }
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(5, token);
    }

    /** Answers like the server at the native mapping offsets: response fields first, then the response id. */
    private sealed class FakeGame(MemoryMappedViewAccessor view)
    {
        private uint served;
        public LiveInvestmentOpcode Opcode => (LiveInvestmentOpcode)view.ReadUInt32(48);
        public ulong Operation => view.ReadUInt64(56);
        public LiveInvestmentContext Expected => new(view.ReadUInt64(64), view.ReadUInt64(72), view.ReadUInt64(80));

        public async Task<uint> NextAsync(CancellationToken token)
        {
            while (true)
            {
                uint request = view.ReadUInt32(40);
                if (request != 0 && request != served) { Thread.MemoryBarrier(); return request; }
                await Task.Delay(1, token);
            }
        }

        public byte[] Block()
        {
            var block = new byte[80];
            view.ReadArray(48, block, 0, block.Length);
            return block;
        }

        public void Skip(uint request) => served = request;

        /**
         * Returns once Studio has serviced the mapping in full after every write made before the call. A pass writes the
         * heartbeat and then reads the response under one lock, but its read may precede its own heartbeat write. So the
         * second pass to write a new heartbeat sees the earlier writes, and a third starts only once the second has finished.
         */
        public async Task ServicedAsync(CancellationToken token)
        {
            Thread.MemoryBarrier();
            long seen = view.ReadInt64(16);
            for (int passes = 0; passes < 3;)
            {
                await Task.Delay(1, token);
                long beat = view.ReadInt64(16);
                if (beat != seen) { seen = beat; passes++; }
            }
        }

        public void Answer(uint request, LiveInvestmentStatus status, LiveInvestmentContext context,
            byte[] payload, string reason = "",
            uint? answerId = null)
        {
            view.Write(128, (uint)status);
            view.Write(136, view.ReadUInt64(56));
            view.Write(144, view.ReadUInt32(48));
            view.Write(148, (uint)payload.Length);
            view.Write(152, context.AccountSoid);
            view.Write(160, context.CharacterSoid);
            view.Write(168, context.ContentHash);
            var text = new byte[64];
            Encoding.ASCII.GetBytes(reason, text);
            view.WriteArray(176, text, 0, text.Length);
            view.WriteArray(240, payload, 0, payload.Length);
            Thread.MemoryBarrier();
            view.Write(44, answerId ?? request);
            if (answerId is null) served = request;
        }
    }

    /** Items and buckets read, two characters with the first selected, and a client signed in to the account. */
    private static byte[] HelloPayload()
    {
        var bytes = new byte[56];
        U32(bytes, 0, (uint)(LiveInvestmentReadiness.Items | LiveInvestmentReadiness.Buckets));
        bytes[4] = 2; bytes[5] = 1;
        U64(bytes, 8, Character); bytes[8 + 8] = 2; bytes[8 + 9] = 1;
        U64(bytes, 8 + 16, OtherCharacter);
        return bytes;
    }

    private static byte[] InventoryPayload()
    {
        var bytes = new byte[31584];
        U16(bytes, 0, 1); U16(bytes, 2, 2); U16(bytes, 4, 1); U16(bytes, 6, 2);
        U64(bytes, 8, 0x5000000000000001); U32(bytes, 8 + 8, StackHash); I32(bytes, 8 + 12, 25); I32(bytes, 8 + 16, 4); bytes[8 + 20] = 2;
        for (int index = 0; index < 2; index++)
        {
            int item = 16832 + index * 24;
            U64(bytes, item, 0x4000000000000200UL + (ulong)index); U32(bytes, item + 8, ItemHash); I32(bytes, item + 12, 1);
            I32(bytes, item + 16, 6 + index);
            bytes[item + 20] = index == 0 ? (byte)3 : (byte)0xFF;
            bytes[item + 21] = index == 0 ? (byte)0 : (byte)0xFF;
        }
        U32(bytes, 25232, StackHash); I32(bytes, 25232 + 4, 3); I32(bytes, 25232 + 8, 8); bytes[25232 + 12] = 5;
        bytes[30560] = 2; bytes[30560 + 1] = 0xFF; U16(bytes, 30560 + 2, 10);
        bytes[30564] = 3; bytes[30564 + 1] = 16; U16(bytes, 30564 + 2, 9);
        return bytes;
    }

    /** One held bounty with known objectives: the second objective at 2 of 5. */
    private static byte[] PursuitPayload()
    {
        var bytes = new byte[45296];
        U16(bytes, 0, 1);
        U64(bytes, 8, 0x4000000000000100); U32(bytes, 8 + 8, ItemHash); I32(bytes, 8 + 12, 9); U32(bytes, 8 + 16, 2);
        bytes[8 + 20] = (byte)(LivePursuitFlags.Bounty | LivePursuitFlags.ObjectivesKnown); bytes[8 + 21] = 2;
        int objective = 8 + 24 + 16;
        U32(bytes, objective, 0x1234); I32(bytes, objective + 4, 2); I32(bytes, objective + 8, 5);
        bytes[objective + 12] = (byte)LivePursuitObjectiveFlags.Known;
        return bytes;
    }

    /** Values for Sources: account flag set, profile flag clear, character flag set, value -40, and an invalid row. */
    private static byte[] UnlockPayload()
    {
        var bytes = new byte[72];
        int[] values = [1, 0, 1, -40];
        for (int index = 0; index < values.Length; index++) { bytes[index * 8] = 1; I32(bytes, index * 8 + 4, values[index]); }
        return bytes;
    }

    private static void PassRow(byte[] bytes, int index, uint hash, byte rank, LiveSeasonPassRowFlags flags)
    {
        int row = 8 + index * 8;
        U32(bytes, row, hash); bytes[row + 4] = rank; U16(bytes, row + 6, (ushort)flags);
    }

    private static byte[] Receipt(LiveReceiptState state, LiveInvestmentWait wait = LiveInvestmentWait.None)
    {
        var bytes = new byte[8];
        U32(bytes, 0, (uint)state); U32(bytes, 4, (uint)wait);
        return bytes;
    }

    private static LiveInvestmentReply Reply(LiveInvestmentOpcode opcode, LiveInvestmentContext context,
        byte[] payload, ulong operationId = 0) =>
        new(LiveInvestmentStatus.Ok, "", opcode, operationId, context, Started, payload);

    private static byte[] Changed(byte[] bytes, int offset, int value) { U32(bytes, offset, (uint)value); return bytes; }
    private static byte[] Changed16(byte[] bytes, int offset, int value) { U16(bytes, offset, (ushort)value); return bytes; }
    private static byte[] Changed8(byte[] bytes, int offset, byte value) { bytes[offset] = value; return bytes; }
    private static void U16(byte[] bytes, int at,
        ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
    private static void U32(byte[] bytes, int at,
        uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void I32(byte[] bytes, int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), value);
    private static void U64(byte[] bytes, int at,
        ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), value);
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }

    private static void Reject(Action action, string message)
    { try { action(); } catch (InvalidDataException) { return; } throw new InvalidDataException(message); }

    private static void RejectArgument(Action action, string message)
    { try { action(); } catch (ArgumentException) { return; } throw new InvalidDataException(message); }

    private static async Task Rejected<T>(Task task, string message) where T : Exception
    { try { await task; } catch (T) { return; } throw new InvalidDataException(message); }
}
