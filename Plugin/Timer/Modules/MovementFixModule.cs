/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using Microsoft.Extensions.Logging;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Managers;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Modules;

internal interface IMovementFixModule
{
}

// Fixes for stock CS2 movement bugs, one file per fix under MovementFix/.
internal unsafe partial class MovementFixModule : IModule, IMovementFixModule, IGameListener, IEntityListener
{
    private readonly InterfaceBridge            _bridge;
    private readonly IInlineHookManager         _inlineHookManager;
    private readonly IEventHookManager          _eventHook;
    private readonly ILogger<MovementFixModule> _logger;

    private static IMovementExtension _movementExtension = null!;
    private static IMapInfoModule     _mapInfo           = null!;
    private static IEntityManager     _entityManager     = null!;

    // cvars
    // ReSharper disable InconsistentNaming

    private readonly IConVar timer_slopefix;
    private readonly IConVar timer_uphill;
    private readonly IConVar timer_telehop;
    private readonly IConVar timer_edgebug;
    private readonly IConVar timer_stairs;
    private readonly IConVar timer_triggerjump;
    private readonly IConVar timer_teleport_keep_angles;
    private readonly IConVar timer_mpbhops;
    private readonly IConVar timer_path_triggers;

    private readonly IConVar sv_standable_normal;
    private readonly IConVar? sv_stepsize;

    // ReSharper restore InconsistentNaming

    private static bool  _slopefixEnabled;
    private static bool  _uphillEnabled;
    private static bool  _telehopEnabled;
    private static bool  _edgebugEnabled;
    private static bool  _stairsEnabled;
    private static bool  _triggerJumpEnabled;
    private static bool  _keepTeleportAnglesEnabled;
    private static bool  _mpbhopsEnabled;
    private static bool  _pathTriggersEnabled;
    private static float _standableNormal;
    private static float _stepSize;

    // CGlobalVars*, set once a map is loaded.
    private static nint _globals;

    // Counts user commands; ProcessMove runs once per sub-tick step.
    private static readonly int[]  _moveTick     = new int[PlayerSlot.MaxPlayerCount];
    private static readonly bool[] _isFakeClient = new bool[PlayerSlot.MaxPlayerCount];

    public MovementFixModule(InterfaceBridge            bridge,
                             IInlineHookManager         inlineHookManager,
                             IEventHookManager          eventHook,
                             ILogger<MovementFixModule> logger,
                             IMovementExtension         movementExtension,
                             IMapInfoModule             mapInfo)
    {
        _bridge            = bridge;
        _inlineHookManager = inlineHookManager;
        _eventHook         = eventHook;
        _logger            = logger;
        _movementExtension = movementExtension;
        _mapInfo           = mapInfo;
        _entityManager     = bridge.EntityManager;

        timer_slopefix = bridge.ConVarManager.CreateConVar("timer_slopefix",
                                                           true,
                                                           "Keep the speed a downhill slope gives when landing on it without colliding with it first")
            !;

        timer_uphill = bridge.ConVarManager.CreateConVar("timer_uphill",
                                                         true,
                                                         "Land players on an uphill slope without the collision that takes away their speed depending on where in the tick they reach it")
            !;

        timer_telehop = bridge.ConVarManager.CreateConVar("timer_telehop",
                                                          true,
                                                          "Give back the speed a collision or landing took in the same tick a trigger_teleport fires")
            !;

        timer_edgebug = bridge.ConVarManager.CreateConVar("timer_edgebug",
                                                          true,
                                                          "Land players on the edge of a block instead of letting them slide off it depending on where in the tick they hit it")
            !;

        timer_stairs = bridge.ConVarManager.CreateConVar("timer_stairs",
                                                         true,
                                                         "On surf maps, let players slide up stairs at speed: on the ground, and landing on them after hitting a step's face")
            !;

        timer_triggerjump = bridge.ConVarManager.CreateConVar("timer_triggerjump",
                                                              true,
                                                              "Touch the teleports, pushes and trigger_multiples (zones included) lying in the gap between a landing player and the ground")
            !;

        timer_teleport_keep_angles = bridge.ConVarManager.CreateConVar("timer_teleport_keep_angles",
                                                                       false,
                                                                       "Keep players' view angles and velocity when a trigger_teleport moves them, instead of turning both to the destination")
            !;

        timer_mpbhops = bridge.ConVarManager.CreateConVar("timer_mpbhops",
                                                          true,
                                                          "On bhop maps, keep bhop platforms still and teleport or boost each player as the platform would have (from the next map)")
            !;

        timer_path_triggers = bridge.ConVarManager.CreateConVar("timer_path_triggers",
                                                                true,
                                                                "Touch the teleports, pushes and trigger_multiples (zones included) a player passes through during a tick, not only where it ends")
            !;

        sv_standable_normal = bridge.ConVarManager.FindConVar("sv_standable_normal")!;
        // Development-only, so only a full search finds it.
        sv_stepsize         = bridge.ConVarManager.FindConVar("sv_stepsize", true);
    }

    public bool Init()
    {
        ResetPlayerState();
        RefreshConVars();

        _bridge.ConVarManager.InstallChangeHook(timer_slopefix, OnConVarChanged);
        _bridge.ConVarManager.InstallChangeHook(timer_uphill, OnConVarChanged);
        _bridge.ConVarManager.InstallChangeHook(timer_telehop, OnConVarChanged);
        _bridge.ConVarManager.InstallChangeHook(timer_edgebug, OnConVarChanged);
        _bridge.ConVarManager.InstallChangeHook(timer_stairs, OnConVarChanged);
        _bridge.ConVarManager.InstallChangeHook(timer_triggerjump, OnConVarChanged);
        _bridge.ConVarManager.InstallChangeHook(timer_teleport_keep_angles, OnConVarChanged);
        _bridge.ConVarManager.InstallChangeHook(timer_mpbhops, OnConVarChanged);
        _bridge.ConVarManager.InstallChangeHook(timer_path_triggers, OnConVarChanged);
        _bridge.ConVarManager.InstallChangeHook(sv_standable_normal, OnConVarChanged);

        if (sv_stepsize is not null)
        {
            _bridge.ConVarManager.InstallChangeHook(sv_stepsize, OnConVarChanged);
        }

        InstallHooks();

        _bridge.HookManager.PlayerRunCommand.InstallHookPre(OnPlayerRunCommandPre);
        _bridge.HookManager.PlayerPostThink.InstallForward(OnPlayerPostThink);
        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnPathTriggersMovePre);
        _bridge.HookManager.PlayerProcessMovePost.InstallForward(OnPathTriggersMovePost);
        _bridge.ModSharp.InstallGameListener(this);
        _bridge.EntityManager.InstallEntityListener(this);
        _eventHook.ListenEvent("player_jump", OnBhopBlockJump);

        return true;
    }

    public void Shutdown()
    {
        _bridge.ModSharp.RemoveGameListener(this);
        _bridge.EntityManager.RemoveEntityListener(this);
        _bridge.HookManager.PlayerRunCommand.RemoveHookPre(OnPlayerRunCommandPre);
        _bridge.HookManager.PlayerPostThink.RemoveForward(OnPlayerPostThink);
        _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnPathTriggersMovePre);
        _bridge.HookManager.PlayerProcessMovePost.RemoveForward(OnPathTriggersMovePost);

        _bridge.ConVarManager.RemoveChangeHook(timer_slopefix, OnConVarChanged);
        _bridge.ConVarManager.RemoveChangeHook(timer_uphill, OnConVarChanged);
        _bridge.ConVarManager.RemoveChangeHook(timer_telehop, OnConVarChanged);
        _bridge.ConVarManager.RemoveChangeHook(timer_edgebug, OnConVarChanged);
        _bridge.ConVarManager.RemoveChangeHook(timer_stairs, OnConVarChanged);
        _bridge.ConVarManager.RemoveChangeHook(timer_triggerjump, OnConVarChanged);
        _bridge.ConVarManager.RemoveChangeHook(timer_teleport_keep_angles, OnConVarChanged);
        _bridge.ConVarManager.RemoveChangeHook(timer_mpbhops, OnConVarChanged);
        _bridge.ConVarManager.RemoveChangeHook(timer_path_triggers, OnConVarChanged);
        _bridge.ConVarManager.RemoveChangeHook(sv_standable_normal, OnConVarChanged);

        if (sv_stepsize is not null)
        {
            _bridge.ConVarManager.RemoveChangeHook(sv_stepsize, OnConVarChanged);
        }

        // InlineHookManager shuts down first and removes the detours.
        if (_triggerFilterVtable != null)
        {
            FreeTriggerFilterVtable();
        }

        _globals    = nint.Zero;
        _globalVars = null;
    }

    public void OnGameInit()
    {
        _globalVars = _bridge.ModSharp.GetGlobals();
        _globals    = _globalVars.GetAbsPtr();
        ResetBhopBlocks();
    }

    public void OnGameShutdown()
    {
        _globals    = nint.Zero;
        _globalVars = null;
        ResetBhopBlocks();
    }

    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    private void OnConVarChanged(IConVar conVar)
        => RefreshConVars();

    private void RefreshConVars()
    {
        _slopefixEnabled           = timer_slopefix.GetBool();
        _uphillEnabled             = timer_uphill.GetBool();
        _telehopEnabled            = timer_telehop.GetBool();
        _edgebugEnabled            = timer_edgebug.GetBool();
        _stairsEnabled             = timer_stairs.GetBool();
        _triggerJumpEnabled        = timer_triggerjump.GetBool();
        _keepTeleportAnglesEnabled = timer_teleport_keep_angles.GetBool();
        _mpbhopsEnabled            = timer_mpbhops.GetBool();
        _pathTriggersEnabled       = timer_path_triggers.GetBool();
        _standableNormal           = sv_standable_normal.GetFloat();
        _stepSize                  = sv_stepsize?.GetFloat() ?? 18.0f;
    }

    private static void ResetPlayerState()
    {
        Array.Fill(_moveTick, 0);
        Array.Fill(_isFakeClient, false);
        Array.Fill(_moved, false);
        Array.Fill(_collided, false);
        Array.Fill(_speedLossTick, int.MinValue);
        Array.Fill(_teleportTick, int.MinValue);
        Array.Fill(_landTick, int.MinValue);
        Array.Fill(_touchingCount, 0);
        Array.Fill(_stepTraced, false);
        Array.Fill(_pathTriggerCount, 0);
        ResetBhopBlocks();
    }

    private static HookReturnValue<EmptyHookReturn> OnPlayerRunCommandPre(IPlayerRunCommandHookParams      @params,
                                                                          HookReturnValue<EmptyHookReturn> ret)
    {
        var client = @params.Client;
        int slot   = client.Slot;

        _moveTick[slot]++;
        _isFakeClient[slot] = client.IsFakeClient;
        _moved[slot]        = false;
        _collided[slot]     = false;

        _pathTriggerCount[slot] = 0;

        return new ();
    }

    // The acceleration a sub-tick step is part of. Steps split from one accelerate once between them.
    internal static long GetAccelerationStep(PlayerSlot slot, MoveData* mv)
        => _movementExtension.GetAccelerationStep(slot, (nint) mv);
}
