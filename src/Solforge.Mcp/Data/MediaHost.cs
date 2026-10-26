/**
 * File: ISolforgeMediaHost, the interface the editor implements for the media tools in SolforgeTools.Media.cs (Havok,
 * Wwise audio and USM decoding).
 */
using System.Text.Json.Nodes;

namespace Solforge.Mcp.Data;

/**
 * Decoders for binary middleware data inside tags, implemented by the editor (which has the Havok, Wwise and
 * USM readers): Havok 2012 packfiles (type 26/7), Wwise events/banks/streams (type 26/5, 26/6, class 0x80809802)
 * and CRI USM cutscenes (type 27). Every call is read-only on game data; exports write only to the given folder.
 * Methods return null for an unknown tag and throw InvalidDataException with a readable message for a tag of
 * the wrong kind or bytes that do not decode.
 */
public interface ISolforgeMediaHost
{
    /** Root, kind, class counts, imports, decoded shapes (up to maxShapes), cloth collidables and notes. */
    JsonObject? DescribeHavok(uint tag, bool original, int maxShapes);
    /** Writes the shapes the packfile places by itself as a Wavefront OBJ; {path, triangles, shapes, notes}. */
    JsonObject? ExportHavokObj(uint tag, bool original, string path);
    /** Event, bank or stream: kind, bank, media with sample rate, channels, length and codec. */
    Task<JsonObject?> DescribeAudio(uint tag, CancellationToken cancel);
    /** Decodes one media id (or every media of the tag when null) to PCM16 WAV in folder; {files, skipped}. */
    Task<JsonObject?> ExportAudioWav(uint tag, uint? media, string folder, CancellationToken cancel);
    /**
     * Decodes raw Wwise media bytes that are not in a Sol package (a retail dump payload) to PCM16 WAV in
     * folder; {files, codec, sample_rate, channels, seconds}. Throws InvalidDataException with a readable
     * message when the bytes are not a decodable Wwise stream.
     */
    JsonObject? ExportWwiseBytes(byte[] bytes, uint tag, string folder, CancellationToken cancel);
    /** USM container facts: file name, streams with codec, size, resolution, frame rate, audio format. */
    Task<JsonObject?> DescribeVideo(uint tag, CancellationToken cancel);
    /** Demuxes the USM: MPEG video (.m2v), audio tracks (.adx and decoded .wav), subtitles (.sbt); {files}. */
    Task<JsonObject?> ExportVideo(uint tag, string folder, int? audioChannel, bool includeVideo, CancellationToken cancel);
}
