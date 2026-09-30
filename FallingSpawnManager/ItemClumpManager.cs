using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

#nullable disable

namespace FallingSpawnManager
{
    /// <summary>
    /// Серверный менеджер слияния лежащих на земле стаков предметов (EntityItem).
    /// Работает с ЛЮБЫМИ EntityItem в мире, независимо от того, кто и как их создал
    /// (наш SpawnDrops, игрок, другие моды, ваниль).
    ///
    /// Архитектура (принципиально отличается от подхода «патч на каждое столкновение»):
    ///   * Никаких Harmony-патчей. Жизненный цикл предметов отслеживается через события
    ///     OnEntitySpawn / OnEntityLoaded / OnEntityDespawn, которые мы и так используем.
    ///     Нет хука на Entity.OnCollided (он вызывается для ВСЕХ сущностей каждый тик).
    ///   * Одна общая очередь кандидатов и один тик-листенер вместо RegisterCallback
    ///     на каждый предмет. Очередь монотонна по времени (задержка у всех одинаковая),
    ///     поэтому достаточно обычной Queue: смотрим только на голову.
    ///   * Бюджет попыток на тик (MaxAttemptsPerTick): при загрузке мира с тысячами
    ///     предметов нагрузка размазывается, а не бьёт по одному тику.
    ///   * Кандидатом становится только «новичок» (заспавнился / загрузился). Он сам ищет
    ///     соседей и вливается в них. Лежащие давно предметы повторно не сканируются.
    ///   * Маленький радиус по умолчанию (1.5 блока по горизонтали, 1 по вертикали):
    ///     предметы не «исчезают» с места, где лежали, и не сливаются между этажами.
    ///   * Упаковка «от большего к меньшему»: самые большие стаки принимают, а доноры
    ///     берутся от самых маленьких. Это минимизирует итоговое число сущностей.
    ///   * Чёрный список по полному коду коллектибла через ванильный WildcardUtil.Match
    ///     ('*', без учёта регистра, "@regex"), например "game:gear-*". Результат
    ///     кэшируется на CollectibleObject, строка Code.ToString() строится один раз.
    /// Слияние проходит через ItemSlot.TryPutInto + GetMergableQuantity, то есть все
    /// правила ванили (атрибуты, свежесть/transition-состояния) соблюдаются.
    /// </summary>
    public static class ItemClumpManager
    {
        // Период тика менеджера. Точность не важна, достаточно нескольких раз в секунду.
        private const int TickIntervalMs = 250;

        // Предмет считается «успокоившимся», если скорость не выше 0.075 блока/тик.
        private const double SettledMotionSq = 0.075 * 0.075;

        private sealed class Candidate
        {
            public EntityItem Entity;
            public long EntityId;
            public long DueMs;
            public int Attempts;
            public bool Cancelled;
        }

        private static ICoreServerAPI sapi;
        private static long tickListenerId;
        private static bool running;

        // Очередь по времени готовности + индекс по EntityId для O(1) дедупликации/отмены.
        private static readonly Queue<Candidate> queue = new();
        private static readonly Dictionary<long, Candidate> byId = new();

        // Переиспользуемые буферы (серверный тик однопоточный).
        private static readonly List<EntityItem> group = new();

        // Кэш результата чёрного списка на коллектибл.
        private static readonly Dictionary<CollectibleObject, bool> blacklistCache = new();
        private static string[] blacklistPatterns = [];

        // Параметры (санитизируются в Initialize).
        private static float radius;
        private static float verticalRadius;
        private static int delayMs;
        private static int maxSettleAttempts;
        private static int maxAttemptsPerTick;
        private static int maxGroupSize;

        // Радиус вокруг игрока, дальше которого предметы считаются «замороженными»
        // (физика их не тикает, скорость и OnGround остаются как при спавне).
        // Берём меньшее из радиуса слежения сервера и дальности симуляции сущностей.
        private static int frozenRange;

        // Параметр для статического предиката поиска (без лямбды-замыкания на каждый вызов).
        private static CollectibleObject matchCollectible;
        private static readonly ActionConsumable<Entity> matcher = MatchEntity;

        private static readonly Comparison<EntityItem> bySizeDesc = CompareBySizeDesc;

        // ---------------------------------------------------------------------
        //  Жизненный цикл
        // ---------------------------------------------------------------------

        public static void Initialize(ICoreServerAPI api, FSMConfig cfg)
        {
            if (running) return;
            if (cfg == null || !cfg.ItemClumpEnabled) return;

            sapi = api;

            radius = Math.Clamp(cfg.ItemClumpRadius, 0.25f, 8f);
            verticalRadius = Math.Clamp(cfg.ItemClumpVerticalRadius, 0.25f, 8f);
            delayMs = Math.Clamp(cfg.ItemClumpDelayMs, 250, 60000);
            maxSettleAttempts = Math.Clamp(cfg.ItemClumpMaxSettleAttempts, 1, 200);
            maxAttemptsPerTick = Math.Clamp(cfg.ItemClumpMaxAttemptsPerTick, 1, 1000);
            maxGroupSize = Math.Clamp(cfg.ItemClumpMaxGroupSize, 2, 256);
            blacklistPatterns = NormalizeBlacklist(cfg.ItemClumpBlacklist);
            blacklistCache.Clear();

            int trackingRange = GlobalConstants.DefaultSimulationRange;
            try { trackingRange = Math.Min(trackingRange, api.World.DefaultEntityTrackingRange * GlobalConstants.ChunkSize); }
            catch { /* оставляем дальность симуляции */ }
            frozenRange = Math.Max(trackingRange, GlobalConstants.ChunkSize);

            api.Event.OnEntitySpawn += OnEntitySpawn;
            api.Event.OnEntityLoaded += OnEntitySpawn;      // одинаковая логика для спавна и загрузки
            api.Event.OnEntityDespawn += OnEntityDespawn;
            tickListenerId = api.Event.RegisterGameTickListener(OnTick, TickIntervalMs);

            running = true;
        }

        public static void Dispose()
        {
            if (sapi != null && running)
            {
                sapi.Event.OnEntitySpawn -= OnEntitySpawn;
                sapi.Event.OnEntityLoaded -= OnEntitySpawn;
                sapi.Event.OnEntityDespawn -= OnEntityDespawn;
                sapi.Event.UnregisterGameTickListener(tickListenerId);
            }

            queue.Clear();
            byId.Clear();
            group.Clear();
            blacklistCache.Clear();
            matchCollectible = null;
            sapi = null;
            running = false;
        }

        // ---------------------------------------------------------------------
        //  События
        // ---------------------------------------------------------------------

        private static void OnEntitySpawn(Entity entity)
        {
            if (entity is not EntityItem item) return;
            Enqueue(item);
        }

        private static void OnEntityDespawn(Entity entity, EntityDespawnData despawn)
        {
            if (entity is not EntityItem item) return;
            if (byId.Remove(item.EntityId, out Candidate c))
                c.Cancelled = true; // запись в очереди пропустится при извлечении
        }

        private static void Enqueue(EntityItem item)
        {
            if (byId.ContainsKey(item.EntityId)) return;

            var c = new Candidate
            {
                Entity = item,
                EntityId = item.EntityId,
                DueMs = sapi.World.ElapsedMilliseconds + delayMs
            };
            byId[c.EntityId] = c;
            queue.Enqueue(c);
        }

        // ---------------------------------------------------------------------
        //  Тик
        // ---------------------------------------------------------------------

        private static void OnTick(float dt)
        {
            long now = sapi.World.ElapsedMilliseconds;
            int budget = maxAttemptsPerTick;

            while (budget > 0 && queue.Count > 0)
            {
                Candidate c = queue.Peek();

                if (c.Cancelled)
                {
                    queue.Dequeue();
                    continue;
                }

                if (c.DueMs > now) break; // очередь монотонна, дальше тоже рано

                queue.Dequeue();
                budget--;
                Process(c, now);
            }
        }

        private static void Process(Candidate c, long now)
        {
            EntityItem e = c.Entity;

            if (!IsValid(e))
            {
                byId.Remove(c.EntityId);
                return;
            }

            // Предмет вдали от игроков не симулируется, он «заморожен» и не двигается:
            // ждать его успокоения бессмысленно (он никогда не успокоится), сливаем сразу.
            bool frozen = NoPlayerNear(e.ServerPos.XYZ);

            if (!frozen && !IsSettled(e))
            {
                // Ещё летит или движется: пробуем позже, но не бесконечно.
                if (++c.Attempts >= maxSettleAttempts)
                {
                    byId.Remove(c.EntityId);
                    return;
                }
                c.DueMs = now + delayMs;
                queue.Enqueue(c);
                return;
            }

            // Снимаем с учёта ДО слияния: если предмет останется, он может
            // снова стать кандидатом (например, при перезагрузке чанка).
            byId.Remove(c.EntityId);
            TryClump(e, frozen);
        }

        // ---------------------------------------------------------------------
        //  Слияние
        // ---------------------------------------------------------------------

        private static void TryClump(EntityItem root, bool frozen)
        {
            ItemStack rootStack = root.Slot.Itemstack;
            CollectibleObject coll = rootStack.Collectible;

            // Нестакуемый или уже полный стак ни принять, ни (осмысленно) отдать не может.
            if (coll.MaxStackSize <= 1 || rootStack.StackSize >= coll.MaxStackSize) return;
            if (IsBlacklisted(coll)) return;

            matchCollectible = coll;
            Entity[] found;
            try
            {
                found = sapi.World.GetEntitiesAround(root.ServerPos.XYZ, radius, verticalRadius, matcher);
            }
            finally
            {
                matchCollectible = null;
            }

            if (found == null || found.Length < 2) return;

            group.Clear();
            group.Add(root);
            for (int i = 0; i < found.Length && group.Count < maxGroupSize; i++)
            {
                if (found[i] is not EntityItem other || other == root) continue;
                if (!IsValid(other) || (!frozen && !IsSettled(other))) continue;
                group.Add(other);
            }

            if (group.Count < 2) { group.Clear(); return; }

            // Крупные стаки первыми (получатели), при равенстве по EntityId для детерминизма.
            group.Sort(bySizeDesc);

            // Доноры идут с конца (самые маленькие), получатели с начала (самые большие).
            for (int d = group.Count - 1; d > 0; d--)
            {
                EntityItem donor = group[d];

                for (int r = 0; r < d; r++)
                {
                    ItemStack ds = donor.Slot.Itemstack;
                    if (ds == null || ds.StackSize <= 0) break;

                    EntityItem recv = group[r];
                    ItemStack rs = recv.Slot.Itemstack;
                    if (rs == null) continue;

                    int space = rs.Collectible.MaxStackSize - rs.StackSize;
                    if (space <= 0) continue;

                    // Правила ванили: совпадение атрибутов, свежесть и т. п.
                    if (rs.Collectible.GetMergableQuantity(rs, ds, EnumMergePriority.AutoMerge) <= 0) continue;

                    int moved = donor.Slot.TryPutInto(sapi.World, recv.Slot, Math.Min(space, ds.StackSize));
                    if (moved <= 0) continue;

                    // Переприсваивание через свойство Itemstack помечает watched-атрибуты
                    // грязными, иначе клиенты увидят старый размер стака.
                    recv.Itemstack = recv.Slot.Itemstack;
                }

                ItemStack left = donor.Slot.Itemstack;
                if (left == null || left.StackSize <= 0)
                    donor.Die(EnumDespawnReason.Removed);
                else
                    donor.Itemstack = left;
            }

            group.Clear();
        }

        // ---------------------------------------------------------------------
        //  Предикаты
        // ---------------------------------------------------------------------

        private static bool MatchEntity(Entity e)
        {
            return e is EntityItem ei
                && ei.Alive
                && ei.Slot?.Itemstack?.Collectible == matchCollectible;
        }

        private static int CompareBySizeDesc(EntityItem a, EntityItem b)
        {
            int cmp = b.Slot.Itemstack.StackSize.CompareTo(a.Slot.Itemstack.StackSize);
            return cmp != 0 ? cmp : a.EntityId.CompareTo(b.EntityId);
        }

        private static bool IsValid(EntityItem e)
        {
            return e != null
                && e.Alive
                && e.World != null
                && e.Slot?.Itemstack != null
                && e.Slot.Itemstack.StackSize > 0;
        }

        private static bool NoPlayerNear(Vec3d pos)
        {
            int r = frozenRange;
            foreach (IPlayer player in sapi.World.AllOnlinePlayers)
            {
                EntityPlayer eplr = player.Entity;
                if (eplr != null && eplr.Pos.InRangeOf(pos, r * r, r))
                    return false;
            }
            return true;
        }

        private static bool IsSettled(EntityItem e)
        {
            // Опора: земля или жидкость (в воде/лаве предмет не «лежит», а плавает).
            // Полёт по воздуху не подходит: такой предмет ещё движется.
            if (!e.OnGround && !e.FeetInLiquid && !e.Swimming) return false;
            var m = e.ServerPos.Motion;
            return m.X * m.X + m.Y * m.Y + m.Z * m.Z <= SettledMotionSq;
        }

        // ---------------------------------------------------------------------
        //  Чёрный список
        // ---------------------------------------------------------------------

        private static string[] NormalizeBlacklist(List<string> raw)
        {
            var result = new List<string>();
            if (raw == null) return result.ToArray();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in raw)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;

                string code = entry.Trim();
                // Регистр не важен: WildcardUtil сравнивает без учёта регистра.
                // Код без домена считаем ванильным: "gear-rusty" -> "game:gear-rusty".
                // Паттерны-регулярки ("@...") не трогаем.
                if (code[0] != '@' && !code.Contains(':')) code = "game:" + code;

                if (seen.Add(code)) result.Add(code);
            }
            return result.ToArray();
        }

        private static bool IsBlacklisted(CollectibleObject coll)
        {
            if (blacklistPatterns.Length == 0) return false;
            if (blacklistCache.TryGetValue(coll, out bool cached)) return cached;

            bool hit = false;
            if (coll.Code != null)
            {
                // Один раз на коллектибл (дальше кэш), поэтому аллокация строки здесь не страшна.
                string code = coll.Code.ToString();
                hit = WildcardUtil.Match(blacklistPatterns, code);
            }

            blacklistCache[coll] = hit;
            return hit;
        }
    }
}