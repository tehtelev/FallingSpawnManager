using FSMMgr.Managers;
using HarmonyLib;
using System;
using System.Collections.Concurrent;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

#nullable disable

namespace FSMMgr.Patches;

/// <summary>
/// Патчи Harmony для EntityBlockFalling (только серверная сторона):
///   1) Initialize: AlwaysActive, полный SimulationRange, State = Active.
///   2) OnGameTick: освобождение слота дедупликации, сторожевой таймер "застряла и не падает"
///      (форум VS #5775) и проверка "рядом нет игрока".
///   3) OnEntityDespawn: страховка, если сущность исчезла, не поставив блок (жёсткий таймаут ванили,
///      другой мод). Unload не трогаем: сущность сохранится с чанком и приземлится после загрузки.
///
/// Обе развязки (таймаут, нет игрока) выполняются в главном потоке через EnqueueMainThreadTask: при
/// RegionTicking OnGameTick идёт в рабочем потоке, а SetBlock оттуда небезопасен.
///
/// Публичные SimulateInstantFall / SpawnDrops использует FallingSpawnManager.
/// </summary>
public static class EntityBlockFallingPatch
{
    private static readonly AccessTools.FieldRef<EntityBlockFalling, bool> _fallHandledRef =
        AccessTools.FieldRefAccess<EntityBlockFalling, bool>("fallHandled");

    private static readonly AccessTools.FieldRef<EntityBlockFalling, ItemStack[]> _dropsRef =
        AccessTools.FieldRefAccess<EntityBlockFalling, ItemStack[]>("drops");

    private static readonly Action<EntityBlockFalling, bool, BlockPos> _updateBlock =
        AccessTools.MethodDelegate<Action<EntityBlockFalling, bool, BlockPos>>(
            AccessTools.Method(typeof(EntityBlockFalling), "UpdateBlock", new[] { typeof(bool), typeof(BlockPos) }));

    private sealed class ExtraData
    {
        public long LastPlayerCheckMs;
        public long SpawnTimeMs;      // для сторожевого таймера
        public bool PositionFreed;    // слот дедупликации уже освобождён
        public bool SettleQueued;     // развязка уже поставлена в главный поток
    }

    // ConcurrentDictionary по EntityId вместо ConditionalWeakTable (дорогой lock на каждый вызов).
    // Запись чистится в постфиксе OnEntityDespawn.
    private static readonly ConcurrentDictionary<long, ExtraData> _extra = new();

    private const int PlayerCheckIntervalMs = 2000;

    // =========================================================================
    //  Initialize
    // =========================================================================

    [HarmonyPostfix]
    [HarmonyPatch(typeof(EntityBlockFalling), "Initialize")]
    public static void EntityBlockFalling_Initialize_Postfix(
        EntityBlockFalling __instance,
        EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
    {
        if (api.Side != EnumAppSide.Server || !FallingSpawnManager.IsActive) return;

        __instance.AlwaysActive = true;
        __instance.SimulationRange = GlobalConstants.DefaultSimulationRange;
        // AlwaysActive обходит UpdateTrackedEntityState: без явного Active сущность в ~110 блоках
        // от игрока не получает пассивную физику и никогда не приземляется.
        __instance.State = EnumEntityState.Active;

        // TryAdd: повторный Initialize того же экземпляра не должен сбрасывать отсчёт таймера.
        _extra.TryAdd(__instance.EntityId, new ExtraData { SpawnTimeMs = api.World.ElapsedMilliseconds });
    }

    // =========================================================================
    //  OnEntityDespawn: чистка словаря и страховка
    // =========================================================================

    [HarmonyPostfix]
    [HarmonyPatch(typeof(EntityBlockFalling), "OnEntityDespawn")]
    public static void EntityBlockFalling_OnEntityDespawn_Postfix(
        EntityBlockFalling __instance,
        EntityDespawnData despawn)
    {
        if (__instance.Api?.Side != EnumAppSide.Server) return;
        if (!_extra.TryRemove(__instance.EntityId, out _)) return; // мы эту сущность не вели

        // Все наши пути и OnFallToGround ставят fallHandled до Die(), так что сюда попадают
        // только внешние despawn. Угольные кучи (DoRemoveBlock=false) живут по ванильному циклу.
        if (_fallHandledRef(__instance) || !__instance.DoRemoveBlock) return;
        if (despawn?.Reason == EnumDespawnReason.Unload) return;
        if (!FallingSpawnManager.IsActive || __instance.World == null) return;

        _fallHandledRef(__instance) = true;
        try
        {
            // Initialize уже убрал исходный блок. Здесь только уведомляем соседей, если это ещё не сделано.
            if (!__instance.InitialBlockRemoved)
            {
                __instance.InitialBlockRemoved = true;
                _updateBlock(__instance, true, __instance.initialPos);
            }

            SimulateInstantFall(
                __instance.World, __instance.Block, __instance.Block,
                __instance.removedBlockentity, __instance.blockEntityAttributes,
                __instance.Pos.AsBlockPos, _dropsRef(__instance), doRemoveBlock: false);
        }
        catch (Exception e)
        {
            try { __instance.Api.Logger.Warning("[FSM] Falling entity despawn settle failed at {0}: {1}", __instance.initialPos, e.Message); }
            catch (Exception) { /* логгер мог закрыться */ }
        }
    }

    // =========================================================================
    //  OnGameTick
    // =========================================================================

    [HarmonyPostfix]
    [HarmonyPatch(typeof(EntityBlockFalling), "OnGameTick")]
    public static void EntityBlockFalling_OnGameTick_Postfix(EntityBlockFalling __instance, float dt)
    {
        if (__instance.Api?.Side != EnumAppSide.Server) return;
        if (!FallingSpawnManager.IsActive) return;
        if (!__instance.Alive) return;

        ExtraData data = _extra.GetOrAdd(__instance.EntityId, _ => new ExtraData
        {
            SpawnTimeMs = __instance.Api.World.ElapsedMilliseconds
        });

        // Слот дедупликации освобождаем, когда сущность упала дальше чем на блок: ванильный
        // FallingEntityAlreadyExists её на initialPos уже не находит. Один раз за жизнь.
        // Освобождение по fallHandled намеренно НЕТ: сущность ещё "живёт" на месте (lingerTicks),
        // как в ваниле, иначе снежный блок на неполном слое и песок над листвой порождают
        // бесконечный цикл новых сущностей.
        if (!data.PositionFreed && __instance.initialPos.Y - __instance.Pos.Y > 1.0)
        {
            data.PositionFreed = true;
            FallingSpawnManager.ReleasePosition(__instance);
        }

        if (_fallHandledRef(__instance)) return;

        // Угольные кучи спавнят сущность напрямую (TryPartialCollapse, DoRemoveBlock=false):
        // их жизненный цикл завязан на ваниль, наши развязки не применяем.
        if (!__instance.DoRemoveBlock || __instance.removedBlockentity is BlockEntityCoalPile)
            return;

        long now = __instance.Api.World.ElapsedMilliseconds;

        int timeout = FallingSpawnManager.stuckTimeoutMs;
        if (timeout > 0 && now - data.SpawnTimeMs > timeout)
        {
            SettleAt(__instance, data, __instance.Pos.AsBlockPos); // застряла рядом с игроком: там, где висит
            return;
        }

        if (now - data.LastPlayerCheckMs < PlayerCheckIntervalMs) return;
        data.LastPlayerCheckMs = now;

        if (!FallingSpawnManager.HasPlayerNearby(__instance.Pos.XYZ))
            SettleAt(__instance, data, __instance.initialPos);
    }

    // initialPos для "игрока нет" (позиция логическая, никто не смотрит) и Pos для сторожевого
    // таймера (приземляем там, где блок реально висит). Всё выполняется в главном потоке.
    private static void SettleAt(EntityBlockFalling entity, ExtraData data, BlockPos atPos)
    {
        if (_fallHandledRef(entity) || data.SettleQueued) return;
        data.SettleQueued = true;
        BlockPos at = atPos.Copy();

        entity.Api.Event.EnqueueMainThreadTask(() =>
        {
            data.SettleQueued = false;
            // Сущность могла приземлиться сама или исчезнуть за это время (тогда её подберёт страховка despawn).
            // Despawned проверяем отдельно: при Unload Alive остаётся true.
            if (_fallHandledRef(entity) || !entity.Alive || entity.State == EnumEntityState.Despawned) return;
            _fallHandledRef(entity) = true;

            try
            {
                if (!entity.InitialBlockRemoved)
                {
                    entity.InitialBlockRemoved = true;
                    _updateBlock(entity, true, entity.initialPos);
                }

                SimulateInstantFall(
                    entity.World, entity.Block, entity.Block,
                    entity.removedBlockentity,        // живой BE нужен только для DropContents
                    entity.blockEntityAttributes,     // снимок, сделанный в Initialize
                    at, _dropsRef(entity), doRemoveBlock: false);
            }
            catch (Exception e)
            {
                try { entity.Api.Logger.Warning("[FSM] Falling entity settle failed at {0}: {1}", at, e.Message); }
                catch (Exception) { /* логгер мог закрыться */ }
            }
            finally
            {
                // Даже при исключении сущность не должна висеть и занимать слот лимита.
                entity.Die(EnumDespawnReason.Removed);
            }
        }, "FSMFallingSettle");
    }

    // =========================================================================
    //  Публичные утилиты (FallingSpawnManager)
    // =========================================================================

    /// <summary> Предметы блока и, если BE контейнер, его содержимое в центре клетки. </summary>
    public static void SpawnDrops(IWorldAccessor world, BlockPos pos, ItemStack[] drops, BlockEntity be)
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
    /// Проходит траекторию падения без сущности: ставит блок на первую твёрдую опору или выбрасывает предметы.
    /// </summary>
    /// <param name="block">Падающий/приземляющийся вариант (rock-granite падает как crackedrock-granite).</param>
    /// <param name="sourceBlock">Исходный блок на startPos. Проверяется ДО удаления, если doRemoveBlock.</param>
    /// <param name="be">Живой BE, только для DropContents.</param>
    /// <param name="beTree">Снимок BE до снятия блока. Именно он идёт в CanAcceptFallOnto / OnFallOnto / FromTreeAttributes.</param>
    /// <param name="doRemoveBlock">true: функция сама снимает блок и уведомляет соседей. false: блок уже снят вызывающим.</param>
    /// <remarks>
    /// Соседи уведомляются после снятия и после установки, как в EntityBlockFalling.UpdateBlock. Сервер ставит
    /// такие уведомления в очередь (до 500 за тик) и не загружает чанки, поэтому стоимость за тик ограничена,
    /// а колонна или куча продолжает падать. Порождённые заявки возвращаются в ту же очередь.
    /// </remarks>
    public static void SimulateInstantFall(
        IWorldAccessor world,
        Block block,
        Block sourceBlock,
        BlockEntity be,
        TreeAttribute beTree,
        BlockPos startPos,
        ItemStack[] drops,
        bool doRemoveBlock)
    {
        IBlockAccessor accessor = world.BlockAccessor;

        if (doRemoveBlock && accessor.GetBlock(startPos) != sourceBlock) return;

        BlockPos finalPos = startPos.Copy();
        int worldHeight = accessor.MapSizeY;

        for (int i = 0; i < worldHeight; i++)
        {
            BlockPos belowPos = finalPos.DownCopy();
            Block belowBlock = accessor.GetMostSolidBlock(belowPos);

            // Блок снизу может принять падение сам (воронка, рыхлая земля, угольная куча).
            // Передаём СНИМОК, а не повторную сериализацию живого BE.
            if (belowBlock.CanAcceptFallOnto(world, belowPos, block, beTree))
            {
                if (doRemoveBlock) RemoveStartBlock(accessor, startPos);
                belowBlock.OnFallOnto(world, belowPos, block, beTree);
                return;
            }

            if (belowBlock.Replaceable >= 6000 || belowBlock.IsLiquid())
                finalPos = belowPos;
            else
                break;
        }

        // Блок не сдвигается и не меняет вариант: решать нечего. Это падение только вбок при твёрдой опоре.
        // Снятие и повторная установка лишь запустили бы OnBlockPlaced и поставили ту же заявку заново.
        if (doRemoveBlock && block == sourceBlock && finalPos.Equals(startPos)) return;

        if (doRemoveBlock) RemoveStartBlock(accessor, startPos);

        Block targetBlock = accessor.GetBlock(finalPos);
        Block supportBlock = accessor.GetMostSolidBlock(finalPos.DownCopy());

        bool supported = supportBlock.Replaceable < 6000 && !supportBlock.IsLiquid();

        // Снег ложится на то, что есть, поднимая уровень снега (ванильный UpdateSnowLayer), а не заменяя.
        if (supported && block.BlockMaterial == EnumBlockMaterial.Snow)
        {
            Block occupied = accessor.GetMostSolidBlock(finalPos);
            if (occupied.Id != 0)
            {
                Block snowBlock = occupied.GetSnowCoveredVariant(finalPos, occupied.snowLevel + 1);
                if (snowBlock != null && snowBlock != occupied)
                    accessor.ExchangeBlock(snowBlock.Id, finalPos);
                return;
            }

            // Ваниль (EntityBlockFalling.UpdateBlock) не ставит снег в клетку с жидкостью.
            if (accessor.GetBlock(finalPos, BlockLayersAccess.Fluid).Id != 0) return;
        }

        bool canPlace = supported && (targetBlock.IsLiquid() || targetBlock.Replaceable >= 6000);

        if (canPlace)
        {
            accessor.SetBlock(block.BlockId, finalPos);
            accessor.TriggerNeighbourBlockUpdate(finalPos);

            // Данные BE на новой позиции из СНИМКА: живой BE мог быть detached/пустым.
            if (beTree != null)
            {
                BlockEntity newBe = accessor.GetBlockEntity(finalPos);
                if (newBe != null)
                {
                    beTree.SetInt("posx", finalPos.X);
                    beTree.SetInt("posy", finalPos.InternalY);
                    beTree.SetInt("posz", finalPos.Z);
                    newBe.FromTreeAttributes(beTree, world);
                }
            }
            return;
        }

        SpawnDrops(world, finalPos, drops, be);
    }

    private static void RemoveStartBlock(IBlockAccessor accessor, BlockPos startPos)
    {
        accessor.SetBlock(0, startPos);
        accessor.TriggerNeighbourBlockUpdate(startPos);
    }
}