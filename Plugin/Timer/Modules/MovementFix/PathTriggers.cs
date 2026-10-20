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
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

// The physics touches triggers once per tick, by overlap where the player ends it, so a fast player passes thin triggers
// without touching them, whatever the tickrate. Each movement step traces the hull along its path; after the tick,
// start touching the triggers it passed that the game won't touch itself, in the order it reached them, and end it on the
// player's next tick, as the trigger jump fix does.
internal unsafe partial class MovementFixModule
{
    // Longer traces take another path in vphysics2 that misplaces hull triggers (measured 2026-10). A step moves this far
    // only above 32768 u/s, so a longer one had its origin set rather than moved.
    private const float MaxPathTraceLength = 512.0f;

    // By slot: where the running movement step started, when it is traced.
    private static readonly Vector[] _stepStart  = new Vector[PlayerSlot.MaxPlayerCount];
    private static readonly bool[]   _stepTraced = new bool[PlayerSlot.MaxPlayerCount];

    // By slot: the triggers the tick's movement passed, by handle, in the order it reached them.
    private static readonly uint[] _pathTriggers     = new uint[(int) PlayerSlot.MaxPlayerCount * MaxTriggerHits];
    private static readonly int[]  _pathTriggerCount = new int[PlayerSlot.MaxPlayerCount];

    private static void OnPathTriggersMovePre(IPlayerProcessMoveForwardParams param)
    {
        int slot = param.Client.Slot;

        _stepTraced[slot] = _pathTriggersEnabled && _canTriggerJump && !_isFakeClient[slot];

        if (_stepTraced[slot])
        {
            _stepStart[slot] = param.Info->AbsOrigin;
        }
    }

    private static void OnPathTriggersMovePost(IPlayerProcessMoveForwardParams param)
    {
        int slot = param.Client.Slot;

        if (!_stepTraced[slot])
        {
            return;
        }

        _stepTraced[slot] = false;

        var pawn = param.Pawn.GetAbsPtr();

        if (!IsAlive(pawn) || GetMoveType(pawn) != MoveType.Walk)
        {
            return;
        }

        var start  = _stepStart[slot];
        var end    = param.Info->AbsOrigin;
        var length = (end - start).LengthSqr();

        // Standing still, the game's own touches are all there is.
        if (length < 1e-6f || length > MaxPathTraceLength * MaxPathTraceLength)
        {
            return;
        }

        CollectPathTriggers(slot, param.Service.GetAbsPtr(), start, end);
    }

    // With the hull the step ended with.
    private static void CollectPathTriggers(int slot, nint service, Vector start, Vector end)
    {
        var hull  = CreatePlayerHull(service);
        var hits  = stackalloc nint[MaxTriggerHits];
        var count = CollectTriggers(&hull, start, end, hits);

        var collected = _pathTriggers.AsSpan(slot * MaxTriggerHits, MaxTriggerHits);

        for (var i = 0; i < count && _pathTriggerCount[slot] < MaxTriggerHits; i++)
        {
            var handle = GetRefHandle(hits[i]);

            if (!collected[.._pathTriggerCount[slot]].Contains(handle))
            {
                collected[_pathTriggerCount[slot]++] = handle;
            }
        }
    }

    // True when a teleport took the player off the path the tick's movement left them at the end of.
    private static bool TouchPathTriggers(IPlayerPawn player, int slot)
    {
        var count = _pathTriggerCount[slot];

        if (count == 0)
        {
            return false;
        }

        _pathTriggerCount[slot] = 0;

        var pawn = player.GetAbsPtr();

        if (!IsAlive(pawn))
        {
            return false;
        }

        var gameHits  = stackalloc nint[MaxTriggerHits];
        var gameCount = CollectGameTouches(pawn, gameHits);

        var origin = *CBaseEntity_GetAbsOrigin(pawn);
        var handle = GetRefHandle(pawn);

        for (var i = 0; i < count; i++)
        {
            var triggerHandle = _pathTriggers[slot * MaxTriggerHits + i];
            var trigger       = EntityFromHandle(triggerHandle);

            // CBaseTrigger::StartTouch fires its outputs again for a player it already lists.
            if (trigger == nint.Zero
                || Contains(gameHits, gameCount, trigger)
                || !CanTouch(trigger, pawn, handle)
                || IsMarkedForDeletion(trigger)
                || IsMarkedForDeletion(pawn))
            {
                continue;
            }

            if (!TouchUntilNextTick(slot, trigger, triggerHandle, pawn, handle))
            {
                return false;
            }

            // The triggers reached after it are on a path the player no longer took.
            if (*CBaseEntity_GetAbsOrigin(pawn) != origin)
            {
                return true;
            }
        }

        return false;
    }
}
