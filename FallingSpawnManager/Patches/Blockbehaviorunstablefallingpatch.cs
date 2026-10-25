using FSMMgr.Managers;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace FSMMgr.Patches;

/// <summary>
/// Перехватывает падение сыпучих блоков (песок, гравий и т. п.), чтобы спавнить
/// сущность падающего блока через наш менеджер вместо прямого вызова оригинала.
/// </summary>
public static class BlockBehaviorUnstableFallingPatch
{
    private static readonly AccessTools.FieldRef<BlockBehaviorUnstableFalling, AssetLocation> _fallSoundRef =
        AccessTools.FieldRefAccess<BlockBehaviorUnstableFalling, AssetLocation>("fallSound");

    private static readonly AccessTools.FieldRef<BlockBehaviorUnstableFalling, float> _impactDamageMulRef =
        AccessTools.FieldRefAccess<BlockBehaviorUnstableFalling, float>("impactDamageMul");

    private static readonly AccessTools.FieldRef<BlockBehaviorUnstableFalling, AssetLocation> _variantAfterFalling =
        AccessTools.FieldRefAccess<BlockBehaviorUnstableFalling, AssetLocation>("variantAfterFalling");

    private static readonly AccessTools.FieldRef<BlockBehaviorUnstableFalling, float> _dustIntensityRef =
        AccessTools.FieldRefAccess<BlockBehaviorUnstableFalling, float>("dustIntensity");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BlockBehaviorUnstableFalling), "createFallingBlock")]
    public static bool createFallingBlock_Prefix(
        BlockBehaviorUnstableFalling __instance,
        IWorldAccessor world,
        BlockPos ourPos)
    {
        // Менеджер выключен или идёт его Dispose: отрабатывает ванильный код.
        if (!FallingSpawnManager.IsActive)
            return true;

        // Исходный блок на позиции. Падающий вариант (rock-granite -> crackedrock-granite) хранится отдельно:
        // по исходному блоку заявка потом проверяется, по варианту блок ставится.
        Block currentBlock = world.BlockAccessor.GetBlock(ourPos);

        // Тот же чек, что в ванилле: блок на позиции уже не наш.
        if (__instance.block != currentBlock)
            return false;

        // Ванильный FallingEntityAlreadyExists, но за O(1) через менеджер.
        if (FallingSpawnManager.IsPositionActiveOrPending(ourPos))
            return false;

        Block fallingBlock = currentBlock;
        AssetLocation variant = _variantAfterFalling(__instance);
        if (variant != null)
            fallingBlock = world.BlockAccessor.GetBlock(variant);

        BlockEntity be = world.BlockAccessor.GetBlockEntity(ourPos);

        FallingSpawnManager.RequestSpawn(
            fallingBlock,
            be,
            ourPos,
            _fallSoundRef(__instance),
            _impactDamageMulRef(__instance),
            canFallSideways: true,      // в оригинале всегда true
            _dustIntensityRef(__instance),
            doRemoveBlock: true,
            positionOffset: null
        );

        return false; // тело оригинального метода больше не выполняется
    }
}