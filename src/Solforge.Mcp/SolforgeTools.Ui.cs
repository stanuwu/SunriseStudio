/**
 * File: MCP tools that drive and watch the editor UI: list_ui_controls and ui_* actions, viewport frames and change
 * waits, read_screen_text (ScreenOcr.cs) and the local viewport stream URL.
 */
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * Driving Solforge's own UI and watching the live viewport. list_ui_controls and the viewport reads are plain reads;
 * ui_invoke, ui_set_value, ui_set_checked and ui_select wait for the user's approve click in
 * Solforge, which works the same from every MCP client. Every image result also carries text (frame id, size, time,
 * what changed and where, the labelled objects on screen) so models without vision can follow along.
 */
public static partial class SolforgeTools
{
    /** Argument schema for tools that target a control by id. */
    private static readonly (string, string, string, bool)[] Target =
    [
        ("id", "string", "Control id from list_ui_controls, e.g. 'main:1A2B3C4D'", false),
        ("query", "string", "Or the control's visible label, e.g. 'Build to folder...'; must match one control", false),
        ("kind", "string", "Optional kind to narrow the query: " + string.Join(", ", UiControls.Kinds), false),
        ("window", "string", "Optional panel name to narrow the query, e.g. 'Inspector'", false)
    ];

    /** Yields the UI, viewport and OCR tools. */
    private static IEnumerable<McpTool> UiTools(ISolforgeHost host,
        Func<string, string, JsonObject, Func<JsonObject, CancellationToken, Task<McpToolResult>>, McpTool> read)
    {
        yield return read("list_ui_controls", "Solforge's own UI controls drawn right now (buttons, menus and menu items, " +
            "checkboxes, tabs, text fields, combo boxes, sliders, number fields, list rows) with id, kind, visible label, panel " +
            "(window), menu/tab/combo scope, screen rect, value, checked state and combo options. Controls inside a closed menu, " +
            "combo or tab are not drawn: open it with ui_invoke first. The AI workspace and Settings > AI are never listed. " +
            "Then ui_invoke, ui_set_value, ui_set_checked or ui_select.",
            McpJson.Schema(("query", "string", "Optional words of the label, panel or scope; best matches first", false),
                ("kind", "string", "Optional kind: " + string.Join(", ", UiControls.Kinds), false),
                ("window", "string", "Optional panel name the control must be in", false),
                ("all", "boolean", "Include controls scrolled out of view (default false)", false),
                ("offset", "integer", "First row (default 0)", false), ("limit", "integer", "Rows per page (default 100, max 500)", false)),
            async (args, cancel) =>
            {
                var controls = await host.UiControlsAsync(cancel).ConfigureAwait(false);
                var found = UiControls.Find(controls, McpJson.String(args, "query"), McpJson.String(args, "kind"), McpJson.String(args, "window"),
                    McpJson.Bool(args, "all", false));
                var page = Pages.Page(found, McpJson.Int(args, "offset", 0), McpJson.Int(args, "limit", 100), UiControls.ToJson);
                page["drawn"] = controls.Count;
                page["windows"] = new JsonArray(controls.Select(control => control.Window).Where(window => window.Length > 0).Distinct()
                    .Select(window => (JsonNode)window).ToArray());
                return McpToolResult.Json(page);
            });

        McpTool Act(string name, string description, UiOperation operation, params (string, string, string, bool)[] extra) =>
            new(name, description, McpJson.Schema([.. Target, .. extra]), true, async (args, cancel) =>
            {
                if (host.Paused) return McpToolResult.Error("The user paused context publishing in Solforge's AI workspace.");
                string? value = McpJson.String(args, operation == UiOperation.Select ? "option" : "value");
                if (operation is UiOperation.SetValue or UiOperation.Select && value is null)
                    return McpToolResult.Error(operation == UiOperation.Select ? "Give the option to choose." : "Give the value to set.");
                if (operation == UiOperation.SetChecked && args["checked"] is not JsonValue)
                    return McpToolResult.Error("Give checked: true or false.");
                var (control, error) = UiControls.Resolve(await host.UiControlsAsync(cancel).ConfigureAwait(false), args);
                if (control is null) return McpToolResult.Error(error ?? "No such control.");
                // A combo's value is one of its options.
                var effective = operation == UiOperation.SetValue && control.Kind == "combo" ? UiOperation.Select : operation;
                if (!UiControls.Allows(control.Kind, effective))
                    return McpToolResult.Error($"{control.Id} is a {control.Kind}; {name} does not apply to it.");
                bool check = McpJson.Bool(args, "checked", true);
                if (!await host.ApproveAsync(name, UiControls.Summary(control, effective, value, check), cancel).ConfigureAwait(false))
                    return McpToolResult.Error("The user declined this in Solforge.");
                long before = host.Viewport.Latest?.Id ?? 0;
                var (ok, message) = await host.UiCommandAsync(new UiCommand(control.Id, effective, value, check), cancel).ConfigureAwait(false);
                if (!ok) return McpToolResult.Error(message);
                var result = McpToolResult.Text(message);
                result.Texts.Add($"Viewport frame before: {before}. wait_for_viewport_change with since={before} shows the effect; " +
                    "list_ui_controls shows controls the click opened.");
                return result;
            });

        yield return Act("ui_invoke", "Click a Solforge control: a button, menu item, list row, radio button or tab; toggles a " +
            "checkbox; opens a menu or combo box so its items can be listed. Name it by id or by visible label (query).", UiOperation.Invoke);
        yield return Act("ui_set_value", "Type into a Solforge text field, or set a slider or number field (a combo box takes one of " +
            "its options). The value replaces the whole field.", UiOperation.SetValue,
            ("value", "string", "New text or number (invariant culture, e.g. 2.5)", true));
        yield return Act("ui_set_checked", "Turn a Solforge checkbox or checkable menu item on or off.", UiOperation.SetChecked,
            ("checked", "boolean", "true for on, false for off", true));
        yield return Act("ui_select", "Choose an option in a Solforge combo box or list by its visible text (opens the list, " +
            "picks the row, closes it), or select a tab or row.", UiOperation.Select,
            ("option", "string", "The option's visible text (or its index for simple lists)", true));

        // Live viewport: no GPU readback per call; frames come from the continuous capture.
        yield return read("get_viewport_frame", "The latest live frame of the 3D viewport as a small JPEG, instantly, from the " +
            "continuous capture (no readback per call), plus text: frame id, size, time, age, what changed since the previous " +
            "frame and the labelled objects on screen. Pass since=<frame id> to skip the image when nothing changed. width, " +
            "quality and fps tune the capture for later frames. get_viewport_screenshot is the full-size PNG with labels.",
            McpJson.Schema(("since", "integer", "Frame id you already have; same id returns text only", false),
                ("image", "boolean", "Include the JPEG (default true)", false),
                ("describe", "boolean", "Include the labelled objects on screen (default true)", false),
                ("ocr", "boolean", "Also read the text on screen with Windows OCR (lines with [x, y, w, h] boxes in frame pixels; about 0.3-1 s). read_screen_text reads a region or the full-size screenshot", false),
                ("width", "integer", "Capture width in pixels for later frames (160-1920, default 960)", false),
                ("quality", "integer", "JPEG quality for later frames (20-95, default 70)", false),
                ("fps", "integer", "Capture rate (1-30, default 8)", false)),
            async (args, cancel) =>
            {
                if (host.CredentialDocumentVisible) return McpToolResult.Error(CaptureRefusal);
                var feed = host.Viewport;
                Tune(feed, args);
                var frame = feed.Latest ?? await feed.WaitForChangeAsync(0, TimeSpan.FromSeconds(3), cancel).ConfigureAwait(false);
                if (frame is null) return NoFrames(feed);
                bool same = args["since"] is JsonValue && McpJson.Int(args, "since", -1) == frame.Id;
                var result = FrameResult(host, [frame], same ? "unchanged since your frame" : null, McpJson.Bool(args, "image", true) && !same,
                    McpJson.Bool(args, "describe", true));
                if (McpJson.Bool(args, "ocr", false)) await AddScreenText(result, frame, cancel).ConfigureAwait(false);
                return result;
            });
        yield return read("get_viewport_frames", "Frames captured after a frame id, oldest first (the ring keeps the last 32 " +
            "changes): watch an animation or a camera move between calls. Each frame comes with its id, time and change text.",
            McpJson.Schema(("since", "integer", "Frame id; frames after it are returned (default 0)", false),
                ("max", "integer", "Most frames (default 4, max 10)", false), ("image", "boolean", "Include the JPEGs (default true)", false)),
            (args, _) =>
            {
                if (host.CredentialDocumentVisible) return Task.FromResult(McpToolResult.Error(CaptureRefusal));
                var feed = host.Viewport;
                feed.Want();
                var frames = feed.Since(McpJson.Int(args, "since", 0), Math.Clamp(McpJson.Int(args, "max", 4), 1, 10));
                if (frames.Count == 0)
                    return Task.FromResult(feed.Latest is null ? NoFrames(feed)
                        : McpToolResult.Json(new JsonObject { ["frames"] = new JsonArray(), ["latest"] = feed.Latest.Id, ["status"] = feed.Status }));
                return Task.FromResult(FrameResult(host, frames, null, McpJson.Bool(args, "image", true), false));
            });
        yield return read("wait_for_viewport_change", "Wait until the viewport shows something new (a frame newer than since), " +
            "up to timeout seconds; returns that frame with its change text, or says nothing changed. Use after an action or " +
            "while the user moves the camera.",
            McpJson.Schema(("since", "integer", "Frame id to compare with (default: the latest now)", false),
                ("timeout", "number", "Seconds to wait (default 10, max 60)", false),
                ("image", "boolean", "Include the JPEG (default true)", false), ("describe", "boolean", "Include on-screen objects (default true)", false),
                ("ocr", "boolean", "Also read the text on screen with Windows OCR (lines with [x, y, w, h] boxes in frame pixels; about 0.3-1 s). read_screen_text reads a region or the full-size screenshot", false)),
            async (args, cancel) =>
            {
                if (host.CredentialDocumentVisible) return McpToolResult.Error(CaptureRefusal);
                var feed = host.Viewport;
                feed.Want();
                long since = args["since"] is JsonValue ? McpJson.Int(args, "since", 0) : feed.Latest?.Id ?? 0;
                double seconds = args["timeout"] is JsonValue value && value.TryGetValue(out double number) ? number : 10;
                var frame = await feed.WaitForChangeAsync(since, TimeSpan.FromSeconds(Math.Clamp(seconds, 0.1, 60)), cancel).ConfigureAwait(false);
                if (frame is null)
                    return McpToolResult.Json(new JsonObject { ["changed"] = false, ["since"] = since, ["latest"] = feed.Latest?.Id, ["status"] = feed.Status,
                        ["lastCaptureMs"] = feed.LastCapture is { } at ? (long?)(DateTimeOffset.UtcNow - at).TotalMilliseconds : null });
                var changed = FrameResult(host, [frame], null, McpJson.Bool(args, "image", true), McpJson.Bool(args, "describe", true));
                if (McpJson.Bool(args, "ocr", false)) await AddScreenText(changed, frame, cancel).ConfigureAwait(false);
                return changed;
            });
        var regionSchema = McpJson.Schema(
            ("source", "string", "'frame' (default): the live viewport frame (fast, capture width, 960 by default); 'screenshot': the full-size viewport PNG, better for small text", false),
            ("frame", "integer", "A frame id still in the ring (get_viewport_frames) instead of the latest", false),
            ("region", "array", "Optional [x, y, width, height] in image pixels to read only that part (HUD corner, a panel, a button)", false),
            ("contains", "string", "Keep only lines containing this text (case-insensitive): where is 'Launch'?", false),
            ("words", "boolean", "Include each word with its own box (default false: lines only)", false),
            ("scale", "number", "Upscale before OCR (default auto: 2x for images or regions up to 1280 px, else 1x)", false),
            ("lang", "string", "OCR language tag, e.g. 'en-US' (default: the Windows user languages, else en-US)", false));
        regionSchema["properties"]!["region"]!["items"] = new JsonObject { ["type"] = "number" };
        yield return read("read_screen_text", "Read the text on screen (OCR with Windows.Media.Ocr, built into Windows; no GPU, " +
            "no download): every line with its [x, y, width, height] box in image pixels, optionally only a region or only lines " +
            "containing some text. Reads the live viewport frame by default or the full-size screenshot (source='screenshot'). " +
            "Destiny's letter-spaced labels can come back split ('N ESS U S'); read a tight region or try scale 2-3 for small HUD text.",
            regionSchema,
            async (args, cancel) =>
            {
                if (host.CredentialDocumentVisible) return McpToolResult.Error(CaptureRefusal);
                var region = ScreenOcr.ParseRegion(args["region"], out string? bad);
                if (bad is not null) return McpToolResult.Error(bad);
                byte[]? image;
                var source = new JsonObject();
                if (string.Equals(McpJson.String(args, "source"), "screenshot", StringComparison.OrdinalIgnoreCase))
                {
                    image = await host.ScreenshotAsync(null, false, cancel).ConfigureAwait(false);
                    if (image is null) return McpToolResult.Error("No viewport screenshot (is a map open and the viewport on?).");
                    source["source"] = "screenshot";
                }
                else
                {
                    var feed = host.Viewport;
                    feed.Want();
                    ViewportFrame? frame;
                    if (args["frame"] is JsonValue)
                    {
                        long id = McpJson.Int(args, "frame", 0);
                        frame = feed.Since(id - 1, 1).FirstOrDefault(item => item.Id == id);
                        if (frame is null) return McpToolResult.Error($"Frame {id} is no longer in the ring; latest is {feed.Latest?.Id}.");
                    }
                    else frame = feed.Latest ?? await feed.WaitForChangeAsync(0, TimeSpan.FromSeconds(3), cancel).ConfigureAwait(false);
                    if (frame is null) return NoFrames(feed);
                    image = frame.Jpeg;
                    source["source"] = "frame";
                    source["frame"] = frame.Id;
                }
                double scale = args["scale"] is JsonValue value && value.TryGetValue(out double number) ? number : 0;
                var reply = await ScreenOcr.Shared.ReadAsync(image, region, scale, McpJson.String(args, "lang"), cancel).ConfigureAwait(false);
                if (!ScreenOcr.Ok(reply)) return McpToolResult.Error(reply["error"]?.ToString() ?? "Screen OCR failed.");
                var summary = ScreenOcr.Summarize(reply, McpJson.Bool(args, "words", false), McpJson.String(args, "contains"));
                foreach (var (key, node) in source) summary[key] = node?.DeepClone();
                return McpToolResult.Json(summary);
            });
        yield return read("get_viewport_stream", "Where to watch the live viewport in a browser on this PC (the local URL), plus " +
            "capture rate, size and status.", McpJson.Schema(),
            (_, _) =>
            {
                var feed = host.Viewport;
                var info = new JsonObject
                {
                    ["local"] = feed.LocalUrl,
                    ["fps"] = feed.Fps, ["width"] = feed.MaxWidth, ["quality"] = feed.Quality, ["status"] = feed.Status, ["latest"] = feed.Latest?.Id
                };
                return Task.FromResult(McpToolResult.Json(info));
            });
    }

    /** UI-UE5 §5.3: captures are refused while the configured credential file is the active or a visible document. */
    private const string CaptureRefusal = Solforge.Harness.Credentials.CredentialGuard.Refusal + " Switch away from it before a capture.";

    /** Applies the width, quality and fps arguments to the viewport feed. */
    private static void Tune(ViewportFeed feed, JsonObject args)
    {
        feed.Want();
        if (args["width"] is JsonValue) feed.MaxWidth = McpJson.Int(args, "width", feed.MaxWidth);
        if (args["quality"] is JsonValue) feed.Quality = McpJson.Int(args, "quality", feed.Quality);
        if (args["fps"] is JsonValue) feed.Fps = McpJson.Int(args, "fps", feed.Fps);
    }

    /** The ocr option of the frame tools: OCR text as one more text block; a failure is a note, never a tool error. */
    private static async Task AddScreenText(McpToolResult result, ViewportFrame frame, CancellationToken cancel)
    {
        var reply = await ScreenOcr.Shared.ReadAsync(frame.Jpeg, null, 0, null, cancel).ConfigureAwait(false);
        var summary = ScreenOcr.Summarize(reply, false, null);
        summary["frame"] = frame.Id;
        result.Texts.Add(new JsonObject { ["screenText"] = summary }.ToJsonString(McpJson.Indented));
    }

    /** Tool error for when the viewport feed has produced no frame yet. */
    private static McpToolResult NoFrames(ViewportFeed feed) => McpToolResult.Error(
        $"No live viewport frame yet ({feed.Status}). The capture starts on this call; try again in a second. If the 3D viewport " +
        "is off, see get_editor_state and set_viewport_rendering.");

    /** Builds the tool result for viewport frames (images and metadata). */
    private static McpToolResult FrameResult(ISolforgeHost host, IReadOnlyList<ViewportFrame> frames, string? note, bool images, bool describe)
    {
        var now = DateTimeOffset.UtcNow;
        var feed = host.Viewport;
        var text = new JsonObject
        {
            ["frames"] = new JsonArray(frames.Select(frame => (JsonNode)frame.Describe(now)).ToArray()),
            ["latest"] = feed.Latest?.Id, ["status"] = feed.Status
        };
        if (note is not null) text["note"] = note;
        if (describe)
            text["objects"] = new JsonArray(host.Visible.OrderByDescending(item => item.Width * item.Height).Take(25)
                .Select(item => (JsonNode)new JsonObject
                {
                    ["id"] = item.Id, ["label"] = item.Label, ["kind"] = item.Kind,
                    ["rect"] = new JsonArray(Math.Round(item.X), Math.Round(item.Y), Math.Round(item.Width), Math.Round(item.Height))
                }).ToArray());
        var result = McpToolResult.Json(text);
        if (images) foreach (var frame in frames) result.Images.Add(frame.Jpeg);
        return result;
    }
}
