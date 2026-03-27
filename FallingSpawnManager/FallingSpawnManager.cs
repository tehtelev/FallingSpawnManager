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


[assembly: ModDependency("game", "1.22.0-rc.5")]
[assembly: ModInfo(
    "Falling blocks spawn manager",
    "fallingspawnmanager",
    Website = "https://github.com/tehtelev/FallingSpawnManager",
    Description = "Limits the number of blocks falling at the same time.",
    Version = "0.1.0",
    Authors = new[] { "Tehtelev" }
)]



namespace FallingSpawnManager
{
    /// <summary>
    /// Server-side spawn manager for falling blocks.
    /// Limits the number of concurrently existing EntityBlockFalling instances,
    /// queues spawn requests, and performs instant simulation for blocks outside player range.
    /// </summary>
    public class FallingSpawnManager : ModSystem
    {
        // Total number of loaded EntityBlockFalling instances on the server
        private static int totalFallingBlocks = 0;
        // Queue of spawn requests waiting for a free slot
        private static Queue<SpawnRequest> requestQueue = new Queue<SpawnRequest>();
        // Positions that already have a pending request — prevents duplicates
        private static HashSet<BlockPos> pendingPositions = new HashSet<BlockPos>();

        private static ICoreServerAPI sapi;
        // Radius around a player within which entities are created (in blocks)
        private static int activeRange = 128;
        // патчи Harmony
        private Harmony harmony;
        // Конфигурация мода, загружается при старте
        private FSMConfig? _config;
        // максимальное число падающих блоков
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
            // грузим конфиг
            // если конфиг с ошибкой или не найден, то генерируется стандартный
            _config = api.LoadModConfig<FSMConfig>("FallingSpawnManagerConfig.json") ?? new FSMConfig();
            api.StoreModConfig(_config, "FallingSpawnManagerConfig.json");

            // проверяем, что конфиг валиден, и обрезаются значения
            maxFallingLimit = Math.Clamp(_config.MaxFallingLimit, 10, 10000);


        }


        public override void Start(ICoreAPI api)
        {
            harmony = new Harmony("fallingspawnmanager");

            // Регистрация всех патчей
            RegisterPatches(api);
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
            sapi = api;

            // Determine entity tracking radius from server settings
            try
            {
                int trackingChunks = api.World.DefaultEntityTrackingRange;
                activeRange = trackingChunks * GlobalConstants.ChunkSize;
            }
            catch { /* keep default of 128 */ }

            api.Event.OnEntitySpawn += OnEntitySpawn;
            api.Event.OnEntityLoaded += OnEntityLoaded;
            api.Event.OnEntityDespawn += OnEntityDespawn;
            // Spawn queue tick — every 32 ms
            api.Event.RegisterGameTickListener(OnGameTick, 32);
        }

        // Counters: only track EntityBlockFalling, not living creatures
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
        /// Process the spawn queue each game tick.
        /// Spawn as many entities as the limit allows.
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

                // Verify the block at the source position hasn't changed while the request was queued
                block = sapi.World.BlockAccessor.GetBlock(request.InitialPos);
                if (block == null || block.Id == 0 || block != request.Block)
                    continue;

                // If a falling entity already exists at this position — defer the spawn
                existing = sapi.World.GetNearestEntity(
                    request.InitialPos.ToVec3d().Add(0.5, 0.5, 0.5), 1, 1.5f,
                    e => !e.IsCreature && e is EntityBlockFalling ebf && ebf.initialPos.Equals(request.InitialPos));

                if (existing != null)
                {
                    request.RetryCount++;
                    if (request.RetryCount >= 300) // ~10 seconds at 32 ms tick interval
                    {
                        // After 300 failed attempts — give up and drop items
                        var drops = request.Block.GetDrops(sapi.World, request.InitialPos, null);
                        EntityBlockFallingPatch.SpawnDrops(sapi.World, request.InitialPos, drops, request.BlockEntity);
                        continue;
                    }
                    // Return the request to the back of the queue for another attempt
                    pendingPositions.Add(request.InitialPos);
                    requestQueue.Enqueue(request);
                    continue;
                }

                // Create the entity and apply position offset if specified
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
        /// Requests a block to fall.
        /// If a player is nearby — creates an entity (queued if at the limit).
        /// If no players are nearby — performs instant simulation without an entity.
        /// </summary>
        public void RequestSpawn(Block block, BlockEntity be, BlockPos initialPos,
                                 AssetLocation fallSound, float impactDamageMul,
                                 bool canFallSideways, float dustIntensity,
                                 bool doRemoveBlock = true, Vec3d positionOffset = null)
        {
            // Skip duplicates — this position already has a pending request
            if (pendingPositions.Contains(initialPos))
                return;

            // Check whether at least one player is within activeRange
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
                // No players nearby — no need to create an entity, simulate instantly
                InstantFallSimulation(block, be, initialPos, fallSound, impactDamageMul,
                                      canFallSideways, dustIntensity, doRemoveBlock, positionOffset);
                return;
            }

            // Player is nearby — add to queue to create a full entity
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
        /// Thin wrapper: retrieves drops and delegates simulation to the static EntityBlockFalling method.
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
            // On mod unload, clear the queue and unsubscribe from events to avoid holding world object references
            requestQueue?.Clear();
            pendingPositions?.Clear();
            totalFallingBlocks = 0;
            if (sapi != null)
            {
                sapi.Event.OnEntitySpawn -= OnEntitySpawn;
                sapi.Event.OnEntityLoaded -= OnEntityLoaded;
                sapi.Event.OnEntityDespawn -= OnEntityDespawn;
            }

            // Unpatch all Harmony patches applied by this mod
            harmony?.UnpatchAll("fallingspawnmanager");
        }

        /// <summary>
        /// Data for a single spawn request held in the queue.
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
            public int RetryCount; // How many times this request has been returned to the queue due to a occupied position
        }



    }


    /// <summary>
    /// Конфигуратор сети
    /// </summary>
    public class FSMConfig
    {
        public int MaxFallingLimit = 500;
    }
}