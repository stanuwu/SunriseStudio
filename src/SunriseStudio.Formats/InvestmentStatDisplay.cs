using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public readonly record struct InvestmentStatPoint(int Input, int Output);

/** Display curves transform authored base values without changing the values stored in item definitions. */
public sealed class InvestmentStatCurve
{
    private readonly InvestmentStatPoint[] points;
    public byte StatIndex { get; }
    public byte GroupIndex { get; }
    public uint GroupHash { get; }
    public IReadOnlyList<InvestmentStatPoint> Points { get; }

    public InvestmentStatCurve(byte statIndex, byte groupIndex, uint groupHash, IEnumerable<InvestmentStatPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        this.points = points.Take(4097).ToArray();
        if (statIndex == byte.MaxValue || groupIndex >= 128 || this.points.Length > 4096)
            throw new InvalidDataException("Invalid stat display curve identity or point count.");
        for (int i = 0; i < this.points.Length; i++)
        {
            var point = this.points[i];
            if ((float)point.Output >= 2147483648f)
                throw new InvalidDataException("Stat display output exceeds the native integer range.");
            if (i > 0 && (point.Input <= this.points[i - 1].Input
                || (float)point.Input <= (float)this.points[i - 1].Input
                || (long)point.Input - this.points[i - 1].Input > int.MaxValue))
                throw new InvalidDataException("Stat display inputs must increase within the native interpolation range.");
        }
        StatIndex = statIndex; GroupIndex = groupIndex; GroupHash = groupHash;
        Points = new ReadOnlyCollection<InvestmentStatPoint>(this.points);
    }

    /** Native interpolation uses float arithmetic, clamps its endpoints and rounds ties to even. */
    public int Evaluate(int raw)
    {
        if (points.Length == 0) return 0;
        float input = raw, value;
        if (input <= (float)points[0].Input) value = points[0].Output;
        else if (input >= (float)points[^1].Input) value = points[^1].Output;
        else
        {
            int next = 1;
            while (input >= (float)points[next].Input) next++;
            var before = points[next - 1]; var after = points[next];
            float fraction = (input - (float)before.Input) / (after.Input - before.Input);
            value = ((float)after.Output - (float)before.Output) * fraction + (float)before.Output;
        }
        if (!float.IsFinite(value)) throw new InvalidDataException("Stat display interpolation is not finite.");
        return checked((int)Math.Round(value, MidpointRounding.ToEven));
    }
}

public sealed partial class InvestmentCatalog
{
    private static readonly int StatDisplayPointer = TagView.FieldOffset<ItemDisplayStrings>("TalentGridItemBlock"),
        StatGroupRowsField = TagView.FieldOffset<SStatGroupTable>("Table");

    /** Missing context-dependent values stay raw instead of being labelled final gameplay stats. */
    private IReadOnlyDictionary<byte, InvestmentStatCurve> ReadDisplayCurves(InvestmentItem item, List<string> diagnostics)
    {
        var result = new Dictionary<byte, InvestmentStatCurve>();
        if (item.DisplayTag == 0) return result;
        try
        {
            RequireClass(catalog, item.DisplayTag, DisplayClass);
            ReadOnlySpan<byte> display = catalog.Read(item.DisplayTag);
            int block = TagPointer.Checked(display, StatDisplayPointer);
            if (block < 0) return result;
            if (block < 4 || !TagView.Fits<SItemDisplayStatBlock>(display, block)
                || new TagPointer(display, StatDisplayPointer).TargetClass != SItemDisplayStatBlock.Class)
                throw new InvalidDataException("Item stat display block has an unexpected class.");
            byte groupIndex = TagView.At<SItemDisplayStatBlock>(display, block).StatGroupIndex;
            if (groupIndex == byte.MaxValue) return result;
            if (!catalog.ContainsTag(sources.StatGroups)) throw new InvalidDataException("Stat groups are unavailable.");
            RequireClass(catalog, sources.StatGroups, SStatGroupTable.Class);
            ReadOnlySpan<byte> groups = catalog.Read(sources.StatGroups);
            int groupStride = Unsafe.SizeOf<SStatGroup>(), statStride = Unsafe.SizeOf<SStatGroupRow>();
            int pointStride = Unsafe.SizeOf<SStatCurvePoint>();
            var groupRows = TagArray.Checked(groups, StatGroupRowsField, SStatGroup.Class, groupStride);
            if (groupIndex >= 128 || groupIndex >= groupRows.Count)
                throw new InvalidDataException("Item stat group is outside its native index.");
            ref readonly var group = ref TagView.At<SStatGroup>(groups, groupRows.Data + groupIndex * groupStride);
            uint groupHash = group.GroupHash;
            var stats = TagArray.Checked(groups, TagView.OffsetOf(groups, in group.Stats), SStatGroupRow.Class, statStride);
            if (stats.Count > 256) throw new InvalidDataException("Stat group contains too many rows.");
            var seen = new HashSet<byte>();
            for (int i = 0; i < stats.Count; i++)
            {
                ref readonly var row = ref TagView.At<SStatGroupRow>(groups, stats.Data + i * statStride);
                byte stat = row.StatIndex;
                if (!seen.Add(stat)) throw new InvalidDataException("Stat group contains duplicate stat rows.");
                if (stat == 24)
                { diagnostics.Add("Magazine display needs item context; the raw value remains editable."); continue; }
                var curve = TagArray.Checked(groups, TagView.OffsetOf(groups, in row.DisplayCurve), SStatCurvePoint.Class, pointStride);
                if (curve.Count > 4096) throw new InvalidDataException("Stat display point count is invalid.");
                var points = new InvestmentStatPoint[curve.Count];
                for (int j = 0; j < curve.Count; j++)
                {
                    ref readonly var point = ref TagView.At<SStatCurvePoint>(groups, curve.Data + j * pointStride);
                    points[j] = new(point.Input, point.Output);
                }
                result.Add(stat, new(stat, groupIndex, groupHash, points));
            }
        }
        catch (Exception error) when (Recoverable(error))
        { result.Clear(); diagnostics.Add("Stat display: " + error.Message); }
        return new ReadOnlyDictionary<byte, InvestmentStatCurve>(result);
    }
}
