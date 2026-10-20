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
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Modules.MapInfo;
using Source2Surf.Timer.Native;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

// mpbhops_but_working, on bhop maps. A bhop platform (a func_door or func_button a touch moves) moves for everyone, so
// it stays still instead, and each player gets what it would have done to them: a teleport when they stay on one that
// drops into a trigger_teleport, or land on it again soon after, and its speed when they jump off one that rises.
internal unsafe partial class MovementFixModule
{
    private const float BlockTeleportDelay = 0.06f;
    private const float BlockCooldown      = 1.10f;

    private const uint SF_DOOR_PTOUCH            = 1024;
    private const uint SF_DOOR_SILENT            = 4096;
    private const uint SF_BUTTON_DONTMOVE        = 1;
    private const uint SF_BUTTON_TOUCH_ACTIVATES = 256;

    // By handle. Teleport is the trigger_teleport a dropping block falls into; 0 for a rising one.
    private readonly record struct BhopBlock(uint Teleport, float Speed);

    private enum BlockVerdict
    {
        Dropping,
        Rising,
        NotTouched,
        NoTeleport,
        Still,
        NoReturn,
    }

    private static readonly Dictionary<uint, BhopBlock> _bhopBlocks    = [];
    private static readonly List<uint>                  _pendingBlocks = [];
    private static          bool                        _blocksActive;
    private static          bool                        _blocksQueued;

    private static readonly uint[]  _lastGround = new uint[PlayerSlot.MaxPlayerCount];
    private static readonly uint[]  _lastBlock  = new uint[PlayerSlot.MaxPlayerCount];
    private static readonly float[] _punishTime = new float[PlayerSlot.MaxPlayerCount];

    private static IGlobalVars? _globalVars;

    // The map's mode is known from its activation on, and every teleport has spawned by the frame after it.
    public void OnGameActivate()
        => _bridge.ModSharp.InvokeFrameAction(() =>
        {
            _blocksActive = true;
            _pendingBlocks.Clear();

            foreach (var classname in (ReadOnlySpan<string>) ["func_door", "func_button"])
            {
                IBaseEntity? entity = null;

                while ((entity = _entityManager.FindEntityByClassname(entity, classname)) != null)
                {
                    _pendingBlocks.Add(entity.Handle.GetValue());
                }
            }

            TrackPendingBlocks();
        });

    // A round restart's, once the map is up.
    public void OnEntitySpawned(IBaseEntity entity)
    {
        if (!_blocksActive || entity.Classname is not ("func_door" or "func_button"))
        {
            return;
        }

        _pendingBlocks.Add(entity.Handle.GetValue());

        if (!_blocksQueued)
        {
            _blocksQueued = true;

            _bridge.ModSharp.InvokeFrameAction(() =>
            {
                _blocksQueued = false;
                TrackPendingBlocks();
            });
        }
    }

    public void OnEntityDeleted(IBaseEntity entity)
        => _bhopBlocks.Remove(entity.Handle.GetValue());

    private static void ResetBhopBlocks()
    {
        _bhopBlocks.Clear();
        _pendingBlocks.Clear();
        _blocksActive = false;
        _blocksQueued = false;
        Array.Fill(_lastGround, uint.MaxValue);
        Array.Fill(_lastBlock, uint.MaxValue);
        Array.Fill(_punishTime, 0.0f);
    }

    private void TrackPendingBlocks()
    {
        if (_pendingBlocks.Count == 0 || !_mpbhopsEnabled || !_canTriggerJump || _mapInfo.GetCurrentGameMode() != EGameMode.Bhop)
        {
            _pendingBlocks.Clear();

            return;
        }

        Span<int> verdicts = stackalloc int[Enum.GetValues<BlockVerdict>().Length];

        foreach (var handle in _pendingBlocks)
        {
            if (_entityManager.FindEntityByHandle(new CEntityHandle<IBaseEntity>(handle)) is { IsValidEntity: true } block)
            {
                verdicts[(int) TrackBhopBlock(block)]++;
            }
        }

        _pendingBlocks.Clear();

        _logger.LogInformation("mpbhops: {Dropping} dropping and {Rising} rising bhop platforms kept still; skipped {NotTouched} not moved by touch, "
                               + "{NoTeleport} dropping into no trigger_teleport, {Still} not moving, {NoReturn} rising for good",
                               verdicts[(int) BlockVerdict.Dropping],
                               verdicts[(int) BlockVerdict.Rising],
                               verdicts[(int) BlockVerdict.NotTouched],
                               verdicts[(int) BlockVerdict.NoTeleport],
                               verdicts[(int) BlockVerdict.Still],
                               verdicts[(int) BlockVerdict.NoReturn]);
    }

    private static BlockVerdict TrackBhopBlock(IBaseEntity entity)
    {
        var button = entity.Classname == "func_button";
        var touch  = button ? SF_BUTTON_TOUCH_ACTIVATES : SF_DOOR_PTOUCH;
        var flags  = entity.SpawnFlags;

        if ((flags & touch) == 0)
        {
            return BlockVerdict.NotTouched;
        }

        var start    = entity.GetNetVar<Vector>("m_vecPosition1");
        var end      = entity.GetNetVar<Vector>("m_vecPosition2");
        var teleport = 0u;

        if (start.Z > end.Z)
        {
            if (entity.GetCollisionProperty() is not { } collision
                || (teleport = FindTeleportBetween(start, end, collision.Mins, collision.Maxs)) == 0)
            {
                return BlockVerdict.NoTeleport;
            }
        }
        else if (start.Z == end.Z)
        {
            return BlockVerdict.Still;
        }
        // Rising, and back down after a while: a booster.
        else if (entity.GetNetVar<float>("m_flWait") <= 0.0f)
        {
            return BlockVerdict.NoReturn;
        }

        entity.SpawnFlags = button ? (flags & ~touch) | SF_BUTTON_DONTMOVE : (flags & ~touch) | SF_DOOR_SILENT;
        entity.AcceptInput("Lock");

        _bhopBlocks[entity.Handle.GetValue()] = new (teleport, entity.GetNetVar<float>("m_flSpeed"));

        return teleport != 0 ? BlockVerdict.Dropping : BlockVerdict.Rising;
    }

    // The trigger_teleport the block would drop into.
    private static uint FindTeleportBetween(Vector start, Vector end, Vector mins, Vector maxs)
    {
        var low  = new Vector(MathF.Min(end.X - start.X, 0.0f), MathF.Min(end.Y - start.Y, 0.0f), end.Z - start.Z);
        var high = new Vector(MathF.Max(end.X - start.X, 0.0f), MathF.Max(end.Y - start.Y, 0.0f), 0.0f);
        var ray  = new TraceShapeRay(new TraceShapeHull { Mins = low + mins, Maxs = high + maxs });
        var hits = stackalloc nint[MaxTriggerHits];
        var count = CollectTriggers(&ray, start, hits);

        for (var i = 0; i < count; i++)
        {
            if (DesignerName(hits[i]).SequenceEqual("trigger_teleport"u8))
            {
                return GetRefHandle(hits[i]);
            }
        }

        return 0;
    }

    // At the end of the player's tick, standing on what the movement left them on.
    private static void ApplyBhopBlocks(nint pawn, int slot)
    {
        var ground = *(uint*) (pawn + CBaseEntity_m_hGroundEntity_offset);

        _lastGround[slot] = ground;

        if (!_bhopBlocks.TryGetValue(ground, out var block)
            || block.Teleport == 0
            || _globalVars is not { } globals
            || !IsAlive(pawn)
            || GetMoveType(pawn) != MoveType.Walk)
        {
            return;
        }

        var time = globals.CurTime;
        var diff = time - _punishTime[slot];

        if (_lastBlock[slot] != ground || diff > BlockCooldown)
        {
            _lastBlock[slot]  = ground;
            _punishTime[slot] = time + BlockTeleportDelay;

            return;
        }

        if (diff <= BlockTeleportDelay || _touchingCount[slot] >= MaxTriggerHits)
        {
            return;
        }

        var teleport = EntityFromHandle(block.Teleport);

        if (teleport == nint.Zero)
        {
            return;
        }

        _lastBlock[slot] = uint.MaxValue;

        var handle = GetRefHandle(pawn);

        if (!CanTouch(teleport, pawn, handle) || IsMarkedForDeletion(teleport))
        {
            return;
        }

        // As the block dropping into it would.
        TouchUntilNextTick(slot, teleport, block.Teleport, pawn, handle);
    }

    // Jumping off a rising block: its speed, as when it rises under them.
    private void OnBhopBlockJump(IGameEvent e)
    {
        if (_bhopBlocks.Count == 0
            || e.GetPlayerController("userid") is not { IsValidEntity: true, IsFakeClient: false } controller
            || !_bhopBlocks.TryGetValue(_lastGround[(int) controller.PlayerSlot], out var block)
            || block.Teleport != 0
            || controller.GetPlayerPawn() is not { IsAlive: true } pawn)
        {
            return;
        }

        var baseVelocity = pawn.BaseVelocity;
        baseVelocity.Z    += block.Speed;
        pawn.BaseVelocity =  baseVelocity;
    }
}
