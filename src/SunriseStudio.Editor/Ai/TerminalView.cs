/**
 * File: ImGui view of the AI terminal: draws the VtScreen.cs buffer, forwards keyboard input to ConPtyTerminal.cs and
 * uses TerminalSelection.cs for copy.
 */
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor.Ai;

/**
 * Solforge Phase 47B: draws a ConPTY screen with ImGui and turns keys into terminal input while the
 * terminal has focus. Characters come from the form's KeyPress (control characters included); keys
 * that make no character (arrows, Home, Delete...) come from ImGui.
 * The mouse selects (drag, double-click a word, triple-click a line, Shift+click extends, also into the
 * scrollback); Ctrl+Shift+C copies, Ctrl+V / Ctrl+Shift+V / Shift+Insert paste and right-click opens Copy/Paste/Select all.
 * A paste of files copied in Explorer, or files dropped on the terminal, types their quoted paths; a clipboard image is
 * saved as a PNG under TerminalPaste.Folder and its path typed (TerminalPaste.cs).
 * Ctrl+C sends ^C; a second Ctrl+C within a second kills the engine's process tree (HelpCatalog lists these keys).
 */
internal sealed class TerminalView : IDisposable
{
    /** Typed characters waiting to be sent. */
    private readonly Queue<char> typed = new();
    /** Current mouse selection. */
    private readonly TerminalSelection selection = new();
    /** Detects a double press of the interrupt key. */
    private readonly DoublePress interrupt = new();
    private int scroll;
    private bool restart;
    /** What was typed on the engine's input line since the last Enter, while every key on it is known (see LineCommand). */
    private readonly System.Text.StringBuilder line = new();
    private bool lineKnown = true;
    /** Backspaces still to send, one per frame, to clear a line LineCommand took (engines drop a burst of them). */
    private int erase;
    /** Paths of clipboard images saved on a worker thread, typed by the next Draw on the UI thread. */
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> saved = new();
    private const string MenuId = "##ai-terminal-menu";
    /** Measured once per frame to get the font's fractional advance; CalcTextSize rounds one "M" up to a whole pixel. */
    private static readonly string Ruler = new('M', 256);

    public ConPtyTerminal? Terminal { get; private set; }
    public bool Focused { get; private set; }
    public string? Failure { get; private set; }
    /** Where the terminal was last drawn, in ImGui (client) coordinates; empty before the first Draw. */
    public (Vector2 Min, Vector2 Max) Area { get; private set; }
    /** True when the terminal was drawn in the last two seconds, so a drop never lands in a hidden AI panel. */
    public bool Visible => Environment.TickCount64 - drawn < 2000;
    private long drawn = long.MinValue / 2;
    /** True while a paste or drop can reach a running engine. */
    public bool Accepting => Terminal is { HasExited: false } && !Stopped;
    /** Ctrl+C twice ended the engine; its last screen stays for reading and copying until Restart or Start. */
    public bool Stopped { get; private set; }
    /**
     * Sees each typed line when Enter is pressed (only lines typed key by key; a paste, Tab, Esc, Ctrl+C or a cursor key
     * makes the line unknown until the next Enter). Returning true keeps the Enter from the engine and erases the line
     * there, so Solforge commands such as /automatic never reach Claude Code, Cline or agy.
     */
    public Func<string, bool>? LineCommand { get; set; }

    /** Starts the command in a folder with a screen size and environment. */
    public void Start(string commandLine, string folder, int columns, int rows, IReadOnlyDictionary<string, string>? environment = null)
    {
        Stop();
        Failure = null;
        try { Terminal = new ConPtyTerminal(commandLine, folder, Math.Max(20, columns), Math.Max(5, rows), environment); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        { Failure = exception.Message; Terminal = null; }
    }

    /** Stops the session. */
    public void Stop()
    {
        Terminal?.Dispose();
        Terminal = null;
        Stopped = false;
        typed.Clear();
        selection.Clear();
        interrupt.Reset();
        scroll = 0;
    }

    /** Kills the engine with every process it started and keeps the screen; Draw then offers Restart. */
    public void StopEngine()
    {
        if (Terminal is not { } terminal || Stopped) return;
        terminal.KillTree();
        Stopped = true;
        typed.Clear();
        interrupt.Reset();
    }

    /** True once after the "engine stopped" Restart button was pressed. */
    public bool TakeRestart()
    {
        bool value = restart;
        restart = false;
        return value;
    }

    /** Puts the selection on the clipboard; false when nothing is selected. */
    public bool Copy()
    {
        if (Terminal is not { } terminal) return false;
        string text;
        lock (terminal.Screen.Gate) text = selection.Text(terminal.Screen.LineAt);
        if (text.Length == 0) return false;
        ImGui.SetClipboardText(text);
        return true;
    }

    /** Selects all text in the buffer. */
    public void SelectAll()
    {
        if (Terminal is not { } terminal) return;
        var screen = terminal.Screen;
        lock (screen.Gate) selection.SelectAll(screen.FirstLine, screen.Pushed + screen.Rows - 1);
    }

    /** From the form's KeyPress; only kept while the terminal has focus. */
    public void OnChar(char c)
    {
        if (Focused && Terminal is not null) typed.Enqueue(c);
    }

    /** Pastes text into the terminal. */
    public void Paste(string text)
    {
        if (Terminal is not { } terminal || Stopped || text.Length == 0) return;
        terminal.Send(PasteText(text, terminal.Screen.BracketedPaste));
        scroll = 0;
        lineKnown = false;
    }

    /**
     * Pastes whatever the clipboard holds: files as quoted paths, text as text, an image as the quoted path of a PNG saved
     * on a worker thread (typed by a later frame). Falls back to ImGui's clipboard text when the clipboard cannot be read.
     */
    public void PasteClipboard()
    {
        if (!Accepting) return;
        switch (TerminalPaste.Read())
        {
            case { Files: { } files }: Paste(TerminalPaste.Paths(files)); break;
            case { Text: { } text }: Paste(text); break;
            case { Png: { } png }: SaveInBackground(() => TerminalPaste.SavePng(png, TerminalPaste.Folder)); break;
            case { Image: { } image }: SaveInBackground(() => TerminalPaste.SaveImage(image, TerminalPaste.Folder)); break;
            default: Paste(ImGui.GetClipboardText() ?? ""); break;
        }
    }

    /** Files dropped on Solforge over the terminal (or while it has focus): types their quoted paths and takes focus. */
    public void DropFiles(IEnumerable<string> paths)
    {
        if (!Accepting) return;
        Paste(TerminalPaste.Paths(paths));
        Focused = true;
    }

    /** Runs an image save off the UI thread; the path is typed by the next Draw. A failed save is kept in Failure. */
    private void SaveInBackground(Func<string> save) => _ = Task.Run(() =>
    {
        try { saved.Enqueue(TerminalPaste.Quote(save()) + " "); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException
            or ArgumentException or OutOfMemoryException) { Failure = "Could not save the pasted image: " + error.Message; }
    });

    /** Sends Enter for text Solforge put on the input line itself (a paste it then submits), so the next typed line is known again. */
    public void Submit()
    {
        if (Terminal is not { } terminal || Stopped) return;
        terminal.Send("\r");
        line.Clear();
        lineKnown = true;
    }

    /**
     * What a paste sends: line breaks become Enter (CR) as Windows Terminal sends them, and in bracketed mode the text is
     * wrapped in 200~/201~ with any end marker inside it removed, so pasted text cannot end the paste early.
     */
    internal static string PasteText(string text, bool bracketed)
    {
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        return bracketed ? "\x1b[200~" + text.Replace("\x1b[201~", "") + "\x1b[201~" : text;
    }

    /** Draws the terminal and handles mouse and resize. */
    public void Draw(ImFontPtr font, Vector2 size)
    {
        while (saved.TryDequeue(out string? path)) Paste(path);
        if (Stopped && Terminal is not null)
        {
            // A row above the kept screen rather than a band over it, so its button gets the clicks.
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(SolTheme.Sun, "Engine stopped.");
            ImGui.SameLine();
            if (SolTheme.PrimaryButton("Restart##ai-terminal-restart")) restart = true;
            size.Y = Math.Max(1, size.Y - ImGui.GetFrameHeightWithSpacing());
        }
        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        Area = (origin, origin + size);
        drawn = Environment.TickCount64;
        draw.AddRectFilled(origin, origin + size, SolTheme.U32(SolTheme.Bg));
        ImGui.InvisibleButton("##ai-terminal", Vector2.Max(size, Vector2.One));
        bool hovered = ImGui.IsItemHovered(), pressed = ImGui.IsItemClicked(ImGuiMouseButton.Left),
            menu = ImGui.IsItemClicked(ImGuiMouseButton.Right);
        if (pressed || menu) Focused = true;
        // A click in the terminal's own menu keeps focus, so a Paste from it can be followed by typing.
        else if ((ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsMouseClicked(ImGuiMouseButton.Right)) && !hovered
            && !ImGui.IsPopupOpen(MenuId)) Focused = false;
        if (hovered) { scroll = Math.Max(0, scroll + (int)(ImGui.GetIO().MouseWheel * 3)); }
        if (Terminal is not { } terminal)
        {
            draw.AddText(origin + new Vector2(8), SolTheme.U32(SolTheme.Dim), Failure ?? "Press Start to run the Pi harness.");
            return;
        }
        ImGui.PushFont(font);
        // AddText advances each glyph by the font's unrounded advance, so the grid must use the same pitch or a long
        // run drifts off its backgrounds, the cursor and the next colour run. Rows get a whole-pixel pitch.
        var cell = new Vector2(ImGui.CalcTextSize(Ruler).X / Ruler.Length, MathF.Ceiling(ImGui.CalcTextSize("M").Y));
        float fontSize = ImGui.GetFontSize();
        ImGui.PopFont();
        int columns = Math.Max(20, (int)((size.X - 8) / cell.X)), rows = Math.Max(5, (int)((size.Y - 8) / cell.Y));
        terminal.Resize(columns, rows);
        if (Focused) Input(terminal);
        var screen = terminal.Screen;
        lock (screen.Gate)
        {
            scroll = Math.Min(scroll, screen.Scrollback.Count);
            var start = new Vector2(MathF.Floor(origin.X + 4), MathF.Floor(origin.Y + 4));
            Select(screen, start, cell, pressed);
            for (int row = 0; row < screen.Rows; row++)
            {
                int source = row - scroll;
                var line = source < 0 ? screen.Scrollback[screen.Scrollback.Count + source] : screen.Line(source);
                DrawLine(draw, font, fontSize, start + new Vector2(0, row * cell.Y), cell, line);
            }
            if (selection.Span(screen.LineAt) is { } span)
            {
                uint shade = SolTheme.U32(SolTheme.Sun) & 0x60FFFFFF;
                for (int row = 0; row < screen.Rows; row++)
                    if (TerminalSelection.Columns(span, screen.Pushed + row - scroll, screen.Columns) is { } selected)
                    {
                        var from = start + new Vector2(selected.From * cell.X, row * cell.Y);
                        draw.AddRectFilled(from, from + new Vector2((selected.To - selected.From) * cell.X, cell.Y), shade);
                    }
            }
            if (scroll == 0 && screen.CursorVisible && Focused && (ImGui.GetTime() % 1.0) < .6)
            {
                var at = start + new Vector2(screen.CursorX * cell.X, screen.CursorY * cell.Y);
                draw.AddRectFilled(at, at + cell, SolTheme.U32(SolTheme.Sun) & 0xB0FFFFFF);
            }
        }
        if (!Stopped && Focused && interrupt.Pending(ImGui.GetTime()))
        {
            const string hint = "Ctrl+C again stops the engine";
            var at = new Vector2(origin.X + size.X - ImGui.CalcTextSize(hint).X - 10, origin.Y + 4);
            draw.AddRectFilled(at - new Vector2(4, 0), at + ImGui.CalcTextSize(hint) + new Vector2(4, 0), SolTheme.U32(SolTheme.Bg));
            draw.AddText(at, SolTheme.U32(SolTheme.Sun), hint);
        }
        if (terminal.HasExited && !Stopped)
        {
            // Sits on the last grid row over a cleared band rather than overprinting that row's text.
            var at = new Vector2(MathF.Floor(origin.X + 4), MathF.Floor(origin.Y + 4) + (rows - 1) * cell.Y);
            draw.AddRectFilled(at, at + new Vector2(columns * cell.X, cell.Y), SolTheme.U32(SolTheme.Bg));
            draw.AddText(at + new Vector2(4, 0), SolTheme.U32(SolTheme.Sun), "[process exited - press Restart]");
        }
        if (!Focused) draw.AddRect(origin, origin + size, SolTheme.U32(SolTheme.Line));
        else draw.AddRect(origin, origin + size, SolTheme.U32(SolTheme.Sun) & 0x80FFFFFF);
        DrawMenu(menu);
    }

    /**
     * Left press, drag and release over the grid. Character mode follows the gap nearest the mouse, word and line modes
     * the cell under it; dragging past the top or bottom edge scrolls a line a frame, so the selection reaches scrollback.
     */
    private void Select(VtScreen screen, Vector2 start, Vector2 cell, bool pressed)
    {
        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        if (selection.Dragging)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) { selection.Release(); return; }
            if (mouse.Y < start.Y) scroll = Math.Min(screen.Scrollback.Count, scroll + 1);
            else if (mouse.Y >= start.Y + screen.Rows * cell.Y) scroll = Math.Max(0, scroll - 1);
        }
        if (!pressed && !selection.Dragging) return;
        float x = (mouse.X - start.X) / cell.X;
        int row = Math.Clamp((int)MathF.Floor((mouse.Y - start.Y) / cell.Y), 0, screen.Rows - 1);
        long line = screen.Pushed + row - scroll;
        var caret = new TerminalPoint(line, Math.Clamp((int)MathF.Round(x), 0, screen.Columns));
        var under = new TerminalPoint(line, Math.Clamp((int)MathF.Floor(x), 0, screen.Columns - 1));
        if (pressed) selection.Press(caret, under, ImGui.GetMouseClickedCount(ImGuiMouseButton.Left), io.KeyShift);
        else selection.Drag(caret, under);
    }

    /** Draws the right-click menu. */
    private void DrawMenu(bool open)
    {
        if (open) ImGui.OpenPopup(MenuId);
        if (!ImGui.BeginPopup(MenuId)) return;
        if (ImGui.MenuItem("Copy", "Ctrl+Shift+C", false, selection.Active)) Copy();
        if (ImGui.MenuItem("Paste", "Ctrl+V", false, Accepting)) PasteClipboard();
        if (ImGui.MenuItem("Select all")) SelectAll();
        ImGui.EndPopup();
    }

    /** Draws one line of cells with colours and styles. */
    private static void DrawLine(ImDrawListPtr draw, ImFontPtr font, float fontSize, Vector2 at, Vector2 cell, VtCell[] line)
    {
        uint defaultText = SolTheme.U32(SolTheme.Text);
        int x = 0;
        var run = new System.Text.StringBuilder();
        while (x < line.Length)
        {
            var first = line[x];
            uint fg = first.Inverse ? Abgr(first.Background, SolTheme.U32(SolTheme.Bg)) : Abgr(first.Foreground, defaultText);
            uint bg = first.Inverse ? Abgr(first.Foreground, defaultText) : Abgr(first.Background, 0);
            if (first.Dim) fg = Fade(fg, bg != 0 ? bg : SolTheme.U32(SolTheme.Bg));
            int end = x;
            run.Clear();
            while (end < line.Length && line[end].Foreground == first.Foreground && line[end].Background == first.Background
                && line[end].Inverse == first.Inverse && line[end].Dim == first.Dim && line[end].Underline == first.Underline
                && line[end].Strike == first.Strike)
            {
                // Glyphs are one cell wide, so the right half of a wide character pads with a space.
                char c = line[end++].Char;
                run.Append(c == VtScreen.Continuation ? ' ' : c);
            }
            var from = at + new Vector2(x * cell.X, 0);
            if (bg != 0) draw.AddRectFilled(from, from + new Vector2((end - x) * cell.X, cell.Y), bg);
            string text = run.ToString();
            if (text.AsSpan().TrimEnd(' ').Length > 0) draw.AddText(font, fontSize, from, fg, text);
            float width = (end - x) * cell.X;
            if (first.Underline) draw.AddLine(from + new Vector2(0, cell.Y - 1.5f), from + new Vector2(width, cell.Y - 1.5f), fg);
            if (first.Strike) draw.AddLine(from + new Vector2(0, MathF.Floor(cell.Y / 2) + .5f), from + new Vector2(width, MathF.Floor(cell.Y / 2) + .5f), fg);
            x = end;
        }
    }

    /** Halfway from one 0xAABBGGRR colour to another, for faint (SGR 2) text. */
    private static uint Fade(uint color, uint toward)
    {
        uint mix = 0xFF000000;
        for (int shift = 0; shift < 24; shift += 8)
            mix |= (((color >> shift) & 0xFF) + ((toward >> shift) & 0xFF)) / 2 << shift;
        return mix;
    }

    /** 0xAARRGGBB (0 = fallback) to ImGui's 0xAABBGGRR. */
    private static uint Abgr(uint argb, uint fallback) => argb == 0 ? fallback
        : argb & 0xFF00FF00 | (argb & 0xFF) << 16 | (argb >> 16) & 0xFF;

    /** Reads keyboard input and sends it to the terminal. */
    private void Input(ConPtyTerminal terminal)
    {
        var io = ImGui.GetIO();
        // The shortcut system must not see keys typed into the terminal.
        io.WantTextInput = true;
        while (typed.TryDequeue(out char c))
        {
            // Ctrl+Shift+C arrives as the same ^C character as Ctrl+C; Shift makes it Copy.
            if (c == 3 && io.KeyShift) { Copy(); continue; }
            if (Stopped) continue;
            if (c == '\r' && lineKnown && line.Length > 0 && LineCommand?.Invoke(line.ToString()) == true)
            {
                erase += line.Length;
                line.Clear();
                selection.Clear();
                scroll = 0;
                continue;
            }
            TrackLine(c);
            if (c == 3)
            {
                if (interrupt.Press(ImGui.GetTime())) { StopEngine(); return; }
                terminal.Send("\x03");
            }
            else
            {
                interrupt.Reset();
                // Ctrl+V and Ctrl+Shift+V both arrive as ^V.
                if (c == '\b') terminal.Send("\x7f");
                else if (c == 22 && io.KeyCtrl) PasteClipboard();
                else terminal.Send(c.ToString());
            }
            selection.Clear();
            scroll = 0;
        }
        if (Stopped) return;
        if (erase > 0) { terminal.Send("\x7f"); erase--; }
        string arrows = terminal.Screen.ApplicationCursor ? "\x1bO" : "\x1b[";
        (ImGuiKey Key, string Text)[] keys =
        [
            (ImGuiKey.UpArrow, arrows + "A"), (ImGuiKey.DownArrow, arrows + "B"), (ImGuiKey.RightArrow, arrows + "C"),
            (ImGuiKey.LeftArrow, arrows + "D"), (ImGuiKey.Home, "\x1b[H"), (ImGuiKey.End, "\x1b[F"),
            (ImGuiKey.Delete, "\x1b[3~"), (ImGuiKey.PageUp, "\x1b[5~"), (ImGuiKey.PageDown, "\x1b[6~"), (ImGuiKey.Insert, "\x1b[2~")
        ];
        foreach (var (key, text) in keys)
        {
            if (!ImGui.IsKeyPressed(key)) continue;
            // Shift+Insert is the classic Windows console paste; it makes no character, so it is caught here.
            if (key == ImGuiKey.Insert && io.KeyShift && !io.KeyCtrl) { PasteClipboard(); continue; }
            terminal.Send(text);
            scroll = 0;
            lineKnown = false;
        }
        if (io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.Tab)) { terminal.Send("\x1b[Z"); lineKnown = false; }
    }

    /** Follows one typed character on the input line for LineCommand. */
    private void TrackLine(char c)
    {
        if (c is '\r' or '\n') { line.Clear(); lineKnown = true; }
        else if (c == '\b') { if (line.Length > 0) line.Length--; }
        else if (c >= ' ' && c != '\x7f') line.Append(c);
        else lineKnown = false;
    }

    /** Stops the session. */
    public void Dispose() => Stop();
}
