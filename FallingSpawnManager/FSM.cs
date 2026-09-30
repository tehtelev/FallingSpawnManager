using FSMMgr.Managers;
using FSMMgr.Patches;
using FSMMgr.Utils;
using HarmonyLib;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace FSMMgr
{
    /// <summary>
    /// Точка входа мода (ModSystem). Отвечает за:
    ///   - инициализацию мода (только на серверной стороне);
    ///   - загрузку всех настроек конфигов;
    ///   - применение и снятие Harmony-патчей;
    ///   - очистку мусора при выгрузке (диспоз обоих менеджеров).
    ///
    /// Весь функционал менеджеров в папке Managers:
    ///   FallingSpawnManager — спаун падающих блоков;
    ///   ItemClumpManager    — слияние стаков предметов.
    /// </summary>
    public class FSM : ModSystem
    {
        // Флаг инициализации, чтобы не регистрировать события/патчи повторно при перезагрузке мода
        private bool _initialized = false;

        // Harmony-инстанс мода
        private Harmony? harmony;

        // Загруженная конфигурация мода
        private FSMConfig? _config;

        /// <summary>
        /// Серверный мод, клиенту не нужен.
        /// </summary>
        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Server;
        }

        /// <summary>
        /// Загрузка конфигурации мода. Если файла нет или он с ошибкой — берём значения по умолчанию.
        /// Санитизация конкретных значений — ответственность каждого менеджера (см. Initialize).
        /// </summary>
        public override void StartPre(ICoreAPI api)
        {
            _config = api.LoadModConfig<FSMConfig>("FallingSpawnManagerConfig.json") ?? new FSMConfig();
            api.StoreModConfig(_config, "FallingSpawnManagerConfig.json");
        }

        /// <summary>
        /// Применение патчей и инициализация всех менеджеров.
        /// </summary>
        public override void StartServerSide(ICoreServerAPI api)
        {
            if (_initialized)
                return; // защита от повторного старта/патчинга

            harmony = new Harmony("fallingspawnmanager");

            // Применяем все патчи мода
            RegisterPatches(api);

            // Инициализируем менеджеры — каждый сам санитизирует свои настройки из конфига
            FallingSpawnManager.Initialize(api, _config);
            ItemClumpManager.Initialize(api, _config);

            _initialized = true;
        }

        /// <summary>
        /// Регистрация всех Harmony-патчей.
        /// </summary>
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

        /// <summary>
        /// Очистка мусора при выгрузке мода: диспоз обоих менеджеров и снятие всех патчей.
        /// </summary>
        public override void Dispose()
        {
            // Очистка мусора модов
            ItemClumpManager.Dispose();
            FallingSpawnManager.Dispose();

            // Снимаем все Harmony-патчи, применённые этим модом
            harmony?.UnpatchAll("fallingspawnmanager");

            _initialized = false; // для повторного старта после релоада
        }
    }
}