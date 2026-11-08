using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using SunriseStudio.Live;
using SunriseStudio.Viewport;

try
{
    if (args is ["--capture-live", var game, var output, var duration])
        return LiveCapture.Run(game, output, int.Parse(duration));
    if (args is not ([] or ["--audit-live-library"] or ["--publisher", _]))
    {
        Console.Error.WriteLine("usage: [--audit-live-library | --publisher <exe> | --capture-live <game> <output> <seconds>]");
        return 2;
    }
    return Run(args);
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

static int Run(string[] args)
{
if (args is ["--audit-live-library"]) { LiveAuditChecks.Run().GetAwaiter().GetResult(); return 0; }
LiveAuditChecks.Run().GetAwaiter().GetResult();
CommandChecks.Run();
WorldScaleChecks.Run();
CuiChecks.Run();
var frame = new byte[128];
void U(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(at), value);
void F(int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(frame.AsSpan(at), value);
void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
U(0, LiveProtocol.Magic); U(4, LiveProtocol.Version); U(8, 128);
U(24, 2); BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(32), 10000);
U(40, 123); U(44, 31); U(48, 7);
F(56, 2); F(60, 4); F(64, 6);
F(68, 2); F(72, 4); F(76, 7);
F(80, 1); F(100, 1); F(104, 1.5f); F(108, 1.75f);
U(112, 0x12340009);
var read = LiveProtocol.Read(frame, 123, 10010)!;
Check(read.Player == new Vector3(2, 4, 6) && read.CurrentSlice == 7, "Native field offsets differ.");
Check(read.PlayerHandle is null, "A player handle was read without its flag.");
U(44, 31 | 32); read = LiveProtocol.Read(frame, 123, 10010)!; U(44, 31);
Check(read.PlayerHandle == 0x12340009, "The player handle offset differs from the native frame.");
Check(read.Camera!.Position == new Vector3(2, 4, 7), "Native camera position differs.");
Check(LiveProtocol.Read(frame, 123, 12000) is null, "Expired frame was accepted.");
Check(LiveProtocol.Read(frame, 123, 9000) is null, "Future frame was accepted.");
U(44, 8);
read = LiveProtocol.Read(frame, 123, 10010)!;
Check(read.Player is null && read.Camera is null && read.CurrentSlice is null, "Load retained stale state.");
U(44, 31); F(104, float.NaN);
try { LiveProtocol.Read(frame, 123, 10010); throw new Exception("Invalid lens was accepted."); }
catch (InvalidDataException) { }
F(104, 1.5f);
var camera = new ViewerCamera();
var forward = Vector3.Normalize(new Vector3(1, 2, .4f));
var up = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
var eye = new Vector3(10, 20, 30);
camera.SetExternalPose(eye, forward, up, MathF.PI / 2);
Check(Vector3.Distance(camera.Position, eye) < 1e-5f, "External eye moved.");
Check(Vector3.Distance(camera.Up, up) < 1e-5f, "Camera roll was lost.");
var clip = Vector4.Transform(new Vector4(eye + forward * 20, 1), camera.ViewProjection(2));
Check(Math.Abs(clip.X / clip.W) < 1e-5f && Math.Abs(clip.Y / clip.W) < 1e-5f, "Camera heading missed center.");
Check(Math.Abs(camera.Projection(2).M11 - 1) < 1e-5f, "Horizontal field of view differs.");
camera.ClearExternalPose();
Check(!camera.HasExternalPose && Vector3.Distance(camera.Position, eye) < 1e-5f, "Disconnect moved the eye.");
Console.WriteLine("live protocol=pass camera=pass stale=pass");

if (args is ["--publisher", var publisherPath])
{
    using var publisher = Process.Start(new ProcessStartInfo(Path.GetFullPath(publisherPath)) { UseShellExecute = false, CreateNoWindow = true })!;
    // A just-started process lists no modules until its loader runs.
    string? executable = null;
    for (int wait = 0; wait < 1000 && executable is null && !publisher.HasExited; wait++)
    {
        publisher.Refresh();
        executable = publisher.MainModule?.FileName;
        if (executable is null) Thread.Sleep(1);
    }
    using var connection = LiveConnection.Attach(new(publisher.Id, executable ?? throw new Exception("The native publisher did not start.")));
    int received = 0;
    int worlds = 0, placements = 0, cuiScreens = 0, cuiTrees = 0, cuiDetails = 0;
    var cuiScreen = new LiveCuiTarget(CuiChecks.ScreenTag, 0);
    connection.OpenCui();
    connection.WatchCui(cuiScreen);
    connection.RequestCuiWidget(new LiveCuiWidgetTarget(CuiChecks.ScreenTag, 0, 11));
    var deadline = Stopwatch.StartNew();
    while (!publisher.HasExited && deadline.Elapsed.TotalSeconds < 10)
    {
        if (connection.Poll() is { Player: { } player, Camera: { } lens } value)
        {
            Check(player.Y == player.X * 2 && player.Z == 3, "A torn player snapshot was read.");
            Check(lens.Position == player && value.CurrentSlice == 7, "Native snapshot fields differ.");
            received++;
        }
        if (connection.World is { } world)
        {
            Check(world.MapDefinitionTag == 0x80800001 && world.Activity == "mission_lights"
                && world.Slices.Single() == new LiveSlice(123, 7, 4), "Native world layout differs.");
            worlds++;
        }
        if (connection.Placements is { } placementSet)
        {
            Check(placementSet.Rows.Count == 2, "Native placement row stride differs.");
            var row = placementSet.Rows[0];
            Check(row.Identity == 123456 && row.ObjectListTag == 0x80800002 && row.EntryIndex == 3
                && row.Generation == 4 && row.Orientation == Quaternion.Identity
                && row.Position.Y == row.Position.X * 2 && row.Scale == -2, "Native placement layout differs.");
            Check(placementSet.Rows[1] is { Dynamic: true, ClassTag: 0x80800003, ObjectListTag: uint.MaxValue, Scale: 0 },
                "Native dynamic identity fields differ.");
            placements++;
        }
        if (connection.CuiScreens is { } screens)
        {
            Check(connection.CuiAlive && connection.CuiBridgeDiagnostic == "" && screens.Rows.Count == 2
                && screens.Rows[0] is { HierarchyTag: CuiChecks.ScreenTag, ViewHash: 7, Visible: true, Layer: 0 }
                && screens.Top is { Layer: 1, Ordinal: 1, SortLast: true }, "Native CUI screen layout differs.");
            cuiScreens++;
        }
        if (connection.CuiTree is { } tree)
        {
            Check(tree is { Status: LiveCuiStatus.Complete, HierarchyTag: CuiChecks.ScreenTag, Rows.Count: 3 }
                && tree.Rows[2].ParentRow == 1 && tree.Rows.All(row => row.IsWidget)
                && tree.Rows[1] is { NodeId: 11, IsText: true, Width: 30 },
                "Native CUI tree layout differs.");
            cuiTrees++;
        }
        if (connection.CuiDetail is { Status: LiveCuiStatus.Complete } detail)
        {
            Check(detail is { Text: "Hi", NodeId: 11, HierarchyTag: CuiChecks.ScreenTag, IsText: true },
                "Native CUI widget record differs.");
            cuiDetails++;
        }
        Thread.Sleep(1);
    }
    if (!publisher.HasExited) { publisher.Kill(); publisher.WaitForExit(); }
    Check(publisher.ExitCode == 0 && received > 20, $"Native publisher failed: exit={publisher.ExitCode}, frames={received}.");
    Check(connection.HasExited && connection.Poll() is null, "Exited process retained a snapshot.");
    Check(worlds > 20 && placements > 20, "Native world snapshots are missing.");
    Check(cuiScreens > 0 && cuiTrees > 0 && cuiDetails > 0,
        $"Native CUI answers are missing: screens={cuiScreens}, trees={cuiTrees}, details={cuiDetails}.");
    Console.WriteLine($"live native_mapping=pass frames={received} worlds={worlds} placements={placements} cui_screens={cuiScreens}");
}
return 0;
}
