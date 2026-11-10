using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SunriseStudio.Live;

/** The game serves every opcode between None and the last one. */
public enum LiveInvestmentOpcode : uint
{
    None, Hello, InventorySnapshot, SeasonPassSnapshot, PursuitSnapshot, GrantItem, SetQuantity, SetSeasonalExperience,
    ClaimSeasonPassReward, ResetSeasonPassClaim, ReadReceipt, AcknowledgeReceipt, UnlockValues, SetPursuitObjective,
    CompletePursuitObjectives
}

/** The transport outcome of one request. A mutation's own outcome is in its receipt. Only an Ok answer carries a payload. */
public enum LiveInvestmentStatus : uint { None, Ok, Invalid, Refused }
/** Unknown means the game holds no receipt with that id in this process. */
public enum LiveReceiptState : uint { Unknown, Queued, Deferred, Refused, Published, CommittedUnpublished }
/** Why a non-terminal operation is waiting. */
public enum LiveInvestmentWait : uint { None, PublicationPeer, AccountResync, Catalog }
/** The saved row array a held row lives in: the account's profile, the character's instances or its stacks. */
public enum LiveInventoryScope : byte { Profile, CharacterItem, CharacterStack }

/** The game catalogs that finished loading. */
[Flags]
public enum LiveInvestmentReadiness : uint
{
    None = 0, Items = 1, ItemDetails = 2, Buckets = 4, Progressions = 8, SeasonPass = 16, Rewards = 32, Objectives = 64,
    Pursuits = 128
}
/** What the game says of one pass row: whether it exists, is claimed or acquired, and how its rank and condition stand. */
[Flags]
public enum LiveSeasonPassRowFlags : ushort
{
    None = 0, Available = 1, Claimed = 2, Acquired = 4, Automatic = 8, RankMet = 16, ConditionKnown = 32, ConditionMet = 64
}
/** What the game says of one held pursuit. ObjectivesKnown is clear when it cannot read the pursuit's contract. */
[Flags]
public enum LivePursuitFlags : byte { None = 0, Bounty = 1, ObjectivesKnown = 2, ObjectivesComplete = 4 }
/** What the game says of one objective. Skipped is an optional objective whose saved row it did not read. */
[Flags]
public enum LivePursuitObjectiveFlags : byte { None = 0, Known = 1, Complete = 2, Skipped = 4 }
/** The kind of saved row a source reads. Zero is never valid, so unused source slots stay zero. */
public enum LiveUnlockKind : byte { Flag = 1, Value }
/** The saved bank a source reads. A value source names the account or character bank, never the profile bank. */
public enum LiveUnlockBank : byte { Account = 1, Profile, Character }

/** The account, selected character and package content one request or answer is bound to. */
public readonly record struct LiveInvestmentContext(ulong AccountSoid, ulong CharacterSoid, ulong ContentHash)
{
    /** True when every part is set, as a mutation's expected context must be. */
    public bool Complete => AccountSoid != 0 && CharacterSoid != 0 && ContentHash != 0;
}

/** One answer copied out of the mapping. GameStarted comes from the control block, and Payload holds the declared bytes. */
public sealed record LiveInvestmentReply(LiveInvestmentStatus Status, string Reason, LiveInvestmentOpcode Opcode,
    ulong OperationId, LiveInvestmentContext Context, ulong GameStarted, byte[] Payload);

/** One character of the live account. At most one is selected. */
public sealed record LiveInvestmentCharacter(ulong Soid, byte CharacterClass, bool Selected);

/**
 * The live account's characters and the catalogs the game has read. GameStarted identifies the game process instance, and
 * receipts do not outlive it. PeerOwnsAccount is true when a signed-in game client bound to this account can show a change.
 */
public sealed record LiveInvestmentHello(LiveInvestmentContext Context, LiveInvestmentReadiness Readiness, ulong GameStarted,
    IReadOnlyList<LiveInvestmentCharacter> Characters, bool PeerOwnsAccount);

/** One installed bucket's row count. EquipmentSlot is -1 for a bucket that equips nothing. */
public sealed record LiveBucket(byte BucketId, ushort SlotCount, sbyte EquipmentSlot);
/** BucketId is NoBucket when the definition names no installed bucket. */
public sealed record LiveProfileRow(ulong Instance, uint DefinitionHash, int Quantity, int MutationSerial, byte BucketId);
/** One instance the selected character holds. EquipmentSlot is Unequipped for one that is not worn. */
public sealed record LiveCharacterItem(ulong Instance, uint DefinitionHash, int Quantity, int MutationSerial,
    byte BucketId, byte EquipmentSlot)
{
    /** True when the instance fills an equipment slot. */
    public bool Equipped => EquipmentSlot != LiveInvestmentProtocol.Unequipped;
}
/** One stack the selected character holds. A stack has no instance of its own. */
public sealed record LiveCharacterStack(uint DefinitionHash, int Quantity, int MutationSerial, byte BucketId);
/** The selected character and the profile, read under one game lock. */
public sealed record LiveInventorySnapshot(LiveInvestmentContext Context, IReadOnlyList<LiveBucket> Buckets,
    IReadOnlyList<LiveProfileRow> Profile, IReadOnlyList<LiveCharacterItem> Items, IReadOnlyList<LiveCharacterStack> Stacks);

/** Index is the game's claim index. An unavailable placeholder row has item hash zero. */
public sealed record LiveSeasonPassRow(ushort Index, uint ItemHash, byte RequiredRank, LiveSeasonPassRowFlags Flags);
/** The account's seasonal XP, the rank it gives and every row of the game's pass. */
public sealed record LiveSeasonPassSnapshot(LiveInvestmentContext Context, int Experience, ushort Rank,
    IReadOnlyList<LiveSeasonPassRow> Rows);

/** One objective as the game evaluated it. A flag objective's Value is 0 or 1. */
public sealed record LivePursuitObjective(uint ObjectiveHash, int Value, int Target, LivePursuitObjectiveFlags Flags);
/** One held pursuit with its saved item flags and its objectives in the order of its installed contract. */
public sealed record LiveHeldPursuit(ulong Instance, uint ItemHash, int MutationSerial, uint ItemFlags, LivePursuitFlags Flags,
    IReadOnlyList<LivePursuitObjective> Objectives)
{
    /** The saved item flag of a tracked item (Sunrise state/account/inventory/item_state.h kTrackedItemFlag). */
    private const uint TrackedItemFlag = 0x2;

    /** True when the player tracks this pursuit. */
    public bool Tracked => (ItemFlags & TrackedItemFlag) != 0;
}
/** The selected character's held pursuits. */
public sealed record LivePursuitSnapshot(LiveInvestmentContext Context, IReadOnlyList<LiveHeldPursuit> Held);

/** One saved flag or value row. Index is the row inside its bank, bound from the installed slot tables. */
public readonly record struct LiveUnlockSource(LiveUnlockKind Kind, LiveUnlockBank Bank, ushort Index);
/** Known is false when the bank cannot hold this row, and Value is then 0. A known flag reads 0 or 1. */
public sealed record LiveUnlockValue(LiveUnlockSource Source, bool Known, int Value);
/** Saved rows read for the selected character under one game lock, in the order they were requested. */
public sealed record LiveUnlockSnapshot(LiveInvestmentContext Context, IReadOnlyList<LiveUnlockValue> Values);

/**
 * The game's record of one mutation. Reason is the label sent with this answer: why the mutation was refused, or why a
 * published grant stopped part way. Context is the target the game bound the operation to.
 */
public sealed record LiveInvestmentReceipt(ulong OperationId, LiveReceiptState State, string Reason, LiveInvestmentWait Wait,
    LiveInvestmentContext Context)
{
    /** True once the game has finished with the mutation, so the receipt no longer changes. */
    public bool Terminal => State is LiveReceiptState.Refused or LiveReceiptState.Published or LiveReceiptState.CommittedUnpublished;
}

/** A mutation's identity. Studio never reuses an id, so an acknowledged receipt cannot be mistaken for a new one. */
public sealed record LiveInvestmentOperation(ulong Id, LiveInvestmentContext Expected, ulong GameStarted)
{
    /** Draws a random nonzero id bound to the target and the game instance it was read from. */
    public static LiveInvestmentOperation Create(LiveInvestmentContext expected, ulong gameStarted)
    {
        if (!expected.Complete || gameStarted == 0)
            throw new ArgumentException("An operation needs an account, a selected character and the game's catalog.",
                nameof(expected));
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        ulong id;
        do { RandomNumberGenerator.Fill(bytes); id = BinaryPrimitives.ReadUInt64LittleEndian(bytes); } while (id == 0);
        return new(id, expected, gameStarted);
    }
}

/**
 * Mirrors the investment mapping in Sunrise's middleware/studio/studio_investment_wire.h. Offsets are absolute in the
 * mapping, except payload field offsets, which count from the start of their payload or record.
 */
public static class LiveInvestmentProtocol
{
    /** The mapping's magic word, "SSIV" in memory, and the layout version both sides must share. */
    public const uint Magic = 0x56495353, Version = 3;
    /** Sizes of the control block, the request block, the response header and the reason label inside it. */
    public const int ControlBytes = 48, RequestBlockBytes = 80, ResponseHeaderBytes = 112, ReasonBytes = 64;
    /** The room for a request's payload and for an answer's, which the largest snapshot fills. */
    public const int RequestPayloadBytes = 40, ResponsePayloadBytes = PursuitBytes;
    /** Where the request block, the response block and the response payload start. */
    public const int RequestBlockOffset = 48, ResponseBlockOffset = 128, ResponsePayloadOffset = 240;
    /** The whole mapping's size. */
    public const int MappingBytes = ResponsePayloadOffset + ResponsePayloadBytes;
    // Mirror investment_wire::kConnectIntervalMs and kHeartbeatTimeoutMs.
    public const int ConnectIntervalMilliseconds = 1000, HeartbeatTimeoutMilliseconds = 3000;

    /** Control block fields Studio writes: magic, version, mapping size, its process id and its heartbeat. */
    public const int MagicOffset = 0, VersionOffset = 4, BytesOffset = 8, EditorPidOffset = 12, HeartbeatOffset = 16;
    /** Control block fields the game writes, then the request id Studio writes and the response id the game answers with. */
    public const int GameVersionOffset = 24, GamePidOffset = 28, GameStartedOffset = 32, RequestOffset = 40, ResponseOffset = 44;
    /** Request block fields: opcode, payload size, operation id and the expected context. */
    public const int OpcodeOffset = 48, RequestPayloadBytesOffset = 52, OperationOffset = 56, ExpectedOffset = 64;
    /** Where a request's payload starts. */
    public const int RequestPayloadOffset = 88;
    /** Response header fields: status, a reserved word, the operation id and the opcode answered. */
    public const int StatusOffset = 128, ResponseReservedOffset = 132, ResponseOperationOffset = 136, ResponseOpcodeOffset = 144;
    /** Response header fields: payload size, the context the answer is bound to and the reason label. */
    public const int ResponsePayloadBytesOffset = 148, ContextOffset = 152, ReasonOffset = 176;

    /** Request payload sizes of the grant, quantity, XP and pass row mutations. */
    public const int GrantItemBytes = 8, SetQuantityBytes = 32, SetSeasonalExperienceBytes = 8, SeasonPassRowBytes = 8;
    /** GrantItemRequest fields. */
    public const int GrantHashOffset = 0, GrantQuantityOffset = 4;
    /** SetQuantityRequest fields that name the row. */
    public const int SetScopeOffset = 0, SetInstanceOffset = 8, SetHashOffset = 16;
    /** SetQuantityRequest fields the game compares with the row, then the new quantity. */
    public const int SetExpectedSerialOffset = 20, SetExpectedQuantityOffset = 24, SetQuantityOffset = 28;
    /** SetSeasonalExperienceRequest fields. */
    public const int ExperienceExpectedOffset = 0, ExperienceReplacementOffset = 4;
    /** Fields of a pass row claim or claim reset. */
    public const int PassRowIndexOffset = 0, PassHashOffset = 4;

    /** Answer payload sizes of Hello, the three snapshots and a receipt. */
    public const int HelloBytes = 56, InventoryBytes = 31584, SeasonPassBytes = 2056, PursuitBytes = 45296, ReceiptBytes = 8;
    /** Rows the fixed arrays of Hello and the inventory snapshot hold. */
    public const int CharacterCapacity = 3, BucketCapacity = 255, ProfileRowCapacity = 701, CharacterStackCapacity = 333;
    /** The native character array's 350 rows: 333 unequipped rows, which include held pursuits, and 17 equipment slots. */
    public const int UnequippedItemCapacity = 333, EquipmentSlotCount = 17;
    /** Instances one character holds, worn or not. */
    public const int CharacterItemCapacity = UnequippedItemCapacity + EquipmentSlotCount;
    /** Rows the pass snapshot and the pursuit snapshot hold, and the objectives of one held pursuit. */
    public const int SeasonPassRowCapacity = 256, HeldPursuitCapacity = UnequippedItemCapacity, PursuitObjectiveCapacity = 7;
    /** The bucket id of a row whose definition names no installed bucket, and the equipment slot of an instance not worn. */
    public const byte NoBucket = 0xFF, Unequipped = 0xFF;
    /** The equipment slot of a bucket that equips nothing. */
    public const sbyte NoEquipmentSlot = -1;

    /** Hello fields before its character array. */
    public const int HelloReadinessOffset = 0, HelloCharacterCountOffset = 4, HelloPeerOffset = 5, HelloReservedOffset = 6;
    /** Where Hello's characters start and the size of one. */
    public const int HelloCharactersOffset = 8, CharacterBytes = 16;
    /** Fields of one Hello character. */
    public const int CharacterSoidOffset = 0, CharacterClassOffset = 8, CharacterSelectedOffset = 9, CharacterReservedOffset = 10;

    /** The inventory snapshot's row counts. */
    public const int ProfileCountOffset = 0, ItemCountOffset = 2, StackCountOffset = 4, BucketCountOffset = 6;
    /** Where the inventory snapshot's arrays start, and its reserved tail. */
    public const int ProfileOffset = 8, ItemsOffset = 16832, StacksOffset = 25232,
        BucketsOffset = 30560, InventoryTailOffset = 31580;
    /** Sizes of one profile row, character item, character stack and bucket. */
    public const int ProfileRowBytes = 24, ItemBytes = 24, StackBytes = 16, BucketBytes = 4;
    /** Fields a profile row and a character item share. */
    public const int RowInstanceOffset = 0, RowHashOffset = 8, RowQuantityOffset = 12, RowSerialOffset = 16, RowBucketOffset = 20;
    /** What follows the shared fields: a profile row's padding, and a character item's slot and padding. */
    public const int ProfileReservedOffset = 21, ItemEquipmentSlotOffset = 21, ItemReservedOffset = 22;
    /** Fields of one character stack. */
    public const int StackHashOffset = 0, StackQuantityOffset = 4, StackSerialOffset = 8, StackBucketOffset = 12;
    /** A character stack's padding. */
    public const int StackReservedOffset = 13;
    /** Fields of one bucket. */
    public const int BucketIdOffset = 0, BucketEquipmentSlotOffset = 1, BucketSlotCountOffset = 2;

    /** The pass snapshot's fields, where its rows start and the size of one. */
    public const int PassExperienceOffset = 0, PassRankOffset = 4, PassRowCountOffset = 6, PassRowsOffset = 8, PassRowBytes = 8;
    /** Fields of one pass row. */
    public const int RowItemHashOffset = 0, RowRankOffset = 4, RowReservedOffset = 5, RowFlagsOffset = 6;

    /** The pursuit snapshot's count and padding, where its held pursuits start, and the sizes of one and of one objective. */
    public const int HeldCountOffset = 0, PursuitReservedOffset = 2, HeldOffset = 8, HeldBytes = 136, ObjectiveBytes = 16;
    /** Fields that name one held pursuit. */
    public const int HeldInstanceOffset = 0, HeldHashOffset = 8, HeldSerialOffset = 12, HeldItemFlagsOffset = 16;
    /** A held pursuit's state, objective count and padding, and where its objectives start. */
    public const int HeldFlagsOffset = 20, HeldObjectiveCountOffset = 21, HeldReservedOffset = 22, HeldObjectivesOffset = 24;
    /** Fields of one objective. */
    public const int ObjectiveHashOffset = 0, ObjectiveValueOffset = 4, ObjectiveTargetOffset = 8, ObjectiveFlagsOffset = 12;
    /** An objective's padding. */
    public const int ObjectiveReservedOffset = 13;

    /** A receipt's fields. */
    public const int ReceiptStateOffset = 0, ReceiptWaitOffset = 4;

    /** Sources one unlockValues request reads: a 4-byte header and nine 4-byte sources fill the request payload. */
    public const int UnlockReadCapacity = 9, UnlockValuesRequestBytes = RequestPayloadBytes, UnlockValuesBytes = 72;
    /** The unlockValues request's count, where its sources start and the size of one. */
    public const int UnlockCountOffset = 0, UnlockSourcesOffset = 4, UnlockSourceBytes = 4;
    /** Fields of one unlock source. */
    public const int UnlockKindOffset = 0, UnlockBankOffset = 1, UnlockIndexOffset = 2;
    /** The size and fields of one unlock value. */
    public const int UnlockValueBytes = 8, UnlockStateOffset = 0, UnlockStateReservedOffset = 1, UnlockValueOffset = 4;

    /** SetPursuitObjectiveRequest and CompletePursuitObjectivesRequest share their first three fields. */
    public const int SetObjectiveBytes = 32, CompleteObjectivesBytes = 16;
    /** The fields that name the held pursuit in both pursuit requests. */
    public const int PursuitInstanceOffset = 0, PursuitHashOffset = 8, PursuitSerialOffset = 12;
    /** SetPursuitObjectiveRequest fields that name the objective and the value the game compares. */
    public const int ObjectiveRequestHashOffset = 16, ObjectiveOrdinalOffset = 20, ObjectiveExpectedOffset = 24;
    /** The value SetPursuitObjectiveRequest writes. */
    public const int ObjectiveReplacementOffset = 28;

    private const uint ReadinessBits = 0xFF;
    private const ushort SeasonPassRowBits = 0x7F;
    private const byte PursuitBits = 0x7, ObjectiveBits = 0x7;
    /** Unlock read states: invalid when the bank cannot hold the row, known when the value is the saved one. */
    private const byte UnlockInvalid = 0, UnlockKnown = 1;

    /** The name of the mapping Studio creates for one game process. */
    public static string MappingName(int processId) => $@"Local\SunriseStudio.Investment.{processId}";
    /** Mutations carry an operation id and a complete expected context. */
    public static bool IsMutation(LiveInvestmentOpcode opcode) =>
        opcode is >= LiveInvestmentOpcode.GrantItem and <= LiveInvestmentOpcode.ResetSeasonPassClaim
            or LiveInvestmentOpcode.SetPursuitObjective or LiveInvestmentOpcode.CompletePursuitObjectives;
    /** Receipt requests carry the id of an earlier mutation and no context. */
    public static bool IsReceiptRequest(LiveInvestmentOpcode opcode) =>
        opcode is LiveInvestmentOpcode.ReadReceipt or LiveInvestmentOpcode.AcknowledgeReceipt;

    /** The payload size a request of this opcode carries. */
    public static int RequestBytes(LiveInvestmentOpcode opcode) => opcode switch
    {
        LiveInvestmentOpcode.GrantItem => GrantItemBytes,
        LiveInvestmentOpcode.SetQuantity => SetQuantityBytes,
        LiveInvestmentOpcode.SetSeasonalExperience => SetSeasonalExperienceBytes,
        LiveInvestmentOpcode.ClaimSeasonPassReward or LiveInvestmentOpcode.ResetSeasonPassClaim => SeasonPassRowBytes,
        LiveInvestmentOpcode.UnlockValues => UnlockValuesRequestBytes,
        LiveInvestmentOpcode.SetPursuitObjective => SetObjectiveBytes,
        LiveInvestmentOpcode.CompletePursuitObjectives => CompleteObjectivesBytes,
        _ => 0
    };

    /** The payload size an accepted answer to this opcode carries. */
    public static int ResponseBytes(LiveInvestmentOpcode opcode) => opcode switch
    {
        LiveInvestmentOpcode.Hello => HelloBytes,
        LiveInvestmentOpcode.InventorySnapshot => InventoryBytes,
        LiveInvestmentOpcode.SeasonPassSnapshot => SeasonPassBytes,
        LiveInvestmentOpcode.PursuitSnapshot => PursuitBytes,
        LiveInvestmentOpcode.UnlockValues => UnlockValuesBytes,
        _ when IsMutation(opcode) || IsReceiptRequest(opcode) => ReceiptBytes,
        _ => 0
    };

    /** Encodes a whole request block with every unused byte zero. The request id goes in the control block separately. */
    public static void EncodeRequest(Span<byte> block, LiveInvestmentOpcode opcode, ulong operationId,
        LiveInvestmentContext expected, ReadOnlySpan<byte> payload)
    {
        if (block.Length != RequestBlockBytes) throw new ArgumentException("The request block has the wrong size.",
            nameof(block));
        if (opcode is LiveInvestmentOpcode.None or > LiveInvestmentOpcode.CompletePursuitObjectives)
            throw new ArgumentOutOfRangeException(nameof(opcode));
        if (payload.Length != RequestBytes(opcode)) throw new ArgumentException("The payload does not fit this command.",
            nameof(payload));
        bool identified = IsMutation(opcode) || IsReceiptRequest(opcode);
        if ((operationId != 0) != identified)
            throw new ArgumentException("Only mutations and receipt requests carry an operation id.", nameof(operationId));
        if (IsMutation(opcode) ? !expected.Complete : expected != default)
            throw new ArgumentException("Only mutations carry an expected context, and it names every part.", nameof(expected));
        block.Clear();
        PutU32(block, OpcodeOffset - RequestBlockOffset, (uint)opcode);
        PutU32(block, RequestPayloadBytesOffset - RequestBlockOffset, (uint)payload.Length);
        PutU64(block, OperationOffset - RequestBlockOffset, operationId);
        PutContext(block[(ExpectedOffset - RequestBlockOffset)..], expected);
        payload.CopyTo(block[(RequestPayloadOffset - RequestBlockOffset)..]);
    }

    /** Encodes a grant of a positive quantity of one item. */
    public static byte[] GrantItem(uint itemHash, uint quantity)
    {
        if (itemHash == 0) throw new ArgumentOutOfRangeException(nameof(itemHash));
        if (quantity is 0 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(quantity));
        var bytes = new byte[GrantItemBytes];
        PutU32(bytes, GrantHashOffset, itemHash);
        PutU32(bytes, GrantQuantityOffset, quantity);
        return bytes;
    }

    /**
     * Quantity 0 removes the row. Instance is zero for character stacks and profile stacks without their own item.
     * A character row belongs to the expected context's character.
     */
    public static byte[] SetQuantity(LiveInventoryScope scope, ulong instance, uint hash,
        int expectedSerial, int expectedQuantity,
        int quantity)
    {
        if (scope > LiveInventoryScope.CharacterStack) throw new ArgumentOutOfRangeException(nameof(scope));
        if (scope == LiveInventoryScope.CharacterItem ? instance == 0 : scope == LiveInventoryScope.CharacterStack
            && instance != 0)
            throw new ArgumentOutOfRangeException(nameof(instance));
        if (hash == 0) throw new ArgumentOutOfRangeException(nameof(hash));
        if (quantity < 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        var bytes = new byte[SetQuantityBytes];
        bytes[SetScopeOffset] = (byte)scope;
        PutU64(bytes, SetInstanceOffset, instance);
        PutU32(bytes, SetHashOffset, hash);
        PutI32(bytes, SetExpectedSerialOffset, expectedSerial);
        PutI32(bytes, SetExpectedQuantityOffset, expectedQuantity);
        PutI32(bytes, SetQuantityOffset, quantity);
        return bytes;
    }

    /** The game compares its current experience with expected before it writes the replacement. */
    public static byte[] SetSeasonalExperience(int expected, int replacement)
    {
        if (replacement < 0) throw new ArgumentOutOfRangeException(nameof(replacement));
        var bytes = new byte[SetSeasonalExperienceBytes];
        PutI32(bytes, ExperienceExpectedOffset, expected);
        PutI32(bytes, ExperienceReplacementOffset, replacement);
        return bytes;
    }

    /** Names one pass row by its claim index for a claim or claim reset. The game checks that the row's item still matches. */
    public static byte[] SeasonPassRow(ushort rowIndex, uint itemHash)
    {
        if (rowIndex >= SeasonPassRowCapacity) throw new ArgumentOutOfRangeException(nameof(rowIndex));
        if (itemHash == 0) throw new ArgumentOutOfRangeException(nameof(itemHash));
        var bytes = new byte[SeasonPassRowBytes];
        PutU16(bytes, PassRowIndexOffset, rowIndex);
        PutU32(bytes, PassHashOffset, itemHash);
        return bytes;
    }

    /** Encodes up to UnlockReadCapacity sources for the selected character. A value source cannot name the profile bank. */
    public static byte[] UnlockValues(IReadOnlyList<LiveUnlockSource> sources)
    {
        if (sources.Count is 0 or > UnlockReadCapacity) throw new ArgumentOutOfRangeException(nameof(sources));
        var bytes = new byte[UnlockValuesRequestBytes];
        PutU16(bytes, UnlockCountOffset, (ushort)sources.Count);
        for (int index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            if (source.Kind is not (LiveUnlockKind.Flag or LiveUnlockKind.Value)
                || source.Bank is not (LiveUnlockBank.Account or LiveUnlockBank.Profile or LiveUnlockBank.Character)
                || source is { Kind: LiveUnlockKind.Value, Bank: LiveUnlockBank.Profile })
                throw new ArgumentException("A source names a kind or bank the game does not read.", nameof(sources));
            int at = UnlockSourcesOffset + index * UnlockSourceBytes;
            bytes[at + UnlockKindOffset] = (byte)source.Kind;
            bytes[at + UnlockBankOffset] = (byte)source.Bank;
            PutU16(bytes, at + UnlockIndexOffset, source.Index);
        }
        return bytes;
    }

    /**
     * The game compares the objective's saved row with expected before it writes value. A flag value is 0 or 1.
     * The ordinal is the objective's position in the pursuit's installed contract.
     */
    public static byte[] SetPursuitObjective(ulong instance, uint itemHash, int expectedSerial, uint objectiveHash,
        byte ordinal, int expected, int value)
    {
        if (ordinal >= PursuitObjectiveCapacity) throw new ArgumentOutOfRangeException(nameof(ordinal));
        var bytes = new byte[SetObjectiveBytes];
        PutHeldPursuit(bytes, instance, itemHash, expectedSerial);
        PutU32(bytes, ObjectiveRequestHashOffset, objectiveHash);
        bytes[ObjectiveOrdinalOffset] = ordinal;
        PutI32(bytes, ObjectiveExpectedOffset, expected);
        PutI32(bytes, ObjectiveReplacementOffset, value);
        return bytes;
    }

    /** Asks the game to set every incomplete objective of one held pursuit to its target. The pursuit is not turned in. */
    public static byte[] CompletePursuitObjectives(ulong instance, uint itemHash, int expectedSerial)
    {
        var bytes = new byte[CompleteObjectivesBytes];
        PutHeldPursuit(bytes, instance, itemHash, expectedSerial);
        return bytes;
    }

    private static void PutHeldPursuit(Span<byte> bytes, ulong instance, uint itemHash, int expectedSerial)
    {
        if (instance == 0) throw new ArgumentOutOfRangeException(nameof(instance));
        if (itemHash == 0) throw new ArgumentOutOfRangeException(nameof(itemHash));
        PutU64(bytes, PursuitInstanceOffset, instance);
        PutU32(bytes, PursuitHashOffset, itemHash);
        PutI32(bytes, PursuitSerialOffset, expectedSerial);
    }

    /**
     * Reads the answer to one request.
     * @param control The 48-byte control block.
     * @param response The response block from its start, holding at least the declared payload.
     * @param gameStarted The instance the connection already saw, or 0 before its first answer.
     * @return Null until the response id matches the request.
     */
    public static LiveInvestmentReply? Read(ReadOnlySpan<byte> control, ReadOnlySpan<byte> response, uint gamePid,
        ulong gameStarted, uint request, LiveInvestmentOpcode opcode, ulong operationId)
    {
        if (gamePid == 0 || request == 0 || control.Length != ControlBytes || U32(control, MagicOffset) != Magic
            || U32(control, VersionOffset) != Version || U32(control, BytesOffset) != MappingBytes
            || U32(control, GameVersionOffset) != Version)
            throw new InvalidDataException("The investment link has an incompatible layout.");
        if (U32(control, ResponseOffset) != request) return null;
        if (U32(control, GamePidOffset) != gamePid) throw new InvalidDataException("The investment reply belongs to another game process.");
        ulong started = U64(control, GameStartedOffset);
        if (started == 0 || (gameStarted != 0 && started != gameStarted))
            throw new InvalidDataException("The investment reply belongs to another game instance.");
        if (response.Length < ResponseHeaderBytes) throw new InvalidDataException("The investment reply is incomplete.");
        uint status = U32(response, StatusOffset - ResponseBlockOffset);
        if (status is (uint)LiveInvestmentStatus.None or > (uint)LiveInvestmentStatus.Refused)
            throw new InvalidDataException($"The game sent unknown investment status {status}.");
        Zero(response.Slice(ResponseReservedOffset - ResponseBlockOffset, sizeof(uint)), "investment reply");
        if (U32(response, ResponseOpcodeOffset - ResponseBlockOffset) != (uint)opcode
            || U64(response, ResponseOperationOffset - ResponseBlockOffset) != operationId)
            throw new InvalidDataException("The investment reply answers another request.");
        uint payloadBytes = U32(response, ResponsePayloadBytesOffset - ResponseBlockOffset);
        var context = ReadContext(response[(ContextOffset - ResponseBlockOffset)..]);
        string reason = ReadReason(response.Slice(ReasonOffset - ResponseBlockOffset, ReasonBytes));
        uint expected = status == (uint)LiveInvestmentStatus.Ok ? (uint)ResponseBytes(opcode) : 0;
        if (payloadBytes != expected || response.Length < ResponseHeaderBytes + payloadBytes)
            throw new InvalidDataException($"The investment reply declares {payloadBytes} payload bytes.");
        if (status == (uint)LiveInvestmentStatus.Invalid && context != default)
            throw new InvalidDataException("A rejected investment request names a context.");
        if (status == (uint)LiveInvestmentStatus.Refused && reason.Length == 0)
            throw new InvalidDataException("A refused investment request has no reason.");
        return new((LiveInvestmentStatus)status, reason, opcode, operationId, context, started,
            response.Slice(ResponseHeaderBytes, (int)payloadBytes).ToArray());
    }

    /** Decodes Hello and checks its selected character against the answer's context. */
    public static LiveInvestmentHello ReadHello(LiveInvestmentReply reply)
    {
        var bytes = Payload(reply, LiveInvestmentOpcode.Hello, HelloBytes);
        uint readiness = Bits(U32(bytes, HelloReadinessOffset), ReadinessBits, "catalog readiness");
        int count = Count(bytes[HelloCharacterCountOffset], CharacterCapacity, "investment hello character");
        byte peer = bytes[HelloPeerOffset];
        if (peer > 1) throw new InvalidDataException("The investment hello's client state is invalid.");
        Zero(bytes[HelloReservedOffset..HelloCharactersOffset], "investment hello");
        var characters = Rows(bytes, HelloCharactersOffset, CharacterBytes, count,
            CharacterCapacity, "investment hello character",
            (row, _) =>
            {
                byte selected = row[CharacterSelectedOffset];
                if (U64(row, CharacterSoidOffset) == 0 || selected > 1)
                    throw new InvalidDataException("An investment hello character is invalid.");
                Zero(row[CharacterReservedOffset..], "investment hello character");
                return new LiveInvestmentCharacter(U64(row, CharacterSoidOffset), row[CharacterClassOffset], selected != 0);
            });
        var chosen = characters.Where(character => character.Selected).ToArray();
        if (chosen.Length > 1 || (chosen.Length == 1 ? chosen[0].Soid : 0) != reply.Context.CharacterSoid)
            throw new InvalidDataException("The investment hello's selected character differs from its context.");
        return new(reply.Context, (LiveInvestmentReadiness)readiness, reply.GameStarted, characters, peer != 0);
    }

    /** Decodes the inventory snapshot's counted rows and checks every unused slot is zero. */
    public static LiveInventorySnapshot ReadInventory(LiveInvestmentReply reply)
    {
        var bytes = Payload(reply, LiveInvestmentOpcode.InventorySnapshot, InventoryBytes);
        int profile = Count(U16(bytes, ProfileCountOffset), ProfileRowCapacity, "profile row");
        int items = Count(U16(bytes, ItemCountOffset), CharacterItemCapacity, "character item");
        int stacks = Count(U16(bytes, StackCountOffset), CharacterStackCapacity, "character stack");
        int buckets = Count(U16(bytes, BucketCountOffset), BucketCapacity, "inventory bucket");
        Zero(bytes[InventoryTailOffset..], "inventory snapshot");
        return new(reply.Context,
            Rows(bytes, BucketsOffset, BucketBytes, buckets, BucketCapacity, "inventory bucket", ReadBucket),
            Rows(bytes, ProfileOffset, ProfileRowBytes, profile, ProfileRowCapacity, "profile row", ReadProfileRow),
            Rows(bytes, ItemsOffset, ItemBytes, items, CharacterItemCapacity, "character item", ReadItem),
            Rows(bytes, StacksOffset, StackBytes, stacks, CharacterStackCapacity, "character stack", ReadStack));
    }

    /** Decodes the pass snapshot. A row's index is its place in the array. */
    public static LiveSeasonPassSnapshot ReadSeasonPass(LiveInvestmentReply reply)
    {
        var bytes = Payload(reply, LiveInvestmentOpcode.SeasonPassSnapshot, SeasonPassBytes);
        int count = Count(U16(bytes, PassRowCountOffset), SeasonPassRowCapacity, "season pass row");
        var rows = Rows(bytes, PassRowsOffset, PassRowBytes, count, SeasonPassRowCapacity, "season pass row",
            (row, index) =>
            {
                Zero(row.Slice(RowReservedOffset, 1), "season pass row");
                uint hash = U32(row, RowItemHashOffset);
                var flags = (LiveSeasonPassRowFlags)Bits(U16(row, RowFlagsOffset), SeasonPassRowBits, "season pass row state");
                if ((hash != 0) != flags.HasFlag(LiveSeasonPassRowFlags.Available))
                    throw new InvalidDataException("A season pass row's item disagrees with its availability.");
                return new LiveSeasonPassRow((ushort)index, hash, row[RowRankOffset], flags);
            });
        return new(reply.Context, Amount(I32(bytes, PassExperienceOffset), "seasonal XP"), U16(bytes, PassRankOffset), rows);
    }

    /** Decodes the held pursuits and their objectives. */
    public static LivePursuitSnapshot ReadPursuits(LiveInvestmentReply reply)
    {
        var bytes = Payload(reply, LiveInvestmentOpcode.PursuitSnapshot, PursuitBytes);
        int count = Count(U16(bytes, HeldCountOffset), HeldPursuitCapacity, "held pursuit");
        Zero(bytes[PursuitReservedOffset..HeldOffset], "pursuit snapshot");
        return new(reply.Context,
            Rows(bytes, HeldOffset, HeldBytes, count, HeldPursuitCapacity, "held pursuit", ReadHeldPursuit));
    }

    /** Reads the answer to one unlockValues request: one value per requested source, in request order. */
    public static LiveUnlockSnapshot ReadUnlockValues(LiveInvestmentReply reply, IReadOnlyList<LiveUnlockSource> sources)
    {
        var bytes = Payload(reply, LiveInvestmentOpcode.UnlockValues, UnlockValuesBytes);
        var values = Rows(bytes, 0, UnlockValueBytes, sources.Count, UnlockReadCapacity, "unlock value", (row, index) =>
        {
            Zero(row[UnlockStateReservedOffset..UnlockValueOffset], "unlock value");
            byte state = row[UnlockStateOffset];
            int value = I32(row, UnlockValueOffset);
            var source = sources[index];
            if (state > UnlockKnown || (state == UnlockKnown && source.Kind == LiveUnlockKind.Flag && value is not (0 or 1))
                || state == UnlockInvalid && value != 0)
                throw new InvalidDataException("An unlock value has an unknown state.");
            return new LiveUnlockValue(source, state == UnlockKnown, value);
        });
        return new(reply.Context, values);
    }

    /** Reads the receipt that answers a mutation or a receipt request. */
    public static LiveInvestmentReceipt ReadReceipt(LiveInvestmentReply reply)
    {
        if (!IsMutation(reply.Opcode) && !IsReceiptRequest(reply.Opcode))
            throw new InvalidDataException("The investment reply holds no receipt.");
        var bytes = Payload(reply, reply.Opcode, ReceiptBytes);
        uint state = U32(bytes, ReceiptStateOffset), wait = U32(bytes, ReceiptWaitOffset);
        if (state > (uint)LiveReceiptState.CommittedUnpublished || wait > (uint)LiveInvestmentWait.Catalog)
            throw new InvalidDataException("The investment receipt has an unknown state.");
        var receipt = new LiveInvestmentReceipt(reply.OperationId, (LiveReceiptState)state,
            reply.Reason, (LiveInvestmentWait)wait,
            reply.Context);
        if (receipt.State == LiveReceiptState.Unknown)
        {
            if (wait != 0 || reply.Context != default) throw new InvalidDataException("An unknown investment receipt names a target.");
            return receipt;
        }
        if (!receipt.Context.Complete) throw new InvalidDataException("The investment receipt does not name its target.");
        // A queued operation may already name what it will wait for; a deferred one always does.
        if (receipt.State != LiveReceiptState.Queued
            && (receipt.State == LiveReceiptState.Deferred) != (receipt.Wait != LiveInvestmentWait.None))
            throw new InvalidDataException("The investment receipt's wait disagrees with its state.");
        if (receipt.State == LiveReceiptState.Refused && receipt.Reason.Length == 0)
            throw new InvalidDataException("A refused investment receipt has no reason.");
        return receipt;
    }

    /** Each catalog the game reads, in the order Studio names them. */
    private static readonly (LiveInvestmentReadiness Flag, string Name)[] Catalogs =
    [
        (LiveInvestmentReadiness.Items, "Items"), (LiveInvestmentReadiness.ItemDetails, "Item details"),
        (LiveInvestmentReadiness.Buckets, "Buckets"), (LiveInvestmentReadiness.Progressions, "Progressions"),
        (LiveInvestmentReadiness.SeasonPass, "Season pass"), (LiveInvestmentReadiness.Rewards, "Rewards"),
        (LiveInvestmentReadiness.Objectives, "Objectives"), (LiveInvestmentReadiness.Pursuits, "Pursuits")
    ];

    /** Names of the catalogs the game has not finished reading. */
    public static IEnumerable<string> Unread(LiveInvestmentReadiness readiness) =>
        Catalogs.Where(part => !readiness.HasFlag(part.Flag)).Select(part => part.Name);

    /** User-facing text for a reason label the game sent. A label Studio does not know is returned unchanged. */
    public static string Describe(string reason) => reason switch
    {
        "" => "The game did not give a reason.",
        "account_changed" or "character_changed" => "The character changed since you started this.",
        "catalog_changed" or "item_unavailable" => "The game does not have this item. Build and install, then restart.",
        // Also given for a held row whose bucket the game has no descriptor for.
        "item_definition" => "The game does not have this item or its bucket. Build and install, then restart.",
        "not_ready" => "The game is still reading its installed data.",
        "selected_character" => "Select a character in the game first.",
        "account_state" => "The game could not use its saved account.",
        "request_field" or "request_opcode" or "request_size" or "request_padding" or "request_operation" or "request_context" =>
            "Studio sent a request the game could not read.",
        "serial" => "The character's inventory cannot record another change.",
        "pursuits_unsupported" => "The game cannot read pursuit objectives from its installed data.",
        "rewards_unsupported" => "The game cannot read rewards from its installed data.",
        "publication_peer" => "Sign in to the game with this account first.",
        "operation_reused" => "The game already holds a different operation with this id.",
        "receipt_pending" => "The operation has not finished.",
        "editor_disconnected" => "Studio disconnected before the game ran it.",
        "allocation" or "reward_allocation" => "The game ran out of memory for this request.",
        "transaction" or "inventory_commit" or "reward_preflight" or "random_source" => "The game could not save the change.",
        "inventory_stage" or "inventory_encode" => "The game could not build the update for its client.",
        "inventory_row" or "experience_changed" or "reward_hash" or "reward_index" or "pursuit_changed" or "objective_changed"
            or "value_changed" => "It changed in the game. Try again.",
        "item_route" => "The game cannot place this kind of item.",
        "inventory_scope" => "The game keeps this item in another inventory.",
        "item_detail" or "item_identity" or "socket_owner" or "quest_state" => "The game could not resolve this item.",
        "reward_placement" or "inventory_destination" or "reward_count" or "loadout" => "The game could not place this item.",
        "acquisition_flag" or "perk_acquisition" or "wrapper_acquisition_flag" or "wrapper_flag_capacity" =>
            "The game could not record this item as acquired.",
        "claim_rejected" => "The game's claim rules refused it.",
        "equipped" => "Equipped; unequip before editing.",
        // A grant is also bounded by the slots of the item's bucket and its recovery bucket, and an account perk by 1.
        "quantity" => "The quantity is out of range, or more than the account can hold of this item at once.",
        "unsupported_removal" => "The game cannot remove this item.",
        "experience_range" => "XP cannot be negative.",
        "experience_spent" => "XP cannot go below what the artifact has already spent.",
        "artifact_unsupported" => "Lowering XP needs the artifact's sale rows, which the game cannot read from its installed data.",
        "rank" => "The season rank is too low.",
        "already_claimed" => "Already claimed.",
        "not_claimed" => "Not claimed.",
        "automatic_reward" => "The game grants this reward by itself.",
        "reward_condition" => "Not available to this character.",
        "instance_capacity" => "No room in the inventory.",
        "profile_change_capacity" or "character_change_capacity" => "Too many inventory changes for one update.",
        "no_room" => "Its bucket is full and it has nowhere to overflow, so the game would drop it.",
        "expired" => "This bounty has expired.",
        "pursuit_contract" => "The game cannot read this pursuit's objectives.",
        "objective_source" => "This objective's progress is not one saved value.",
        "objective_value" => "A flag is set or clear.",
        "flag_state" => "The game holds an unexpected value for this flag.",
        "value_unchanged" => "It already has that value.",
        "objective_target" => "No saved value completes every objective.",
        "objectives_complete" => "Every objective is already complete.",
        _ => reason
    };

    private static LiveBucket ReadBucket(ReadOnlySpan<byte> row, int index)
    {
        sbyte slot = (sbyte)row[BucketEquipmentSlotOffset];
        if (row[BucketIdOffset] == NoBucket || slot < NoEquipmentSlot || slot >= EquipmentSlotCount)
            throw new InvalidDataException("An inventory bucket record is invalid.");
        return new(row[BucketIdOffset], U16(row, BucketSlotCountOffset), slot);
    }

    private static LiveProfileRow ReadProfileRow(ReadOnlySpan<byte> row, int index)
    {
        Zero(row[ProfileReservedOffset..], "profile row");
        return new(U64(row, RowInstanceOffset), U32(row, RowHashOffset),
            Amount(I32(row, RowQuantityOffset), "profile row quantity"), I32(row, RowSerialOffset), row[RowBucketOffset]);
    }

    private static LiveCharacterItem ReadItem(ReadOnlySpan<byte> row, int index)
    {
        byte slot = row[ItemEquipmentSlotOffset];
        if (U64(row, RowInstanceOffset) == 0 || (slot >= EquipmentSlotCount && slot != Unequipped))
            throw new InvalidDataException("A character item record is invalid.");
        Zero(row[ItemReservedOffset..], "character item");
        return new(U64(row, RowInstanceOffset), U32(row, RowHashOffset),
            Amount(I32(row, RowQuantityOffset), "character item quantity"), I32(row, RowSerialOffset), row[RowBucketOffset], slot);
    }

    private static LiveCharacterStack ReadStack(ReadOnlySpan<byte> row, int index)
    {
        Zero(row[StackReservedOffset..], "character stack");
        return new(U32(row, StackHashOffset), Amount(I32(row, StackQuantityOffset), "character stack quantity"),
            I32(row, StackSerialOffset), row[StackBucketOffset]);
    }

    private static LiveHeldPursuit ReadHeldPursuit(ReadOnlySpan<byte> row, int index)
    {
        int objectiveCount = Count(row[HeldObjectiveCountOffset], PursuitObjectiveCapacity, "pursuit objective");
        if (U64(row, HeldInstanceOffset) == 0) throw new InvalidDataException("A held pursuit record is invalid.");
        Zero(row[HeldReservedOffset..HeldObjectivesOffset], "held pursuit");
        var objectives = Rows(row, HeldObjectivesOffset, ObjectiveBytes, objectiveCount,
            PursuitObjectiveCapacity, "pursuit objective",
            (objective, _) =>
            {
                var flags = (LivePursuitObjectiveFlags)Bits(objective[ObjectiveFlagsOffset],
                    ObjectiveBits, "pursuit objective state");
                Zero(objective[ObjectiveReservedOffset..], "pursuit objective");
                return new LivePursuitObjective(U32(objective, ObjectiveHashOffset), I32(objective, ObjectiveValueOffset),
                    I32(objective, ObjectiveTargetOffset), flags);
            });
        return new(U64(row, HeldInstanceOffset), U32(row, HeldHashOffset), I32(row, HeldSerialOffset),
            U32(row, HeldItemFlagsOffset),
            (LivePursuitFlags)Bits(row[HeldFlagsOffset], PursuitBits, "held pursuit state"), objectives);
    }

    private delegate T RowReader<out T>(ReadOnlySpan<byte> row, int index);

    /** Decodes the counted prefix of a fixed array and requires every later slot to be zero. */
    private static T[] Rows<T>(ReadOnlySpan<byte> bytes, int offset, int stride, int count, int capacity,
        string what, RowReader<T> read)
    {
        var rows = new T[count];
        for (int index = 0; index < count; index++) rows[index] = read(bytes.Slice(offset + index * stride, stride), index);
        Zero(bytes.Slice(offset + count * stride, (capacity - count) * stride), what);
        return rows;
    }

    private static ReadOnlySpan<byte> Payload(LiveInvestmentReply reply, LiveInvestmentOpcode opcode, int size)
    {
        if (reply.Opcode != opcode || reply.Status != LiveInvestmentStatus.Ok || reply.Payload.Length != size)
            throw new InvalidDataException($"The investment reply holds no {opcode} payload.");
        return reply.Payload;
    }

    private static int Count(int count, int capacity, string what) =>
        count <= capacity ? count : throw new InvalidDataException($"The {what} count exceeds its capacity.");

    private static uint Bits(uint value, uint defined, string what) =>
        (value & ~defined) == 0 ? value : throw new InvalidDataException($"The {what} has unknown bits.");

    /** A quantity or an XP total, which the game never holds below zero. */
    private static int Amount(int value, string what) =>
        value >= 0 ? value : throw new InvalidDataException($"The {what} is negative.");

    private static void Zero(ReadOnlySpan<byte> bytes, string what)
    {
        if (bytes.IndexOfAnyExcept((byte)0) >= 0) throw new InvalidDataException($"The {what} has nonzero padding.");
    }

    /** The game zeroes the field before it copies a label, so every byte after the terminator is zero. */
    private static string ReadReason(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        if (end < 0) throw new InvalidDataException("The investment reason is not terminated.");
        var text = bytes[..end];
        if (text.IndexOfAnyExceptInRange((byte)' ',
            (byte)'~') >= 0) throw new InvalidDataException("The investment reason is not ASCII.");
        Zero(bytes[end..], "investment reason");
        return Encoding.ASCII.GetString(text);
    }

    private static LiveInvestmentContext ReadContext(ReadOnlySpan<byte> bytes) =>
        new(U64(bytes, 0), U64(bytes, sizeof(ulong)), U64(bytes, 2 * sizeof(ulong)));

    private static void PutContext(Span<byte> bytes, LiveInvestmentContext context)
    {
        PutU64(bytes, 0, context.AccountSoid);
        PutU64(bytes, sizeof(ulong), context.CharacterSoid);
        PutU64(bytes, 2 * sizeof(ulong), context.ContentHash);
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static int I32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
    private static void PutU16(Span<byte> bytes, int offset,
        ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes[offset..], value);
    private static void PutU32(Span<byte> bytes, int offset,
        uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], value);
    private static void PutI32(Span<byte> bytes, int offset,
        int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], value);
    private static void PutU64(Span<byte> bytes, int offset,
        ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes[offset..], value);
}
