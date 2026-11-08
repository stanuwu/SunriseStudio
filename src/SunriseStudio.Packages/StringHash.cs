namespace SunriseStudio.Packages;

/** FNV-1 string hashes as the game stores them; StringBankWriter.Hash computes one. */
public static class StringHash
{
    /** FNV-1 of no bytes; the game stores it for an empty or unnamed string. */
    public const uint Empty = 0x811C9DC5;

    /** A definition hash no installed or added row uses; 0, 0xFFFFFFFF and Empty are skipped. */
    public static uint NewDefinitionHash(string key, IReadOnlySet<uint> used)
    {
        for (int attempt = 0; attempt < 4096; attempt++)
        {
            uint hash = StringBankWriter.Hash(attempt == 0 ? key : $"{key}#{attempt}");
            if (hash is not (0 or uint.MaxValue or Empty) && !used.Contains(hash)) return hash;
        }
        throw new InvalidDataException("No unused definition hash was found.");
    }
}
