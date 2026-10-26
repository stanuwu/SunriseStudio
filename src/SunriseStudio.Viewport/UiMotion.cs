using System.Diagnostics;
using System.Numerics;
using ImGuiNET;

namespace SunriseStudio.Viewport;

/**
 * UI motion (docs/UI-UE5.md §3): eased and sprung values keyed by ImGui context plus id. Motion is decoration only:
 * nothing here delays input, hit-testing or a state change; the drawn value only catches up with its target.
 *
 * Each ImGui context (the main window and every pop-out) keeps its own states. Call BeginFrame once per context per
 * frame after NewFrame and EndFrame before Render; EndFrame evicts states untouched for 2 s. A state seen for the
 * first time (or again after eviction) starts at its target, so nothing animates on first appearance unless a helper
 * asks for a start value (FadeIn starts at 0).
 */
public static partial class UiMotion
{
    /** Snaps every value to its target, makes Pulse return 1 and removes the drawer slide. */
    public static bool ReduceMotion { get; set; }

    private static float speed = 1;
    /** Global speed multiplier (Settings > Interface), 0.5 to 2. */
    public static float Speed
    {
        get => speed;
        set => speed = float.IsFinite(value) ? Math.Clamp(value, .5f, 2f) : 1;
    }

    /** True when any value in any context moved during its last completed frame (or a Pulse was drawn). */
    public static bool Animating
    {
        get
        {
            double now = Now;
            foreach (var context in contexts.Values)
                if ((context.MovedLast || context.Moved) && now - context.LastEnd < .5) return true;
            return false;
        }
    }

    /** Seconds on the motion clock (Now) of the last value change or pointer/keyboard input seen by BeginFrame. */
    public static double LastActivity { get; private set; } = double.NegativeInfinity;

    /** The motion clock in seconds: monotonic and shared by every context, so frame pacing can compare it. */
    public static double Now => Clock.Elapsed.TotalSeconds;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private const double EvictAfter = 2, EvictScanEvery = .5;
    private const float Settle = .001f, ColorSettle = 1f / 512, VelocitySettle = .01f, MaxSpringStep = 1f / 120;

    /** One animated value: up to four channels and their velocities, the frame it was touched and per-id flags. */
    internal struct State
    {
        public Vector4 Value, Velocity, Extra;
        public double Touched;
        public int Flags;
    }

    private sealed class Context
    {
        public readonly Dictionary<uint, int> Index = new(256);
        public State[] States = new State[256];
        public readonly Stack<int> Free = new(64);
        public int Count;
        public double Time, LastEnd, LastScan;
        public bool Moved, MovedLast;
    }

    private static readonly Dictionary<IntPtr, Context> contexts = new(8);
    private static readonly List<uint> evicting = new(64);
    private static IntPtr currentPointer;
    private static Context? current;

    /** Once per ImGui context per frame, after NewFrame: reads the frame step and records input as activity. */
    public static void BeginFrame()
    {
        var context = Current();
        var io = ImGui.GetIO();
        context.Time = Now;
        context.Moved = false;
        bool input = io.MouseDelta.X != 0 || io.MouseDelta.Y != 0 || io.MouseWheel != 0 || io.MouseWheelH != 0
            || io.MouseDown[0] || io.MouseDown[1] || io.MouseDown[2] || io.InputQueueCharacters.Size > 0;
        if (input) LastActivity = context.Time;
    }

    /** Once per ImGui context per frame, before Render: closes the frame and evicts states untouched for 2 s. */
    public static void EndFrame()
    {
        var context = Current();
        context.MovedLast = context.Moved;
        context.Moved = false;
        context.LastEnd = Now;
        if (context.LastEnd - context.LastScan < EvictScanEvery) return;
        context.LastScan = context.LastEnd;
        evicting.Clear();
        foreach (var (id, slot) in context.Index)
            if (context.LastEnd - context.States[slot].Touched > EvictAfter) evicting.Add(id);
        foreach (uint id in evicting)
        {
            context.Free.Push(context.Index[id]);
            context.Index.Remove(id);
        }
        // Contexts that stopped drawing (a closed pop-out) are dropped with their states.
        if (contexts.Count > 1)
        {
            IntPtr stale = IntPtr.Zero;
            foreach (var (pointer, other) in contexts)
                if (other != context && context.LastEnd - other.LastEnd > EvictAfter * 5) { stale = pointer; break; }
            if (stale != IntPtr.Zero) { contexts.Remove(stale); if (currentPointer == stale) { currentPointer = IntPtr.Zero; current = null; } }
        }
    }

    /** ImGui.GetID within the current id stack. */
    public static uint Id(string key) => ImGui.GetID(key);

    /** A derived id for a second value that belongs to the same item (for example a hover cache next to a fill). */
    public static uint Mix(uint id, uint salt)
    {
        uint h = id ^ (salt * 0x9E3779B9u);
        h ^= h >> 16; h *= 0x85EBCA6Bu; h ^= h >> 13;
        return h == 0 ? 1 : h;
    }

    /** Exponential ease: v += (t - v) * (1 - exp(-rate * dt)). Starts at the target. */
    public static float Ease(uint id, float target, float rate = 18f) => EaseFrom(id, target, target, rate);

    /** Exponential ease that starts at start when the state is new. */
    public static float EaseFrom(uint id, float start, float target, float rate = 18f)
    {
        ref var state = ref Slot(id, new(start, 0, 0, 0), out _);
        state.Value.X = Step(state.Value.X, target, rate, Settle);
        return state.Value.X;
    }

    /** An eased colour (each channel), settled below 1/512 per channel. */
    public static Vector4 EaseColor(uint id, Vector4 target, float rate = 20f)
    {
        ref var state = ref Slot(id, target, out _);
        state.Value = new(Step(state.Value.X, target.X, rate, ColorSettle), Step(state.Value.Y, target.Y, rate, ColorSettle),
            Step(state.Value.Z, target.Z, rate, ColorSettle), Step(state.Value.W, target.W, rate, ColorSettle));
        return state.Value;
    }

    /** An eased 2D value. */
    public static Vector2 EaseVec(uint id, Vector2 target, float rate = 18f)
    {
        ref var state = ref Slot(id, new(target, 0, 0), out _);
        state.Value.X = Step(state.Value.X, target.X, rate, Settle * 100);
        state.Value.Y = Step(state.Value.Y, target.Y, rate, Settle * 100);
        return new(state.Value.X, state.Value.Y);
    }

    /** A damped spring toward target (semi-implicit Euler in steps of at most 1/120 s, so a hitch cannot blow it up). */
    public static float Spring(uint id, float target, float stiffness = 380f, float damping = 30f)
        => SpringFrom(id, target, target, stiffness, damping);

    /** A damped spring that starts at start when the state is new. */
    public static float SpringFrom(uint id, float start, float target, float stiffness = 380f, float damping = 30f)
    {
        ref var state = ref Slot(id, new(start, 0, 0, 0), out _);
        float value = state.Value.X, velocity = state.Velocity.X;
        SpringStep(ref value, ref velocity, target, stiffness, damping, Settle);
        state.Value.X = value; state.Velocity.X = velocity;
        return value;
    }

    /** A damped spring on two channels (x and width of a tab underline, for example). Starts at the target. */
    public static Vector2 SpringVec(uint id, Vector2 target, float stiffness = 380f, float damping = 30f)
    {
        ref var state = ref Slot(id, new(target, 0, 0), out _);
        float x = state.Value.X, vx = state.Velocity.X, y = state.Value.Y, vy = state.Velocity.Y;
        SpringStep(ref x, ref vx, target.X, stiffness, damping, .05f);
        SpringStep(ref y, ref vy, target.Y, stiffness, damping, .05f);
        state.Value.X = x; state.Velocity.X = vx; state.Value.Y = y; state.Velocity.Y = vy;
        return new(x, y);
    }

    /** 0..1 sine with the given period in seconds (the launching dot); 1 when ReduceMotion. Drawing it keeps frames coming. */
    public static float Pulse(float period)
    {
        if (ReduceMotion || period <= 0) return 1;
        MarkMoved();
        double phase = Now * Speed / period;
        return (float)(.5 - .5 * Math.Cos(phase * Math.Tau));
    }

    /**
     * For decorations that move with the clock rather than a UiMotion value (a spinner, a progress sheen, a shimmer):
     * keeps Animating true while they are drawn, so frame pacing gives them the same steady frames as Pulse. Nothing
     * with ReduceMotion, where those decorations stand still.
     */
    public static void KeepAnimating()
    {
        if (!ReduceMotion) MarkMoved();
    }

    /** Forgets one state, so its next use starts fresh (FadeIn starts at 0 again, for example). */
    public static void Reset(uint id)
    {
        var context = Current();
        if (!context.Index.Remove(id, out int slot)) return;
        context.Free.Push(slot);
    }

    // ---- Internals shared with UiMotion.Widgets.cs ----

    /** Marks the current context as moving this frame (Animating) and records activity. */
    internal static void MarkMoved()
    {
        var context = Current();
        context.Moved = true;
        LastActivity = Now;
    }

    /** The frame step of the current context, already scaled by Speed. */
    internal static float Dt => Math.Clamp(ImGui.GetIO().DeltaTime, .0001f, .1f) * Speed;

    /** Per-id flags (hover caches); 0 for a new state. */
    internal static int GetFlags(uint id)
    {
        var context = Current();
        return context.Index.TryGetValue(id, out int slot) ? context.States[slot].Flags : 0;
    }

    internal static void SetFlags(uint id, int flags)
    {
        ref var state = ref Slot(id, Vector4.Zero, out _);
        state.Flags = flags;
    }

    /**
     * The whole state for id in the current context, created with initial when new; marks it touched. The ref is
     * valid only until the next state is created (the pool may grow), so use it before calling another helper.
     */
    internal static ref State Raw(uint id, Vector4 initial, out bool created) => ref Slot(id, initial, out created);

    /** One exponential step for widgets (honours ReduceMotion, Speed and settling). */
    internal static float StepTo(float value, float target, float rate, float settle) => Step(value, target, rate, settle);

    private static Context Current()
    {
        IntPtr pointer = ImGui.GetCurrentContext();
        if (pointer == currentPointer && current is not null) return current;
        if (!contexts.TryGetValue(pointer, out var context))
        {
            context = new Context { LastEnd = Now, LastScan = Now };
            contexts[pointer] = context;
        }
        currentPointer = pointer;
        current = context;
        return context;
    }

    private static ref State Slot(uint id, Vector4 initial, out bool created)
    {
        var context = Current();
        if (!context.Index.TryGetValue(id, out int slot))
        {
            if (!context.Free.TryPop(out slot))
            {
                slot = context.Count++;
                if (slot >= context.States.Length) Array.Resize(ref context.States, context.States.Length * 2);
            }
            context.Index[id] = slot;
            context.States[slot] = new State { Value = initial };
            created = true;
        }
        else created = false;
        ref var state = ref context.States[slot];
        state.Touched = Now;
        return ref state;
    }

    /** One exponential step toward target; snaps when settled and reports motion otherwise. */
    private static float Step(float value, float target, float rate, float settle)
    {
        if (ReduceMotion || !float.IsFinite(value)) return target;
        float next = value + (target - value) * (1 - MathF.Exp(-rate * Dt));
        if (MathF.Abs(target - next) < settle) return target;
        MarkMoved();
        return next;
    }

    private static void SpringStep(ref float value, ref float velocity, float target, float stiffness, float damping, float settle)
    {
        if (ReduceMotion || !float.IsFinite(value) || !float.IsFinite(velocity)) { value = target; velocity = 0; return; }
        float dt = Dt, k = stiffness * Speed * Speed, c = damping * Speed;
        // Dt already carries Speed; the spring constants scale with it too, so cancel one factor.
        dt /= Speed;
        while (dt > 0)
        {
            float h = MathF.Min(dt, MaxSpringStep);
            velocity += (-k * (value - target) - c * velocity) * h;
            value += velocity * h;
            dt -= h;
        }
        if (MathF.Abs(target - value) < settle && MathF.Abs(velocity) < Math.Max(VelocitySettle, settle * 10)) { value = target; velocity = 0; return; }
        MarkMoved();
    }
}
