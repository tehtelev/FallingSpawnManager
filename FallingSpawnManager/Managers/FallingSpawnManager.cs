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
    /// в очередь и прогоняет мгновенную симуляцию для блоков за пределами видимости игроков.
    ///
    /// Мгновенная симуляция (SimulateInstantFall) выполняется не синхронно внутри
    /// RequestSpawn, а через отдельную очередь с лимитом на тик (instantQueue /
    /// maxInstantPerTick). pendingPositions применяется как guard для обеих веток:
    /// и для очереди сущностей, и для мгновенной очереди. Это защищает от повторной
    /// обработки одной и той же позиции и от каскада синхронных SetBlock/ExchangeBlock
    /// вызовов при массовом обрушении, которое забивало главный поток на десятки секунд.
    ///
    /// Проверка " не занята ли позиция уже существующей сущностью" раньше делалась
    /// через sapi.World.GetNearestEntity(...): пространственный запрос по партиционированию
    /// мира (проход по entity-партициям в радиусе плюс аллокации и лямбда на каждую попавшую
    /// сущность), вызывавшийся на заявку в очереди каждый тик. Теперь то же самое
    /// делается за O(1) через activeFallingPositions, набор initialPos всех сейчас активных
    /// EntityBlockFalling, который поддерживается в актуальном состоянии событиями
    /// OnEntitySpawn/OnEntityLoaded/OnEntityDespawn.
    ///
    /// Это статический менеджер со своим чистым функционалом. Инициализацию мода,
    /// загрузку конфигов и применение патчей курирует FSM (FSM.cs).
    /// </summary>
    public static class FallingSpawnManager
    {
        // Флаг инициализации, чтобы не регистрировать события повторно при перезагрузке мода
        private static bool _initialized = false;

        // Общее число загруженных сущностей EntityBlockFalling на сервере
        private static int totalFallingBlocks = 0;

        // Набор initialPos всех сейчас активных (заспавненных) EntityBlockFalling.
        // Заполняется в OnEntitySpawn/OnEntityLoaded, чистится в OnEntityDespawn, симметрично
        // totalFallingBlocks выше. Заменяет дорогой sapi.World.GetNearestEntity(...)
        // в ProcessEntityQueue: O(1) HashSet.Replace вместо пространственного запроса.
        private static HashSet<BlockPos> activeFallingPositions = [];

        // Заявки на спавн сущностей, которые ждут свободного слота.
        // Список со случайным выбором элемента (см. PopRandom) вместо строгого FIFO даёт
        // более естественный порядок обвала, сохраняя O(1) на извлечение через приём
        // "поменять с последним и убрать последний".
        private static List<SpawnRequest> requestQueue = [];

        // Отдельная очередь для заявок на мгновенную симуляцию (когда игрока рядом нет).
        // Такие заявки обрабатываются порциями в ProcessInstantQueue, а не синхронно внутри RequestSpawn.
        private static List<SpawnRequest> instantQueue = [];

        // Общий генератор случайных чисел для выбора следующей заявки на обработку.
        // Доступ только из серверного тика (однопоточно), поэтому обычный Random безопасен.
        private static readonly Random _rng = new Random();


        // Сколько мгновенных симуляций разрешено прогонять за один тик менеджера.
        // Ограничивает нагрузку на главный поток при массовых обрушениях вне зоны видимости игроков.
        // Настраивается через конфиг (см. FSMConfig.MaxInstantPerTick).
        public static int maxInstantPerTick;

        // Позиции, у которых уже есть заявка, чтобы не дублировать спавн.
        // Сюда попадают позиции из обеих очередей (requestQueue и instantQueue), что и есть
        // guard от реентерабельной обработки одной и той же позиции.
        private static HashSet<BlockPos> pendingPositions = [];

        private static ICoreServerAPI sapi;

        // Радиус вокруг игрока, в котором создаются сущности (в блоках)
        private static int activeRange = 128;

        // Максимальное число падающих блоков
        public static int maxFallingLimit;

        // Сторожевой таймер (мс) против известного vanilla-бага "Falling block
        // entity does not settle" (форум VS #5775): падающие сущности при массовом
        // обвале могут скучиваться и физически застревать друг об друга, никогда не
        // приземляясь сами, даже рядом с игроком. Используется в EntityBlockFallingPatch:
        // если сущность жива дольше этого времени и всё ещё не приземлилась, её
        // принудительно укладывают на текущей позиции. 0 отключает таймер полностью.
        public static int stuckTimeoutMs;


        /// <summary>
        /// Инициализация менеджера: санитизация настроек из конфига, регистрация
        /// событий мира и тик-листенер очередей. Вызывается FSM после применения патчей.
        /// </summary>
        public static void Initialize(ICoreServerAPI api, FSMConfig cfg)
        {
            if (_initialized)
                return; // защита от повторного старта/патчинга

            sapi = api;

            // Обрезаем значение до валидного диапазона
            maxFallingLimit = Math.Clamp(cfg.MaxFallingLimit, 10, 10000);

            // MaxInstantPerTick настраивается из config (по умолчанию 100). Нижняя граница 1,
            // значение 0 отключило бы троттлинг и вернуло старое поведение "выполнить всё разом",
            // которое и вызывала лаг. Верхнюю границу берём с запасом.
            maxInstantPerTick = Math.Clamp(cfg.MaxInstantPerTick, 1, 10000);

            // StuckTimeoutMs настраивается из config (по умолчанию 15000 мс = 15 сек).
            // В отличие от MaxInstantPerTick, здесь 0 является валидным значением и отключает
            // сторожевой таймер полностью (см. EntityBlockFallingPatch.OnGameTick).
            // Верхнюю границу берём в 2 минуты, больше не имеет практического смысла.
            stuckTimeoutMs = Math.Clamp(cfg.StuckTimeoutMs, 0, 120000);

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

            // Тик очереди спавна каждые 32 мс
            api.Event.RegisterGameTickListener(OnGameTick, 32);

            _initialized = true;
        }

        /// <summary>
        /// Очистка менеджера при выгрузке мода: чистим очереди и отвязываем события,
        /// чтобы не держать ссылки на объекты мира. Патчи снимает fsm
        /// </summary>
        public static void Dispose()
        {
            requestQueue?.Clear();
            instantQueue?.Clear();
            pendingPositions?.Clear();
            activeFallingPositions?.Clear(); // чистим и набор активных позиций
            totalFallingBlocks = 0;
            if (sapi != null)
            {
                sapi.Event.OnEntitySpawn -= OnEntitySpawn;
                sapi.Event.OnEntityLoaded -= OnEntityLoaded;
                sapi.Event.OnEntityDespawn -= OnEntityDespawn;
            }

            _initialized = false; // для повторного старта после релоада
        }


        public static bool IsPositionActiveOrPending(BlockPos pos)
        {
            return pendingPositions.Contains(pos) || activeFallingPositions.Contains(pos);
        }


        /// <summary>
        /// Достаёт случайный элемент из списка за O(1): меняет местами с последним
        /// и удаляет последний. Порядок оставшихся элементов при этом не сохраняется,
        ///но нам порядок и не нужен, это и весь смысл.
        /// </summary>
        private static SpawnRequest PopRandom(List<SpawnRequest> list)
        {
            int index = _rng.Next(list.Count);
            SpawnRequest picked = list[index];
            int lastIndex = list.Count - 1;
            list[index] = list[lastIndex];
            list.RemoveAt(lastIndex);
            return picked;
        }

        // Считаем только EntityBlockFalling, а не живых существ.
        // Помимо счётчика totalFallingBlocks, теперь также поддерживаем
        // activeFallingPositions, набор initialPos активных сущностей, который
        // заменяет дорогой пространственный запрос в ProcessEntityQueue.
        private static void OnEntitySpawn(Entity entity)
        {
            if (entity.IsCreature || entity is not EntityBlockFalling ebf) return;
            totalFallingBlocks++;
            activeFallingPositions.Add(ebf.initialPos);
        }

        private static void OnEntityLoaded(Entity entity)
        {
            if (entity.IsCreature || entity is not EntityBlockFalling ebf) return;
            totalFallingBlocks++;
            activeFallingPositions.Add(ebf.initialPos);
        }

        private static void OnEntityDespawn(Entity entity, EntityDespawnData despawn)
        {
            if (entity.IsCreature || entity is not EntityBlockFalling ebf) return;
            totalFallingBlocks--;
            activeFallingPositions.Remove(ebf.initialPos);
        }

        /// <summary>
        /// Обрабатываем очереди спавна каждый тик:
        ///   1) очередь сущностей ограничена maxFallingLimit;
        ///   2).queue мгновенных симуляций ограничена maxInstantPerTick, чтобы массовое
        ///      обрушение вне зоны видимости игроков размазилось по многим тикам,
        ///      а не выполнялось одним синхронным ударом.
        /// </summary>
        private static void OnGameTick(float dt)
        {
            ProcessEntityQueue();
            ProcessInstantQueue();
        }

        private static void ProcessEntityQueue()
        {
            SpawnRequest request;
            Block block;

            while (totalFallingBlocks < maxFallingLimit && requestQueue.Count > 0)
            {
                request = PopRandom(requestQueue);

                block = sapi.World.BlockAccessor.GetBlock(request.InitialPos);
                if (block == null || block.Id == 0 || block != request.Block)
                {
                    pendingPositions.Remove(request.InitialPos);   // блок уже не тот, снимаем guard
                    continue;
                }

                bool existing = activeFallingPositions.Contains(request.InitialPos);
                if (existing)
                {
                    request.RetryCount++;
                    if (request.RetryCount >= 300)
                    {
                        pendingPositions.Remove(request.InitialPos);
                        var drops = request.Block.GetDrops(sapi.World, request.InitialPos, null);
                        EntityBlockFallingPatch.SpawnDrops(sapi.World, request.InitialPos, drops, request.BlockEntity);
                        continue;
                    }
                    // guard остаётся висеть, заявка всё ещё «в работе»
                    requestQueue.Add(request);
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

                // только теперь снимаем guard, entity уже в activeFallingPositions (OnEntitySpawn отработал)
                pendingPositions.Remove(request.InitialPos);

            }
        }

        // Метод обработки очереди мгновенных симуляций с лимитом на тик.
        private static void ProcessInstantQueue()
        {
            int processed = 0;

            while (processed < maxInstantPerTick && instantQueue.Count > 0)
            {
                var request = PopRandom(instantQueue);

                pendingPositions.Remove(request.InitialPos);

                Block currentBlock = sapi.World.BlockAccessor.GetBlock(request.InitialPos);
                if (currentBlock == null || currentBlock.Id == 0 || currentBlock != request.Block)
                {
                    processed++;
                    continue;
                }

                var drops = request.Block.GetDrops(sapi.World, request.InitialPos, null);

                // Передаём СНИМОК (BlockEntityTree), а не живой BE. Живой BE передаётся
                // только ради DropContents контейнера — если блок не сможет лечь на место.
                EntityBlockFallingPatch.SimulateInstantFall(
                    sapi.World,
                    request.Block,
                    request.BlockEntity,
                    request.BlockEntityTree,
                    request.InitialPos,
                    drops,
                    request.DoRemoveBlock);

                processed++;
            }
        }

        /// <summary>
        /// Просит блок упасть.
        /// Если игрок рядом, создаёт заявку на спавн сущности (в очередь, если достигнут лимит).
        /// Если игрока нет рядом — ставит заявку в队列 мгновенной симуляции.
        /// </summary>
        public static void RequestSpawn(Block block, BlockEntity be, BlockPos initialPos,
            AssetLocation fallSound, float impactDamageMul,
            bool canFallSideways, float dustIntensity,
            bool doRemoveBlock = true, Vec3d positionOffset = null)
        {
            if (pendingPositions.Contains(initialPos))
                return;

            pendingPositions.Add(initialPos);

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

            // Снимок BE делаем ЗДЕСЬ, пока блок ещё не снят и инвентарь ещё в исходном
            // состоянии. Для instant-пути это критично: там нет EntityBlockFalling.Initialize,
            // который бы сделал снимок сам, а живой BE к моменту обработки очереди
            // может быть уже изменён/удалён (см. BlockEntityCoalPile.TryPartialCollapse).
            TreeAttribute beTree = null;
            if (be != null)
            {
                beTree = new TreeAttribute();
                be.ToTreeAttributes(beTree);
            }

            var request = new SpawnRequest
            {
                Block = block,
                BlockEntity = be,
                BlockEntityTree = beTree,
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
                instantQueue.Add(request);
                return;
            }

            requestQueue.Add(request);
        }
    }
}