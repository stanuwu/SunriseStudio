/**
 * File: The Solforge AI control pipe client (README-runtime "Control pipe"). Connects with PipeOptions.CurrentUserOnly, sends
 * the token only when GetNamedPipeServerProcessId equals the process this editor started, writes "<token>\n<command>\n"
 * and reads one JSON line, all within 5 s. Commands: status, focus, quit, quit-force.
 */
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Solforge.AiLauncher;

/** One control pipe answer. Ok is false when the pipe did not answer or refused. */
public sealed record ControlReply(bool Ok, JsonElement? Body, string? Error);

/** Sends control commands to the Solforge AI this editor started. */
public static class ControlPipeClient
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    /** @param pipeName the name without \\.\pipe\; @param expectedPid the started process's id. */
    public static async Task<ControlReply> SendAsync(string pipeName, string token, int expectedPid, string command,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(deadline.Token).ConfigureAwait(false);
            // The token goes only to the process this launcher started (the Electron main process serves the pipe).
            if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out uint serverPid) || serverPid != (uint)expectedPid)
                return new ControlReply(false, null, "control pipe identity mismatch");
            byte[] request = Encoding.UTF8.GetBytes(token + "\n" + command + "\n");
            await client.WriteAsync(request, deadline.Token).ConfigureAwait(false);
            await client.FlushAsync(deadline.Token).ConfigureAwait(false);
            var reply = new MemoryStream();
            var buffer = new byte[1024];
            while (reply.Length < 16384)
            {
                int read = await client.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                if (read == 0) break;
                reply.Write(buffer, 0, read);
                if (Array.IndexOf(buffer, (byte)'\n', 0, read) >= 0) break;
            }
            string line = Encoding.UTF8.GetString(reply.ToArray()).Split('\n')[0].Trim();
            if (line.Length == 0) return new ControlReply(false, null, "no reply");
            using var json = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
            var root = json.RootElement.Clone();
            bool ok = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out var flag) && flag.ValueKind == JsonValueKind.True;
            string? error = !ok && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
            return new ControlReply(ok, root, ok ? null : error ?? "refused");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new ControlReply(false, null, "control pipe timed out"); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or TimeoutException) { return new ControlReply(false, null, "control pipe unavailable"); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);
}
