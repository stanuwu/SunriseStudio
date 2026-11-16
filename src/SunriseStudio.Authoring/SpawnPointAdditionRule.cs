using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** Added spawn points: each copies its source row, takes the edit's pose and joins the end of the bank's rows. */
internal sealed class SpawnPointAdditionRule : IObjectEditRule
{
    public ObjectEditLayout Layout => ObjectEditLayout.SpawnPoint;
    public IReadOnlyCollection<ObjectEditKind> Kinds => [ObjectEditKind.AddSpawnPoint];

    public void Apply(ObjectEditContext context, IReadOnlyList<ObjectEdit> edits)
    {
        foreach (var bank in edits.GroupBy(edit => edit.Target.SourceTag))
        {
            byte[] current = context.Current(bank.Key);
            var installed = ObjectRows.Rows<SSpawnPoints, SSpawnPointRow>(current, 0, nameof(SSpawnPoints.SpawnPoints),
                SSpawnPointRow.Class);
            // The rows must grow in place, so the bank's own rows keep the offsets their edits name.
            if (installed.IsEmpty || TagView.OffsetOf(current, in installed[^1]) + SpawnPointAdditions.Stride != current.Length)
                throw new NotSupportedException("Spawn points can be added only to a bank that ends with its rows.");
            var rows = bank.Select(edit => SpawnPointAdditions.Row(context.Catalog, current, edit)).ToArray();
            var (grown, _) = TagRelocator.GrowArrays(current,
                [new ArrayGrowth(SpawnPointAdditions.Field, SpawnPointAdditions.Stride, rows)]);
            context.ReplaceCurrent(bank.Key, grown);
            foreach (var edit in bank) context.Produced(edit, bank.Key);
        }
    }
}

/** Added spawn rows follow the bank's own rows, in edit key order. */
public static class SpawnPointAdditions
{
    internal static int Field => TagView.FieldOffset<SSpawnPoints>(nameof(SSpawnPoints.SpawnPoints));
    internal static int Stride => Unsafe.SizeOf<SSpawnPointRow>();

    /** A copy of the source row in these bytes with the edit's pose; every other field is the source's. */
    internal static byte[] Row(PackageCatalog catalog, byte[] bank, ObjectEdit edit)
    {
        ObjectRows.Locate(catalog, edit.Target, bank);
        byte[] row = bank.AsSpan(edit.Target.Offset, Stride).ToArray();
        ObjectRows.WriteTransform(ObjectEditLayout.SpawnPoint, row, 0,
            edit.Rotation is { } turn ? new Quaternion(turn[0], turn[1], turn[2], turn[3]) : null,
            ObjectEditCompiler.Vector(edit.Position!), null);
        return row;
    }

    /** The added spawn points of one bank, in the order their rows follow. */
    public static ObjectEdit[] Of(IEnumerable<ObjectEdit> edits, uint bank) =>
        edits.Where(edit => edit.Kind == ObjectEditKind.AddSpawnPoint && edit.Target.SourceTag == bank)
            .OrderBy(edit => edit.Key, StringComparer.Ordinal).ToArray();

    /**
     * The addition whose row starts at `offset` of the compiled bank.
     * @return Null for a row the bank had before the project.
     */
    public static ObjectEdit? At(byte[] bank, uint tag, int offset, IEnumerable<ObjectEdit> edits)
    {
        var added = Of(edits, tag);
        if (added.Length == 0) return null;
        int[] rows = ObjectRows.RowOffsets<SSpawnPointRow>(bank, Field, SSpawnPointRow.Class);
        int index = Array.IndexOf(rows, offset) - (rows.Length - added.Length);
        return index >= 0 ? added[index] : null;
    }

    /** @return Where the addition's row starts in the compiled bank, or -1 when the bank does not hold it. */
    public static int Offset(byte[] bank, ObjectEdit addition, IEnumerable<ObjectEdit> edits)
    {
        var added = Of(edits, addition.Target.SourceTag);
        int index = Array.FindIndex(added, edit => edit.Key == addition.Key);
        int[] rows = ObjectRows.RowOffsets<SSpawnPointRow>(bank, Field, SSpawnPointRow.Class);
        int at = rows.Length - added.Length + index;
        return index < 0 || at < 0 ? -1 : rows[at];
    }
}
