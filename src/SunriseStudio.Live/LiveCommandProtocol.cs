using System.Buffers.Binary;
using System.Text;

namespace SunriseStudio.Live;

/** Values match the status field the game writes; 0 means no answer yet. */
public enum LiveCommandStatus : uint
{
    None = 0, Ok = 1, Invalid = 2, Disabled = 3, NotLoaded = 4, CompileError = 5, Failed = 6
}

/** Reloaded counts the bindings the game reopened; ok with 0 means no started mission uses the mod. */
public sealed record LiveCommandReply(LiveCommandStatus Status, uint Reloaded, string Message);

/** A command reply with one result per manifest entry; empty when no manifest went with it. */
public sealed record LiveTagReply(LiveCommandReply Reply, IReadOnlyList<LiveReloadResult> Results);

/** Studio writes one request at a time; the game answers each request id once and writes the response id last. */
public static class LiveCommandProtocol
{
    public const uint Magic = 0x4D435353;
    /** Version 2 adds kinds and keeps the layout. A reloadMod request still says version 1, so an older game answers it. */
    public const uint Version = 2;
    public const uint OldestVersion = 1;
    public const int MappingBytes = 320;
    public const int HeartbeatOffset = 16;
    public const int RequestOffset = 24;
    public const int ResponseOffset = 28;
    public const int KindOffset = 32;
    public const int ModIdOffset = 48;
    public const int ModIdBytes = 72;
    public const int MessageOffset = 120;
    public const int MessageBytes = 192;
    public const int StatusOffset = 36;
    public const int GamePidOffset = 40;
    public const int ReloadedOffset = 44;
    public const uint ReloadModKind = 1, ReloadTagsKind = 2, PatchTagsKind = 3, ReloadBubbleKind = 4, QueryResidentKind = 5;
    public static string MappingName(int processId) => $@"Local\SunriseStudio.Command.{processId}";

    /** @return The header version a request of this kind is written with. */
    public static uint VersionFor(uint kind) => kind == ReloadModKind ? OldestVersion : Version;

    /** The message is UTF-8 and leaves room for the terminating zero. */
    public static byte[] EncodeMessage(string message)
    {
        byte[] text = Encoding.UTF8.GetBytes(message);
        if (text.Length >= MessageBytes) throw new ArgumentException("The message does not fit the command link.", nameof(message));
        var bytes = new byte[MessageBytes];
        text.CopyTo(bytes, 0);
        return bytes;
    }

    /** The id is printable ASCII and leaves room for the terminating zero. */
    public static byte[] EncodeModId(string modId)
    {
        if (string.IsNullOrEmpty(modId) || modId.Length >= ModIdBytes || !modId.All(character => character is > ' ' and < '\x7F'))
            throw new ArgumentException("The mod id does not fit the command link.", nameof(modId));
        var bytes = new byte[ModIdBytes];
        Encoding.ASCII.GetBytes(modId.AsSpan(), bytes);
        return bytes;
    }

    /** Returns null until the response id matches the request. */
    public static LiveCommandReply? Read(ReadOnlySpan<byte> bytes, uint processId, uint request)
    {
        if (processId == 0 || request == 0 || bytes.Length != MappingBytes || U32(bytes, 0) != Magic
            || U32(bytes, 4) is < OldestVersion or > Version || U32(bytes, 8) != MappingBytes)
            throw new InvalidDataException("The command link has an incompatible layout.");
        if (U32(bytes, ResponseOffset) != request) return null;
        if (U32(bytes, GamePidOffset) != processId) throw new InvalidDataException("The command reply belongs to another game process.");
        uint status = U32(bytes, StatusOffset);
        if (status > (uint)LiveCommandStatus.Failed) throw new InvalidDataException($"The game sent unknown command status {status}.");
        var message = bytes.Slice(MessageOffset, MessageBytes);
        int end = message.IndexOf((byte)0);
        return new((LiveCommandStatus)status, U32(bytes, ReloadedOffset), Encoding.UTF8.GetString(end < 0 ? message : message[..end]));
    }

    /** The game's own message wins over the fallback text for a refused request. */
    public static string Describe(LiveCommandReply reply) => reply.Status switch
    {
        LiveCommandStatus.Ok when reply.Reloaded == 0 => "Scripts updated. No started mission uses this mod.",
        LiveCommandStatus.Ok => $"Reloaded {reply.Reloaded} running mission{(reply.Reloaded == 1 ? "" : "s")}",
        _ when reply.Message.Length != 0 => reply.Message,
        LiveCommandStatus.Invalid => "The game refused the request.",
        LiveCommandStatus.Disabled => "Mission scripting is off in the game.",
        LiveCommandStatus.NotLoaded => "The mod did not load in this game session. Close the game and use Build and install.",
        LiveCommandStatus.CompileError => "A script did not compile. The old program keeps running.",
        LiveCommandStatus.Failed => "The game could not reload the mod.",
        _ => "The game did not complete the request."
    };

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
}
