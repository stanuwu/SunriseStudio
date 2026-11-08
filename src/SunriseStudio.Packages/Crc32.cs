namespace SunriseStudio.Packages;

/** CRC-32 (IEEE, reflected 0xEDB88320, all-ones start and final inversion). */
public static class Crc32
{
    private const uint Polynomial = 0xEDB88320;
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(value =>
    {
        uint crc = (uint)value;
        for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (Polynomial & (0u - (crc & 1)));
        return crc;
    }).ToArray();

    public static uint Compute(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes) crc = (crc >> 8) ^ Table[(byte)(crc ^ value)];
        return ~crc;
    }
}
