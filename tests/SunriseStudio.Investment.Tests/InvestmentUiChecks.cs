using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Projects;
using SunriseStudio.Rendering;

internal static class InvestmentUiChecks
{
    /** Real frames must discard old drafts before an identical tag can be edited in another project. */
    public static void Run(string install, string temporary, string? capture)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckFrames(install, temporary, capture); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("Investment UI check failed.", failure);
    }

    private static void CheckFrames(string install, string temporary, string? capture)
    {
        var one = StudioProject.Create(Path.Combine(temporary, "ui-one"), "First project");
        var two = StudioProject.Create(Path.Combine(temporary, "ui-two"), "Second project");
        string settings = Path.Combine(temporary, "ui-settings.json");
        new StudioSettings { DiskCacheEnabled = false }.Save(settings);
        using var form = new StudioForm(new(Game: install, Project: one.ManifestPath, Tag: 0x81319AF9,
            Page: "Investment", Warp: true, Smoke: true, Frames: 200, WaitForNames: true,
            SettingsPath: settings, Capture: capture));
        const BindingFlags access = BindingFlags.Instance | BindingFlags.NonPublic;
        int phase = 0, count = 0;
        // The smoke budget counts frames after the editor is ready and names are indexed.
        int Frames() => (int)typeof(StudioForm).GetField("smokeFrames", access)!.GetValue(form)!;
        form.FrameRendered += () =>
        {
            var work = form.Workspace;
            if (work.Error.Length != 0) throw new InvalidDataException(work.Error);
            if (!form.EditorReady || work.IsBusy || !work.InvestmentDetailCurrent || work.SelectedInvestmentItem is not { } detail) return;
            var page = form.Pages.Pages.OfType<InvestmentPage>().Single();
            var context = new StudioPageContext(work, form.SceneViewport!, _ => { });
            void Row() => typeof(InvestmentPage).GetMethod("BeginRow", access)!.Invoke(page, [context, false]);
            object Popup() => typeof(InvestmentPage).GetField("popup", access)!.GetValue(page)!;
            switch (phase)
            {
                case 0:
                    count = detail.Stats.Count;
                    typeof(InvestmentPage).GetMethod("BeginEdit", access)!.Invoke(page, [context, detail.Stats[0].Field]);
                    Popup().GetType().GetProperty("Value")!.SetValue(Popup(), "123");
                    phase++; break;
                case 1: work.OpenProject(two.ManifestPath); phase++; break;
                case 2:
                    Check(Popup().GetType().GetProperty("Field")!.GetValue(Popup()) is null,
                        "Old field draft survived a project switch.");
                    Check(work.Project!.Changes.Count == 0, "Old draft changed the second project.");
                    Row(); phase++; break;
                case 3: work.OpenProject(one.ManifestPath); phase++; break;
                case 4:
                    Check(typeof(InvestmentPage).GetField("rowDocument", access)!.GetValue(page) is null, "Old row draft survived a project switch.");
                    work.AddInvestmentStat(detail.Stats[0].Index, 9); phase++; break;
                case 5: Check(detail.Stats.Count == count + 1, "UI did not refresh appended stats."); work.Undo(); phase++; break;
                case 6: Check(detail.Stats.Count == count, "UI structural undo failed."); work.Redo(); phase++; break;
                case 7: Check(detail.Stats.Count == count + 1, "UI structural redo failed."); work.RemoveInvestmentStat(detail.Stats[^1]); phase++; break;
                case 8: Check(detail.Stats.Count == count, "UI stat removal failed."); Row(); phase++; break;
                case 9:
                    if (capture is not null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(capture)!);
                        ((Dx11Renderer)typeof(StudioForm).GetField("renderer", access)!.GetValue(form)!).CapturePng(capture);
                    }
                    form.Close(); break;
            }
        };
        Application.Run(form);
        if (form.Failure is { } error) throw new InvalidDataException(error);
        Check(phase == 9 && StudioProject.Open(two.ManifestPath).Changes.Count == 0,
            $"Investment UI did not finish its project checks in {Frames()} frames.");
        Console.WriteLine($"investment_ui drafts=isolated add=pass remove=pass undo=pass redo=pass refresh=pass frames={Frames()}");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
