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
    /// </summary>
    public class FallingSpawnManager : ModSystem
    {
        // Флаг инициализации, чтобы не регистрировать события повторно при перезагрузке мода
        private bool _initialized = false;

        // Общее число загруженных сущностей EntityBlockFalling на сервере
        private static int totalFallingBlocks = 0;

        // Очередь заявок на спавн, которые ждут свободного слота
        private static Queue<SpawnRequest> requestQueue = new Queue<SpawnRequest>();

        // Позиции с уже повешенной заявкой — чтобы не дублировать спавн
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
        /// Обрабатываем очередь спавна каждый тик. Спавним столько сущностей, сколько позволяет лимит.
        /// </summary>
        private void OnGameTick(float dt)
        {
            SpawnRequest request;
            Block block;
            Entity existing;
            EntityBlockFalling entityBf;

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
                entityBf = new EntityBlockFalling(
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

        /// <summary>
        /// Просит блок упасть.
        /// Если игрок рядом — создаёт сущность (в очередь, если достигнут лимит).
        /// Если никого нет — прогоняет мгновенную симуляцию без создания сущности.
        /// </summary>
        public void RequestSpawn(Block block, BlockEntity be, BlockPos initialPos,
                                 AssetLocation fallSound, float impactDamageMul,
                                 bool canFallSideways, float dustIntensity,
                                 bool doRemoveBlock = true, Vec3d positionOffset = null)
        {
            // Пропускаем дубликаты — для этой позиции уже есть заявка
            if (pendingPositions.Contains(initialPos))
                return;

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

            if (!hasPlayerNearby)
            {
                // Игроков рядом нет — сущность создавать не надо, прогоняем мгновенную симуляцию
                InstantFallSimulation(block, be, initialPos, fallSound, impactDamageMul,
                                      canFallSideways, dustIntensity, doRemoveBlock, positionOffset);
                return;
            }

            // Игрок рядом — ставим заявку в очередь на полное создание сущности
            pendingPositions.Add(initialPos);
            requestQueue.Enqueue(new SpawnRequest
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
            });
        }

        /// <summary>
        /// Простой оберточный метод: берёт дропы и передаёт симуляцию в статический метод EntityBlockFalling.
        /// </summary>
        private void InstantFallSimulation(Block block, BlockEntity be, BlockPos initialPos,
                                           AssetLocation fallSound, float impactDamageMul,
                                           bool canFallSideways, float dustIntensity,
                                           bool doRemoveBlock, Vec3d positionOffset)
        {
            var drops = block.GetDrops(sapi.World, initialPos, null);
            EntityBlockFallingPatch.SimulateInstantFall(sapi.World, block, be, initialPos, drops, doRemoveBlock);
        }


        public override void Dispose()
        {
            // При выгрузке мода чистим очередь и отвязываем события, чтобы не держать ссылки на объекты мира
            requestQueue?.Clear();
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
        /// Данные одной заявки на спавн в очереди.
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