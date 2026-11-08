using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Rendering;

internal static class WideHashUiChecks
{
    public static void Run(string root, string capture)
    {
        string install = Path.Combine(root, "alias-modal-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_alias_0100_0.pkg"), 0x100,
            [new(0x80807DAE, 0x1000, new byte[80], "Original bubble")]);
        var project = StudioProject.Create(Path.Combine(root, "alias-modal-project"), "Authored wide references");
        using var catalog = PackageCatalog.Open(install);
        var asset = project.AddDerivedAsset("Owned bubble", 0x80A00000, new byte[80], new(0xC00, 0x80807DAE, 0x1000), catalog);
        string settings = Path.Combine(root, "alias-modal-settings.json");
        new StudioSettings { BrowserWidth = 260, InspectorWidth = 340 }.Save(settings);
        Exception? failure = null;
        int phase = 0, frames = 0;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new StudioForm(new(Game: install, Project: project.ManifestPath, Tag: 0x82000000,
                    Warp: true, Smoke: true, Frames: 24, SettingsPath: settings));
                const BindingFlags access = BindingFlags.NonPublic | BindingFlags.Instance;
                form.FrameRendered += () =>
                {
                    if (!form.EditorReady || form.Workspace.IsBusy) return;
                    try
                    {
                        if (phase == 0)
                        {
                            typeof(StudioForm).GetMethod("BeginWideHashes", access)!.Invoke(form, [asset.Id]);
                            var draft = (WideHashAliasDraft)typeof(StudioForm).GetField("wideHashDraft", access)!.GetValue(form)!;
                            draft.Add("0xFEDCBA9876543210"); draft.Add("0123456789ABCDEF");
                            phase = 1;
                        }
                        else if (phase == 1 && ++frames >= 3)
                        {
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
        if (failure is not null) throw new InvalidOperationException("Wide-hash modal failed.", failure);
        if (phase != 2) throw new InvalidDataException("Wide-hash modal was not captured.");
        Console.WriteLine("editor_wide_hash_ui modal=pass aliases=2 draft_only=true");
    }
}
