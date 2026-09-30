using FallingSpawnManager.Patches;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
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
	/// </summary>
	public class FallingSpawnManager : ModSystem
	{
		// Флаг инициализации, чтобы не регистрировать события повторно при перезагрузке мода
		private bool _initialized = false;

		// Общее число загруженных сущностей EntityBlockFalling на сервере
		private static int totalFallingBlocks = 0;

		// Набор initialPos всех сейчас активных (заспавненных) EntityBlockFalling.
		// Заполняется в OnEntitySpawn/OnEntityLoaded, чистится в OnEntityDespawn, симметрично
		// totalFallingBlocks выше. Заменяет дорогой sapi.World.GetNearestEntity(...)
		// в ProcessEntityQueue: O(1) HashSet.Contains вместо пространственного запроса.
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

		// Harmony-патчи
		private Harmony harmony;

		// Конфигурация мода, загружается при старте
		private FSMConfig? _config;

		// Максимальное число падающих блоков
		public static int maxFallingLimit;

		// Сторожевой таймер (мс) против известного vanilla-бага "Falling block
		// entity does not settle" (форум VS #5775): падающие сущности при массовом
		// обвале могут скучиваться и физически застревать друг об друга, никогда не
		// приземляясь сами, даже рядом с игроком. Используется в EntityBlockFallingPatch:
		// если сущность жива дольше этого времени и всё ещё не приземлилась, её
		// принудительно укладывают на текущей позиции. 0 отключает таймер полностью.
		public static int stuckTimeoutMs;


		// Запуск только на стороне сервера, клиенту мод не нужен
		public override bool ShouldLoad(EnumAppSide forSide)
		{
			return forSide == EnumAppSide.Server;
		}

        public static bool IsPositionActiveOrPending(BlockPos pos)
        {
            return pendingPositions.Contains(pos) || activeFallingPositions.Contains(pos);
        }


		/// <summary>
		/// Достаёт случайный элемент из списка за O(1): меняет местами с последним
		/// и удаляет последний. Порядок оставшихся элементов при этом не сохраняется,
		/// но нам порядок и не нужен, это и есть весь смысл.
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


		/// <summary>
		/// Загрузка конфигурации и начальная инициализация
		/// </summary>
		/// <param name="api"></param</param>
		public override void StartPre(ICoreAPI api)
		{
			// Грузим конфиг. Если его нет или он с ошибкой, берём значения по умолчанию
			_config = api.LoadModConfig<FSMConfig>("FallingSpawnManagerConfig.json") ?? new FSMConfig();
			api.StoreModConfig(_config, "FallingSpawnManagerConfig.json");

			// Обрезаем значение до валидного диапазона
			maxFallingLimit = Math.Clamp(_config.MaxFallingLimit, 10, 10000);

			// MaxInstantPerTick настраивается из config (по умолчанию 100). Нижняя граница 1,
			// значение 0 отключило бы троттлинг и вернуло старое поведение "выполнить всё разом",
			// которое и вызывало лаг. Верхнюю границу берём с запасом.
			maxInstantPerTick = Math.Clamp(_config.MaxInstantPerTick, 1, 10000);

			// StuckTimeoutMs настраивается из config (по умолчанию 15000 мс = 15 сек).
			// В отличие от MaxInstantPerTick, здесь 0 является валидным значением и отключает
			// сторожевой таймер полностью (см. EntityBlockFallingPatch.OnGameTick).
			// Верхнюю границу берём в 2 минуты, больше не имеет практического смысла.
			stuckTimeoutMs = Math.Clamp(_config.StuckTimeoutMs, 0, 120000);
		}


		/// <summary>
		/// Регистрация всех патчей с помощью Harmony
		/// </summary>
		/// <param name="api"></param</param>
		private void RegisterPatches(ICoreAPI api)
		{
			// EntityBlockFalling.Initialize
			var initMethod = AccessTools.Method(typeof(EntityBlockFalling), "Initialize",
				[typeof(EntityProperties), typeof(ICoreAPI), typeof(long)]);
			if (initMethod != null)
				harmony.Patch(initMethod, postfix: new HarmonyMethod(typeof(EntityBlockFallingPatch), nameof(EntityBlockFallingPatch.EntityBlockFalling_Initialize_Postfix)));
			else
				api.Logger.Error("Initialize not found");

			// EntityBlockFalling.OnGameTick
			var tickMethod = AccessTools.Method(typeof(EntityBlockFalling), "OnGameTick", [typeof(float)]);
			if (tickMethod != null)
				harmony.Patch(tickMethod, postfix: new HarmonyMethod(typeof(EntityBlockFallingPatch), nameof(EntityBlockFallingPatch.EntityBlockFalling_OnGameTick_Postfix)));
			else
				api.Logger.Error("OnGameTick not found");

			// EntityBlockFalling.OnEntityDespawn чистит запись в ConcurrentDictionary<long, ExtraData>
			// (замена ConditionalWeakTable), что предотвращает утечки памяти на каждую
			// упавшую сущность.
			var despawnMethod = AccessTools.Method(typeof(EntityBlockFalling), "OnEntityDespawn",
				[typeof(EntityDespawnData)]);
			if (despawnMethod != null)
				harmony.Patch(despawnMethod, postfix: new HarmonyMethod(typeof(EntityBlockFallingPatch), nameof(EntityBlockFallingPatch.EntityBlockFalling_OnEntityDespawn_Postfix)));
			else
				api.Logger.Error("OnEntityDespawn not found");

			// BlockBehaviorUnstableRock.collapseLayer
			var collapseMethod = AccessTools.Method(typeof(BlockBehaviorUnstableRock), "collapseLayer",
				[typeof(IWorldAccessor), typeof(IOrderedEnumerable<BlockPos>), typeof(int)]);
			if (collapseMethod != null)
				harmony.Patch(collapseMethod, prefix: new HarmonyMethod(typeof(BlockBehaviorUnstableRockPatch), nameof(BlockBehaviorUnstableRockPatch.collapseLayer_Prefix)));
			else
				api.Logger.Error("collapseLayer not found");

			// BlockBehaviorUnstableFalling.createFallingBlock
			var tryFallingMethod = AccessTools.Method(typeof(BlockBehaviorUnstableFalling), "createFallingBlock",
				[typeof(IWorldAccessor), typeof(BlockPos)]);
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

			// Тик очереди спавна каждые 32 мс
			api.Event.RegisterGameTickListener(OnGameTick, 32);

			// Слияние лежащих стаков предметов (любые EntityItem в мире)
			ItemClumpManager.Initialize(api, _config);

			_initialized = true;

		}

		// Считаем только EntityBlockFalling, а не живых существ.
		// Помимо счётчика totalFallingBlocks, теперь также поддерживаем
		// activeFallingPositions, набор initialPos активных сущностей, который
		// заменяет дорогой пространственный запрос в ProcessEntityQueue.
		private void OnEntitySpawn(Entity entity)
		{
			if (entity.IsCreature || entity is not EntityBlockFalling ebf) return;
			totalFallingBlocks++;
			activeFallingPositions.Add(ebf.initialPos);
		}

		private void OnEntityLoaded(Entity entity)
		{
			if (entity.IsCreature || entity is not EntityBlockFalling ebf) return;
			totalFallingBlocks++;
			activeFallingPositions.Add(ebf.initialPos);
		}

		private void OnEntityDespawn(Entity entity, EntityDespawnData despawn)
		{
			if (entity.IsCreature || entity is not EntityBlockFalling ebf) return;
			totalFallingBlocks--;
			activeFallingPositions.Remove(ebf.initialPos);
		}

		/// <summary>
		/// Обрабатываем очереди спавна каждый тик:
		///   1) очередь сущностей ограничена maxFallingLimit;
		///   2).queue мгновенных симуляций ограничена maxInstantPerTick, чтобы массовое
		///      обрушение вне зоны видимости игроков размазывалось по многим тикам,
		///      а не выполнялось одним синхронным ударом.
		/// </summary>
		private void OnGameTick(float dt)
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
		/// никого рядом — ставит заявку в очередь мгновенной симуляции.
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

		public override void Dispose()
		{
			// При выгрузке мода чистим очереди и отвязываем события, чтобы не держать ссылки на объекты мира
			requestQueue?.Clear();
			instantQueue?.Clear();
			pendingPositions?.Clear();
			activeFallingPositions?.Clear(); // чистим и набор активных позиций
			ItemClumpManager.Dispose();
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
		/// и для instantQueue. RetryCount актуален только для requestQueue).
		/// </summary>
        private struct SpawnRequest
        {
            public Block Block;
            public BlockEntity BlockEntity;

            // Снимок атрибутов BE, сделанный в момент RequestSpawn.
            // Нужен для instant-пути: пока заявка ждёт в очереди, живой BE мог
            // измениться или умереть, а OnFallOnto/FromTreeAttributes нужен
            // именно тот снимок, что был при постановке заявки.
            public TreeAttribute BlockEntityTree;

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

		// Сколько мгновенных симуляций падения (для блоков вне зоны видимости
		// игроков) разрешено прогонять за один тик менеджера. Раньше было жёстко
		// зашито константой (20), теперь настраивается.
		public int MaxInstantPerTick = 100;

		// Сторожевой таймер (мс) против известного vanilla-бага "Falling block
		// entity does not settle": падающие сущности могут физически застревать при
		// массовом obвале и никогда не приземляться сами. Если сущность жива дольше
		// этого времени и ещё не приземлилась, она принудительно укладывается на
		// текущей позиции. 0 отключает таймер полностью.
		public int StuckTimeoutMs = 15000;

		// ----- Слияние предметов на земле (ItemClumpManager) -----

		// Включает слияние лежащих стаков одного типа.
		public bool ItemClumpEnabled = true;

		// Радиус поиска соседних предметов (блоки): по горизонтали и по вертикали.
		public float ItemClumpRadius = 5f;
		public float ItemClumpVerticalRadius = 5.0f;

		// Пауза (мс) после спавна/загрузки предмета и между повторными попытками,
		// если он ещё не успокоился.
		public int ItemClumpDelayMs = 1500;

		// Сколько раз повторять попытку, пока предмет не лёг на землю.
		public int ItemClumpMaxSettleAttempts = 20;

		// Бюджет попыток слияния за один тик менеджера (250 мс).
		public int ItemClumpMaxAttemptsPerTick = 25;

		// Максимум предметов в одной группе слияния.
		public int ItemClumpMaxGroupSize = 32;

		// Полные коды предметов/блоков, которые не сливаются. Поддерживается '*':
		// "game:gear-*" или "@regex". Регистр не важен, код без домена считается ванильным.
		public List<string> ItemClumpBlacklist = [];
	}
}