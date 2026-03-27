using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace FallingSpawnManager.Patches;

public static class BlockBehaviorUnstableFallingPatch
{
    // Поля из оригинального класса
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
        var fsm = world.Api.ModLoader.GetModSystem<FallingSpawnManager>();
        if (fsm == null)
            return true; // если менеджер не загружен, пусть работает оригинал


        Block block = world.BlockAccessor.GetBlock(ourPos);
        
        if (_variantAfterFalling(__instance) != (AssetLocation)null)
            block = world.BlockAccessor.GetBlock(_variantAfterFalling(__instance));

        BlockEntity be = world.BlockAccessor.GetBlockEntity(ourPos);



        // Заменяем спавн на вызов нашего менеджера
        fsm.RequestSpawn(
            block,
            be,
            ourPos,
            _fallSoundRef(__instance),
            _impactDamageMulRef(__instance),
            canFallSideways: true,      // в оригинале всегда true
            _dustIntensityRef(__instance),
            doRemoveBlock: true,
            positionOffset: null
        );

        return false;
    }

  
}