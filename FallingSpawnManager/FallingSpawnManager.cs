using FallingSpawnManager.Patches;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;


namespace FallingSpawnManager
{
    /// <summary>
    /// Серверный менеджер спавна падающих блоков.
    /// Ограничивает число одновременно существующих EntityBlockFalling, ставит заявки
    /// в очередь и прогоняет мгновенную симуляцию для блоков за пределами видимости игроков.
    ///
    /// FIX: мгновенная симуляция (InstantFallSimulation) раньше выполнялась синхронно
    /// прямо внутри RequestSpawn, без какого-либо троттлинга и без защиты от повторной
    /// обработки одной и той же позиции. При массовом обрушении (например, большого
    /// прямоугольника снега вне трекинг-радиуса игрока) это давало каскад синхронных
    /// SetBlock/ExchangeBlock вызовов, триггерящих соседние обрушения в том же кадре,
    /// что забивало главный поток на десятки секунд и могло приводить к дублированию
    /// блоков при повторном входе для одной позиции.
    ///
    /// Теперь мгновенные заявки тоже идут через очередь с лимитом на тик (instantQueue /
    /// maxInstantPerTick), а pendingPositions используется как guard для ОБЕИХ веток —
    /// и для очереди сущностей, и для мгновенной очереди.
    /// </summary>
    public class FallingSpawnManager : ModSystem
    {
        // Флаг инициализации, чтобы не регистрировать события повторно при перезагрузке мода
        private bool _initialized = false;

        // Общее число загруженных сущностей EntityBlockFalling на сервере
        private static int totalFallingBlocks = 0;

        // Очередь заявок на спавн сущностей, которые ждут свободного слота
        private static Queue<SpawnRequest> requestQueue = new Queue<SpawnRequest>();

        // FIX: отдельная очередь для заявок на мгновенную симуляцию (когда игрока рядом нет).
        // Раньше такие заявки обрабатывались синхронно и немедленно внутри RequestSpawn.
        private static Queue<SpawnRequest> instantQueue = new Queue<SpawnRequest>();

        // FIX: сколько мгновенных симуляций разрешено прогонять за один тик менеджера.
        // Ограничивает нагрузку на главный поток при массовых обрушениях вне зоны видимости
        // игроков. Значение можно вынести в конфиг, если понадобится тонкая настройка.
        private const int maxInstantPerTick = 100;

        // Позиции с уже повешенной заявкой — чтобы не дублировать спавн.
        // FIX: теперь сюда попадают позиции из ОБЕИХ очередей (requestQueue и instantQueue),
        // а не только из requestQueue, как было раньше. Это и есть guard от реентерабельной
        // обработки одной и той же позиции, которая, судя по всему, приводила к дюпу блоков.
        private static HashSet<BlockPos> pendingPositions = new HashSet<BlockPos>();

        private static ICoreServerAPI sapi;

        // Радиус вокруг игрока, в котором создаются сущности (в блоках)
        private static int activeRange = 128;

        // Harmony-патчи
        private Harmony harmony;

        // Конфигурация мода, загружается при старте
        private FSMConfig? _config;

        // Максимальное число падающих блоков
        public static int maxFallingLimit;

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Server;
        }




        /// <summary>
        /// Загрузка конфигурации и начальная инициализация
        /// </summary>
        /// <param name="api"></param>
        public override void StartPre(ICoreAPI api)
        {
            // Грузим конфиг. Если его нет или он с ошибкой — берём значения по умолчанию
            _config = api.LoadModConfig<FSMConfig>("FallingSpawnManagerConfig.json") ?? new FSMConfig();
            api.StoreModConfig(_config, "FallingSpawnManagerConfig.json");

            // Обрезаем значение до валидного диапазона
            maxFallingLimit = Math.Clamp(_config.MaxFallingLimit, 10, 10000);
        }


        /// <summary>
        /// Регистрация всех патчей с помощью Harmony
        /// </summary>
        /// <param name="api"></param>
        private void RegisterPatches(ICoreAPI api)
        {
            // EntityBlockFalling.Initialize
            var initMethod = AccessTools.Method(typeof(EntityBlockFalling), "Initialize",
                new[] { typeof(EntityProperties), typeof(ICoreAPI), typeof(long) });
            if (initMethod != null)
                harmony.Patch(initMethod, postfix: new HarmonyMethod(typeof(EntityBlockFallingPatch), nameof(EntityBlockFallingPatch.EntityBlockFalling_Initialize_Postfix)));
            else
                api.Logger.Error("Initialize not found");

            // EntityBlockFalling.OnGameTick
            var tickMethod = AccessTools.Method(typeof(EntityBlockFalling), "OnGameTick", new[] { typeof(float) });
            if (tickMethod != null)
                harmony.Patch(tickMethod, postfix: new HarmonyMethod(typeof(EntityBlockFallingPatch), nameof(EntityBlockFallingPatch.EntityBlockFalling_OnGameTick_Postfix)));
            else
                api.Logger.Error("OnGameTick not found");

            // FIX: EntityBlockFalling.OnEntityDespawn — нужен, чтобы чистить запись в
            // ConcurrentDictionary<long, ExtraData> (замена ConditionalWeakTable), иначе
            // это была бы утечка памяти на каждую упавшую сущность.
            var despawnMethod = AccessTools.Method(typeof(EntityBlockFalling), "OnEntityDespawn",
                new[] { typeof(EntityDespawnData) });
            if (despawnMethod != null)
                harmony.Patch(despawnMethod, postfix: new HarmonyMethod(typeof(EntityBlockFallingPatch), nameof(EntityBlockFallingPatch.EntityBlockFalling_OnEntityDespawn_Postfix)));
            else
                api.Logger.Error("OnEntityDespawn not found");

            // BlockBehaviorUnstableRock.collapseLayer
            var collapseMethod = AccessTools.Method(typeof(BlockBehaviorUnstableRock), "collapseLayer",
                new[] { typeof(IWorldAccessor), typeof(IOrderedEnumerable<BlockPos>), typeof(int) });
            if (collapseMethod != null)
                harmony.Patch(collapseMethod, prefix: new HarmonyMethod(typeof(BlockBehaviorUnstableRockPatch), nameof(BlockBehaviorUnstableRockPatch.collapseLayer_Prefix)));
            else
                api.Logger.Error("collapseLayer not found");

            // BlockBehaviorUnstableFalling.createFallingBlock
            var tryFallingMethod = AccessTools.Method(typeof(BlockBehaviorUnstableFalling), "createFallingBlock",
                new Type[] { typeof(IWorldAccessor), typeof(BlockPos) });
            if (tryFallingMethod != null)
            {
                harmony.Patch(tryFallingMethod,
                    prefix: new HarmonyMethod(typeof(BlockBehaviorUnstableFallingPatch), nameof(BlockBehaviorUnstableFallingPatch.createFallingBlock_Prefix)));
            }
            else
            {
                api.Logger.Error("Could not find BlockBehaviorUnstableFalling.createFallingBlock");
            }
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            if (_initialized)
                return; // защита от повторного старта/патчинга

            harmony = new Harmony("fallingspawnmanager");

            // Регистрация всех патчей
            RegisterPatches(api);

            sapi = api;

            // Радиус слежения берём из настроек сервера
            try
            {
                int trackingChunks = api.World.DefaultEntityTrackingRange;
                activeRange = trackingChunks * GlobalConstants.ChunkSize;
            }
            catch { /* оставляем дефолтное значение 128 */ }

            api.Event.OnEntitySpawn += OnEntitySpawn;
            api.Event.OnEntityLoaded += OnEntityLoaded;
            api.Event.OnEntityDespawn += OnEntityDespawn;

            // Тик очереди спавна — каждые 32 мс
            api.Event.RegisterGameTickListener(OnGameTick, 32);

            _initialized = true;

        }

        // Считаем только EntityBlockFalling, а не живых существ
        private void OnEntitySpawn(Entity entity)
        {
            if (!entity.IsCreature && entity is EntityBlockFalling) totalFallingBlocks++;
        }

        private void OnEntityLoaded(Entity entity)
        {
            if (!entity.IsCreature && entity is EntityBlockFalling) totalFallingBlocks++;
        }

        private void OnEntityDespawn(Entity entity, EntityDespawnData despawn)
        {
            if (!entity.IsCreature && entity is EntityBlockFalling) totalFallingBlocks--;
        }

        /// <summary>
        /// Обрабатываем очереди спавна каждый тик:
        ///   1) очередь сущностей — как и раньше, ограничена maxFallingLimit;
        ///   2) FIX: очередь мгновенных симуляций — ограничена maxInstantPerTick,
        ///      чтобы массовое обрушение вне зоны видимости игроков размазывалось
        ///      по многим тикам, а не выполнялось одним синхронным ударом.
        /// </summary>
        private void OnGameTick(float dt)
        {
            ProcessEntityQueue();
            ProcessInstantQueue();
        }

        private void ProcessEntityQueue()
        {
            SpawnRequest request;
            Block block;
            Entity existing;

            while (totalFallingBlocks < maxFallingLimit && requestQueue.Count > 0)
            {
                request = requestQueue.Dequeue();
                pendingPositions.Remove(request.InitialPos);

                // Проверяем, что блок на исходной позиции не сменился за время ожидания в очереди
                block = sapi.World.BlockAccessor.GetBlock(request.InitialPos);
                if (block == null || block.Id == 0 || block != request.Block)
                    continue;

                // Если сущность уже существует на этой позиции — откладываем спавн
                existing = sapi.World.GetNearestEntity(
                    request.InitialPos.ToVec3d().Add(0.5, 0.5, 0.5), 1, 1.5f,
                    e => !e.IsCreature && e is EntityBlockFalling ebf && ebf.initialPos.Equals(request.InitialPos));

                if (existing != null)
                {
                    request.RetryCount++;
                    if (request.RetryCount >= 300) // ~10 секунд при тике в 32 мс
                    {
                        // Если 300 раз подряд позиция занята — сдаёмся и выбрасываем предметы
                        var drops = request.Block.GetDrops(sapi.World, request.InitialPos, null);
                        EntityBlockFallingPatch.SpawnDrops(sapi.World, request.InitialPos, drops, request.BlockEntity);
                        continue;
                    }

                    // Возвращаем заявку в конец очереди ещё раз
                    pendingPositions.Add(request.InitialPos);
                    requestQueue.Enqueue(request);
                    continue;
                }

                // Создаём сущность и применяем смещение позиции, если оно задано
                var entityBf = new EntityBlockFalling(
                    request.Block, request.BlockEntity, request.InitialPos,
                    request.FallSound, request.ImpactDamageMul,
                    request.CanFallSideways, request.DustIntensity)
                {
                    DoRemoveBlock = request.DoRemoveBlock
                };

                sapi.World.SpawnEntity(entityBf);

                if (request.PositionOffset != null && request.PositionOffset != Vec3d.Zero)
                {
                    entityBf.Pos.X += request.PositionOffset.X;
                    entityBf.Pos.Y += request.PositionOffset.Y;
                    entityBf.Pos.Z += request.PositionOffset.Z;
                }
            }
        }

        // FIX: новый метод — обработка очереди мгновенных симуляций с лимитом на тик.
        private void ProcessInstantQueue()
        {
            int processed = 0;

            while (processed < maxInstantPerTick && instantQueue.Count > 0)
            {
                var request = instantQueue.Dequeue();

                // Снимаем guard сразу при извлечении из очереди — с этого момента
                // повторная заявка на ту же позицию (если вдруг придёт) будет
                // обработана заново, а не проигнорирована навсегда.
                pendingPositions.Remove(request.InitialPos);

                // Блок на исходной позиции мог измениться, пока заявка ждала в очереди
                // (например, игрок успел что-то там сломать/поставить, или структура
                // уже обвалилась от соседнего срабатывания). SimulateInstantFall и сам
                // делает похожую проверку, но делаем её и здесь, чтобы не тратить время
                // на GetDrops и не звать симуляцию впустую.
                Block currentBlock = sapi.World.BlockAccessor.GetBlock(request.InitialPos);
                if (currentBlock == null || currentBlock.Id == 0 || currentBlock != request.Block)
                {
                    processed++;
                    continue;
                }

                var drops = request.Block.GetDrops(sapi.World, request.InitialPos, null);
                EntityBlockFallingPatch.SimulateInstantFall(
                    sapi.World, request.Block, request.BlockEntity,
                    request.InitialPos, drops, request.DoRemoveBlock);

                processed++;
            }
        }

        /// <summary>
        /// Просит блок упасть.
        /// Если игрок рядом — создаёт заявку на спавн сущности (в очередь, если достигнут лимит).
        /// Если никого нет — FIX: ставит заявку в очередь мгновенной симуляции вместо
        /// немедленного синхронного выполнения.
        /// </summary>
        public void RequestSpawn(Block block, BlockEntity be, BlockPos initialPos,
                                 AssetLocation fallSound, float impactDamageMul,
                                 bool canFallSideways, float dustIntensity,
                                 bool doRemoveBlock = true, Vec3d positionOffset = null)
        {
            // Пропускаем дубликаты — для этой позиции уже есть заявка в ЛЮБОЙ из очередей.
            // FIX: раньше pendingPositions защищал только путь через requestQueue; мгновенный
            // путь не проверялся и не регистрировался вообще, что и открывало окно для
            // реентерабельной обработки одной и той же позиции (каскад через
            // OnNeighbourBlockChange соседей внутри SetBlock/ExchangeBlock).
            if (pendingPositions.Contains(initialPos))
                return;

            pendingPositions.Add(initialPos);

            // Проверяем, есть ли игрок внутри activeRange
            bool hasPlayerNearby = false;
            Vec3d posVec = initialPos.ToVec3d();
            EntityPlayer eplr;
            foreach (IPlayer player in sapi.World.AllOnlinePlayers)
            {
                eplr = player.Entity;
                if (eplr != null && eplr.Pos.InRangeOf(posVec, activeRange * activeRange, activeRange))
                {
                    hasPlayerNearby = true;
                    break;
                }
            }

            var request = new SpawnRequest
            {
                Block = block,
                BlockEntity = be,
                InitialPos = initialPos.Copy(),
                FallSound = fallSound,
                ImpactDamageMul = impactDamageMul,
                CanFallSideways = canFallSideways,
                DustIntensity = dustIntensity,
                DoRemoveBlock = doRemoveBlock,
                PositionOffset = positionOffset ?? Vec3d.Zero
            };

            if (!hasPlayerNearby)
            {
                // FIX: раньше здесь стоял прямой синхронный вызов InstantFallSimulation(...).
                // Теперь заявка просто уходит в instantQueue и будет обработана порциями
                // в ProcessInstantQueue — по maxInstantPerTick штук за тик.
                instantQueue.Enqueue(request);
                return;
            }

            // Игрок рядом — ставим заявку в очередь на полное создание сущности
            requestQueue.Enqueue(request);
        }

        public override void Dispose()
        {
            // При выгрузке мода чистим очереди и отвязываем события, чтобы не держать ссылки на объекты мира
            requestQueue?.Clear();
            instantQueue?.Clear(); // FIX: чистим и новую очередь тоже
            pendingPositions?.Clear();
            totalFallingBlocks = 0;
            if (sapi != null)
            {
                sapi.Event.OnEntitySpawn -= OnEntitySpawn;
                sapi.Event.OnEntityLoaded -= OnEntityLoaded;
                sapi.Event.OnEntityDespawn -= OnEntityDespawn;
            }

            // Снимаем все Harmony-патчи, применённые этим модом
            harmony?.UnpatchAll("fallingspawnmanager");

            _initialized = false; // для повторного старта после релоада

        }

        /// <summary>
        /// Данные одной заявки на спавн в очереди (используется и для requestQueue,
        /// и для instantQueue — RetryCount актуален только для requestQueue).
        /// </summary>
        private struct SpawnRequest
        {
            public Block Block;
            public BlockEntity BlockEntity;
            public BlockPos InitialPos;
            public AssetLocation FallSound;
            public float ImpactDamageMul;
            public bool CanFallSideways;
            public float DustIntensity;
            public bool DoRemoveBlock;
            public Vec3d PositionOffset;

            // Сколько раз заявку уже возвращали в очередь из-за занятой позиции
            // (используется только в requestQueue / ProcessEntityQueue)
            public int RetryCount;
        }



    }


    /// <summary>
    /// Конфигурация мода
    /// </summary>
    public class FSMConfig
    {
        public int MaxFallingLimit = 500;
    }
}