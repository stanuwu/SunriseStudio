using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

/** One socket override of a reward row. An index of InvestmentCatalog.Absent names nothing. */
public sealed record InvestmentSocketOverride(int Offset, ushort SocketType, ushort PlugItem, ushort PlugSet,
    ushort RollSet, uint Selection)
{
    /** The selection of an override that names its plug directly (Sunrise rewards/definition.h kFixedPlugSelection). */
    private const uint FixedPlugSelection = 0xFFFFFFFF;
    /** True when the override names its plug rather than selecting one from a set. */
    public bool FixedPlug => Selection == FixedPlugSelection;
}

/** The socket overrides a reward row carries. */
public static class InvestmentRewards
{
    private const ushort Absent = InvestmentCatalog.Absent;

    /** Socket overrides need a socket type, and a fixed plug must be an installed item (rewards/definition.h valid_socket). */
    internal static IReadOnlyList<InvestmentSocketOverride> ReadSockets(ReadOnlySpan<byte> blob, int field,
        int itemCount, List<string> problems)
    {
        try
        {
            int stride = Unsafe.SizeOf<RewardSocketOverride>();
            var rows = TagArray.Checked(blob, field, RewardSocketOverride.Class, stride);
            var sockets = new InvestmentSocketOverride[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                int p = rows.Data + i * stride;
                ref readonly var row = ref TagView.At<RewardSocketOverride>(blob, p);
                var socket = sockets[i] = new(p, row.SocketType, row.Plug, row.PlugSet, row.RollSet, row.Selection);
                if (socket.SocketType == Absent) problems.Add($"Socket override {i} has no socket type.");
                if (socket.PlugItem != Absent
                    && socket.PlugItem >= itemCount) problems.Add($"Socket override {i} plugs item {socket.PlugItem}, outside the item index.");
            }
            return sockets;
        }
        catch (Exception error) when (InvestmentCatalog.Recoverable(error)) { problems.Add("Socket overrides: " + error.Message); return []; }
    }
}
