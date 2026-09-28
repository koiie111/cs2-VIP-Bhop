using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
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
    public override string ModuleVersion => "v2.6.0";

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
 *  3. Server-side, at every per-player entry point (ProcessUsercmds -> OnSimulateUserCommands ->
 *     ProcessMovement) the global value is written directly to memory (no change callbacks / broadcasts):
 *     "true" for a VIP, the real value for everyone else. The plugin never calls game functions itself and
 *     never skips them (doing so re-ran VIP commands on CSS 1.0.375+/KHook: speedhack, teleports), and
 *     does not rely on post hooks (they do not fire there).
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
    private const string ProcessUsercmdsKey = "VIP_Bhop_CCSPlayerController_ProcessUsercmds";
    private const string SetupMoveKey = "VIP_Bhop_CCSPlayer_MovementServices_SetupMove";

    private readonly VIP_Bhop _plugin;
    private readonly PlayerState[] _states = new PlayerState[65];

    private readonly ConVar? _autoBhop;
    private readonly ConVar? _enableBhop;
    private readonly ConVarFlags _autoBhopFlags;
    private readonly ConVarFlags _enableBhopFlags;

    private readonly MemoryFunctionVoid<IntPtr, IntPtr>? _processMovement;
    private readonly MemoryFunctionVoid<IntPtr>? _simulateUserCommands;

    // void CCSPlayer_MovementServices::SetupMove(PlayerCommand* pc, CMoveData* mv): runs right before
    // ProcessMovement and already reads the movement cvars.
    private readonly MemoryFunctionVoid<IntPtr, IntPtr, IntPtr>? _setupMove;

    // void* CCSPlayerController::ProcessUsercmds(CUserCmd* cmds, int numcmds, bool paused, float margin)
    // Returns a pointer: it must be declared as IntPtr, a narrower type corrupts the result.
    private readonly MemoryFunctionWithReturn<IntPtr, IntPtr, int, bool, float, IntPtr>? _processUsercmds;

    private readonly MemoryFunctionVoid<CCSPlayerPawnBase>? _postThink;

    // Rebuilt every tick: MovementServices, controller and pawn pointers of VIPs with active bhop
    private readonly HashSet<IntPtr> _activeServices = new();
    private readonly HashSet<IntPtr> _activeControllers = new();
    private readonly HashSet<IntPtr> _activePawns = new();

    // Real server values, captured every tick (the override never outlives a VIP's own processing)
    private bool _realAutoBhop;
    private bool _realEnableBhop;

    // Diagnostics (css_vipbhop_status)
    private readonly Dictionary<IntPtr, int> _controllerSlots = new();
    private readonly Dictionary<IntPtr, int> _serviceSlots = new();
    private readonly Dictionary<IntPtr, int> _pawnSlots = new();
    private long _postThinkCalls;

    // css_vipbhop_trace: order of hook calls over a few ticks
    private int _traceTicksLeft;
    private readonly List<string> _trace = new();
    private System.Text.StringBuilder? _traceLine;
    private readonly long[] _simulateBySlot = new long[65];
    private readonly long[] _ticksBySlot = new long[65];
    private long _simulateCalls;
    private long _usercmdsCalls;
    private long _preCalls;
    private long _setupMoveCalls;
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

        // Per-player value at every entry point of a player's command processing. All hooks are pass-through.
        try
        {
            _simulateUserCommands = new MemoryFunctionVoid<IntPtr>(GameData.GetSignature(SimulateUserCommandsKey));
            _setupMove = new MemoryFunctionVoid<IntPtr, IntPtr, IntPtr>(GameData.GetSignature(SetupMoveKey));
            _processMovement = new MemoryFunctionVoid<IntPtr, IntPtr>(GameData.GetSignature(ProcessMovementKey));
            _simulateUserCommands.Hook(SimulateUserCommandsPre, HookMode.Pre);
            _setupMove.Hook(SetupMovePre, HookMode.Pre);
            _processMovement.Hook(ProcessMovementPre, HookMode.Pre);
        }
        catch (Exception e)
        {
            _simulateUserCommands?.Unhook(SimulateUserCommandsPre, HookMode.Pre);
            _setupMove?.Unhook(SetupMovePre, HookMode.Pre);
            _simulateUserCommands = null;
            _setupMove = null;
            _processMovement = null;
            plugin.Logger.LogError(e,
                "[VIP Bhop] {0} / {1} / {2} missing or outdated in gamedata/vip_bhop.json. Bhop is disabled",
                SimulateUserCommandsKey, SetupMoveKey, ProcessMovementKey);
            return;
        }

        // ProcessUsercmds is the outermost entry point (OnSimulateUserCommands runs inside it). Part of the
        // jump handling runs there before the simulation, so without it a VIP's "true" can reach the next
        // player. Optional: without it the module works, but that leak is possible.
        try
        {
            _processUsercmds =
                new MemoryFunctionWithReturn<IntPtr, IntPtr, int, bool, float, IntPtr>(
                    GameData.GetSignature(ProcessUsercmdsKey));
            _processUsercmds.Hook(ProcessUsercmdsPre, HookMode.Pre);
        }
        catch (Exception e)
        {
            _processUsercmds = null;
            plugin.Logger.LogWarning(e,
                "[VIP Bhop] {0} missing or outdated in gamedata/vip_bhop.json, VIPs may stick to the ground",
                ProcessUsercmdsKey);
        }

        // Pawn PostThink runs per player outside the command processing (signature maintained by CSS itself).
        // Without it a VIP's "true" can reach other players there.
        try
        {
            _postThink = VirtualFunctions.CCSPlayerPawnBase_PostThinkFunc;
            _postThink.Hook(PostThinkPre, HookMode.Pre);
        }
        catch (Exception e)
        {
            _postThink = null;
            plugin.Logger.LogWarning(e, "[VIP Bhop] CCSPlayerPawnBase_PostThink hook failed");
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
        plugin.RegisterListener<Listeners.OnServerPreEntityThink>(() => Trace("PRE"));
        plugin.RegisterListener<Listeners.OnServerPostEntityThink>(() =>
        {
            Trace("POST");
            EnsureRealValues();
        });
        plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        plugin.AddCommand("css_vipbhop_status", "VIP Bhop diagnostics (server console or @css/root)", OnStatusCommand);
        plugin.AddCommand("css_vipbhop_trace", "VIP Bhop: hook call order for 3 ticks (server console or @css/root)", OnTraceCommand);

        plugin.Logger.LogInformation(
            "[VIP Bhop] {0} loaded: OnSimulateUserCommands+SetupMove+ProcessMovement OK, ProcessUsercmds {1}, PostThink {2}",
            plugin.ModuleVersion, _processUsercmds != null ? "OK" : "NOT FOUND", _postThink != null ? "OK" : "NOT FOUND");
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
        if (_traceTicksLeft > 0) FlushTraceTick();

        _activeServices.Clear();
        _activeControllers.Clear();
        _activePawns.Clear();
        _controllerSlots.Clear();
        _serviceSlots.Clear();
        _pawnSlots.Clear();

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

            var pawn = player.PlayerPawn.Value;
            var services = pawn?.MovementServices;
            if (pawn != null) _pawnSlots[pawn.Handle] = player.Slot;
            if (services != null) _serviceSlots[services.Handle] = player.Slot;

            if (!bhop || !player.PawnIsAlive || pawn == null || services == null) continue;

            _activeServices.Add(services.Handle);
            _activeControllers.Add(player.Handle);
            _activePawns.Add(pawn.Handle);

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

    private HookResult ProcessUsercmdsPre(DynamicHook hook)
    {
        _usercmdsCalls++;
        var controller = hook.GetParam<IntPtr>(0);
        Trace("U", _controllerSlots, controller);
        ApplyFor(_activeControllers.Contains(controller));
        return HookResult.Continue;
    }

    private HookResult PostThinkPre(DynamicHook hook)
    {
        _postThinkCalls++;
        var pawn = hook.GetParam<IntPtr>(0);
        Trace("T", _pawnSlots, pawn);
        ApplyFor(_activePawns.Contains(pawn));
        return HookResult.Continue;
    }

    private HookResult SimulateUserCommandsPre(DynamicHook hook)
    {
        _simulateCalls++;
        var controller = hook.GetParam<IntPtr>(0);
        if (_controllerSlots.TryGetValue(controller, out var slot)) _simulateBySlot[slot]++;
        Trace("S", _controllerSlots, controller);

        ApplyFor(_activeControllers.Contains(controller));
        return HookResult.Continue;
    }

    private HookResult SetupMovePre(DynamicHook hook)
    {
        _setupMoveCalls++;
        var services = hook.GetParam<IntPtr>(0);
        Trace("P", _serviceSlots, services);
        ApplyFor(_activeServices.Contains(services));
        return HookResult.Continue;
    }

    private HookResult ProcessMovementPre(DynamicHook hook)
    {
        _preCalls++;
        var services = hook.GetParam<IntPtr>(0);
        Trace("M", _serviceSlots, services);
        ApplyFor(_activeServices.Contains(services));
        return HookResult.Continue;
    }

    // Pass-through only: game functions are never called or skipped from here
    private void ApplyFor(bool vip)
    {
        if (!vip)
        {
            EnsureRealValues();
            return;
        }

        _vipCalls++;
        _autoBhop!.GetPrimitiveValue<bool>() = true;
        _enableBhop!.GetPrimitiveValue<bool>() = true;
    }

    // Called at every non-VIP entry point: nobody but a VIP may ever run with a value other than the real one
    private void EnsureRealValues()
    {
        ref var autoBhop = ref _autoBhop!.GetPrimitiveValue<bool>();
        ref var enableBhop = ref _enableBhop!.GetPrimitiveValue<bool>();
        if (autoBhop == _realAutoBhop && enableBhop == _realEnableBhop) return;

        _staleRestores++;
        autoBhop = _realAutoBhop;
        enableBhop = _realEnableBhop;
    }

    // Trace format per tick: PRE/POST = entity think phase, U = ProcessUsercmds, S = OnSimulateUserCommands,
    // P = SetupMove, M = ProcessMovement, T = PostThink; then the player slot, "*" for an active VIP, and the value of
    // sv_autobunnyhopping at entry ("+" true, "-" false). A non-VIP entry with "+" means it came in with a VIP's value.
    private void Trace(string marker) => _traceLine?.Append(marker).Append(' ');

    private void Trace(string marker, Dictionary<IntPtr, int> slots, IntPtr pointer)
    {
        if (_traceLine == null) return;

        var slot = slots.TryGetValue(pointer, out var s) ? s.ToString() : "?";
        var vip = _activeControllers.Count > 0 &&
                  (marker == "U" || marker == "S" ? _activeControllers.Contains(pointer)
                      : marker == "M" || marker == "P" ? _activeServices.Contains(pointer)
                      : _activePawns.Contains(pointer));
        var value = _autoBhop!.GetPrimitiveValue<bool>() ? "+" : "-";
        _traceLine.Append(marker).Append(slot).Append(vip ? "*" : "").Append(value).Append(' ');
    }

    private void FlushTraceTick()
    {
        if (_traceLine != null) _trace.Add(_traceLine.ToString());
        _traceTicksLeft--;
        if (_traceTicksLeft > 0)
        {
            _traceLine = new System.Text.StringBuilder();
            return;
        }

        _traceLine = null;
        Server.PrintToConsole("[VIP Bhop] trace (U=usercmds S=simulate P=setupmove M=movement T=postthink, *=VIP, +/-=autobhop at entry):");
        for (var i = 1; i < _trace.Count; i++)
            Server.PrintToConsole($"[VIP Bhop] tick {i}: {_trace[i]}");
        _trace.Clear();
    }

    private void OnTraceCommand(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root")) return;

        _trace.Clear();
        _traceLine = new System.Text.StringBuilder();
        _traceTicksLeft = 4; // first (partial) tick is dropped
        info.ReplyToCommand("[VIP Bhop] tracing 3 ticks, result goes to the server console");
    }

    private void OnStatusCommand(CCSPlayerController? caller, CommandInfo info)
    {
        // server console / rcon, or a root admin from the client console
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root")) return;

        info.ReplyToCommand($"[VIP Bhop] hook: {(IsWorking ? "OK" : "NOT INSTALLED")}");
        info.ReplyToCommand(
            $"[VIP Bhop] real values: {AutoBhopName}={_realAutoBhop} {EnableBhopName}={_realEnableBhop}, " +
            $"current: {_autoBhop?.GetPrimitiveValue<bool>()}/{_enableBhop?.GetPrimitiveValue<bool>()}");
        info.ReplyToCommand(
            $"[VIP Bhop] flags: {AutoBhopName}={_autoBhop?.Flags} {EnableBhopName}={_enableBhop?.Flags}");
        info.ReplyToCommand(
            $"[VIP Bhop] {_plugin.ModuleVersion}, ProcessUsercmds hook: {(_processUsercmds != null ? "OK" : "NOT FOUND")}, " +
            $"PostThink hook: {(_postThink != null ? "OK" : "NOT FOUND")}");
        info.ReplyToCommand(
            $"[VIP Bhop] usercmds={_usercmdsCalls} simulate={_simulateCalls} setupMove={_setupMoveCalls} processMovement={_preCalls} postThink={_postThinkCalls} vip={_vipCalls} restores={_staleRestores}");

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

        _postThink?.Unhook(PostThinkPre, HookMode.Pre);
        _processUsercmds?.Unhook(ProcessUsercmdsPre, HookMode.Pre);
        _simulateUserCommands?.Unhook(SimulateUserCommandsPre, HookMode.Pre);
        _setupMove?.Unhook(SetupMovePre, HookMode.Pre);
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
