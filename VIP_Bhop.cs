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
    public override string ModuleVersion => "v2.0.1";

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
 *  3. Server-side the global value is swapped to "true" only for the duration of
 *     CCSPlayer_MovementServices::ProcessMovement of that VIP and restored right after.
 *     The value is written directly to memory, so no change callbacks / broadcasts fire.
 * Client prediction and server movement agree -> native bhop without sticking or jitter.
 */
public class Bhop : VipFeatureBase, IDisposable
{
    public override string Feature => "Bhop";

    private const string AutoBhopName = "sv_autobunnyhopping";
    private const string EnableBhopName = "sv_enablebunnyhopping";

    // Key in addons/counterstrikesharp/gamedata/vip_bhop.json
    private const string ProcessMovementKey = "VIP_Bhop_CCSPlayer_MovementServices_ProcessMovement";

    private readonly VIP_Bhop _plugin;
    private readonly PlayerState[] _states = new PlayerState[65];

    private readonly ConVar? _autoBhop;
    private readonly ConVar? _enableBhop;
    private readonly ConVarFlags _autoBhopFlags;
    private readonly ConVarFlags _enableBhopFlags;

    private readonly MemoryFunctionVoid<IntPtr, IntPtr>? _processMovement;

    // MovementServices pointer -> VIP pawn, rebuilt every tick
    private readonly Dictionary<IntPtr, CCSPlayerPawn> _activeServices = new();

    // Real server values. Captured only while no override is applied, so they can never pick up "true" from a VIP.
    private bool _realAutoBhop;
    private bool _realEnableBhop;
    private bool _overridden;

    // Diagnostics (css_vipbhop_status)
    private long _preCalls;
    private long _postCalls;
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

        try
        {
            _processMovement = new MemoryFunctionVoid<IntPtr, IntPtr>(GameData.GetSignature(ProcessMovementKey));
            _processMovement.Hook(ProcessMovementPre, HookMode.Pre);
            _processMovement.Hook(ProcessMovementPost, HookMode.Post);
        }
        catch (Exception e)
        {
            _processMovement = null;
            plugin.Logger.LogError(e,
                "[VIP Bhop] {0} is missing or outdated in gamedata/vip_bhop.json. Bhop is disabled", ProcessMovementKey);
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

        // Safety net: an override must never survive until the next tick
        if (_overridden)
        {
            _staleRestores++;
            Restore();
        }

        _realAutoBhop = _autoBhop!.GetPrimitiveValue<bool>();
        _realEnableBhop = _enableBhop!.GetPrimitiveValue<bool>();
        var serverAutoBhop = _realAutoBhop;
        var serverEnableBhop = _realEnableBhop;

        foreach (var player in Utilities.GetPlayers())
        {
            if (player is not { IsValid: true, IsBot: false, IsHLTV: false } ||
                player.Connected != PlayerConnectedState.Connected) continue;

            var state = _states[player.Slot];
            var bhop = state.Enabled && state.Active;

            // Per-client values: only sent when they change
            SyncClient(player, state, bhop || serverAutoBhop, bhop || serverEnableBhop);

            if (!bhop || !player.PawnIsAlive) continue;

            var pawn = player.PlayerPawn.Value;
            var services = pawn?.MovementServices;
            if (pawn == null || services == null) continue;

            _activeServices[services.Handle] = pawn;

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

    private HookResult ProcessMovementPre(DynamicHook hook)
    {
        _preCalls++;

        if (_activeServices.Count > 0 && _activeServices.ContainsKey(hook.GetParam<IntPtr>(0)))
        {
            _vipCalls++;
            _autoBhop!.GetPrimitiveValue<bool>() = true;
            _enableBhop!.GetPrimitiveValue<bool>() = true;
            _overridden = true;
        }
        else if (_overridden)
        {
            // Post of the previous VIP call did not run: non-VIP must always move with the real values
            _staleRestores++;
            Restore();
        }

        return HookResult.Continue;
    }

    private HookResult ProcessMovementPost(DynamicHook hook)
    {
        _postCalls++;
        if (_overridden) Restore();

        return HookResult.Continue;
    }

    private void Restore()
    {
        _autoBhop!.GetPrimitiveValue<bool>() = _realAutoBhop;
        _enableBhop!.GetPrimitiveValue<bool>() = _realEnableBhop;
        _overridden = false;
    }

    private void OnStatusCommand(CCSPlayerController? caller, CommandInfo info)
    {
        // server console / rcon only
        if (caller != null) return;

        info.ReplyToCommand($"[VIP Bhop] hook: {(IsWorking ? "OK" : "NOT INSTALLED")}");
        info.ReplyToCommand(
            $"[VIP Bhop] real values: {AutoBhopName}={_realAutoBhop} {EnableBhopName}={_realEnableBhop}, overridden now: {_overridden}");
        info.ReplyToCommand(
            $"[VIP Bhop] flags: {AutoBhopName}={_autoBhop?.Flags} {EnableBhopName}={_enableBhop?.Flags}");
        info.ReplyToCommand(
            $"[VIP Bhop] ProcessMovement pre={_preCalls} post={_postCalls} vip={_vipCalls} staleRestores={_staleRestores}");

        foreach (var player in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false }))
        {
            var st = _states[player.Slot];
            info.ReplyToCommand(
                $"[VIP Bhop] #{player.Slot} {player.PlayerName}: enabled={st.Enabled} active={st.Active} " +
                $"sent={st.SentAutoBhop}/{st.SentEnableBhop} inMovementSet={player.PlayerPawn.Value?.MovementServices is { } ms && _activeServices.ContainsKey(ms.Handle)}");
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

        _processMovement.Unhook(ProcessMovementPre, HookMode.Pre);
        _processMovement.Unhook(ProcessMovementPost, HookMode.Post);

        if (_overridden) Restore();

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
