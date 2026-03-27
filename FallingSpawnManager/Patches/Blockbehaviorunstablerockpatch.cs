using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

#nullable disable

namespace FallingSpawnManager.Patches;

/// <summary>
/// Patches BlockBehaviorUnstableRock.collapseLayer so that it routes through
/// FallingSpawnManager.RequestSpawn instead of directly spawning EntityBlockFalling.
///
/// What changes vs the original:
///   - The manual duplicate-entity guard is removed — FallingSpawnManager handles
///     de-duplication via its pendingPositions HashSet.
///   - world.SpawnEntity(new EntityBlockFalling(...)) is replaced with
///     fsm.RequestSpawn(...).
///   - The three checkCollapsibleNeighbours calls at the end are wrapped in
///     RegisterCallback with staggered delays (300 / 400 / 500 ms) so that
///     cascading collapses don't overflow the callback stack in a single tick.
/// </summary>
public static class BlockBehaviorUnstableRockPatch
{
    // Protected fields of BlockBehaviorUnstableRock — FieldRef gives zero-overhead ref access
    private static readonly AccessTools.FieldRef<BlockBehaviorUnstableRock, AssetLocation> _fallSoundRef =
        AccessTools.FieldRefAccess<BlockBehaviorUnstableRock, AssetLocation>("fallSound");

    private static readonly AccessTools.FieldRef<BlockBehaviorUnstableRock, float> _impactDamageMulRef =
        AccessTools.FieldRefAccess<BlockBehaviorUnstableRock, float>("impactDamageMul");

    private static readonly AccessTools.FieldRef<BlockBehaviorUnstableRock, float> _dustIntensityRef =
        AccessTools.FieldRefAccess<BlockBehaviorUnstableRock, float>("dustIntensity");

    private static readonly AccessTools.FieldRef<BlockBehaviorUnstableRock, Block> _collapsedBlockRef =
        AccessTools.FieldRefAccess<BlockBehaviorUnstableRock, Block>("collapsedBlock");

    /// <summary>
    /// Prefix returns false → original method body is skipped entirely.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(BlockBehaviorUnstableRock), "collapseLayer")]
    public static bool collapseLayer_Prefix(
        BlockBehaviorUnstableRock __instance,
        IWorldAccessor world,
        IOrderedEnumerable<BlockPos> yorderedPositions,
        int y)
    {
        
        FallingSpawnManager fsm = world.Api.ModLoader.GetModSystem<FallingSpawnManager>();

        AssetLocation fallSound = _fallSoundRef(__instance);
        float impactDamageMul = _impactDamageMulRef(__instance);
        float dustIntensity = _dustIntensityRef(__instance);

        Block block;
        BlockBehaviorUnstableRock bh;

        foreach (BlockPos pos in yorderedPositions)
        {
            if (pos.Y < y)
                continue;

            if (pos.Y > y)
            {
                // Capture y for the lambda — pos.Y is the next layer's Y
                int nextY = pos.Y;
                world.Api.Event.RegisterCallback(
                    (dt) => collapseLayer_Prefix(__instance, world, yorderedPositions, nextY),
                    200);
                return false; // skip original
            }

            block = world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Solid);
            bh = block.GetBehavior<BlockBehaviorUnstableRock>();

            if (bh == null || fsm == null)
                continue;

            fsm.RequestSpawn(
                _collapsedBlockRef(bh),
                world.BlockAccessor.GetBlockEntity(pos),
                pos,
                fallSound: fallSound,
                impactDamageMul: impactDamageMul,
                canFallSideways: true,
                dustIntensity: dustIntensity
            );
        }

        // Stagger the neighbour-collapse checks to avoid callback stack overflow
        // when a large cave-in triggers many cascading collapses in the same tick.
        BlockPos firstpos = yorderedPositions.First();
        for (int i = 0; i < 3; i++)
        {
            BlockPos npos = firstpos.AddCopy(
                world.Rand.Next(17) - 8, 0, world.Rand.Next(17) - 8);

            int delay = 300 + i * 100; // 300, 400, 500 ms
            world.Api.Event.RegisterCallback(
                (dt) => BlockBehaviorUnstableRockHelper.CheckCollapsibleNeighbours(__instance, world, npos),
                delay);
        }

        return false; // skip original
    }
}

/// <summary>
/// Thin helper that exposes the protected checkCollapsibleNeighbours method
/// to the patch lambda without using reflection on every callback invocation.
/// </summary>
internal static class BlockBehaviorUnstableRockHelper
{
    private static readonly Action<BlockBehaviorUnstableRock, IWorldAccessor, BlockPos> _checkCollapsibleNeighbours =
        AccessTools.MethodDelegate<Action<BlockBehaviorUnstableRock, IWorldAccessor, BlockPos>>(
            AccessTools.Method(typeof(BlockBehaviorUnstableRock), "checkCollapsibleNeighbours"));

    public static void CheckCollapsibleNeighbours(
        BlockBehaviorUnstableRock instance,
        IWorldAccessor world,
        BlockPos pos)
        => _checkCollapsibleNeighbours(instance, world, pos);
}