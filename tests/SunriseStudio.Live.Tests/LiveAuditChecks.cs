using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using SunriseStudio.Live;

/** These publishers open only the test process's local mappings. */
internal static class LiveAuditChecks
{
    public static async Task Run()
    {
        List<string> failures = [];
        void Case(string name, Action check)
        {
            try { check(); Console.WriteLine($"live_audit case={name} result=pass"); }
            catch (Exception error) { failures.Add($"{name}: {error.Message}"); Console.WriteLine($"live_audit case={name} result=fail"); }
        }
        async Task AsyncCase(string name, Func<Task> check)
        {
            try { await check(); Console.WriteLine($"live_audit case={name} result=pass"); }
            catch (Exception error) { failures.Add($"{name}: {error.Message}"); Console.WriteLine($"live_audit case={name} result=fail"); }
        }
        Case("camera_overflow", Camera);
        Case("camera_normalized_basis", CameraNormalizedBasis);
        Case("placement_rotation_overflow", Placement);
        Case("object_rotation_overflow", ObjectRotation);
        Case("object_frame_size", ObjectSize);
        Case("draw_anchor_overflow", DrawAnchor);
        await AsyncCase("canceled_command_open", CanceledOpen);
        await AsyncCase("command_roundtrip_retry_dispose", CommandLifecycle);
        if (failures.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, failures));
    }

    private static void Camera()
    {
        byte[] bytes = new byte[LiveProtocol.Size];
        U(bytes, 0, LiveProtocol.Magic); U(bytes, 4, LiveProtocol.Version); U(bytes, 8, LiveProtocol.Size);
        U(bytes, 24, 2); Q(bytes, 32, 1000); U(bytes, 40, 7); U(bytes, 44, 1 | 4);
        F(bytes, 80, 1); F(bytes, 100, 1); F(bytes, 104, 1.5f); F(bytes, 108, 1.7f);
        var normal = LiveProtocol.Read(bytes, 7, 1000)!.Camera!;
        Check(normal.Forward == Vector3.UnitX && normal.Up == Vector3.UnitZ, "Valid camera basis changed.");
        F(bytes, 80, float.MaxValue);
        Reject<InvalidDataException>(() => LiveProtocol.Read(bytes, 7, 1000), "Overflowing camera normalized to a zero direction.");
    }

    private static void Placement()
    {
        byte[] bytes = new byte[LiveWorldProtocol.PlacementHeaderBytes + LiveWorldProtocol.PlacementBytes];
        U(bytes, 0, 2); U(bytes, 4, 3); Q(bytes, 8, 1000); U(bytes, 16, 1); U(bytes, 20, 1);
        U(bytes, 80, 42); U(bytes, 84, 9); F(bytes, 108, -2); F(bytes, 124, 1); Q(bytes, 128, 1000); U(bytes, 136, 5);
        Check(LiveWorldProtocol.ReadPlacements(bytes, 1000)!.Rows[0] is { TransformAvailable: true, Scale: -2 },
            "Valid signed-scale placement was rejected.");
        F(bytes, 112, float.MaxValue);
        var row = LiveWorldProtocol.ReadPlacements(bytes, 1000)!.Rows[0];
        Check(!row.TransformAvailable && row.Orientation == Quaternion.Identity && row.Scale is null,
            "Overflowing placement quaternion remained an available transform.");
    }

    private static void CameraNormalizedBasis()
    {
        byte[] bytes = new byte[LiveProtocol.Size];
        U(bytes, 0, LiveProtocol.Magic); U(bytes, 4, LiveProtocol.Version); U(bytes, 8, LiveProtocol.Size);
        U(bytes, 24, 2); Q(bytes, 32, 1000); U(bytes, 40, 7); U(bytes, 44, 1 | 4);
        F(bytes, 80, 1e19f); F(bytes, 92, 1e19f); F(bytes, 96, 1e-21f); F(bytes, 104, 1.5f); F(bytes, 108, 1.7f);
        Reject<InvalidDataException>(() => LiveProtocol.Read(bytes, 7, 1000),
            "Camera basis became parallel only after normalization.");
    }

    private static void DrawAnchor()
    {
        byte[] header = new byte[LivePoseProtocol.HeaderBytes], draw = new byte[LivePoseProtocol.DrawBytes], palette = new byte[32];
        U(header, 4, 1); Q(header, 8, 1000); U(header, 16, 1); U(header, 20, 2);
        U(draw, 0, 42); U(draw, 4, 43); U(draw, 8, 0x80800001); U(draw, 12, 1);
        U(draw, 16, 1); U(draw, 20, 1); U(draw, 28, 2);
        F(palette, 12, 1); F(palette, 16, .5f); F(palette, 20, 1); F(palette, 24, 1.5f);
        Check(LivePoseProtocol.Read(header, draw, palette, 1000)!.Draws.Single().Anchor == new Vector3(1, 2, 3),
            "Valid native dual-quaternion translation changed.");
        Array.Clear(palette); F(palette, 0, float.MaxValue); F(palette, 28, 1);
        var parsed = LivePoseProtocol.Read(header, draw, palette, 1000)!;
        Check(parsed.Draws.Count == 0 && parsed.Rejected == 1,
            "Finite palette lanes produced a non-finite live draw anchor.");
    }

    private static byte[] ObjectFrame()
    {
        byte[] bytes = new byte[LiveObjectProtocol.MappingBytes];
        U(bytes, 0, LiveObjectProtocol.Magic); U(bytes, 4, LiveObjectProtocol.Version); U(bytes, 8, LiveObjectProtocol.MappingBytes);
        U(bytes, LiveObjectProtocol.SequenceOffset, 2); U(bytes, LiveObjectProtocol.ResponseOffset, 3);
        U(bytes, 44, (uint)LiveObjectStatus.Complete); U(bytes, 48, 7); Q(bytes, 56, 1000);
        U(bytes, 92, 1); F(bytes, 108, 0); F(bytes, 124, 1);
        return bytes;
    }

    private static void ObjectRotation()
    {
        var bytes = ObjectFrame();
        Check(LiveObjectProtocol.Read(bytes, 7, 3, 42, 9) is { TransformAvailable: true, Scale: 0 },
            "Valid zero-scale object was rejected.");
        F(bytes, 112, float.MaxValue);
        var row = LiveObjectProtocol.Read(bytes, 7, 3, 42, 9)!;
        Check(!row.TransformAvailable && row.Orientation == Quaternion.Identity && row.Scale is null,
            "Overflowing object quaternion remained an available transform.");
    }

    private static void ObjectSize()
    {
        var bytes = ObjectFrame(); U(bytes, 8, LiveObjectProtocol.MappingBytes - 1);
        Reject<InvalidDataException>(() => LiveObjectProtocol.Read(bytes, 7, 3, 42, 9),
            "Object reply with a conflicting declared mapping size was accepted.");
    }

    /** The actual open helper must not create a mapping after its cancellation was requested. */
    private static async Task CanceledOpen()
    {
        using var connection = new LiveCommandConnection(Environment.ProcessId);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Canceled(connection.ReloadModAsync("dev.test.pre_canceled", cancellation.Token));
        var open = typeof(LiveCommandConnection).GetMethod("OpenAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task task;
        try { task = (Task)open.Invoke(connection, [LiveCommandProtocol.OldestVersion, cancellation.Token])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        await Canceled(task);
        Reject<FileNotFoundException>(() => { using var map = MemoryMappedFile.OpenExisting(LiveCommandProtocol.MappingName(Environment.ProcessId)); },
            "Canceled command creation left a bridge mapping.");
    }

    /** Cancellation revokes the retained native view; a fresh mapping must accept a later request. */
    private static async Task CommandLifecycle()
    {
        int pid = Environment.ProcessId;
        using var connection = new LiveCommandConnection(pid);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        uint previous;
        using (var cancel = new CancellationTokenSource())
        {
            var canceled = connection.ReloadModAsync("dev.test.cancel", cancel.Token);
            using var mapping = MemoryMappedFile.OpenExisting(LiveCommandProtocol.MappingName(pid));
            using var view = mapping.CreateViewAccessor();
            previous = view.ReadUInt32(LiveCommandProtocol.RequestOffset);
            Check(previous != 0 && view.ReadUInt32(LiveCommandProtocol.KindOffset) == LiveCommandProtocol.ReloadModKind,
                "Command request was not published.");
            cancel.Cancel(); await Canceled(canceled);
            Check(view.ReadUInt64(LiveCommandProtocol.HeartbeatOffset) == 0, "Cancel did not revoke the retained native view.");
        }
        var replyTask = connection.ReloadModAsync("dev.test.retry", timeout.Token);
        using (var mapping = MemoryMappedFile.OpenExisting(LiveCommandProtocol.MappingName(pid)))
        using (var view = mapping.CreateViewAccessor())
        {
            uint request = view.ReadUInt32(LiveCommandProtocol.RequestOffset);
            Check(request != 0 && request != previous, "Reconnect reused the old command id.");
            byte[] mod = new byte[LiveCommandProtocol.ModIdBytes]; view.ReadArray(LiveCommandProtocol.ModIdOffset, mod, 0, mod.Length);
            Check(Encoding.ASCII.GetString(mod).TrimEnd('\0') == "dev.test.retry", "Command payload changed.");
            view.Write(36, (uint)LiveCommandStatus.Ok); view.Write(40, (uint)pid); view.Write(44, 2u);
            view.WriteArray(LiveCommandProtocol.MessageOffset, Encoding.UTF8.GetBytes("accepted\0"), 0, 9);
            Thread.MemoryBarrier(); view.Write(LiveCommandProtocol.ResponseOffset, previous);
            await Task.Delay(60, timeout.Token);
            Check(!replyTask.IsCompleted, "An answer to the previous mapping was accepted.");
            view.Write(LiveCommandProtocol.ResponseOffset, request);
            var reply = await replyTask.WaitAsync(timeout.Token);
            Check(reply is { Status: LiveCommandStatus.Ok, Reloaded: 2, Message: "accepted" }, "Current reply was not adopted.");
            Check(view.ReadUInt64(LiveCommandProtocol.HeartbeatOffset) == 0, "Completed command kept the native view live.");
        }
        var disposedTask = connection.ReloadModAsync("dev.test.dispose", timeout.Token);
        using (var mapping = MemoryMappedFile.OpenExisting(LiveCommandProtocol.MappingName(pid)))
        using (var view = mapping.CreateViewAccessor())
        {
            connection.Dispose(); await Canceled(disposedTask);
            Check(view.ReadUInt64(LiveCommandProtocol.HeartbeatOffset) == 0, "Dispose did not revoke the native view.");
        }
    }

    private static async Task Canceled(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { return; }
        throw new InvalidDataException("An already canceled operation completed instead.");
    }
    private static void U(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void Q(byte[] bytes, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), value);
    private static void F(byte[] bytes, int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
    private static void Reject<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidDataException(message); }
}
