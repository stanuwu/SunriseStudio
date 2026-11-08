using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Text;

namespace SunriseStudio.World.Sdk;

/** A read-only view of an activity SDK pack; sections are copied out on request. */
public sealed class ActivitySdkPack : IDisposable
{
    private readonly MemoryMappedFile file;
    private readonly MemoryMappedViewAccessor view;
    private readonly (long Offset, int Count, int Stride)[] sections = new (long, int, int)[ActivitySdkFormat.SectionCount];
    private byte[]? strings;

    /** Section strides from the header, in section order. */
    public IReadOnlyList<int> Strides => sections.Select(x => x.Stride).ToArray();

    private ActivitySdkPack(string path)
    {
        long length = new FileInfo(path).Length;
        if (length < ActivitySdkFormat.HeaderSize) throw new InvalidDataException($"{path} is shorter than an SDK pack header.");
        file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        try { ReadHeader(length); }
        catch { Dispose(); throw; }
    }

    /** Opens a pack and checks its header and section layout; the payload digest is not recomputed. */
    public static ActivitySdkPack Open(string path) => new(path);

    private void ReadHeader(long length)
    {
        var header = new byte[ActivitySdkFormat.HeaderSize];
        view.ReadArray(0, header, 0, header.Length);
        var span = header.AsSpan();
        if (!span[..8].SequenceEqual(ActivitySdkFormat.Magic) || BinaryPrimitives.ReadUInt32LittleEndian(span[ActivitySdkFormat.VersionAt..]) != ActivitySdkFormat.Version
            || BinaryPrimitives.ReadUInt32LittleEndian(span[ActivitySdkFormat.HeaderSizeAt..]) != ActivitySdkFormat.HeaderSize
            || BinaryPrimitives.ReadUInt64LittleEndian(span[ActivitySdkFormat.FileSizeAt..]) != (ulong)length
            || BinaryPrimitives.ReadUInt32LittleEndian(span[ActivitySdkFormat.SectionCountAt..]) != ActivitySdkFormat.SectionCount)
            throw new InvalidDataException(
                $"The SDK pack header is not version {ActivitySdkFormat.Version} with {ActivitySdkFormat.SectionCount} sections.");
        long end = ActivitySdkFormat.HeaderSize;
        for (int index = 0; index < ActivitySdkFormat.SectionCount; index++)
        {
            var row = span[(ActivitySdkFormat.SectionTableAt + index * ActivitySdkFormat.SectionRowSize)..];
            long offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(row);
            int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(row[8..]));
            int stride = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(row[12..]));
            if (offset != end || stride <= 0) throw new InvalidDataException($"SDK section {index} is not contiguous.");
            sections[index] = (offset, count, stride);
            end = checked(offset + (long)count * stride);
        }
        if (end != length) throw new InvalidDataException("The SDK pack sections do not end at the file end.");
    }

    /** Copies one section's rows; refuses a section whose stride is not the row type's size. */
    public T[] Rows<T>(SdkSection section) where T : unmanaged
    {
        var (offset, count, stride) = sections[(int)section];
        if (stride != Unsafe.SizeOf<T>())
            throw new InvalidDataException($"SDK section {section} has stride {stride}, but {typeof(T).Name} is {Unsafe.SizeOf<T>()} bytes.");
        var rows = new T[count];
        view.ReadArray(offset, rows, 0, count);
        return rows;
    }

    /** The UTF-8 text a string reference names; an empty reference is the empty string. */
    public string Text(SdkStringRef value)
    {
        if (value.Length == 0) return "";
        strings ??= Rows<byte>(SdkSection.Strings);
        if ((long)value.Offset + value.Length > strings.Length) throw new InvalidDataException("An SDK string runs past the string section.");
        return Encoding.UTF8.GetString(strings, (int)value.Offset, (int)value.Length);
    }

    public void Dispose()
    {
        view?.Dispose();
        file?.Dispose();
    }
}
