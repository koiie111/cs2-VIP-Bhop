using System.Diagnostics;

namespace VIP_Bhop;

internal unsafe struct BhopCvar
{
    private bool* _address;
    private bool _overridden;
    public bool Baseline { get; private set; }
    public bool Current => *_address;

    public void SetAddress(bool* address)
    {
        if (address != _address)
            _overridden = false;
        _address = address;
    }

    public bool Capture()
    {
        var current = Current;
        var changed = Baseline != current;
        Baseline = current;
        return changed;
    }

    public void Override()
    {
        if (!Current)
        {
            *_address = true;
            _overridden = true;
        }
    }

    public bool Restore()
    {
        if (!_overridden) return false;
        _overridden = false;

        // Override only writes when the observed value was false. A further external false
        // write has already restored it, and an external true is indistinguishable from ours.
        if (!Current) return false;
        *_address = false;
        return true;
    }
}

public class PlayerState
{
    public bool Enabled { get; set; }
    public bool Active { get; set; }
    public float MaxSpeed { get; set; }
    public int Generation { get; set; }
    public bool? SentAutoBhop { get; set; }
    public bool? SentEnableBhop { get; set; }

    internal bool WantsBhop => Enabled && Active;
    internal bool WasGrounded { get; set; }
    internal bool PendingSync { get; set; }
    internal uint? ControllerId { get; set; }
    internal IntPtr ControllerAddress { get; set; }
    internal uint PawnId { get; set; }
    internal IntPtr PawnAddress { get; set; }
    internal IntPtr ServicesAddress { get; set; }

    internal bool NeedsAutoBhop(bool baseline, out bool value)
    {
        value = WantsBhop || baseline;
        return SentAutoBhop != value;
    }

    internal bool NeedsEnableBhop(bool baseline, out bool value)
    {
        value = WantsBhop || baseline;
        return SentEnableBhop != value;
    }

    internal static bool IsCurrent(PlayerState current, PlayerState scheduled, int currentRound, int round, int generation) =>
        ReferenceEquals(current, scheduled) && currentRound == round && scheduled.Generation == generation;
}

public class BhopSettings
{
    public float Timer { get; set; }
    public float MaxSpeed { get; set; }
}

internal static class BhopActivation
{
    // Freeze time can be extended by the engine. round_freeze_end signals when the countdown can start.
    public static float? Remaining(bool warmup, bool frozen, float roundStart, float now, float timer)
    {
        if (warmup) return 0;
        if (frozen) return null;
        return Math.Max(0, roundStart + Math.Max(0, timer) - now);
    }
}

internal static class BhopSpeed
{
    public static bool TryClamp(float x, float y, float maxSpeed, out float clampedX, out float clampedY)
    {
        var speed = Math.Sqrt(x * x + y * y);
        if (Math.Round(speed) <= maxSpeed)
        {
            clampedX = x;
            clampedY = y;
            return false;
        }

        clampedX = (float)(x / speed) * maxSpeed;
        clampedY = (float)(y / speed) * maxSpeed;
        return true;
    }
}

internal enum HookEntry
{
    Usercmds,
    Simulate,
    SetupMove,
    Movement,
    PostThink
}

internal sealed class HookSample(int startTick, int slotCount)
{
    public const int DurationTicks = 10 * 64;

    public int StartTick { get; } = startTick;
    public int ElapsedTicks { get; set; }
    public long[] Calls { get; } = new long[5];
    public long VipCalls { get; set; }
    public long Restores { get; set; }
    public long HookTime { get; set; }
    public uint?[] ControllerIds { get; } = new uint?[slotCount];
    public long[] SimulateBySlot { get; } = new long[slotCount];
    public long[] TicksBySlot { get; } = new long[slotCount];

    public string Report()
    {
        var calls = Calls.Sum();
        var totalMs = HookTime * 1000.0 / Stopwatch.Frequency;
        var usPerCall = calls == 0 ? 0 : totalMs * 1000 / calls;
        return $"[VIP Bhop] last sample: {ElapsedTicks / 64.0:0.00} s game time, {totalMs:0.00} ms handler time, " +
               $"{usPerCall:0.00} us/call (native-to-managed dispatch excluded)\n" +
               $"[VIP Bhop] usercmds={Calls[0]} simulate={Calls[1]} setupMove={Calls[2]} " +
               $"processMovement={Calls[3]} postThink={Calls[4]} vip={VipCalls} restores={Restores}";
    }

    public string PlayerReport(int slot, uint controllerId) =>
        ControllerIds[slot] == controllerId && TicksBySlot[slot] != 0
            ? $"{(double)SimulateBySlot[slot] / TicksBySlot[slot]:0.00}"
            : "n/a";
}
