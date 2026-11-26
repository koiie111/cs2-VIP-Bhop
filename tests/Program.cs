using VIP_Bhop;

CheckCvars();
CheckReplication();
CheckTimers();
CheckSpeed();
Console.WriteLine("VIP-Bhop regression checks passed.");

static unsafe void CheckCvars()
{
    foreach (var baselineAuto in new[] { false, true })
    foreach (var baselineEnable in new[] { false, true })
    {
        var autoMemory = baselineAuto;
        var enableMemory = baselineEnable;
        var auto = new BhopCvar();
        var enable = new BhopCvar();
        auto.SetAddress(&autoMemory);
        enable.SetAddress(&enableMemory);
        auto.Capture();
        enable.Capture();

        for (var transition = 0; transition < 2; transition++)
        {
            auto.Override();
            enable.Override();
            auto.Override();
            enable.Override();
            Check(autoMemory && enableMemory, "Consecutive VIP hooks get both cvars");
            auto.Restore();
            enable.Restore();
            Check(autoMemory == baselineAuto && enableMemory == baselineEnable, "Non-VIP after VIP gets both baseline values");
            Check(!(auto.Capture() | enable.Capture()), "Tick after an override keeps the baseline");
        }

        autoMemory = !baselineAuto;
        Check(auto.Capture() && auto.Baseline == !baselineAuto, "Unowned console changes become the baseline");
    }

    var memory = true;
    var cvar = new BhopCvar();
    cvar.SetAddress(&memory);
    cvar.Capture();
    memory = false;
    cvar.Override();
    cvar.Restore();
    Check(!memory && cvar.Capture() && !cvar.Baseline, "A console change just before VIP movement survives restoration");

    cvar.Override();
    var relocated = true;
    cvar.SetAddress(&relocated);
    Check(!cvar.Restore() && relocated, "An override at a former address must never restore into a new address");
}

static void CheckReplication()
{
    var state = new PlayerState();
    Check(Send(state, false, false) == 2, "Initial sync sends both cvars");
    Check(Send(state, false, false) == 0, "Stable state sends nothing");
    Check(Send(state, false, true) == 1, "Change one baseline cvar");
    state.Enabled = true;
    Check(Send(state, false, true) == 0, "Waiting for round activation");
    state.Active = true;
    Check(Send(state, false, true) == 1, "Active client syncs the remaining cvar");
    state.Enabled = false;
    Check(Send(state, false, true) == 1, "Disabling VIP restores the affected client cvar");
    Check(Send(new PlayerState(), false, true) == 2, "A reused slot receives a fresh pair");
}

static int Send(PlayerState state, bool baselineAuto, bool baselineEnable)
{
    var sends = 0;
    if (state.NeedsAutoBhop(baselineAuto, out var auto))
    {
        state.SentAutoBhop = auto;
        sends++;
    }
    if (state.NeedsEnableBhop(baselineEnable, out var enable))
    {
        state.SentEnableBhop = enable;
        sends++;
    }
    return sends;
}

static void CheckTimers()
{
    var slot = new PlayerState();
    Check(PlayerState.IsCurrent(slot, slot, 1, 1, 0), "Current connection and round");
    Check(!PlayerState.IsCurrent(new PlayerState(), slot, 1, 1, 0), "Reused slot");
    Check(!PlayerState.IsCurrent(slot, slot, 2, 1, 0), "Old round");
    slot.Generation++;
    Check(!PlayerState.IsCurrent(slot, slot, 1, 1, 0), "Old entitlement");
}

static void CheckSpeed()
{
    foreach (var limit in new[] { 250f, 250.5f, 251f })
    foreach (var (x, y) in new[] { (249f, 0f), (250f, 0f), (250.49f, 0f), (250.5f, 0f), (250.51f, 0f),
                                  (251.5f, 0f), (-175f, 180f), (float.NaN, 0f), (float.PositiveInfinity, 1f) })
    {
        var speed = Math.Sqrt(x * x + y * y);
        var expectedClamp = !(Math.Round(speed) <= limit);
        var expectedX = expectedClamp ? (float)(x / speed) * limit : x;
        var expectedY = expectedClamp ? (float)(y / speed) * limit : y;
        var clamped = BhopSpeed.TryClamp(x, y, limit, out var actualX, out var actualY);
        Check(clamped == expectedClamp && actualX.Equals(expectedX) && actualY.Equals(expectedY), "v2.6.1 speed cap");
    }
}

static void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
}
