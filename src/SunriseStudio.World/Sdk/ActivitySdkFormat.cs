using System.Runtime.InteropServices;

namespace SunriseStudio.World.Sdk;

/**
 * The runtime activity SDK pack Sunrise reads: a 976-byte header, then 51 contiguous sections of
 * fixed-stride rows in section order. Only the rows a destination's roster reads are typed here;
 * the strides of the rest come from the installed pack's header.
 */
public static class ActivitySdkFormat
{
    public static ReadOnlySpan<byte> Magic => "SRSDKP01"u8;
    public const uint Version = 47;
    public const int SectionCount = 51;
    public const int HeaderSize = 976;
    public const uint AbsentIndex = uint.MaxValue;
    /** Header offsets: version, header size, file size, payload digest, section count and section table. */
    public const int VersionAt = 8, HeaderSizeAt = 12, FileSizeAt = 16, DigestAt = 24, SectionCountAt = 152, SectionTableAt = 160;
    /** One section table row: u64 offset, u32 row count, u32 stride. */
    public const int SectionRowSize = 16;

    /** Activity flags: root, scenario and content joins exact, extraction present. */
    public const uint ActivityExactMask = 0xF;
}

/** Section order of the pack; the values index the header's section table. */
public enum SdkSection
{
    Strings = 0, Activities = 1, Scenarios = 2, Bubbles = 3, States = 4, Objects = 5, Occurrences = 6, Slots = 7,
    AuthoredSceneResources = 19,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SdkStringRef
{
    public uint Offset, Length;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SdkRange
{
    public uint First, Count;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SdkActivity
{
    public uint ActivityIndex, DefinitionHash;
    public SdkStringRef Id, InternalName, DisplayName;
    public uint ScenarioIndex, Flags;
    public SdkRange Aliases, Capabilities;
    public uint SelectedActivityRootTag, SelectedScenarioTag, MatchmakingConfigTag;
    public uint JoinStatus, BindingDisposition, BindingReason, BindingEvidenceBasis, RunnableStatus, BindingFlags;
    public SdkRange ActivityRootCandidateTags, ScenarioNameCandidateTags, EvidenceRootTags, BindingLocators;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SdkScenario
{
    public uint Tag, Reserved;
    public SdkStringRef Id, Name;
    public SdkRange Bubbles, States, Occurrences;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SdkBubble
{
    public SdkStringRef Id, Name;
    public uint ScenarioIndex, BubbleOrdinal, NameHash, Reserved;
    public SdkRange States;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SdkState
{
    public SdkStringRef Id, EntryId, RegistryId;
    public uint ScenarioIndex, BubbleIndex, StateOrdinal, EntryIndex, SliceSetIndex, MapBubbleIndex;
    public uint StateHash, PublicValue, Flags, RegistryTag;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SdkObject
{
    public SdkStringRef Id;
    public uint ObjectTag, ObjectKey;
    public SdkRange Slots;
    public uint ConfigCount, DescriptorCount, PlacedSubblockCount, PlacedLeafCount, PlacedHopCount, BareTargetCount;
    public uint ReplicatedPlacementCount;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SdkOccurrence
{
    public SdkStringRef Id, ContextRegistryKey, RegistryId, EntryId;
    public uint ScenarioIndex, BubbleIndex, StateIndex, ObjectIndex, RegistryField, ObjectOrdinal;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SdkSlot
{
    public SdkStringRef Id, Name, SenseSchemaId, AuthSchemaId;
    public uint ObjectIndex, SlotIndex, SlotType, ComponentClass, SenseSchema, AuthSchema, Flags, Reserved;
    public SdkRange Aliases, Capabilities;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SdkAuthoredSceneResource
{
    public SdkStringRef Id;
    public uint SlotIndex, ConfigTag, DescriptorOffset, ResourceFieldOffset, ResourceTag, ResourceClass, Flags, Reserved;
}
