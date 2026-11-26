using System.Numerics;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Reflection;
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
    public override string ModuleVersion => "v2.6.2";

    private Bhop? _bhop;
    private IVipCoreApi? _api;

    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = PluginCapability.Get();
        if (_api == null) return;

        _bhop = new Bhop(this, _api);
        if (!_bhop.IsWorking)
        {
            _bhop.Dispose();
            _bhop = null;
            return;
        }

        try
        {
            _api.RegisterFeature(_bhop);
        }
        catch
        {
            Unload(hotReload);
            throw;
        }
    }

    public override void Unload(bool hotReload)
    {
        if (_bhop == null) return;

        try
        {
            _api?.UnRegisterFeature(_bhop);
        }
        finally
        {
            _bhop.Dispose();
        }
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
public unsafe class Bhop : VipFeatureBase, IDisposable
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
    private ConVarFlags? _autoBhopFlags;
    private ConVarFlags? _enableBhopFlags;

    private MemoryFunctionVoid<IntPtr, IntPtr>? _processMovement;
    private MemoryFunctionVoid<IntPtr>? _simulateUserCommands;
    private MemoryFunctionVoid<IntPtr, IntPtr, IntPtr>? _setupMove;

    // ProcessUsercmds returns a pointer. A narrower managed return type corrupts the result.
    private MemoryFunctionWithReturn<IntPtr, IntPtr, int, bool, float, IntPtr>? _processUsercmds;
    private MemoryFunctionVoid<CCSPlayerPawnBase>? _postThink;

    private readonly HashSet<int> _connected = new(65);
    private readonly HashSet<int> _candidates = new(65);
    private readonly List<int> _pendingSync = new(65);

    // Only living, eligible VIPs are indexed here. The owner is a specific connection, not just a slot.
    private readonly Dictionary<IntPtr, PlayerState> _activeServices = new(65);
    private readonly Dictionary<IntPtr, PlayerState> _activeControllers = new(65);
    private readonly Dictionary<IntPtr, PlayerState> _activePawns = new(65);

    private BhopCvar _auto;
    private BhopCvar _enable;
    private bool _mapReady;
    private bool _seedClients = true;
    private bool _disposed;
    private int _round;
    private EventInfo? _featureStateSet;
    private EventInfo? _availabilityChanged;

    private bool _sampleRequested;
    private HookSample? _sample;
    private HookSample? _lastSample;
    private readonly Dictionary<IntPtr, int> _controllerSlots = new(65);
    private readonly Dictionary<IntPtr, int> _serviceSlots = new(65);
    private readonly Dictionary<IntPtr, int> _pawnSlots = new(65);

    private int _traceTicksLeft;
    private readonly List<string> _trace = new(4);
    private System.Text.StringBuilder? _traceLine;

    public Bhop(VIP_Bhop plugin, IVipCoreApi api) : base(api)
    {
        _plugin = plugin;
        for (var i = 0; i < _states.Length; i++) _states[i] = new PlayerState();

        _autoBhop = ConVar.Find(AutoBhopName);
        _enableBhop = ConVar.Find(EnableBhopName);
        if (_autoBhop == null || _enableBhop == null)
        {
            plugin.Logger.LogError("[VIP Bhop] Required cvars were not found");
            return;
        }

        try
        {
            RefreshCvarPointers();
            _auto.Capture();
            _enable.Capture();

            var simulate = new MemoryFunctionVoid<IntPtr>(GameData.GetSignature(SimulateUserCommandsKey));
            var setup = new MemoryFunctionVoid<IntPtr, IntPtr, IntPtr>(GameData.GetSignature(SetupMoveKey));
            var movement = new MemoryFunctionVoid<IntPtr, IntPtr>(GameData.GetSignature(ProcessMovementKey));
            simulate.Hook(SimulateUserCommandsPre, HookMode.Pre);
            _simulateUserCommands = simulate;
            setup.Hook(SetupMovePre, HookMode.Pre);
            _setupMove = setup;
            movement.Hook(ProcessMovementPre, HookMode.Pre);
            _processMovement = movement;

            InstallOptionalHooks();

            _autoBhopFlags = _autoBhop.Flags;
            _autoBhop.Flags = _autoBhopFlags.Value & ~ConVarFlags.FCVAR_REPLICATED;
            _enableBhopFlags = _enableBhop.Flags;
            _enableBhop.Flags = _enableBhopFlags.Value & ~ConVarFlags.FCVAR_REPLICATED;

            plugin.RegisterListener<Listeners.OnClientConnected>(ResetState);
            plugin.RegisterListener<Listeners.OnClientPutInServer>(QueueSync);
            plugin.RegisterListener<Listeners.OnClientDisconnect>(ResetState);
            plugin.RegisterListener<Listeners.OnClientDisconnectPost>(ResetState);
            plugin.RegisterListener<Listeners.OnEntityDeleted>(OnEntityDeleted);
            plugin.RegisterListener<Listeners.OnTick>(OnTick);
            plugin.RegisterListener<Listeners.OnMapStart>(OnMapStart);
            plugin.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
            plugin.RegisterListener<Listeners.OnServerPostEntityThink>(OnPostEntityThink);
            plugin.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
            plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
            plugin.RegisterEventHandler<EventRoundFreezeEnd>(OnFreezeEnd);

            // These notifications are optional extensions to the original VIP Core API.
            var coreType = api.GetType();
            _featureStateSet = coreType.GetInterface("VipCoreApi.IVipFeatureStateApi")?.GetEvent("PlayerFeatureStateSet");
            _availabilityChanged = coreType.GetInterface("VipCoreApi.IVipFeatureAvailabilityApi")?.GetEvent("FeatureAvailabilityChanged");
            _featureStateSet?.AddEventHandler(api, (Action<CCSPlayerController, string, FeatureState>)OnFeatureStateSet);
            _availabilityChanged?.AddEventHandler(api, (Action)RefreshPlayers);
            _mapReady = true;
        }
        catch (Exception e)
        {
            plugin.Logger.LogError(e, "[VIP Bhop] Initialization failed. Check required signatures in gamedata/vip_bhop.json");
            Dispose();
            return;
        }

        plugin.AddCommand("css_vipbhop_status", "VIP Bhop diagnostics ([sample], console or @css/root)", OnStatusCommand);
        plugin.AddCommand("css_vipbhop_trace", "VIP Bhop: hook call order for 3 ticks (console or @css/root)", OnTraceCommand);
        plugin.Logger.LogInformation(
            "[VIP Bhop] {Version} loaded: OnSimulateUserCommands+SetupMove+ProcessMovement OK, ProcessUsercmds {Usercmds}, PostThink {PostThink}",
            plugin.ModuleVersion, _processUsercmds != null ? "OK" : "NOT FOUND", _postThink != null ? "OK" : "NOT FOUND");
    }

    private void InstallOptionalHooks()
    {
        try
        {
            var usercmds = new MemoryFunctionWithReturn<IntPtr, IntPtr, int, bool, float, IntPtr>(
                GameData.GetSignature(ProcessUsercmdsKey));
            usercmds.Hook(ProcessUsercmdsPre, HookMode.Pre);
            _processUsercmds = usercmds;
        }
        catch (Exception e)
        {
            _plugin.Logger.LogWarning(e, "[VIP Bhop] {Key} unavailable. VIPs may stick and per-player isolation may be incomplete",
                ProcessUsercmdsKey);
        }

        try
        {
            var postThink = VirtualFunctions.CCSPlayerPawnBase_PostThinkFunc;
            postThink.Hook(PostThinkPre, HookMode.Pre);
            _postThink = postThink;
        }
        catch (Exception e)
        {
            _plugin.Logger.LogWarning(e, "[VIP Bhop] CCSPlayerPawnBase_PostThink hook failed");
        }
    }

    internal bool IsWorking => !_disposed && _processMovement != null;

    private void RefreshCvarPointers()
    {
        _auto.SetAddress((bool*)Unsafe.AsPointer(ref _autoBhop!.GetPrimitiveValue<bool>()));
        _enable.SetAddress((bool*)Unsafe.AsPointer(ref _enableBhop!.GetPrimitiveValue<bool>()));
    }

    private void OnMapStart(string map)
    {
        if (!IsWorking) return;
        RefreshCvarPointers();
        _auto.Capture();
        _enable.Capture();
        _mapReady = true;
        _seedClients = true;
    }

    private void OnMapEnd()
    {
        RestoreCvars();
        _mapReady = false;
        _round++;
        StopTrace();
        _sampleRequested = false;
        _sample = null;
        _lastSample = null;
        _controllerSlots.Clear();
        for (var slot = 0; slot < _states.Length; slot++)
            ResetState(slot);
        _pendingSync.Clear();
    }

    private PlayerState? GetState(CCSPlayerController player)
    {
        if (player is not { IsValid: true, IsBot: false, IsHLTV: false }) return null;
        var slot = player.Slot;
        if ((uint)slot >= _states.Length) return null;

        var identity = player.EntityHandle.Raw;
        var state = _states[slot];
        if (state.ControllerId is { } previous && previous != identity)
        {
            ResetState(slot);
            state = _states[slot];
        }

        state.ControllerId = identity;
        return state;
    }

    public override void OnPlayerLoaded(CCSPlayerController player, string group) => RefreshPlayer(player);

    public override void OnPlayerRemoved(CCSPlayerController player, string group)
    {
        if (!_mapReady || GetState(player) is not { } state) return;
        state.Generation++;
        state.Enabled = false;
        state.Active = false;
        UpdateEligibility(player.Slot, state);
    }

    public override void OnSelectItem(CCSPlayerController player, FeatureState state) => RefreshPlayer(player);

    private void OnFeatureStateSet(CCSPlayerController player, string feature, FeatureState state)
    {
        if (feature == Feature) RefreshPlayer(player);
    }

    private void RefreshPlayer(CCSPlayerController player)
    {
        if (_mapReady && GetState(player) is { } state)
            RefreshPlayer(player, state, GetGameRules());
    }

    private void RefreshPlayer(CCSPlayerController player, PlayerState state, CCSGameRules? rules)
    {
        var generation = ++state.Generation;
        state.Enabled = IsClientVip(player) && PlayerHasFeature(player) &&
            GetPlayerFeatureState(player) == FeatureState.Enabled;
        state.Active = false;
        if (state.Enabled && rules != null)
        {
            var settings = GetFeatureValue<BhopSettings>(player);
            state.MaxSpeed = settings.MaxSpeed;
            var delay = BhopActivation.Remaining(rules.WarmupPeriod, rules.FreezePeriod,
                rules.RoundStartTime, Server.CurrentTime, settings.Timer);
            state.Active = delay == 0;
            if (delay is > 0)
            {
                PrintToChat(player, GetTranslatedText("bhop.TimeToActivation", Math.Ceiling(delay.Value)));
                var slot = player.Slot;
                var round = _round;
                _plugin.AddTimer(delay.Value, () => Activate(slot, state, round, generation), TimerFlags.STOP_ON_MAPCHANGE);
            }
        }
        UpdateEligibility(player.Slot, state);
    }

    private void UpdateEligibility(int slot, PlayerState state)
    {
        if (state.WantsBhop)
            _candidates.Add(slot);
        else
        {
            _candidates.Remove(slot);
            RemoveMovement(state);
        }

        QueueSync(slot);
    }

    private void QueueSync(int slot)
    {
        if ((uint)slot >= _states.Length || _states[slot].PendingSync || _disposed) return;
        _states[slot].PendingSync = true;
        _pendingSync.Add(slot);
    }

    private void ResetState(int slot)
    {
        if ((uint)slot >= _states.Length || _disposed) return;
        var state = _states[slot];
        if (state.PendingSync) _pendingSync.Remove(slot);
        _connected.Remove(slot);
        _candidates.Remove(slot);
        RemoveMovement(state);
        _states[slot] = new PlayerState();
    }

    private HookResult OnPlayerDeath(EventPlayerDeath gameEvent, GameEventInfo info)
    {
        if (_activeControllers.Count != 0 && gameEvent.Userid is { IsValid: true } player &&
            _activeControllers.TryGetValue(player.Handle, out var state) && state.ControllerId == player.EntityHandle.Raw)
            RemoveMovement(state);

        return HookResult.Continue;
    }

    private void OnEntityDeleted(CEntityInstance entity)
    {
        if (_activeControllers.Count == 0) return;
        // The entity system may no longer resolve a handle during deletion. The
        // listener itself receives the native address before it can be reused.
        var address = ((NativeObject)entity).Handle;
        if (_activePawns.TryGetValue(address, out var state) || _activeControllers.TryGetValue(address, out state))
            RemoveMovement(state);
    }

    private static void RemoveAddress(Dictionary<IntPtr, PlayerState> index, IntPtr address, PlayerState state)
    {
        if (address != IntPtr.Zero && index.TryGetValue(address, out var owner) && ReferenceEquals(owner, state))
            index.Remove(address);
    }

    private void RemoveMovement(PlayerState state)
    {
        if (state.ControllerAddress == IntPtr.Zero) return;
        RemoveAddress(_activeControllers, state.ControllerAddress, state);
        RemoveAddress(_activePawns, state.PawnAddress, state);
        RemoveAddress(_activeServices, state.ServicesAddress, state);
        state.ControllerAddress = IntPtr.Zero;
        state.WasGrounded = false;
        if (_activeControllers.Count == 0)
            RestoreCvars();
    }

    private void OnTick()
    {
        if (!_mapReady) return;
        if (_seedClients)
        {
            // The engine's globals are available on the first tick, even when the
            // plugin was registered before the initial map had finished loading.
            _seedClients = false;
            RefreshPlayers();
        }

        RefreshCvarPointers();
        RestoreCvars();
        var baseChanged = _auto.Capture() | _enable.Capture();
        if (baseChanged)
        {
            foreach (var slot in _connected)
                QueueSync(slot);
        }

        SyncPendingClients();
        foreach (var slot in _candidates)
            RefreshMovement(slot, _states[slot]);

        if (_traceTicksLeft > 0) FlushTraceTick();
        if (_sample is { } sample && Server.TickCount - sample.StartTick >= HookSample.DurationTicks)
        {
            sample.ElapsedTicks = Server.TickCount - sample.StartTick;
            _lastSample = sample;
            _sample = null;
            if (_traceLine == null) _controllerSlots.Clear();
            Server.PrintToConsole("[VIP Bhop] sample complete. Run css_vipbhop_status for results.");
        }

        if (_sampleRequested)
        {
            // Start on a tick boundary so the first and last intervals are both complete.
            _sample = new HookSample(Server.TickCount, _states.Length);
            _sampleRequested = false;
        }
        if (_sample != null || _traceLine != null)
            RefreshDiagnosticSlots();
    }

    private void SyncPendingClients()
    {
        // Keep only clients for which PutInServer arrived before the controller was ready.
        for (var i = _pendingSync.Count - 1; i >= 0; i--)
        {
            var slot = _pendingSync[i];
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is not { IsValid: true, Connected: PlayerConnectedState.Connected }) continue;
            _pendingSync.RemoveAt(i);
            _states[slot].PendingSync = false;
            if (GetState(player) is not { } state) continue;
            _connected.Add(slot);

            if (state.NeedsAutoBhop(_auto.Baseline, out var auto))
            {
                player.ReplicateConVar(AutoBhopName, auto ? "true" : "false");
                state.SentAutoBhop = auto;
            }
            if (state.NeedsEnableBhop(_enable.Baseline, out var enable))
            {
                player.ReplicateConVar(EnableBhopName, enable ? "true" : "false");
                state.SentEnableBhop = enable;
            }
        }
    }

    private void RefreshMovement(int slot, PlayerState state)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true, Connected: PlayerConnectedState.Connected, PawnIsAlive: true } ||
            state.ControllerId != player.EntityHandle.Raw ||
            player.PlayerPawn.Value is not { IsValid: true } pawn || pawn.MovementServices is not { } services)
        {
            RemoveMovement(state);
            return;
        }

        var controllerAddress = player.Handle;
        var pawnAddress = pawn.Handle;
        var servicesAddress = services.Handle;
        var pawnId = pawn.EntityHandle.Raw;
        if (state.ControllerAddress != controllerAddress || state.PawnAddress != pawnAddress ||
            state.ServicesAddress != servicesAddress || state.PawnId != pawnId)
        {
            RemoveMovement(state);
            state.ControllerAddress = controllerAddress;
            state.PawnAddress = pawnAddress;
            state.ServicesAddress = servicesAddress;
            state.PawnId = pawnId;
            _activeControllers[controllerAddress] = state;
            _activePawns[pawnAddress] = state;
            _activeServices[servicesAddress] = state;
        }

        if (state.MaxSpeed > 0)
            ClampSpeed(player, pawn, state);
        else
            state.WasGrounded = false;
    }

    private HookResult ProcessUsercmdsPre(DynamicHook hook) =>
        ApplyMovement(hook, _activeControllers, HookEntry.Usercmds);
    private HookResult SimulateUserCommandsPre(DynamicHook hook) =>
        ApplyMovement(hook, _activeControllers, HookEntry.Simulate);
    private HookResult SetupMovePre(DynamicHook hook) =>
        ApplyMovement(hook, _activeServices, HookEntry.SetupMove);
    private HookResult ProcessMovementPre(DynamicHook hook) =>
        ApplyMovement(hook, _activeServices, HookEntry.Movement);
    private HookResult PostThinkPre(DynamicHook hook) =>
        ApplyMovement(hook, _activePawns, HookEntry.PostThink);

    private HookResult ApplyMovement(DynamicHook hook, Dictionary<IntPtr, PlayerState> active, HookEntry entry)
    {
        if (!_mapReady) return HookResult.Continue;
        if (_sample != null || _traceLine != null)
            return ApplyMeasuredMovement(hook, active, entry);

        if (active.Count != 0 && active.ContainsKey(hook.GetParam<IntPtr>(0)))
            ApplyVipCvars();
        else
            RestoreCvars();
        return HookResult.Continue;
    }

    private HookResult ApplyMeasuredMovement(DynamicHook hook, Dictionary<IntPtr, PlayerState> active, HookEntry entry)
    {
        var sample = _sample;
        var start = sample != null ? Stopwatch.GetTimestamp() : 0;
        var address = hook.GetParam<IntPtr>(0);
        var vip = active.ContainsKey(address);
        var slots = entry switch
        {
            HookEntry.Usercmds or HookEntry.Simulate => _controllerSlots,
            HookEntry.PostThink => _pawnSlots,
            _ => _serviceSlots
        };

        if (_traceLine is { } line)
        {
            line.Append("USPMT"[(int)entry]);
            if (slots.TryGetValue(address, out var slot)) line.Append(slot);
            else line.Append('?');
            if (vip) line.Append('*');
            line.Append(_auto.Current ? '+' : '-').Append(' ');
        }

        if (vip) ApplyVipCvars();
        else RestoreCvars();

        if (sample != null)
        {
            sample.Calls[(int)entry]++;
            if (vip) sample.VipCalls++;
            if (entry == HookEntry.Simulate && slots.TryGetValue(address, out var slot))
                sample.SimulateBySlot[slot]++;
            sample.HookTime += Stopwatch.GetTimestamp() - start;
        }
        return HookResult.Continue;
    }

    private void ApplyVipCvars()
    {
        _auto.Override();
        _enable.Override();
    }

    private void RestoreCvars()
    {
        if ((_auto.Restore() | _enable.Restore()) && _sample is { } sample)
            sample.Restores++;
    }

    private void RefreshDiagnosticSlots()
    {
        _controllerSlots.Clear();
        _serviceSlots.Clear();
        _pawnSlots.Clear();
        foreach (var slot in _connected)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is not { IsValid: true, Connected: PlayerConnectedState.Connected }) continue;
            _controllerSlots[player.Handle] = slot;
            if (_sample is { } sample)
            {
                if (sample.ControllerIds[slot] != player.EntityHandle.Raw)
                {
                    sample.ControllerIds[slot] = player.EntityHandle.Raw;
                    sample.TicksBySlot[slot] = 0;
                    sample.SimulateBySlot[slot] = 0;
                }
                sample.TicksBySlot[slot]++;
            }

            if (_traceLine == null || player.PlayerPawn.Value is not { IsValid: true } pawn) continue;
            _pawnSlots[pawn.Handle] = slot;
            if (pawn.MovementServices is { } services) _serviceSlots[services.Handle] = slot;
        }
    }

    private void OnPreEntityThink() => _traceLine?.Append("PRE ");

    private void OnPostEntityThink()
    {
        _traceLine?.Append("POST ");
        RestoreCvars();
    }

    private void FlushTraceTick()
    {
        _trace.Add(_traceLine!.ToString());
        if (--_traceTicksLeft > 0)
        {
            _traceLine.Clear();
            return;
        }

        Server.PrintToConsole("[VIP Bhop] trace (U=usercmds S=simulate P=setupmove M=movement T=postthink, *=VIP, +/-=autobhop at entry):");
        for (var i = 1; i < _trace.Count; i++)
            Server.PrintToConsole($"[VIP Bhop] tick {i}: {_trace[i]}");
        StopTrace();
    }

    private void StopTrace()
    {
        if (_traceLine == null) return;
        _plugin.RemoveListener<Listeners.OnServerPreEntityThink>(OnPreEntityThink);
        _traceLine = null;
        _traceTicksLeft = 0;
        _trace.Clear();
        if (_sample == null) _controllerSlots.Clear();
        _serviceSlots.Clear();
        _pawnSlots.Clear();
    }

    private void OnTraceCommand(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root")) return;
        if (!_mapReady)
        {
            info.ReplyToCommand("[VIP Bhop] No active map");
            return;
        }

        if (_traceLine == null)
            _plugin.RegisterListener<Listeners.OnServerPreEntityThink>(OnPreEntityThink);
        _trace.Clear();
        _traceLine = new System.Text.StringBuilder();
        _traceTicksLeft = 4; // The first, partial tick is discarded.
        info.ReplyToCommand("[VIP Bhop] tracing 3 ticks, result goes to the server console");
    }

    private void OnStatusCommand(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root")) return;
        if (info.ArgCount > 1 && info.GetArg(1).Equals("sample", StringComparison.OrdinalIgnoreCase))
        {
            if (!_mapReady)
                info.ReplyToCommand("[VIP Bhop] No active map");
            else if (_sampleRequested || _sample != null)
                info.ReplyToCommand("[VIP Bhop] A sample is already running");
            else
            {
                _sampleRequested = true;
                info.ReplyToCommand("[VIP Bhop] Sampling for 10 seconds of game time. Run css_vipbhop_status for results.");
            }
            return;
        }

        info.ReplyToCommand($"[VIP Bhop] {_plugin.ModuleVersion}, required hooks: {(IsWorking ? "OK" : "NOT INSTALLED")}, " +
            $"ProcessUsercmds: {(_processUsercmds != null ? "OK" : "NOT FOUND")}, PostThink: {(_postThink != null ? "OK" : "NOT FOUND")}");
        if (_mapReady)
            info.ReplyToCommand($"[VIP Bhop] real values: {AutoBhopName}={_auto.Baseline} {EnableBhopName}={_enable.Baseline}, " +
                $"current: {_auto.Current}/{_enable.Current}, flags: {_autoBhop?.Flags}/{_enableBhop?.Flags}");

        if (_sampleRequested || _sample != null) info.ReplyToCommand("[VIP Bhop] Sample in progress");
        if (_lastSample is { } sample)
            info.ReplyToCommand(sample.Report());
        else
            info.ReplyToCommand("[VIP Bhop] No completed sample. Use css_vipbhop_status sample.");

        foreach (var player in Utilities.GetPlayers())
        {
            if (player is not { IsBot: false, IsHLTV: false } || (uint)player.Slot >= _states.Length) continue;
            var slot = player.Slot;
            var state = _states[slot];
            var simulated = _lastSample?.PlayerReport(slot, player.EntityHandle.Raw) ?? "n/a";
            info.ReplyToCommand($"[VIP Bhop] #{slot} {player.PlayerName}: enabled={state.Enabled} active={state.Active} " +
                $"sent={state.SentAutoBhop}/{state.SentEnableBhop} inMovementSet={state.ControllerAddress != IntPtr.Zero} " +
                $"inControllerSet={_activeControllers.ContainsKey(player.Handle)} simulate/tick={simulated}");
        }
    }

    private static void ClampSpeed(CCSPlayerController player, CCSPlayerPawn pawn, PlayerState state)
    {
        var grounded = ((PlayerFlags)pawn.Flags & PlayerFlags.FL_ONGROUND) != 0;
        var tookOff = state.WasGrounded && !grounded;
        state.WasGrounded = grounded;
        if (!tookOff || (player.Buttons & PlayerButtons.Jump) == 0) return;

        var velocity = pawn.AbsVelocity;
        var z = velocity.Z;
        if (!float.IsFinite(z) || z <= 0 ||
            !BhopSpeed.TryClamp(velocity.X, velocity.Y, state.MaxSpeed, out var x, out var y)) return;

        // Movement can reuse the landing speed when the engine processes its
        // next jump. Apply the cap after that jump, preserving vertical speed.
        if (float.IsFinite(x) && float.IsFinite(y))
            pawn.Teleport(velocity: new Vector3(x, y, z));
    }

    private HookResult OnRoundStart(EventRoundStart gameEvent, GameEventInfo info)
    {
        if (!_mapReady) return HookResult.Continue;
        _round++;
        RefreshPlayers();
        return HookResult.Continue;
    }

    private HookResult OnFreezeEnd(EventRoundFreezeEnd gameEvent, GameEventInfo info)
    {
        RefreshPlayers();
        return HookResult.Continue;
    }

    private void RefreshPlayers()
    {
        if (!_mapReady) return;
        var rules = GetGameRules();
        foreach (var player in Utilities.GetPlayers())
        {
            if (GetState(player) is { } state)
                RefreshPlayer(player, state, rules);
        }
    }

    private void Activate(int slot, PlayerState state, int round, int generation)
    {
        if (!_mapReady || !PlayerState.IsCurrent(_states[slot], state, _round, round, generation) ||
            Utilities.GetPlayerFromSlot(slot) is not { IsValid: true, Connected: PlayerConnectedState.Connected } player ||
            state.ControllerId != player.EntityHandle.Raw)
            return;

        RefreshPlayer(player, state, GetGameRules());
        if (state.Active) PrintToChat(player, GetTranslatedText("bhop.Activated"));
    }

    private static CCSGameRules? GetGameRules()
    {
        return Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault(g => g.IsValid)?.GameRules;
    }

    private static void RestoreReplicationFlag(ConVar cvar, ConVarFlags originalFlags)
    {
        const ConVarFlags replicated = ConVarFlags.FCVAR_REPLICATED;
        cvar.Flags = (cvar.Flags & ~replicated) | (originalFlags & replicated);
    }

    public void Dispose()
    {
        if (_disposed) return;
        var wasReady = _mapReady;
        _mapReady = false;
        _disposed = true;
        StopTrace();
        _sampleRequested = false;
        _sample = null;
        _controllerSlots.Clear();
        _featureStateSet?.RemoveEventHandler(Api, (Action<CCSPlayerController, string, FeatureState>)OnFeatureStateSet);
        _availabilityChanged?.RemoveEventHandler(Api, (Action)RefreshPlayers);
        Api.OnPlayerSpawn -= OnPlayerSpawn;
        Api.PlayerLoaded -= OnPlayerLoaded;
        Api.PlayerRemoved -= OnPlayerRemoved;

        _postThink?.Unhook(PostThinkPre, HookMode.Pre);
        _processUsercmds?.Unhook(ProcessUsercmdsPre, HookMode.Pre);
        _simulateUserCommands?.Unhook(SimulateUserCommandsPre, HookMode.Pre);
        _setupMove?.Unhook(SetupMovePre, HookMode.Pre);
        _processMovement?.Unhook(ProcessMovementPre, HookMode.Pre);

        if (_autoBhopFlags is { } autoFlags) RestoreReplicationFlag(_autoBhop!, autoFlags);
        if (_enableBhopFlags is { } enableFlags) RestoreReplicationFlag(_enableBhop!, enableFlags);
        if (!wasReady) return;

        RefreshCvarPointers();
        RestoreCvars();
        var auto = _auto.Current ? "true" : "false";
        var enable = _enable.Current ? "true" : "false";
        foreach (var player in Utilities.GetPlayers())
        {
            if (player is not { IsBot: false, IsHLTV: false }) continue;
            player.ReplicateConVar(AutoBhopName, auto);
            player.ReplicateConVar(EnableBhopName, enable);
        }
    }
}
