using System.Buffers.Binary;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

internal static class InvestmentDisplayChecks
{
    public static void Run(string temporaryRoot)
    {
        InvestmentStatPoint[] input = [new(0, 0), new(2, 1), new(4, 3)];
        var curve = new InvestmentStatCurve(14, 0, 0, input);
        Check(curve.Evaluate(int.MinValue) == 0 && curve.Evaluate(int.MaxValue) == 3
            && curve.Evaluate(1) == 0 && curve.Evaluate(2) == 1 && curve.Evaluate(3) == 2,
            "Display curve clamp, interpolation or half-even rounding failed.");
        Check(new InvestmentStatCurve(14, 0, 0, [new(0, 1), new(2, 2)]).Evaluate(1) == 2
            && new InvestmentStatCurve(14, 0, 0, [new(0, -2), new(2, -1)]).Evaluate(1) == -2
            && new InvestmentStatCurve(14, 0, 0, [new(5, 7)]).Evaluate(-10) == 7,
            "Display curve odd/negative ties or one-point clamp failed.");
        input[1] = new(2, 100);
        Check(curve.Evaluate(2) == 1, "Display curve retained mutable caller points.");
        Check(new InvestmentStatCurve(14, 0, 0, []).Evaluate(80) == 0, "Empty authored curve did not return the native zero result.");
        Reject(() => new InvestmentStatCurve(14, 0, 0, [new(0, 1), new(0, 2)]));
        Reject(() => new InvestmentStatCurve(14, 0, 0, [new(2, 1), new(1, 2)]));
        Reject(() => new InvestmentStatCurve(14, 0, 0, [new(int.MinValue, 1), new(int.MaxValue, 2)]));
        Reject(() => new InvestmentStatCurve(14, 0, 0, [new(16777216, 1), new(16777217, 2)]));

        const uint first = 0x80A00000;
        string install = Path.Combine(temporaryRoot, "display-curves", "install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        byte[] item = new byte[200]; Q(item, 0, item.Length); U(item, Inventory("MaximumStack"), 1); item[Inventory("Tier")] = 4;
        byte[] display = new byte[256]; Q(display, 0, display.Length); Q(display, 112, 208 - 112); U(display, 204, 0x80805CF1);
        byte[] groups = new byte[384]; Q(groups, 0, groups.Length); Array(groups, 8, 32, 1, 0x80805D02);
        U(groups, 48, 0x12345678); Array(groups, 64, 128, 2, 0x80805D06);
        groups[144] = 14; Array(groups, 152, 224, 3, 0x80807D1A);
        I(groups, 240, 0); I(groups, 244, 0); I(groups, 248, 2); I(groups, 252, 1); I(groups, 256, 4); I(groups, 260, 3);
        groups[168] = 24; Array(groups, 176, 288, 2, 0x80807D1A); I(groups, 304, 0); I(groups, 308, 30); I(groups, 312, 100); I(groups, 316, 60);
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_investment_display_0100_0.pkg"), 0x100,
        [new(0x80807BE5, 8 << 9, Index(first + 2)), new(0x80805CDB, 8 << 9, Index(first + 3)),
            new(InvestmentCatalog.ItemClass, 8 << 9, item), new(InvestmentCatalog.DisplayClass, 8 << 9, display),
            new(0x80805CFC, 8 << 9, groups)]);
        using var catalog = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        var data = InvestmentCatalog.Load(catalog, sources: new(first, first + 1, 0, 0, 0, 0, 0, 0, 0, first + 4));
        byte[]? changedGroups = null, changedDisplay = null;
        catalog.ReadOverride = tag => tag == first + 4 ? changedGroups : tag == first + 3 ? changedDisplay : null;
        var detail = data.ReadItem(first + 2, item);
        Check(detail.DisplayCurves[14].GroupHash == 0x12345678 && detail.DisplayCurves[14].Evaluate(3) == 2
            && !detail.DisplayCurves.ContainsKey(24) && detail.Diagnostics.Any(value => value.StartsWith("Magazine", StringComparison.Ordinal)),
            "Typed display group was not resolved or magazine was labelled final.");
        changedGroups = (byte[])groups.Clone(); I(changedGroups, 248, 0);
        Invalid("Unordered display curve was accepted.");
        changedGroups = (byte[])groups.Clone(); U(changedGroups, 232, 0x80807D1B);
        Invalid("Wrong display point class was accepted.");
        changedGroups = (byte[])groups.Clone(); changedGroups[168] = 14;
        Invalid("Ambiguous duplicate stat rows were accepted.");
        changedGroups = (byte[])groups.Clone(); Q(changedGroups, 152, 0); Q(changedGroups, 160, 0);
        var empty = data.ReadItem(first + 2, item);
        Check(empty.DisplayCurves[14].Points.Count == 0 && empty.DisplayCurves[14].Evaluate(80) == 0,
            "Empty authored curve fell back to its raw value instead of the native zero result.");
        changedGroups = null; changedDisplay = (byte[])display.Clone(); changedDisplay[228] = 1;
        Invalid("Out-of-range display group was accepted.");
        changedDisplay[228] = byte.MaxValue;
        Check(data.ReadItem(first + 2, item).DisplayCurves.Count == 0, "Unset display group selected a curve.");
        changedDisplay = (byte[])display.Clone(); U(changedDisplay, 204, 0x80805D25);
        Invalid("Wrong item display block class was accepted.");
        Check(catalog.ReadOriginal(first + 3).SequenceEqual(display) && catalog.ReadOriginal(first + 4).SequenceEqual(groups),
            "Display preview changed original package data.");
        Console.WriteLine("investment_display synthetic=pass clamp=checked interpolation=checked half_even=checked malformed=rejected");

        void Invalid(string message)
        {
            var invalid = data.ReadItem(first + 2, item);
            Check(invalid.DisplayCurves.Count == 0 && invalid.Diagnostics.Any(value => value.StartsWith("Stat display:", StringComparison.Ordinal)), message);
        }
    }

    public static void Owned(InvestmentCatalog data, PackageCatalog catalog)
    {
        const uint tag = 0x81319AF9;
        byte[] original = catalog.Read(tag);
        var detail = data.ReadItem(tag, original); var raw = detail.Stats.Single(value => value.Index == 14);
        Check(raw.Value == 80 && detail.DisplayCurves.TryGetValue(14, out var curve)
            && curve.GroupIndex == 21 && curve.GroupHash == 0xEAEF4EA1 && curve.Evaluate(raw.Value) == 600,
            "Forward Path did not resolve its native RPM display curve.");
        Check(!detail.DisplayCurves.ContainsKey(24), "Magazine context was replaced by a base curve.");
        byte[] edited = data.Edit(tag, original, raw.Field, "81");
        var after = data.ReadItem(tag, edited);
        Check(after.Stats.Single(value => value.Index == 14).Value == 81 && after.DisplayCurves[14].Evaluate(81) == 606
            && catalog.ReadOriginal(tag).SequenceEqual(original), "Display preview replaced raw editing or changed the source.");
        Console.WriteLine("investment_display owned=81319AF9 group=21 raw_rpm=80 display_rpm=600 raw_edit=preserved");
    }

    private static byte[] Index(uint tag)
    {
        byte[] data = new byte[72]; Q(data, 0, data.Length); Array(data, 8, 32, 1, 0x80807BE8); U(data, 48, 11); U(data, 64, tag); return data;
    }
    private static void Array(byte[] data, int field, int header, int count, uint type)
    { Q(data, field, count); Q(data, field + 8, header - field - 8); U(data, header - 4, 0x80809FBD); Q(data, header, count); U(data, header + 8, type); }
    private static void I(byte[] data, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), value);
    private static int Inventory(string field) => TagView.FieldOffset<ItemDefinition>("InventoryItemBlock." + field);
    private static void U(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static void Q(byte[] data, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset), value);
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new InvalidDataException("Invalid stat display curve was accepted."); }
}
