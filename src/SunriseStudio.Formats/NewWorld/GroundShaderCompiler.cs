using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace SunriseStudio.Formats.NewWorld;

/** Compiles the owned ground HLSL with the Windows D3DCompiler; no installed shader is copied. */
public static unsafe class GroundShaderCompiler
{
    // Strictness (1 << 11), warnings as errors (1 << 18) and optimization level 3 (1 << 15).
    private const uint Flags = (1 << 11) | (1 << 18) | (1 << 15);
    private const string SourceName = "generated_ground.hlsl";

    /** The embedded source with LF line ends, as World Studio's reference copy stores it. */
    public static byte[] Source()
    {
        using var stream = typeof(GroundShaderCompiler).Assembly.GetManifestResourceStream("SunriseStudio.Formats.GeneratedGround.hlsl")
            ?? throw new InvalidDataException("The ground shader source is missing.");
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return Encoding.ASCII.GetBytes(reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    /** DXBC bytecode of one entry point for vs_5_0 or ps_5_0. */
    public static byte[] Compile(string entryPoint, bool vertex)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Ground shaders need the Windows D3DCompiler.");
        byte[] source = Source();
        byte[] name = Encoding.ASCII.GetBytes(SourceName + "\0"), entry = Encoding.ASCII.GetBytes(entryPoint + "\0"),
            profile = Encoding.ASCII.GetBytes((vertex ? "vs_5_0" : "ps_5_0") + "\0");
        nint code = 0, errors = 0;
        try
        {
            int result;
            fixed (byte* text = source, sourceName = name, entryName = entry, target = profile)
                result = D3DCompile(text, (nuint)source.Length, sourceName, null, null, entryName, target, Flags, 0, &code, &errors);
            if (result < 0)
                throw new InvalidDataException("Ground shader compilation failed: "
                    + (errors != 0 ? Encoding.UTF8.GetString(Blob(errors)) : $"{result:X8}"));
            byte[] bytecode = Blob(code);
            Validate(bytecode, vertex);
            return bytecode;
        }
        finally { Release(code); Release(errors); }
    }

    /** Checks the DXBC container, its chunk table and that the one program chunk is the expected stage. */
    public static void Validate(byte[] raw, bool vertex)
    {
        if (raw.Length is < 36 or > 4 * 1024 * 1024 || raw[0] != 'D' || raw[1] != 'X' || raw[2] != 'B' || raw[3] != 'C')
            throw new InvalidDataException("Expected a bounded DXBC container.");
        uint version = U32(raw, 20), length = U32(raw, 24), count = U32(raw, 28);
        if (version != 1 || length != raw.Length || count < 1 || count > (raw.Length - 32) / 4)
            throw new InvalidDataException("Invalid DXBC header.");
        var spans = new List<(long Start, long End)> { (0, 32 + count * 4) };
        int programs = 0;
        for (int i = 0; i < count; i++)
        {
            uint offset = U32(raw, 32 + 4 * i);
            if (offset < 32 + count * 4 || offset + 8 > raw.Length) throw new InvalidDataException("A DXBC chunk leaves its container.");
            uint size = U32(raw, (int)offset + 4);
            if (offset + 8L + size > raw.Length) throw new InvalidDataException("A DXBC chunk is truncated.");
            spans.Add((offset, offset + 8L + size));
            string kind = Encoding.ASCII.GetString(raw, (int)offset, 4);
            if (kind is "SHDR" or "SHEX")
            {
                if (size < 8 || size % 4 != 0) throw new InvalidDataException("Invalid DXBC shader program.");
                uint token = U32(raw, (int)offset + 8), words = U32(raw, (int)offset + 12);
                if (words * 4 != size || token != (vertex ? 0x10050u : 0x50u)) throw new InvalidDataException("DXBC shader stage differs.");
                programs++;
            }
        }
        spans.Sort();
        if (programs != 1 || spans.Zip(spans.Skip(1)).Any(pair => pair.First.End > pair.Second.Start))
            throw new InvalidDataException("Overlapping or ambiguous DXBC chunks.");
    }

    /** A 40-byte shader header {40, length, FFFFFFFF} (file type 33) and its bytecode (41); subtype 1 is vertex. */
    public static string Add(GeneratedGraph graph, string key, byte[] bytecode, bool vertex)
    {
        Validate(bytecode, vertex);
        int subtype = vertex ? 1 : 0;
        var header = new NativeBuffer(40);
        header.Put64(0, 40); header.Put32(8, (uint)bytecode.Length); header.Put32(12, uint.MaxValue);
        graph.Add(key, header.ToArray(), 0, 33, subtype);
        graph.Add(key + "_dxbc", bytecode, 0, 41, subtype);
        graph.Buffer(key, key + "_dxbc");
        return key;
    }

    private static uint U32(byte[] raw, int at) => BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at));

    // ID3DBlob: vtable slot 2 Release, 3 GetBufferPointer, 4 GetBufferSize.
    private static byte[] Blob(nint blob)
    {
        var table = *(nint**)blob;
        var pointer = ((delegate* unmanaged[Stdcall]<nint, void*>)table[3])(blob);
        var size = ((delegate* unmanaged[Stdcall]<nint, nuint>)table[4])(blob);
        return new ReadOnlySpan<byte>(pointer, checked((int)size)).ToArray();
    }

    private static void Release(nint blob)
    {
        if (blob == 0) return;
        var table = *(nint**)blob;
        ((delegate* unmanaged[Stdcall]<nint, uint>)table[2])(blob);
    }

    [DllImport("d3dcompiler_47.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int D3DCompile(byte* source, nuint size, byte* sourceName, void* defines, void* include,
        byte* entryPoint, byte* target, uint flags1, uint flags2, nint* code, nint* errors);
}
