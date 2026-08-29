using HarmonyLib;
using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

#nullable disable

namespace FallingSpawnManager.Patches;

/// <summary>
/// Перехватывает BlockBehaviorUnstableRock.collapseLayer и направляет спавн через
/// FallingSpawnManager.RequestSpawn вместо прямого создания EntityBlockFalling.
///
/// Чем отличается от оригинала:
///   - Убран ручной контроль дубликатов — дедупликацию делает FallingSpawnManager
///     своим HashSet pendingPositions.
///   - world.SpawnEntity(new EntityBlockFalling(...)) заменён на fsm.RequestSpawn(...).
///   - Три вызова checkCollapsibleNeighbours в конце обёрнуты в RegisterCallback с
///     задержками 300 / 400 / 500 мс, чтобы цепные обвалы не переполняли стек
///     колбэков за один тик.
/// </summary>
public static class BlockBehaviorUnstableRockPatch
{
    // Защищённые поля BlockBehaviorUnstableRock — FieldRef даёт доступ без оверхеда
    private static readonly AccessTools.FieldRef<BlockBehaviorUnstableRock, AssetLocation> _fallSoundRef =
        AccessTools.FieldRefAccess<BlockBehaviorUnstableRock, AssetLocation>("fallSound");

    private static readonly AccessTools.FieldRef<BlockBehaviorUnstableRock, float> _impactDamageMulRef =
        AccessTools.FieldRefAccess<BlockBehaviorUnstableRock, float>("impactDamageMul");

    private static readonly AccessTools.FieldRef<BlockBehaviorUnstableRock, float> _dustIntensityRef =
        AccessTools.FieldRefAccess<BlockBehaviorUnstableRock, float>("dustIntensity");

    private static readonly AccessTools.FieldRef<BlockBehaviorUnstableRock, Block> _collapsedBlockRef =
        AccessTools.FieldRefAccess<BlockBehaviorUnstableRock, Block>("collapsedBlock");

    /// <summary>
    /// Вернув false, мы полностью пропускаем тело оригинального метода.
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
                // Захватываем nextY для лямбды — pos.Y это Y следующего слоя
                int nextY = pos.Y;
                world.Api.Event.RegisterCallback(
                    (dt) => collapseLayer_Prefix(__instance, world, yorderedPositions, nextY),
                    200);
                return false; // не даём отработать оригиналу
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

        // Сдвигаем проверки соседних ярусов по времени, чтобы цепной обвал не переполнил
        // стек колбэков, если сразу сыпется целый пласт.
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

        return false; // не даём отработать оригиналу
    }
}

/// <summary>
/// Тонкая оболочка вокруг защищённого checkCollapsibleNeighbours. Нужна, чтобы лямбда
/// в колбэке могла вызвать метод без рефлексии на каждом вызове.
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