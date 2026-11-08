using System.Collections;
using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;

internal static class NoticesUiChecks
{
    /** Opens Help > Licenses and checks Studio's license and every embedded notice parsed and drew. */
    public static void Run(string root, string? capture = null)
    {
        string install = Path.Combine(root, "notices-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_notices_0100_0.pkg"), 0x100,
            [new(0x80807DAE, 0x1000, new byte[80], "Notices bubble")]);
        string settings = Path.Combine(root, "notices-settings.json");
        new StudioSettings().Save(settings);
        capture ??= Path.Combine(root, "notices.png");
        Exception? failure = null;
        int phase = 0, frames = 0, tabs = 0, tables = 0;
        string license = "", studio = "", dotnet = "", toolkit = "";
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new StudioForm(new(Game: install, Warp: true, Smoke: true, Frames: 24, SettingsPath: settings));
                const BindingFlags access = BindingFlags.NonPublic | BindingFlags.Instance;
                form.FrameRendered += () =>
                {
                    if (!form.EditorReady || form.Workspace.IsBusy) return;
                    try
                    {
                        if (phase == 0) { typeof(StudioForm).GetField("noticesDialog", access)!.SetValue(form, true); phase = 1; }
                        else if (phase == 1 && ++frames >= 3)
                        {
                            var parsed = (Array)typeof(StudioForm).GetField("notices", access)!.GetValue(form)!;
                            tabs = parsed.Length;
                            IEnumerable<object> Blocks(int tab) => ((IEnumerable)parsed.GetValue(tab)!).Cast<object>();
                            static object? Read(object block, string name) => block.GetType().GetProperty(name)!.GetValue(block);
                            static string Content(object block) => (string)Read(block, "Text")! + string.Concat(
                                ((string[][]?)Read(block, "Rows") ?? []).SelectMany(row => row).Select(cell => "\n" + cell));
                            string Text(int tab) => string.Join('\n', Blocks(tab).Select(Content));
                            license = Text(0); studio = Text(1); dotnet = Text(2); toolkit = Text(3);
                            tables = Blocks(1).Count(block => Read(block, "Rows") is not null);
                            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capture))!);
                            ((Dx11Renderer)typeof(StudioForm).GetField("renderer", access)!.GetValue(form)!).CapturePng(capture);
                            phase = 2; form.Close();
                        }
                    }
                    catch (Exception error) { failure = error; form.Close(); }
                };
                Application.Run(form);
                if (form.Failure is { } message) throw new InvalidOperationException(message);
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("Notices dialog failed.", failure);
        if (phase != 2) throw new InvalidDataException("Notices dialog was not drawn.");
        if (tabs != 4 || tables < 2) throw new InvalidDataException($"Notices parsed into {tabs} tabs and {tables} tables.");
        if (!license.Contains("GNU GENERAL PUBLIC LICENSE") || !license.Contains("Version 3, 29 June 2007"))
            throw new InvalidDataException("Studio's GPL 3.0 license text is missing.");
        foreach (string expected in new[] { "Omar Cornut", "Amer Koleci", "Xiph.org Foundation", "vgmstream" })
            if (!studio.Contains(expected)) throw new InvalidDataException($"Studio notices miss {expected}.");
        if (!dotnet.Contains(".NET") || !toolkit.Contains("PedroLamas"))
            throw new InvalidDataException("The .NET or Community Toolkit notices are missing.");
        Console.WriteLine($"editor_notices_ui dialog=pass tabs={tabs} tables={tables}");
    }
}
