/**
 * File: StudioWorkspace partial: puts one plug in one socket of the open item (the MCP apply_item_edit set_socket_plug
 * op), the way CustomPerkAuthoring installs a custom perk's plug.
 */
using SunriseStudio.Formats;

namespace SunriseStudio.Editor;

/** Socket plug assignment for the selected investment item (part of StudioWorkspace). */
public sealed partial class StudioWorkspace
{
    /**
     * Makes the plug the socket's only and initial choice, copied from an embedded choice so it keeps that choice's
     * conditions. Other sockets that share the old plug pool are not changed.
     */
    public void SetInvestmentSocketPlug(int socketIndex, ushort plugIndex) =>
        ApplyInvestmentRows((data, tag, bytes) =>
        {
            var socket = data.ReadItem(tag, bytes).Sockets.FirstOrDefault(row => row.Index == socketIndex)
                ?? throw new InvalidDataException($"The item has no socket {socketIndex}.");
            var templates = data.SocketPlugTemplates(tag, bytes, socket);
            if (templates.Count == 0) throw new InvalidDataException($"Socket {socketIndex} has no embedded plug to copy.");
            ushort template = templates.Contains(socket.InitialPlug) ? socket.InitialPlug : templates[0];
            byte[] added = data.AddSocketPlug(tag, bytes, socket, plugIndex, template, preserveTemplateCondition: true);
            socket = data.ReadItem(tag, added).Sockets.Single(row => row.Index == socketIndex);
            return data.KeepSocketPlug(tag, added, socket, plugIndex);
        });
}
