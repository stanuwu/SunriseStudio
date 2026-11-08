using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SunriseStudio.World.Sdk;

/** The rows a mod pack carries; every other section is written empty. */
public sealed class ActivitySdkTables
{
    private readonly List<byte> strings = [];
    private readonly Dictionary<string, SdkStringRef> interned = new(StringComparer.Ordinal);

    public List<SdkActivity> Activities { get; } = [];
    public List<SdkScenario> Scenarios { get; } = [];
    public List<SdkBubble> Bubbles { get; } = [];
    public List<SdkState> States { get; } = [];
    public List<SdkObject> Objects { get; } = [];
    public List<SdkOccurrence> Occurrences { get; } = [];
    public List<SdkSlot> Slots { get; } = [];
    public List<SdkAuthoredSceneResource> AuthoredSceneResources { get; } = [];

    public byte[] StringBytes => strings.ToArray();

    /** Adds a UTF-8 string once; the empty string is the empty reference. */
    public SdkStringRef Text(string value)
    {
        if (value.Length == 0) return default;
        if (interned.TryGetValue(value, out var known)) return known;
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        var reference = new SdkStringRef { Offset = (uint)strings.Count, Length = (uint)bytes.Length };
        strings.AddRange(bytes);
        interned.Add(value, reference);
        return reference;
    }
}

/** Writes an activity SDK pack image in the layout ActivitySdkPack reads. */
public static class ActivitySdkPackWriter
{
    /**
     * @param strides Section strides, taken from the installed pack so the two layouts cannot drift.
     * @return The whole file: header, then every section in order. Only the payload digest is set;
     *         a mod pack has no installed-build identity.
     */
    public static byte[] Write(IReadOnlyList<int> strides, ActivitySdkTables tables)
    {
        if (strides.Count != ActivitySdkFormat.SectionCount) throw new ArgumentException($"A pack has {ActivitySdkFormat.SectionCount} section strides.", nameof(strides));
        var sections = new byte[ActivitySdkFormat.SectionCount][];
        for (int index = 0; index < sections.Length; index++) sections[index] = [];
        sections[(int)SdkSection.Strings] = tables.StringBytes;
        Put(sections, strides, SdkSection.Activities, tables.Activities);
        Put(sections, strides, SdkSection.Scenarios, tables.Scenarios);
        Put(sections, strides, SdkSection.Bubbles, tables.Bubbles);
        Put(sections, strides, SdkSection.States, tables.States);
        Put(sections, strides, SdkSection.Objects, tables.Objects);
        Put(sections, strides, SdkSection.Occurrences, tables.Occurrences);
        Put(sections, strides, SdkSection.Slots, tables.Slots);
        Put(sections, strides, SdkSection.AuthoredSceneResources, tables.AuthoredSceneResources);

        long size = ActivitySdkFormat.HeaderSize + sections.Sum(x => (long)x.Length);
        var image = new byte[checked((int)size)];
        var header = image.AsSpan(0, ActivitySdkFormat.HeaderSize);
        ActivitySdkFormat.Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[ActivitySdkFormat.VersionAt..], ActivitySdkFormat.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header[ActivitySdkFormat.HeaderSizeAt..], ActivitySdkFormat.HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[ActivitySdkFormat.FileSizeAt..], (ulong)size);
        BinaryPrimitives.WriteUInt32LittleEndian(header[ActivitySdkFormat.SectionCountAt..], ActivitySdkFormat.SectionCount);
        long offset = ActivitySdkFormat.HeaderSize;
        for (int index = 0; index < sections.Length; index++)
        {
            var row = header[(ActivitySdkFormat.SectionTableAt + index * ActivitySdkFormat.SectionRowSize)..];
            BinaryPrimitives.WriteUInt64LittleEndian(row, (ulong)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(row[8..], (uint)(sections[index].Length / strides[index]));
            BinaryPrimitives.WriteUInt32LittleEndian(row[12..], (uint)strides[index]);
            sections[index].CopyTo(image, offset);
            offset += sections[index].Length;
        }
        SHA256.HashData(image.AsSpan(ActivitySdkFormat.HeaderSize), header.Slice(ActivitySdkFormat.DigestAt, SHA256.HashSizeInBytes));
        return image;
    }

    private static void Put<T>(byte[][] sections, IReadOnlyList<int> strides, SdkSection section, List<T> rows) where T : unmanaged
    {
        if (strides[(int)section] != Unsafe.SizeOf<T>())
            throw new InvalidDataException($"The installed SDK stores {section} rows in {strides[(int)section]} bytes, not {Unsafe.SizeOf<T>()}.");
        sections[(int)section] = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(rows)).ToArray();
    }
}
