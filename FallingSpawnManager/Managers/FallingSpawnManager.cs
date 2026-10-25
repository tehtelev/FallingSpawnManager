using FSMMgr.Patches;
using FSMMgr.Utils;
using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace FSMMgr.Managers
{
    /// <summary>
    /// Серверный менеджер спавна падающих блоков.
    /// Ограничивает число одновременно существующих EntityBlockFalling, ставит заявки
    /// в очередь и разрешает падение без сущности для блоков, рядом с которыми нет игроков.
    ///
    /// Игрок рядом (радиус слежения за сущностями, по умолчанию 128 блоков): заявка идёт в
    /// requestQueue, сущность создаётся, пока живых меньше maxFallingLimit.
    /// Игрока нет: заявка идёт в instantQueue, падение разрешается без сущности
    /// (SimulateInstantFall), не более maxInstantPerTick за тик.
    ///
    /// Обе очереди строго FIFO. Соседние блоки колонны просят упасть снизу вверх, и случайный выбор
    /// тратил бюджет на блоки, под которыми опора ещё на месте. FIFO на 6400 блоков с лимитом 1000
    /// разгружался быстрее (645-1245 тиков против 780-1920).
    ///
    /// Отличия от ванили (намеренные):
    ///  - блок, которого просили упасть только вбок и у которого внизу твёрдая опора, остаётся на месте;
    ///  - снег, упавший на снежный блок, добавляет слой над ним, а не впитывается;
    ///  - заявки, которые ждут в очереди при остановке сервера, разрешаются в Dispose, но заявки колонн,
    ///    выгруженных раньше, остаются на стартовых клетках. Сущности в полёте при выгрузке колонн
    ///    остаются ванили: сохраняются с чанком и приземляются после загрузки;
    ///  - requestQueue общая на весь сервер: пока идёт большой обвал, новый блок другого игрока ждёт в хвосте;
    ///  - проверки соседей в BlockBehaviorUnstableRock разнесены на 300/400/500 мс.
    ///
    /// Потоки: при RegionTicking падающие сущности тикают в рабочих потоках, поэтому
    /// activeFallingPositions / trackedEntityIds / freedEntityIds / totalFallingBlocks защищены lock'ом.
    /// Очереди и изменения блоков только в главном потоке.
    /// </summary>
    public static class FallingSpawnManager
    {
        // Один повтор за тик, примерно 10 секунд при тике очереди 32 мс.
        private const int MaxRetries = 300;
        private const int MaxFlushOnDispose = 100000;

        private static bool _initialized;
        // Выставляется на время Dispose: хуки в этот момент идут по ванильному пути,
        // чтобы SetBlock во время слива не добавлял заявки в очередь, которую сливают.
        private static bool _disposing;

        private static ICoreServerAPI sapi;
        private static long tickListenerId;

        // Защищает четыре поля ниже (см. «Потоки» в шапке).
        private static readonly object activeLock = new();

        // Число живых EntityBlockFalling. Считается строго по trackedEntityIds, поэтому повторный
        // OnEntitySpawn/OnEntityLoaded одной и той же сущности не может завысить счётчик.
        private static int totalFallingBlocks;
        private static readonly HashSet<long> trackedEntityIds = [];

        // Сущности, у которых позиция уже освобождена (упали дальше чем на блок), чтобы не удалить
        // её второй раз у новой сущности на том же initialPos.
        private static readonly HashSet<long> freedEntityIds = [];

        // initialPos всех живых сущностей. O(1) вместо GetNearestEntity.
        private static readonly HashSet<BlockPos> activeFallingPositions = [];

        private static readonly Queue<SpawnRequest> requestQueue = new();
        private static readonly Queue<SpawnRequest> instantQueue = new();

        // Заявки, у которых позиция занята в этом тике. Возвращаются в очередь после цикла,
        // чтобы каждая повторялась раз в тик, а не крутилась внутри одного тика.
        private static readonly List<SpawnRequest> blockedThisTick = [];

        // Позиции с заявкой в любой из очередей. Всегда приватные копии, никогда BlockPos вызывающего
        // (вызывающий может его менять, например snowshovel).
        private static readonly HashSet<BlockPos> pendingPositions = [];

        private static int activeRange = 128;

        public static int maxInstantPerTick;
        public static int maxFallingLimit;

        // Сторожевой таймер (мс) против vanilla-бага "Falling block entity does not settle".
        // 0 отключает только этот таймер. Проверка "рядом нет игрока" работает всегда.
        public static int stuckTimeoutMs;

        /// <summary> Менеджер работает и хуки должны использовать его, а не ваниль. </summary>
        public static bool IsActive => _initialized && !_disposing;

        public static void Initialize(ICoreServerAPI api, FSMConfig cfg)
        {
            if (_initialized) return;

            sapi = api;

            maxFallingLimit = Math.Clamp(cfg.MaxFallingLimit, 10, 10000);
            // 0 отключил бы троттлинг и вернул "всё за один тик".
            maxInstantPerTick = Math.Clamp(cfg.MaxInstantPerTick, 1, 10000);
            stuckTimeoutMs = Math.Clamp(cfg.StuckTimeoutMs, 0, 120000);

            try
            {
                activeRange = api.World.DefaultEntityTrackingRange * GlobalConstants.ChunkSize;
            }
            catch { /* оставляем 128 */ }

            api.Event.OnEntitySpawn += OnEntitySpawn;
            api.Event.OnEntityLoaded += OnEntityLoaded;
            api.Event.OnEntityDespawn += OnEntityDespawn;

            tickListenerId = api.Event.RegisterGameTickListener(OnGameTick, 32);

            _initialized = true;
        }

        public static void Dispose()
        {
            if (!_initialized) return;

            // Сначала выключаем хуки: SetBlock во время слива может дойти до RequestSpawn.
            _disposing = true;

            if (sapi != null)
            {
                if (tickListenerId != 0)
                {
                    sapi.Event.UnregisterGameTickListener(tickListenerId);
                    tickListenerId = 0;
                }
                sapi.Event.OnEntitySpawn -= OnEntitySpawn;
                sapi.Event.OnEntityLoaded -= OnEntityLoaded;
                sapi.Event.OnEntityDespawn -= OnEntityDespawn;

                // Мир ещё жив (игроки отключены, сохранение после Dispose модов): разрешаем всё,
                // что осталось, чтобы блоки не сохранились на стартовых клетках.
                IWorldAccessor world = sapi.World;
                if (world != null)
                {
                    FlushQueue(world, instantQueue);
                    FlushQueue(world, requestQueue);
                    List<SpawnRequest> blocked = new(blockedThisTick);
                    foreach (SpawnRequest r in blocked) ResolveInstant(world, r, recheck: false);
                }
            }

            requestQueue.Clear();
            instantQueue.Clear();
            blockedThisTick.Clear();
            pendingPositions.Clear();
            lock (activeLock)
            {
                activeFallingPositions.Clear();
                trackedEntityIds.Clear();
                freedEntityIds.Clear();
                totalFallingBlocks = 0;
            }

            _initialized = false;
            _disposing = false;
        }

        // Очередь сливается через Dequeue, а не foreach: даже если хук добавит заявку, MoveNext не бросит.
        private static void FlushQueue(IWorldAccessor world, Queue<SpawnRequest> queue)
        {
            int flushed = 0;
            while (flushed++ < MaxFlushOnDispose && queue.Count > 0)
                ResolveInstant(world, queue.Dequeue(), recheck: false);
        }

        // =====================================================================
        //  Учёт живых сущностей
        // =====================================================================

        public static bool IsPositionActiveOrPending(BlockPos pos)
        {
            return pendingPositions.Contains(pos) || IsActivePosition(pos);
        }

        private static bool IsActivePosition(BlockPos pos)
        {
            lock (activeLock) return activeFallingPositions.Contains(pos);
        }

        private static int LiveFallingCount
        {
            get { lock (activeLock) return totalFallingBlocks; }
        }

        private static void Track(Entity entity)
        {
            if (entity is not EntityBlockFalling ebf || ebf.initialPos == null) return;
            lock (activeLock)
            {
                if (!trackedEntityIds.Add(ebf.EntityId)) return; // уже учтена
                totalFallingBlocks++;
                activeFallingPositions.Add(ebf.initialPos);
            }
        }

        private static void OnEntitySpawn(Entity entity) => Track(entity);
        private static void OnEntityLoaded(Entity entity) => Track(entity);

        private static void OnEntityDespawn(Entity entity, EntityDespawnData despawn)
        {
            if (entity is not EntityBlockFalling ebf) return;
            lock (activeLock)
            {
                if (!trackedEntityIds.Remove(ebf.EntityId)) return;
                totalFallingBlocks = Math.Max(0, totalFallingBlocks - 1);
                // Позицию уже могли освободить: за это время на initialPos мог появиться новый блок
                // с новой сущностью, и второе удаление разрешило бы ей упасть дважды.
                if (!freedEntityIds.Remove(ebf.EntityId) && ebf.initialPos != null)
                    activeFallingPositions.Remove(ebf.initialPos);
            }
        }

        /// <summary>
        /// Освобождает слот дедупликации, когда сущность упала дальше чем на блок (окно дубликатов как в ванили).
        /// Счётчик живых не трогает. Повторный вызов безопасен.
        /// </summary>
        public static void ReleasePosition(EntityBlockFalling ebf)
        {
            if (ebf.initialPos == null) return;
            lock (activeLock)
            {
                if (!trackedEntityIds.Contains(ebf.EntityId)) return;
                if (!freedEntityIds.Add(ebf.EntityId)) return;
                activeFallingPositions.Remove(ebf.initialPos);
            }
        }

        public static bool HasPlayerNearby(Vec3d pos)
        {
            float rangeSq = (float)activeRange * activeRange;
            foreach (IPlayer player in sapi.World.AllOnlinePlayers)
            {
                EntityPlayer eplr = player.Entity;
                if (eplr != null && eplr.Pos.InRangeOf(pos, rangeSq, activeRange))
                    return true;
            }
            return false;
        }

        // =====================================================================
        //  Тик очередей
        // =====================================================================

        private static void OnGameTick(float dt)
        {
            ProcessEntityQueue();
            ProcessInstantQueue();
        }

        private static void ProcessEntityQueue()
        {
            IWorldAccessor world = sapi.World;

            while (LiveFallingCount < maxFallingLimit && requestQueue.Count > 0)
            {
                SpawnRequest request = requestQueue.Dequeue();
                // try/catch на заявку: исключение (мод в GetDrops/OnFallOnto) не должно
                // обрывать тик и оставлять guard висеть.
                try
                {
                    ProcessEntityRequest(world, request);
                }
                catch (Exception e)
                {
                    pendingPositions.Remove(request.InitialPos);
                    sapi.Logger.Warning("[FSM] Falling request failed at {0}: {1}", request.InitialPos, e.Message);
                }
            }

            // Заблокированные возвращаются после цикла: один повтор за тик.
            if (blockedThisTick.Count > 0)
            {
                foreach (SpawnRequest blocked in blockedThisTick) requestQueue.Enqueue(blocked);
                blockedThisTick.Clear();
            }
        }

        private static void ProcessEntityRequest(IWorldAccessor world, SpawnRequest request)
        {
            // Сравниваем с исходным блоком, а не с падающим вариантом (rock-granite падает как crackedrock-granite).
            Block current = world.BlockAccessor.GetBlock(request.InitialPos);
            if (current == null || current.Id == 0 || current != request.SourceBlock)
            {
                pendingPositions.Remove(request.InitialPos);
                RecheckReplacedBlock(world, request, current);
                return;
            }

            if (IsActivePosition(request.InitialPos))
            {
                request.RetryCount++;
                if (request.RetryCount >= MaxRetries)
                {
                    pendingPositions.Remove(request.InitialPos);
                    GiveUp(world, request);
                    return;
                }
                blockedThisTick.Add(request);
                return;
            }

            // Живой BE на момент спавна: заявка могла ждать долго, а блок мог быть сломан и поставлен заново.
            // Старый BE сериализовал бы устаревшее содержимое и дублировал бы инвентарь.
            EntityBlockFalling entityBf = new(
                request.Block, world.BlockAccessor.GetBlockEntity(request.InitialPos), request.InitialPos,
                request.FallSound, request.ImpactDamageMul,
                request.CanFallSideways, request.DustIntensity)
            {
                DoRemoveBlock = request.DoRemoveBlock
            };

            world.SpawnEntity(entityBf);

            if (request.PositionOffset != null && request.PositionOffset != Vec3d.Zero)
            {
                entityBf.Pos.X += request.PositionOffset.X;
                entityBf.Pos.Y += request.PositionOffset.Y;
                entityBf.Pos.Z += request.PositionOffset.Z;
            }

            // Initialize/OnEntitySpawn уже внесли позицию в activeFallingPositions.
            pendingPositions.Remove(request.InitialPos);
        }

        // Заявка не получила позицию за MaxRetries тиков. Блок всё ещё стоит на месте (источник только что проверен),
        // поэтому его нужно убрать ДО выдачи предметов, иначе предметы дублируются.
        // Заявка, которая не владеет блоком (DoRemoveBlock=false, например угольная куча), просто заканчивается.
        private static void GiveUp(IWorldAccessor world, SpawnRequest request)
        {
            if (!request.DoRemoveBlock) return;

            ItemStack[] drops = request.Block.GetDrops(world, request.InitialPos, null);
            BlockEntity liveBe = world.BlockAccessor.GetBlockEntity(request.InitialPos);
            world.BlockAccessor.SetBlock(0, request.InitialPos);
            world.BlockAccessor.TriggerNeighbourBlockUpdate(request.InitialPos);
            EntityBlockFallingPatch.SpawnDrops(world, request.InitialPos, drops, liveBe);
        }

        // Пока заявка ждала, в клетке оказался другой блок (игрок выкопал песок и поставил гравий).
        // Его собственный createFallingBlock был пропущен guard'ом, и больше его никто не спросит,
        // поэтому даём ему перепроверить себя. Нужный путь (сущность/instant) выберет его же хук.
        private static void RecheckReplacedBlock(IWorldAccessor world, SpawnRequest request, Block current)
        {
            if (current == null || current.Id == 0) return;
            BlockPos pos = request.InitialPos;
            current.OnNeighbourBlockChange(world, pos, pos.DownCopy());
        }

        // В контейнеры могут класть предметы, пока падение ждёт в очереди. Сохраняем живое содержимое.
        // Остальные BE остаются со снимком на момент заявки (в том числе если исходный BE уже исчез).
        private static void RefreshContainerSnapshot(IWorldAccessor world, ref SpawnRequest request)
        {
            BlockEntity liveBe = world.BlockAccessor.GetBlockEntity(request.InitialPos);
            if (liveBe is not IBlockEntityContainer) return;

            TreeAttribute fresh = new();
            liveBe.ToTreeAttributes(fresh);
            request.BlockEntity = liveBe;
            request.BlockEntityTree = fresh;
        }

        private static void ProcessInstantQueue()
        {
            IWorldAccessor world = sapi.World;
            int processed = 0;

            while (processed < maxInstantPerTick && instantQueue.Count > 0)
            {
                SpawnRequest request = instantQueue.Dequeue();
                pendingPositions.Remove(request.InitialPos);
                processed++;
                ResolveInstant(world, request, recheck: true);
            }
        }

        // Общий путь для тика и слива при остановке. recheck=false при сливе: хуки там уже выключены.
        private static void ResolveInstant(IWorldAccessor world, SpawnRequest request, bool recheck)
        {
            try
            {
                Block current = world.BlockAccessor.GetBlock(request.InitialPos);
                if (current == null || current.Id == 0 || current != request.SourceBlock)
                {
                    if (recheck) RecheckReplacedBlock(world, request, current);
                    return;
                }

                RefreshContainerSnapshot(world, ref request);

                ItemStack[] drops = request.Block.GetDrops(world, request.InitialPos, null);

                // Снимок (BlockEntityTree), а не живой BE. Живой нужен только для DropContents.
                EntityBlockFallingPatch.SimulateInstantFall(
                    world,
                    request.Block,
                    request.SourceBlock,
                    request.BlockEntity,
                    request.BlockEntityTree,
                    request.InitialPos,
                    drops,
                    request.DoRemoveBlock);
            }
            catch (Exception e)
            {
                try { sapi?.Logger.Warning("[FSM] Instant fall failed at {0}: {1}", request.InitialPos, e.Message); }
                catch (Exception) { /* логгер мог уже закрыться */ }
            }
        }

        // =====================================================================
        //  Постановка заявки
        // =====================================================================

        /// <summary>
        /// Просит блок упасть. Возвращает true, если создана НОВАЯ заявка, и false, если позиция уже в работе
        /// или менеджер выключен (вызывающий хук проверяет IsActive отдельно). Rock-патч использует результат,
        /// чтобы оборвать цепочку обвала, когда в слое нет ничего нового.
        /// </summary>
        public static bool RequestSpawn(Block block, BlockEntity be, BlockPos initialPos,
            AssetLocation fallSound, float impactDamageMul,
            bool canFallSideways, float dustIntensity,
            bool doRemoveBlock = true, Vec3d positionOffset = null)
        {
            if (!IsActive) return false;

            // Приватная копия: вызывающий может менять свой BlockPos.
            BlockPos pos = initialPos.Copy();
            if (!pendingPositions.Add(pos)) return false;

            bool hasPlayerNearby = HasPlayerNearby(pos.ToVec3d());

            // Снимок BE делаем здесь, пока блок ещё на месте. У instant-пути нет Initialize, который
            // сделал бы его сам, а живой BE к моменту очереди может измениться или исчезнуть.
            TreeAttribute beTree = null;
            if (be != null)
            {
                beTree = new TreeAttribute();
                be.ToTreeAttributes(beTree);
            }

            SpawnRequest request = new()
            {
                Block = block,                                              // падающий/приземляющийся вариант
                SourceBlock = sapi.World.BlockAccessor.GetBlock(pos),       // исходный блок на позиции
                BlockEntity = be,
                BlockEntityTree = beTree,
                InitialPos = pos,
                FallSound = fallSound,
                ImpactDamageMul = impactDamageMul,
                CanFallSideways = canFallSideways,
                DustIntensity = dustIntensity,
                DoRemoveBlock = doRemoveBlock,
                PositionOffset = positionOffset ?? Vec3d.Zero
            };

            if (hasPlayerNearby) requestQueue.Enqueue(request);
            else instantQueue.Enqueue(request);
            return true;
        }
    }
}