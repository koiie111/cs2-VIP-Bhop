using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIP_Bhop;

public class VIP_Bhop : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius, koiie111";
    public override string ModuleName => "[VIP] Bhop (native cvars)";
    public override string ModuleVersion => "v2.2.0";

    private Bhop? _bhop;
    private IVipCoreApi? _api;

    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = PluginCapability.Get();
        if (_api == null) return;

        _bhop = new Bhop(this, _api);
        _api.RegisterFeature(_bhop);
    }

    public override void Unload(bool hotReload)
    {
        if (_bhop == null) return;

        _bhop.Dispose();
        _api?.UnRegisterFeature(_bhop);
    }
}

/*
 * How it works (same approach as cs2kz-metamod autobhop style):
 *  1. FCVAR_REPLICATED is removed from sv_autobunnyhopping / sv_enablebunnyhopping, so the server
 *     never broadcasts these cvars to everyone.
 *  2. Each client gets its own value via CNETMsg_SetConVar (ReplicateConVar -> single recipient):
 *     VIP with active bhop gets "true", everyone else gets the real server value.
 *  3. Server-side the global value is set to "true" only while that VIP's own commands run:
 *     the OnSimulateUserCommands pre hook writes "true" directly to memory (no change callbacks /
 *     broadcasts), calls the original bypassing the hook, puts the previous value back and skips the
 *     original. Post hooks are not needed (they do not fire on CSS 1.0.375+/KHook).
 * Client prediction and server movement agree -> native bhop without sticking or jitter.
 */
public class Bhop : VipFeatureBase, IDisposable
{
    public override string Feature => "Bhop";

    private const string AutoBhopName = "sv_autobunnyhopping";
    private const string EnableBhopName = "sv_enablebunnyhopping";

    // Keys in addons/counterstrikesharp/gamedata/vip_bhop.json
    private const string ProcessMovementKey = "VIP_Bhop_CCSPlayer_MovementServices_ProcessMovement";
    private const string SimulateUserCommandsKey = "VIP_Bhop_CBasePlayerController_OnSimulateUserCommands";

    private readonly VIP_Bhop _plugin;
    private readonly PlayerState[] _states = new PlayerState[65];

    private readonly ConVar? _autoBhop;
    private readonly ConVar? _enableBhop;
    private readonly ConVarFlags _autoBhopFlags;
    private readonly ConVarFlags _enableBhopFlags;

    private readonly MemoryFunctionVoid<IntPtr, IntPtr>? _processMovement;
    private readonly MemoryFunctionVoid<IntPtr>? _simulateUserCommands;

    // Rebuilt every tick: MovementServices pointers and controller pointers of VIPs with active bhop
    private readonly HashSet<IntPtr> _activeServices = new();
    private readonly HashSet<IntPtr> _activeControllers = new();

    // Real server values, captured every tick (the override never outlives a VIP's own processing)
    private bool _realAutoBhop;
    private bool _realEnableBhop;

    // Diagnostics (css_vipbhop_status)
    private readonly Dictionary<IntPtr, int> _controllerSlots = new();
    private readonly long[] _simulateBySlot = new long[65];
    private readonly long[] _ticksBySlot = new long[65];
    private long _simulateCalls;
    private long _preCalls;
    private long _vipCalls;
    private long _staleRestores;

    public Bhop(VIP_Bhop plugin, IVipCoreApi api) : base(api)
    {
        _plugin = plugin;
        for (var i = 0; i < _states.Length; i++) _states[i] = new PlayerState();

        _autoBhop = ConVar.Find(AutoBhopName);
        _enableBhop = ConVar.Find(EnableBhopName);
        if (_autoBhop == null || _enableBhop == null)
        {
            plugin.Logger.LogError("[VIP Bhop] {0}/{1} not found, bhop is disabled", AutoBhopName, EnableBhopName);
            return;
        }

        // Every player's command processing runs inside OnSimulateUserCommands (SetupMove, ProcessMovement, ...).
        // For a VIP the pre hook sets the cvars, calls the original itself (bypassing the hook), puts the values
        // back and skips the original. So "true" exists only while that VIP's own commands run and never
        // leaks to anyone, without relying on post hooks (they do not fire on CSS 1.0.375+/KHook).
        // ProcessMovement is hooked too, as a second guard with the same logic.
        try
        {
            _simulateUserCommands = new MemoryFunctionVoid<IntPtr>(GameData.GetSignature(SimulateUserCommandsKey));
            _processMovement = new MemoryFunctionVoid<IntPtr, IntPtr>(GameData.GetSignature(ProcessMovementKey));
            _simulateUserCommands.Hook(SimulateUserCommandsPre, HookMode.Pre);
            _processMovement.Hook(ProcessMovementPre, HookMode.Pre);
        }
        catch (Exception e)
        {
            _simulateUserCommands?.Unhook(SimulateUserCommandsPre, HookMode.Pre);
            _simulateUserCommands = null;
            _processMovement = null;
            plugin.Logger.LogError(e,
                "[VIP Bhop] {0} / {1} missing or outdated in gamedata/vip_bhop.json. Bhop is disabled",
                SimulateUserCommandsKey, ProcessMovementKey);
            return;
        }

        _realAutoBhop = _autoBhop.GetPrimitiveValue<bool>();
        _realEnableBhop = _enableBhop.GetPrimitiveValue<bool>();

        _autoBhopFlags = _autoBhop.Flags;
        _enableBhopFlags = _enableBhop.Flags;
        _autoBhop.Flags = _autoBhopFlags & ~ConVarFlags.FCVAR_REPLICATED;
        _enableBhop.Flags = _enableBhopFlags & ~ConVarFlags.FCVAR_REPLICATED;

        plugin.RegisterListener<Listeners.OnClientConnected>(slot => ResetState(slot));
        plugin.RegisterListener<Listeners.OnClientDisconnectPost>(slot => ResetState(slot));
        plugin.RegisterListener<Listeners.OnTick>(OnTick);
        plugin.RegisterListener<Listeners.OnServerPostEntityThink>(EnsureRealValues);
        plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        plugin.AddCommand("css_vipbhop_status", "VIP Bhop diagnostics", OnStatusCommand);
    }

    private bool IsWorking => _processMovement != null;

    public override void OnPlayerLoaded(CCSPlayerController player, string group)
    {
        if (!IsWorking) return;

        var state = _states[player.Slot];
        state.Enabled = PlayerHasFeature(player) && GetPlayerFeatureState(player) == FeatureState.Enabled;
        if (state.Enabled)
            state.MaxSpeed = GetFeatureValue<BhopSettings>(player).MaxSpeed;
    }

    public override void OnPlayerRemoved(CCSPlayerController player, string group)
    {
        if (!IsWorking) return;

        _states[player.Slot].Enabled = false;
    }

    public override void OnSelectItem(CCSPlayerController player, FeatureState state)
    {
        if (!IsWorking) return;

        _states[player.Slot].Enabled = state == FeatureState.Enabled;
    }

    private void ResetState(int slot)
    {
        if (slot < 0 || slot >= _states.Length) return;
        _states[slot] = new PlayerState();
    }

    private void OnTick()
    {
        _activeServices.Clear();
        _activeControllers.Clear();
        _controllerSlots.Clear();

        _realAutoBhop = _autoBhop!.GetPrimitiveValue<bool>();
        _realEnableBhop = _enableBhop!.GetPrimitiveValue<bool>();
        var serverAutoBhop = _realAutoBhop;
        var serverEnableBhop = _realEnableBhop;

        foreach (var player in Utilities.GetPlayers())
        {
            if (player is not { IsValid: true, IsBot: false, IsHLTV: false } ||
                player.Connected != PlayerConnectedState.Connected) continue;

            _controllerSlots[player.Handle] = player.Slot;
            _ticksBySlot[player.Slot]++;

            var state = _states[player.Slot];
            var bhop = state.Enabled && state.Active;

            // Per-client values: only sent when they change
            SyncClient(player, state, bhop || serverAutoBhop, bhop || serverEnableBhop);

            if (!bhop || !player.PawnIsAlive) continue;

            var pawn = player.PlayerPawn.Value;
            var services = pawn?.MovementServices;
            if (pawn == null || services == null) continue;

            _activeServices.Add(services.Handle);
            _activeControllers.Add(player.Handle);

            if (state.MaxSpeed > 0)
                ClampSpeed(player, pawn, state.MaxSpeed);
        }
    }

    private static void SyncClient(CCSPlayerController player, PlayerState state, bool autoBhop, bool enableBhop)
    {
        if (state.SentAutoBhop != autoBhop)
        {
            player.ReplicateConVar(AutoBhopName, autoBhop ? "true" : "false");
            state.SentAutoBhop = autoBhop;
        }

        if (state.SentEnableBhop != enableBhop)
        {
            player.ReplicateConVar(EnableBhopName, enableBhop ? "true" : "false");
            state.SentEnableBhop = enableBhop;
        }
    }

    private HookResult SimulateUserCommandsPre(DynamicHook hook)
    {
        _simulateCalls++;
        var controller = hook.GetParam<IntPtr>(0);
        if (_controllerSlots.TryGetValue(controller, out var slot)) _simulateBySlot[slot]++;

        if (_activeControllers.Count == 0 || !_activeControllers.Contains(controller))
        {
            EnsureRealValues();
            return HookResult.Continue;
        }

        RunWithBhop(() => _simulateUserCommands!.Invoke(controller, true));
        return HookResult.Handled;
    }

    private HookResult ProcessMovementPre(DynamicHook hook)
    {
        _preCalls++;
        var services = hook.GetParam<IntPtr>(0);

        if (_activeServices.Count == 0 || !_activeServices.Contains(services))
        {
            EnsureRealValues();
            return HookResult.Continue;
        }

        // Normally already inside the VIP's OnSimulateUserCommands wrapper
        if (_autoBhop!.GetPrimitiveValue<bool>() && _enableBhop!.GetPrimitiveValue<bool>())
            return HookResult.Continue;

        var moveData = hook.GetParam<IntPtr>(1);
        RunWithBhop(() => _processMovement!.Invoke(services, moveData, true));
        return HookResult.Handled;
    }

    private void RunWithBhop(Action original)
    {
        _vipCalls++;
        ref var autoBhop = ref _autoBhop!.GetPrimitiveValue<bool>();
        ref var enableBhop = ref _enableBhop!.GetPrimitiveValue<bool>();
        var prevAutoBhop = autoBhop;
        var prevEnableBhop = enableBhop;

        autoBhop = true;
        enableBhop = true;
        try
        {
            original();
        }
        finally
        {
            autoBhop = prevAutoBhop;
            enableBhop = prevEnableBhop;
        }
    }

    // Guard: nobody but a VIP inside RunWithBhop may ever see a value other than the real one
    private void EnsureRealValues()
    {
        ref var autoBhop = ref _autoBhop!.GetPrimitiveValue<bool>();
        ref var enableBhop = ref _enableBhop!.GetPrimitiveValue<bool>();
        if (autoBhop == _realAutoBhop && enableBhop == _realEnableBhop) return;

        _staleRestores++;
        autoBhop = _realAutoBhop;
        enableBhop = _realEnableBhop;
    }

    private void OnStatusCommand(CCSPlayerController? caller, CommandInfo info)
    {
        // server console / rcon only
        if (caller != null) return;

        info.ReplyToCommand($"[VIP Bhop] hook: {(IsWorking ? "OK" : "NOT INSTALLED")}");
        info.ReplyToCommand(
            $"[VIP Bhop] real values: {AutoBhopName}={_realAutoBhop} {EnableBhopName}={_realEnableBhop}, " +
            $"current: {_autoBhop?.GetPrimitiveValue<bool>()}/{_enableBhop?.GetPrimitiveValue<bool>()}");
        info.ReplyToCommand(
            $"[VIP Bhop] flags: {AutoBhopName}={_autoBhop?.Flags} {EnableBhopName}={_enableBhop?.Flags}");
        info.ReplyToCommand(
            $"[VIP Bhop] simulate={_simulateCalls} processMovement={_preCalls} vipWrapped={_vipCalls} staleRestores={_staleRestores} (must stay 0)");

        foreach (var player in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false }))
        {
            var st = _states[player.Slot];
            info.ReplyToCommand(
                $"[VIP Bhop] #{player.Slot} {player.PlayerName}: enabled={st.Enabled} active={st.Active} " +
                $"sent={st.SentAutoBhop}/{st.SentEnableBhop} inMovementSet={player.PlayerPawn.Value?.MovementServices is { } ms && _activeServices.Contains(ms.Handle)} inControllerSet={_activeControllers.Contains(player.Handle)} " +
                $"simulate/tick={(_ticksBySlot[player.Slot] == 0 ? 0 : (double)_simulateBySlot[player.Slot] / _ticksBySlot[player.Slot]):0.00}");
        }
    }

    private static void ClampSpeed(CCSPlayerController player, CCSPlayerPawn pawn, float maxSpeed)
    {
        var flags = (PlayerFlags)pawn.Flags;
        if (!flags.HasFlag(PlayerFlags.FL_ONGROUND) || !player.Buttons.HasFlag(PlayerButtons.Jump)) return;

        var velocity = pawn.AbsVelocity;
        var speed = Math.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);
        if (Math.Round(speed) <= maxSpeed) return;

        velocity.X = (float)(velocity.X / speed) * maxSpeed;
        velocity.Y = (float)(velocity.Y / speed) * maxSpeed;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        var gameRules = GetGameRules();
        if (gameRules == null) return HookResult.Continue;

        foreach (var player in Utilities.GetPlayers()
                     .Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false }))
        {
            var state = _states[player.Slot];
            state.Active = false;
            var generation = ++state.Generation;

            if (!IsClientVip(player) || !PlayerHasFeature(player) ||
                GetPlayerFeatureState(player) is not FeatureState.Enabled)
            {
                state.Enabled = false;
                continue;
            }

            state.Enabled = true;
            var settings = GetFeatureValue<BhopSettings>(player);
            state.MaxSpeed = settings.MaxSpeed;

            if (gameRules.WarmupPeriod)
            {
                state.Active = true;
                continue;
            }

            PrintToChat(player, GetTranslatedText("bhop.TimeToActivation", settings.Timer));
            _plugin.AddTimer(settings.Timer + gameRules.FreezeTime, () =>
            {
                // player left or a new round started meanwhile
                if (_states[player.Slot] != state || state.Generation != generation || !player.IsValid) return;

                state.Active = true;
                PrintToChat(player, GetTranslatedText("bhop.Activated"));
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }

        return HookResult.Continue;
    }

    private static CCSGameRules? GetGameRules()
    {
        return Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault(g => g.IsValid)?.GameRules;
    }

    public void Dispose()
    {
        if (_processMovement == null) return;

        _simulateUserCommands?.Unhook(SimulateUserCommandsPre, HookMode.Pre);
        _processMovement.Unhook(ProcessMovementPre, HookMode.Pre);
        EnsureRealValues();

        _autoBhop!.Flags = _autoBhopFlags;
        _enableBhop!.Flags = _enableBhopFlags;

        // Return every client to the real server values
        var autoBhop = _autoBhop.GetPrimitiveValue<bool>() ? "true" : "false";
        var enableBhop = _enableBhop!.GetPrimitiveValue<bool>() ? "true" : "false";
        foreach (var player in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false }))
        {
            player.ReplicateConVar(AutoBhopName, autoBhop);
            player.ReplicateConVar(EnableBhopName, enableBhop);
        }
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
}

public class BhopSettings
{
    public float Timer { get; set; }
    public float MaxSpeed { get; set; }
}
