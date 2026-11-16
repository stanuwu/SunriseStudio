using System.Buffers.Binary;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

/** Damage payload reads and the three float writers, on a built payload and the installed gladiator. */
internal static class CombatantDamageChecks
{
    private const uint Config = 0x81234567;
    private const int Payload = 0x100, Parts = 0x500, Types = 0x660, Pools = 0x6B0;
    private const int PartRow = Parts + 16, TypeRow = Types + 16, PoolRow = Pools + 16;

    public static void Run()
    {
        byte[] bytes = Build();
        var damage = CombatantDamage.Read(Config, bytes) ?? throw new InvalidOperationException("Built damage payload was rejected.");
        var part = damage.Parts.Single();
        Check(damage.Payload == Payload && part.Name == "head" && part.Kind == 3 && part.Pool == 0
            && part.Health == 100 && part.Scale == 1 && part.TypeScales.Single().Scale == 1
            && damage.Pools.Single().Fraction == 1, "Damage fields read from the wrong offsets.");

        OnlyChanged(bytes, CombatantDamage.SetPartScale(Config, bytes, 0, 2.5f), PartRow + 0xE4, 2.5f);
        OnlyChanged(bytes, CombatantDamage.SetTypeScale(Config, bytes, 0, 0, 0.5f), TypeRow + 0x24, 0.5f);
        OnlyChanged(bytes, CombatantDamage.SetPoolFraction(Config, bytes, 0, 0.25f), PoolRow + 24, 0.25f);
        OnlyChanged(bytes, CombatantDamage.SetBaseHitPoints(Config, bytes, 750), Payload + 0x1A8, 750);
        Check(CombatantDamage.Read(Config, CombatantDamage.SetBaseHitPoints(Config, bytes, 750))!.BaseHitPoints == 750, "The base hit points did not read back.");
        Reject(() => CombatantDamage.SetBaseHitPoints(Config, bytes, 0));
        Reject(() => CombatantDamage.SetPartScale(Config, bytes, 1, 1));
        Reject(() => CombatantDamage.SetTypeScale(Config, bytes, 0, 1, 1));
        Reject(() => CombatantDamage.SetPoolFraction(Config, bytes, 0, float.NaN));

        Check(CombatantDamage.Read(Config + 1, bytes) is null, "A payload naming another config was accepted.");
        foreach (int marker in new[] { Payload - 4, Parts + 8, Types + 8, Pools + 8 })
        {
            byte[] broken = Build();
            U(broken, marker, 0x80800000);
            Check(CombatantDamage.Read(Config, broken) is null, $"A wrong class at 0x{marker:X} was accepted.");
            Check(CombatantDamage.IsDamageConfig(broken) == (marker != Payload - 4), "The damage config check differs.");
        }
        byte[] badPool = Build();
        U(badPool, PartRow + 0xD0, 1);
        Check(CombatantDamage.Read(Config, badPool) is null, "A part naming a missing pool was accepted.");
        byte[] unnamed = Build();
        U(unnamed, PartRow + 0x10, 0);
        Check(CombatantDamage.Read(Config, unnamed)?.Parts.Single().Name == "(none)", "An unnamed part drew an empty name.");
    }

    /** The gladiator damage config lists body and head parts and four full pools. */
    public static void Installed(PackageCatalog catalog)
    {
        const uint gladiator = 0x8162C466;
        byte[] bytes = catalog.Read(gladiator);
        var damage = CombatantDamage.Read(gladiator, bytes) ?? throw new InvalidOperationException("Gladiator damage was not read.");
        var names = damage.Parts.Select(part => part.Name).ToArray();
        Check(damage.Payload == 0x1068 && names.Contains("body") && names.Contains("head"),
            $"Gladiator damage parts differ: {string.Join(',', names)}.");
        Check(damage.Pools.Count == 4 && damage.Pools.All(pool => pool.Fraction == 1)
            && damage.Parts.Select(part => part.Kind).SequenceEqual(new sbyte[] { 0, 3, 3, 3 }),
            "Gladiator pools or part kinds differ.");
        var edited = CombatantDamage.Read(gladiator, CombatantDamage.SetPartScale(gladiator, bytes, 1, 2));
        Check(edited?.Parts[1].Scale == 2, "Gladiator head scale did not round trip.");
        Console.WriteLine($"pass installed_damage_parts={damage.Parts.Count} pools={damage.Pools.Count}");
    }

    private static byte[] Build()
    {
        var bytes = new byte[0x720];
        U(bytes, 0, Config);
        Q(bytes, 0x18, Payload - 0x18);
        U(bytes, Payload - 4, CombatantDamage.PayloadClass); U(bytes, Payload, Config);
        ArrayAt(bytes, Payload + 0x1B0, Parts, 1, 0x80804C11);
        U(bytes, PartRow + 0x10, StringBankWriter.Hash("head")); F(bytes, PartRow + 0x14, 100);
        bytes[PartRow + 0x18] = 3; F(bytes, PartRow + 0xE4, 1);
        ArrayAt(bytes, PartRow + 0xE8, Types, 1, 0x80804BBB); F(bytes, TypeRow + 0x24, 1);
        ArrayAt(bytes, Payload + 0x368, Pools, 1, 0x80804C5F); F(bytes, PoolRow + 24, 1);
        return bytes;
    }

    private static void OnlyChanged(byte[] before, byte[] after, int offset, float value)
    {
        Check(BinaryPrimitives.ReadSingleLittleEndian(after.AsSpan(offset)) == value, $"Write at 0x{offset:X} missing.");
        Check(before.AsSpan(0, offset).SequenceEqual(after.AsSpan(0, offset))
            && before.AsSpan(offset + 4).SequenceEqual(after.AsSpan(offset + 4)), "A damage write changed other bytes.");
    }

    private static void ArrayAt(byte[] bytes, int field, int header, int count, uint type)
    {
        Q(bytes, field, count); Q(bytes, field + 8, header - field - 8);
        U(bytes, header - 4, 0x80809FBD); Q(bytes, header, count); U(bytes, header + 8, type);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject(Action run)
    {
        try { run(); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException) { return; }
        throw new Exception("Invalid damage edit was accepted.");
    }

    private static void U(byte[] bytes, int p, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(p), v);
    private static void Q(byte[] bytes, int p, long v) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(p), v);
    private static void F(byte[] bytes, int p, float v) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(p), v);
}
