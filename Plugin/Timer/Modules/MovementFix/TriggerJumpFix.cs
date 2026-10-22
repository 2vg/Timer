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
using System.Runtime.InteropServices;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Native;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

// rngfix's trigger jump fix. CS2 lands a player while they still hover up to 2 units above the ground, and triggers fire
// from where the player ends the tick, so landing and jumping straight away skips triggers lying in that gap. After the
// tick, start touching the ones there the game won't touch itself, and end it on the player's next tick, as a physics
// touch of one tick does (and Source 1's touch links did).
internal unsafe partial class MovementFixModule
{
    private const int MaxTriggerHits = 16;

    private static readonly int[]    _landTick   = new int[PlayerSlot.MaxPlayerCount];
    private static readonly Vector[] _landOrigin = new Vector[PlayerSlot.MaxPlayerCount];
    private static readonly bool[]   _landDucked = new bool[PlayerSlot.MaxPlayerCount];

    // By slot: the touches to end on the player's next tick, by handle (a trigger_once removes itself).
    private static readonly uint[] _touchingPawn     = new uint[PlayerSlot.MaxPlayerCount];
    private static readonly uint[] _touchingTriggers = new uint[(int) PlayerSlot.MaxPlayerCount * MaxTriggerHits];
    private static readonly int[]  _touchingCount    = new int[PlayerSlot.MaxPlayerCount];

    // Where TriggerFilterShouldHitEntity writes while a trigger query runs.
    private static nint* _triggerHits;
    private static int   _triggerHitCount;

    private static CTraceFilterVirtualTableDescriptor* _triggerFilterVtable;

    private static void CreateTriggerFilterVtable()
    {
        _triggerFilterVtable = (CTraceFilterVirtualTableDescriptor*) NativeMemory.Alloc((nuint) sizeof(CTraceFilterVirtualTableDescriptor));

        _triggerFilterVtable->Deconstructor   = &TriggerFilterDestructor;
        _triggerFilterVtable->ShouldHitEntity = &TriggerFilterShouldHitEntity;
    }

    private static void FreeTriggerFilterVtable()
    {
        NativeMemory.Free(_triggerFilterVtable);
        _triggerFilterVtable = null;
    }

    private static ReadOnlySpan<byte> DesignerName(nint entity)
    {
        var identity = *(nint*) (entity + 0x10);
        var name     = identity != nint.Zero ? *(byte**) (identity + CEntityIdentity_m_designerName_offset) : null;

        return name == null ? default : MemoryMarshal.CreateReadOnlySpanFromNullTerminated(name);
    }

    // Teleports, boosters, and zones and map logic.
    private static bool IsTrigger(nint entity)
    {
        var classname = DesignerName(entity);

        return classname.SequenceEqual("trigger_teleport"u8)
               || classname.SequenceEqual("trigger_push"u8)
               || classname.SequenceEqual("trigger_multiple"u8)
               || classname.SequenceEqual("trigger_once"u8);
    }

    private static void RecordLanding(int slot, Vector origin, bool ducked)
    {
        _landTick[slot]   = _moveTick[slot];
        _landOrigin[slot] = origin;
        _landDucked[slot] = ducked;
    }

    // Movement has been written back to the pawn by PostThink, so a teleport sticks.
    private static void OnPlayerPostThink(IPlayerThinkForwardParams @params)
    {
        int slot = @params.Client.Slot;

        EndTouches(slot);

        // Reached earlier in the tick than the rest. After a teleport there, the player never stood or landed where the
        // tick ended.
        if (TouchPathTriggers(@params.Pawn, slot))
        {
            return;
        }

        if (_bhopBlocks.Count > 0 && !_isFakeClient[slot])
        {
            ApplyBhopBlocks(@params.Pawn.GetAbsPtr(), slot);
        }

        if (_landTick[slot] != _moveTick[slot])
        {
            return;
        }

        _landTick[slot] = int.MinValue;

        if (_triggerJumpEnabled && _canTriggerJump && !_isFakeClient[slot])
        {
            ApplyTriggerJumpFix(@params.Pawn.GetAbsPtr(), slot);
        }
    }

    private static void ApplyTriggerJumpFix(nint pawn, int slot)
    {
        if (!IsAlive(pawn) || GetMoveType(pawn) != MoveType.Walk || IsInWater(pawn))
        {
            return;
        }

        var filter = stackalloc CTraceFilter[1];

        if (!InitPlayerMovementFilter(filter, pawn))
        {
            return;
        }

        var origin = _landOrigin[slot];
        var height = _landDucked[slot] ? 54.0f : 72.0f;
        var hull   = new TraceShapeRay(new TraceShapeHull { Mins = new (-16, -16, 0), Maxs = new (16, 16, height) });
        var below  = origin;
        below.Z -= LandHeight;

        var trace = stackalloc CGameTrace[1];
        TracePlayerBBox(&origin, &below, &hull, filter, trace);

        if (trace->StartInSolid || trace->Fraction <= 0.0f || trace->Fraction >= 1.0f)
        {
            return;
        }

        // The gap under the hull, reaching 1/32 into the ground for triggers flush with it.
        var bottom   = trace->EndPosition.Z - 0.03125f;
        var gapPoint = new Vector(origin.X, origin.Y, bottom);

        var gap = new TraceShapeRay(new TraceShapeHull
        {
            Mins = new (-16, -16, 0),
            Maxs = new (16, 16, origin.Z - bottom),
        });

        var gapHits  = stackalloc nint[MaxTriggerHits];
        var gapCount = CollectTriggers(&gap, gapPoint, gapHits);

        if (gapCount == 0)
        {
            return;
        }

        var currentHits  = stackalloc nint[MaxTriggerHits];
        var currentCount = -1;

        var handle = GetRefHandle(pawn);

        for (var i = 0; i < gapCount; i++)
        {
            var trigger = gapHits[i];

            if (!CanTouchUnqueried(trigger, pawn, handle))
            {
                continue;
            }

            // Only queried once a trigger could be touched: standing in one, its touching list already rejects it.
            if (currentCount < 0)
            {
                currentCount = CollectGameTouches(pawn, currentHits);
            }

            // CBaseTrigger::StartTouch fires its outputs again for a player it already lists.
            if (Contains(currentHits, currentCount, trigger) || !PassesTriggerFilters(trigger, pawn))
            {
                continue;
            }

            if (!TouchUntilNextTick(slot, trigger, GetRefHandle(trigger), pawn, handle))
            {
                return;
            }
        }
    }

    // What the physics does for a pair that starts touching, ended on the player's next tick. False when the player has
    // as many of these touches as they can.
    private static bool TouchUntilNextTick(int slot, nint trigger, uint triggerHandle, nint pawn, uint pawnHandle)
    {
        if (_touchingCount[slot] >= MaxTriggerHits)
        {
            return false;
        }

        CallTouch(trigger, CBaseEntity_StartTouch_index, pawn);
        CallTouch(trigger, CBaseEntity_Touch_index, pawn);
        CallTouch(pawn, CBaseEntity_StartTouch_index, trigger);
        CallTouch(pawn, CBaseEntity_Touch_index, trigger);

        _touchingPawn[slot] = pawnHandle;
        _touchingTriggers[slot * MaxTriggerHits + _touchingCount[slot]++] = triggerHandle;

        return true;
    }

    // The player's next tick: what the physics does for a pair that stops touching.
    private static void EndTouches(int slot)
    {
        var count = _touchingCount[slot];

        if (count == 0)
        {
            return;
        }

        _touchingCount[slot] = 0;

        var pawn = EntityFromHandle(_touchingPawn[slot]);

        if (pawn == nint.Zero)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var trigger = EntityFromHandle(_touchingTriggers[slot * MaxTriggerHits + i]);

            if (trigger == nint.Zero)
            {
                continue;
            }

            CallTouch(trigger, CBaseEntity_EndTouch_index, pawn);
            CallTouch(pawn, CBaseEntity_EndTouch_index, trigger);
        }
    }

    private static nint EntityFromHandle(uint handle)
        => _entityManager.FindEntityByHandle(new CEntityHandle<IBaseEntity>(handle))?.GetAbsPtr() ?? nint.Zero;

    private static void CallTouch(nint entity, int index, nint other)
        => ((delegate* unmanaged<nint, nint, void>) (*(nint**) entity)[index])(entity, other);

    // Every trigger the hull overlaps at point.
    private static int CollectTriggers(TraceShapeRay* ray, Vector point, nint* hits)
        => CollectTriggers(ray, point, point, hits);

    // Every trigger the hull passes through from start to end, in the order it reaches them (the ones it starts in
    // first). A moving hull reports a trigger it comes within 1/32 of.
    private static int CollectTriggers(TraceShapeRay* ray, Vector start, Vector end, nint* hits)
    {
        var filter = stackalloc CTraceFilter[1];

        *filter = default;

        filter->QueryAttribute = new RnQueryShapeAttr
        {
            m_nInteractsWith  = InteractionLayers.Trigger,
            m_nCollisionGroup = CollisionGroupType.Debris,
            HitTrigger        = true,
        };

        filter->Vtable             = _triggerFilterVtable;
        filter->m_bIterateEntities = true;

        _triggerHits     = hits;
        _triggerHitCount = 0;

        var trace = stackalloc CGameTrace[1];
        CGamePhysicsQueryInterface_TraceShape(g_pPhysicsQuery, ray, &start, &end, filter, trace);

        _triggerHits = null;

        return _triggerHitCount;
    }

    // The physics touches a map trigger (a mesh) the player's hull comes within about 0.1 units of, and a zone (a hull)
    // only on overlap (measured 2026-10). One margin for both, so a map trigger is never touched twice; a zone crossed
    // and ended less than 0.1 units past is touched by neither, as without these fixes.
    private const float GameTouchMargin = 0.1f;

    // The triggers the game touches itself where the player ends the tick.
    private static int CollectGameTouches(nint pawn, nint* hits)
    {
        var service = *(nint*) (pawn + CBasePlayerPawn_m_pMovementServices_offset);
        var height  = service != nint.Zero && *(bool*) (service + CCSPlayer_MovementServices_m_bDucked_offset) ? 54.0f : 72.0f;

        var hull = new TraceShapeRay(new TraceShapeHull
        {
            Mins = new (-16 - GameTouchMargin, -16 - GameTouchMargin, -GameTouchMargin),
            Maxs = new (16 + GameTouchMargin, 16 + GameTouchMargin, height + GameTouchMargin),
        });

        return CollectTriggers(&hull, *CBaseEntity_GetAbsOrigin(pawn), hits);
    }

    private static bool Contains(nint* entities, int count, nint entity)
    {
        for (var i = 0; i < count; i++)
        {
            if (entities[i] == entity)
            {
                return true;
            }
        }

        return false;
    }

    // What the game checks before StartTouch, but the trigger filters and whether it touches the pair itself this tick:
    // reads only.
    private static bool CanTouchUnqueried(nint trigger, nint pawn, uint pawnHandle)
    {
        if (*(bool*) (trigger + CBaseTrigger_m_bDisabled_offset) || IsMarkedForDeletion(trigger) || IsMarkedForDeletion(pawn))
        {
            return false;
        }

        // CUtlVector: the count, then the elements.
        var touching = trigger + CBaseTrigger_m_hTouchingEntities_offset;
        var count    = *(int*) touching;
        var handles  = *(uint**) (touching + 8);

        for (var i = 0; i < count; i++)
        {
            if (handles[i] == pawnHandle)
            {
                return false;
            }
        }

        return true;
    }

    private static bool PassesTriggerFilters(nint trigger, nint pawn)
        => ((delegate* unmanaged<nint, nint, bool>) (*(nint**) trigger)[CBaseTrigger_PassesTriggerFilters_index])(trigger, pawn);

    [UnmanagedCallersOnly]
    private static void TriggerFilterDestructor(nint filter)
    {
    }

    [UnmanagedCallersOnly]
    private static bool TriggerFilterShouldHitEntity(CTraceFilter* filter, nint entity)
    {
        if (entity != nint.Zero && _triggerHits != null && _triggerHitCount < MaxTriggerHits && IsTrigger(entity))
        {
            _triggerHits[_triggerHitCount++] = entity;
        }

        // Keep going, so every trigger gets reported.
        return false;
    }
}
