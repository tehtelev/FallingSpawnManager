using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

#nullable disable

namespace FallingSpawnManager.Patches;

/// <summary>
/// Патчи Harmony для EntityBlockFalling:
///   - В Initialize ставим AlwaysActive = true и нормализуем SimulationRange.
///   - Периодически проверяем, не ушёл ли игрок рядом: если никого нет — блок
///     падает мгновенно, а сущность уничтожается, чтобы не оставалось «фантомов».
///
/// Статические SimulateInstantFall / SpawnDrops сделаны публичными, чтобы
/// FallingSpawnManager (отдельный мод) мог их вызывать без рефлексии.
/// </summary>
public static class EntityBlockFallingPatch
{
    // -------------------------------------------------------------------------
    //  Доступ к приватным полям через рефлексию (FieldRef даёт доступ без оверхеда)
    // -------------------------------------------------------------------------

    private static readonly AccessTools.FieldRef<EntityBlockFalling, bool> _fallHandledRef =
        AccessTools.FieldRefAccess<EntityBlockFalling, bool>("fallHandled");

    private static readonly AccessTools.FieldRef<EntityBlockFalling, ItemStack[]> _dropsRef =
        AccessTools.FieldRefAccess<EntityBlockFalling, ItemStack[]>("drops");

    // -------------------------------------------------------------------------
    //  Состояние на каждый инстанс, которое нельзя добавить как нормальное поле
    // -------------------------------------------------------------------------

    /// <summary> Доп. данные, которые мы вешаем на каждый инстанс EntityBlockFalling. </summary>
    private sealed class ExtraData
    {
        public long LastPlayerCheckMs;
    }

    // ConditionalWeakTable хранит записи, пока жив ключ — очищать на despawn не нужно
    private static readonly ConditionalWeakTable<EntityBlockFalling, ExtraData> _extra = new();

    private const int PlayerCheckIntervalMs = 2000;

    // -------------------------------------------------------------------------
    //  Патч 1 — Initialize
    //
    //  Две правки в одном postfix (он выполняется ПОСЛЕ base.Initialize, поэтому
    //  наши побеждают):
    //
    //  a) AlwaysActive = true
    //     Проще и дешевле, чем патчить геттер базового класса: просто пишем свойство
    //     напрямую — бэкинг-поле задаётся один раз в момент спавна.
    //
    //  b) SimulationRange = GlobalConstants.DefaultSimulationRange
    //     В оригинале пишется (int)(0.75f * DefaultSimulationRange). Полный дефолтный
    //     диапазон физика прорабатывает на том же расстоянии, что сервер уже следит за
    //     сущностями — лишних затрат нет, зато проверка близости игрока теперь срабатывает,
    //     когда все игроки покидают область.
    // -------------------------------------------------------------------------

    [HarmonyPostfix]
    [HarmonyPatch(typeof(EntityBlockFalling), "Initialize")]
    public static void EntityBlockFalling_Initialize_Postfix(
        EntityBlockFalling __instance,
        EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
    {

        __instance.AlwaysActive = true;
        __instance.SimulationRange = GlobalConstants.DefaultSimulationRange;
    }

    // -------------------------------------------------------------------------
    //  Патч 2 — OnGameTick: проверка близости игрока
    //
    //  Раз в PlayerCheckIntervalMs мс проверяем, кто-то всё ещё рядом. Если нет —
    //  прогоняем мгновенное падение и убиваем сущность. Так блоки не накапливаются
    //  в загруженных, но безлюдных чанках.
    //
    //  Postfix вместо транспайлера держит патч простым и устойчивым к обновлениям игры.
    //  Единственный минус — остальное тело OnGameTick за этот тик уже отработает;
    //  но это неважно, потому что сущность уничтожается сразу после.
    // -------------------------------------------------------------------------

    [HarmonyPostfix]
    [HarmonyPatch(typeof(EntityBlockFalling), "OnGameTick")]
    public static void EntityBlockFalling_OnGameTick_Postfix(
        EntityBlockFalling __instance,
        float dt)
    {

        // Только сервер; пропускаем мёртвые или уже обработанные сущности
        if (__instance.Api?.Side != EnumAppSide.Server) return;
        if (!__instance.Alive) return;
        if (_fallHandledRef(__instance)) return;

        var data = _extra.GetOrCreateValue(__instance);
        long now = __instance.Api.World.ElapsedMilliseconds;

        if (now - data.LastPlayerCheckMs < PlayerCheckIntervalMs) return;
        data.LastPlayerCheckMs = now;

        if (!IsPlayerNearby(__instance))
            FallNow(__instance);
    }

    // -------------------------------------------------------------------------
    //  Вспомогательные методы
    // -------------------------------------------------------------------------

    private static bool IsPlayerNearby(EntityBlockFalling entity)
    {
        var sapi = entity.Api as ICoreServerAPI;
        // Пересчитываем радиус так же, как в FallingSpawnManager.RequestSpawn
        int range = (sapi?.World.DefaultEntityTrackingRange ?? 8) * GlobalConstants.ChunkSize;
        Vec3d pos = entity.Pos.XYZ;

        foreach (IPlayer player in entity.Api.World.AllOnlinePlayers)
        {
            EntityPlayer eplr = player.Entity;
            if (eplr != null && eplr.Pos.InRangeOf(pos, range * range, range))
                return true;
        }
        return false;
    }

    private static void FallNow(EntityBlockFalling entity)
    {
        // Защита от повторного входа — OnFallToGround мог поставить этот флаг в тот же тик
        if (_fallHandledRef(entity)) return;
        _fallHandledRef(entity) = true;

        ItemStack[] drops = _dropsRef(entity);

        SimulateInstantFall(
            entity.Api.World,
            entity.Block,
            entity.removedBlockentity,
            entity.initialPos,
            drops,
            doRemoveBlock: false   // блок уже удалён при спавне сущности, не пытаемся удалить его снова
        );

        entity.Die(EnumDespawnReason.Removed);
    }

    // =========================================================================
    //  Публичные статические utilities
    //  (используются FallingSpawnManager и FallNow выше)
    // =========================================================================

    /// <summary>
    /// Выбрасывает стак предметов блока и, если блочный объект был контейнером,
    /// его инвентарь в центре
    /// </summary>
    public static void SpawnDrops(
        IWorldAccessor world,
        BlockPos pos,
        ItemStack[] drops,
        BlockEntity be)
    {
        Vec3d dpos = pos.ToVec3d().Add(0.5, 0.5, 0.5);

        if (drops != null)
        {
            foreach (ItemStack drop in drops)
                world.SpawnItemEntity(drop, dpos);
        }

        if (be is IBlockEntityContainer bec)
            bec.DropContents(dpos);
    }

    /// <summary>
    /// Мгновенно проходит траекторию падения без создания сущности.
    /// Идёт вниз, пока не найдёт твёрдую поверхность, затем либо ставит блок, либо
    /// выбрасывает предметы.  Используется для блоков за пределами видимости игроков.
    /// </summary>
    /// <param name="world">Доступ к миру.</param>
    /// <param name="block">Падающий блок.</param>
    /// <param name="be">Блочный объект,附着 прикреплённый к блоку (может быть null).</param>
    /// <param name="startPos">Позиция, с которой блок упал.</param>
    /// <param name="drops">Заранее вычисленные предметы (могут быть null).</param>
    /// <param name="doRemoveBlock">
    ///     Если true — сначала удаляет блок из <paramref name="startPos"/> (с проверкой
    ///     корректности). Передавать false, если блок уже был удалён.
    /// </param>
    public static void SimulateInstantFall(
        IWorldAccessor world,
        Block block,
        BlockEntity be,
        BlockPos startPos,
        ItemStack[] drops,
        bool doRemoveBlock)
    {
        if (doRemoveBlock)
        {
            // Проверка безопасности: блок мог измениться за время ожидания в очереди
            if (world.BlockAccessor.GetBlock(startPos) != block)
                return;
            world.BlockAccessor.SetBlock(0, startPos);
        }

        BlockPos finalPos = startPos.Copy();

        // Серилизуем блочный объект один раз, чтобы обработчики CanAcceptFallOnto / OnFallOnto
        // могли осмотреть его данные.
        TreeAttribute beTree = null;
        if (be != null)
        {
            beTree = new TreeAttribute();
            be.ToTreeAttributes(beTree);
        }

        int worldHeight = world.BlockAccessor.MapSizeY;

        // Идём вниз по проходимым блокам (воздух, вода, листва и т. п.)
        for (int i = 0; i < worldHeight; i++)
        {
            BlockPos belowPos = finalPos.DownCopy();
            Block belowBlock = world.BlockAccessor.GetMostSolidBlock(belowPos);

            // Даём целевому блоку обработать приземление (например, воронка, рыхлая земля)
            if (belowBlock.CanAcceptFallOnto(world, belowPos, block, beTree))
            {
                belowBlock.OnFallOnto(world, belowPos, block, beTree);
                return;
            }

            if (belowBlock.Replaceable >= 6000 || belowBlock.IsLiquid())
                finalPos = belowPos;  // проходимый — продолжаем падать
            else
                break;               // твёрдый — останавливаемся
        }

        // Проверяем позицию приземления: под ней должна быть твёрдая опора, а в finalPos — свободное место
        Block targetBlock = world.BlockAccessor.GetBlock(finalPos);
        Block supportBlock = world.BlockAccessor.GetMostSolidBlock(finalPos.DownCopy());

        bool canPlace = supportBlock.Replaceable < 6000
                     && !supportBlock.IsLiquid()
                     && (targetBlock.IsLiquid() || targetBlock.Replaceable >= 6000);

        if (canPlace)
        {
            world.BlockAccessor.SetBlock(block.BlockId, finalPos);

            // Восстанавливаем данные блочного объекта на новой позиции
            if (be != null)
            {
                BlockEntity newBe = world.BlockAccessor.GetBlockEntity(finalPos);
                if (newBe != null)
                {
                    TreeAttribute tree = new TreeAttribute();
                    be.ToTreeAttributes(tree);
                    tree.SetInt("posx", finalPos.X);
                    tree.SetInt("posy", finalPos.InternalY);
                    tree.SetInt("posz", finalPos.Z);
                    newBe.FromTreeAttributes(tree, world);
                }
            }

            return;
        }

        // Непригодных мест нет — разбрасываем предметы
        SpawnDrops(world, finalPos, drops, be);
    }
}