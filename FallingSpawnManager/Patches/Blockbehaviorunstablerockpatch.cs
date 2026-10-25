using FSMMgr.Managers;
using HarmonyLib;
using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

#nullable disable

namespace FSMMgr.Patches;

/// <summary>
/// Перехватывает BlockBehaviorUnstableRock.collapseLayer и направляет спавн через
/// FallingSpawnManager.RequestSpawn вместо прямого создания EntityBlockFalling.
///
/// Отличия от оригинала:
///   - Дедупликацию делает FallingSpawnManager (pendingPositions).
///   - Проверки соседей в конце разнесены RegisterCallback'ами на 300 / 400 / 500 мс.
///   - Цепочка обрывается, если в слое нет ничего нового. В ванили заспавненная сущность убирает
///     камень сразу, и один регион не находится дважды, цепочка кончается за несколько уровней.
///     Камень в очереди остаётся на месте (до разрешения падения), поэтому каждый вложенный вызов
///     находил тот же регион заново, а RequestSpawn лишь отвечал "уже в очереди". На тонком слое
///     13x13 это давало вложенность до 108 и 2592 вызовов collapseLayer за один обвал.
///     Теперь слой, где все камни уже ждут или падают, цепочку дальше не передаёт.
/// </summary>
public static class BlockBehaviorUnstableRockPatch
{
    private static readonly AccessTools.FieldRef<BlockBehaviorUnstableRock, AssetLocation> _fallSoundRef =
        AccessTools.FieldRefAccess<BlockBehaviorUnstableRock, AssetLocation>("fallSound");

    private static readonly AccessTools.FieldRef<BlockBehaviorUnstableRock, float> _impactDamageMulRef =
        AccessTools.FieldRefAccess<BlockBehaviorUnstableRock, float>("impactDamageMul");

    private static readonly AccessTools.FieldRef<BlockBehaviorUnstableRock, float> _dustIntensityRef =
        AccessTools.FieldRefAccess<BlockBehaviorUnstableRock, float>("dustIntensity");

    private static readonly AccessTools.FieldRef<BlockBehaviorUnstableRock, Block> _collapsedBlockRef =
        AccessTools.FieldRefAccess<BlockBehaviorUnstableRock, Block>("collapsedBlock");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BlockBehaviorUnstableRock), "collapseLayer")]
    public static bool collapseLayer_Prefix(
        BlockBehaviorUnstableRock __instance,
        IWorldAccessor world,
        IOrderedEnumerable<BlockPos> yorderedPositions,
        int y)
    {
        // Менеджера нет, выключен или идёт Dispose: ваниль. Раньше при fsm == null слой молча пропускался,
        // и камни не обваливались вообще.
        if (!FallingSpawnManager.IsActive)
            return true;

        AssetLocation fallSound = _fallSoundRef(__instance);
        float impactDamageMul = _impactDamageMulRef(__instance);
        float dustIntensity = _dustIntensityRef(__instance);

        bool anyNew = false;

        foreach (BlockPos pos in yorderedPositions)
        {
            if (pos.Y < y)
                continue;

            if (pos.Y > y)
            {
                // Захватываем nextY для лямбды: pos.Y — это Y следующего слоя
                int nextY = pos.Y;
                world.Api.Event.RegisterCallback(
                    (dt) => collapseLayer_Prefix(__instance, world, yorderedPositions, nextY),
                    200);
                return false;
            }

            Block block = world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Solid);
            BlockBehaviorUnstableRock bh = block.GetBehavior<BlockBehaviorUnstableRock>();

            if (bh == null)
                continue;

            // true только для новой заявки. Камни, которые уже ждут или падают, ничего нового не дают.
            if (FallingSpawnManager.RequestSpawn(
                    _collapsedBlockRef(bh),
                    world.BlockAccessor.GetBlockEntity(pos),
                    pos,
                    fallSound: fallSound,
                    impactDamageMul: impactDamageMul,
                    canFallSideways: true,
                    dustIntensity: dustIntensity))
            {
                anyNew = true;
            }
        }

        // В слое нет ничего нового: цепочку проверок соседей не продолжаем.
        if (!anyNew)
            return false;

        // Проверки соседних ярусов сдвигаем по времени, чтобы цепной обвал не переполнил стек колбэков.
        BlockPos firstpos = yorderedPositions.First();
        for (int i = 0; i < 3; i++)
        {
            BlockPos npos = firstpos.AddCopy(
                world.Rand.Next(17) - 8, 0, world.Rand.Next(17) - 8);

            int delay = 300 + i * 100; // 300, 400, 500 мс
            world.Api.Event.RegisterCallback(
                (dt) => BlockBehaviorUnstableRockHelper.CheckCollapsibleNeighbours(__instance, world, npos),
                delay);
        }

        return false;
    }
}

/// <summary>
/// Тонкая оболочка вокруг защищённого checkCollapsibleNeighbours, чтобы лямбда в колбэке
/// вызывала его без рефлексии на каждом вызове.
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