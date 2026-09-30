using FSMMgr.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

#nullable disable

namespace FSMMgr.Managers
{
    /// <summary>
    /// Серверный менеджер слияния лежащих на земле стаков предметов (EntityItem).
    /// Работает с ЛЮБЫМИ EntityItem в мире, независимо от того, кто и как их создал
    /// (наш SpawnDrops, игрок, другие моды, ваниль).

    /// Архитектура:
    ///   * Жизненный цикл предметов отслеживается через события
    ///     OnEntitySpawn / OnEntityLoaded / OnEntityDespawn, которые мы и сами используем.
    ///   * Одна общая очередь кандидатов и один тик-листенер вместо RegisterCallback
    ///     на cada предмет. Очередь монотонна по времени (задержка у всех одинаковая),
    ///     поэтому достаточно обычной Queue: смотрим только на голову.
    ///   * Бюджет попыток на тик (MaxAttemptsPerTick): при загрузке мира, когда в мире тысячи предметов,
    ///     нагрузка размазывается, а не бьёт по одному тику.
    ///   * Кандидатом становится только «новичок» (заспавился / загрузился). Он сам ищет
    ///     соседей и вливается в них. Давно лежащие предметы повторно не сканируются.
    ///   * Малёнький радиус по умолчанию (5 по горизонтали и 2 по вертикали):
    ///     предметы не «исчезают» с места, где лежали, и не сливаются между этажами.
    ///   * Упаковка «от большего к меньшему»: самые большие стаки принимают, а доноры
    ///     берутся от самых маленьких. Это минимизирует итоговое число сущностей.
    ///   * Чёрный список по полному коду коллектибла через ванильный WildcardUtil.Match
    ///     ('*', без учёта регистра, "@regex"), например "game:gear-*". Результат
    ///     кэшируется на CollectibleObject, строка Code.ToString() строится один раз.
    /// Слияние проходит через ItemSlot.TryPutInto + GetMergableQuantity, то есть все
    /// правила ванили (атрибуты, свежесть/transition-состояния) соблюдаются.

    /// Это статический менеджер со своим чистым функционалом. Инициализацию мода,
    /// загрузку конфигов и применение патчей курирует FSM (FSM.cs).
    /// </summary>
    public static class ItemClumpManager
    {
        // Период тика менеджера. Точность не важна, достаточно нескольких раз в секунду.
        private const int TickIntervalMs = 250;

        // Предмет считается «успокоившимся», если скорость не выше 0.005d блока/тик.
        private const double SettledMotionSq = 0.005d;

        private static ICoreServerAPI sapi;
        private static long tickListenerId;
        private static bool running;

        // Очередь по времени готовности + индекс по EntityId для O(1) дедупликации/отмены.
        private static readonly Queue<Candidate> queue = new();
        private static readonly Dictionary<long, Candidate> byId = new();

        // Переиспользуемые буферы (серверный тик однопоточный).
        private static readonly List<EntityItem> group = new();

        // Кэш_result чёрного списка на коллектибл.
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
        // (физика их не тикает, скорость и OnGround остаются при спавне).
        // Берём меньшее из радиуса слежения сервера и дальности симуляции сущностей.
        private static int frozenRange;

        // ---- Отладка (ItemClumpDebug в конфиге) ----
        private static bool debug;
        private static long debugNextLogMs;
        private static int dbgEnqueued, dbgProcessed, dbgInvalid, dbgFrozen, dbgRetried,
                           dbgGaveUp, dbgSearchLt2, dbgGroupLt2, dbgMerged, dbgKilled, dbgQueueMax, dbgHovering;
        private static int dbgSamples;

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

            debug = cfg.ItemClumpDebug;
            debugNextLogMs = 0;
            api.Logger.Notification("[FSM] ItemClump: enabled, radius={0}/{1}, frozenRange={2}, debug={3}",
                radius, verticalRadius, frozenRange, debug);

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
            dbgEnqueued++;
        }

        // ---------------------------------------------------------------------
        //  Тик
        // ---------------------------------------------------------------------

        private static void OnTick(float dt)
        {
            long now = sapi.World.ElapsedMilliseconds;

            // Два независимых бюджета:
            //   clumpBudget: дорогие попытки слияния (запрос GetEntitiesAround + TryPutInto);
            //   scanBudget: дешёвые проверки очереди (жив ли, лежит ли, есть ли игрок рядом).
            // Раньше был один общий бюджет, и очередь целиком забивалась повторными
            // проверками ещё падающих предметов, до реальных слияний дело не доходило.
            int clumpBudget = maxAttemptsPerTick;
            int scanBudget = Math.Max(500, maxAttemptsPerTick * 20);

            while (scanBudget-- > 0 && queue.Count > 0)
            {
                Candidate c = queue.Peek();

                if (c.Cancelled)
                {
                    queue.Dequeue();
                    continue;
                }

                if (c.DueMs > now) break; // очередь монотонна, дальше тоже рано

                EntityItem e = c.Entity;
                dbgProcessed++;

                if (!IsValid(e))
                {
                    queue.Dequeue();
                    byId.Remove(c.EntityId);
                    dbgInvalid++;
                    continue;
                }

                // Предмет вдали от игроков не симулируется, он «заморожен» и не двигается:
                // ждать его успокоения бессмысленно (он никогда не успокоится), сливаем сразу.
                // Принавки заморозки, от самого надёжного к резервному:
                //   1) сервер сам пометил сущность неактивной (вне зоны симуляции);
                //   2) рядом с предметом нет ни одного игрока (по дальности симуляции);
                //   3) ниже, в цикле: предмет висит без опоры и не двигается между проверками.
                bool frozen = e.State == EnumEntityState.Inactive || NoPlayerNear(e.ServerPos);

                if (!frozen && !IsSettled(e))
                {
                    var p = e.ServerPos;

                    // Живая физика не даёт предмету без опоры стоять на месте: за 1.5 с он
                    // обязан сдвинуться. Если позиция не изменилась, предмет завис в воздухе
                    // вне зоны симуляции. Ждать нечего, сливаем как замороженный.
                    if (c.HasLastPos && SqDist(p.X, p.Y, p.Z, c.LastX, c.LastY, c.LastZ) < 1e-4)
                    {
                        frozen = true;
                        dbgHovering++;
                    }
                    else
                    {
                        c.HasLastPos = true;
                        c.LastX = p.X; c.LastY = p.Y; c.LastZ = p.Z;

                        // ещё летит или движется: перепроверим позже, но не бесконечно.
                        queue.Dequeue();
                        if (++c.Attempts >= maxSettleAttempts)
                        {
                            byId.Remove(c.EntityId);
                            dbgGaveUp++;
                        }
                        else
                        {
                            c.DueMs = now + delayMs;
                            queue.Enqueue(c);
                            dbgRetried++;
                        }
                        continue;
                    }
                }

                if (frozen) dbgFrozen++;

                // Готов к слиянию, но бюджет дорогих попыток на этот тик исчерпан:
                // оставляем в голове очереди до следующего тика.
                if (clumpBudget <= 0) break;
                clumpBudget--;

                queue.Dequeue();
                // Снимаем с учёта ДО слияния: если предмет останется, он может
                // снова стать кандидатом (например, при перезагрузке чанка).
                byId.Remove(c.EntityId);
                TryClump(e, frozen);
            }

            if (debug) DebugLog(now);
        }

        private static void DebugLog(long now)
        {
            if (queue.Count > dbgQueueMax) dbgQueueMax = queue.Count;
            if (now < debugNextLogMs) return;
            debugNextLogMs = now + 10000;

            sapi.Logger.Notification(
                "[FSM] ItemClump 10s: enqueued={0} processed={1} invalid={2} frozen={3} (hovering={12}) retried={4} gaveUp={5} " +
                "search<2={6} group<2={7} mergedOps={8} donorsKilled={9} queue={10} (max {11})",
                dbgEnqueued, dbgProcessed, dbgInvalid, dbgFrozen, dbgRetried, dbgGaveUp,
                dbgSearchLt2, dbgGroupLt2, dbgMerged, dbgKilled, queue.Count, dbgQueueMax, dbgHovering);

            dbgEnqueued = dbgProcessed = dbgInvalid = dbgFrozen = dbgRetried = dbgGaveUp =
                dbgSearchLt2 = dbgGroupLt2 = dbgMerged = dbgKilled = dbgQueueMax = dbgHovering = 0;
            dbgSamples = 0;
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

            if (debug && dbgSamples < 5)
            {
                dbgSamples++;
                sapi.Logger.Notification("[FSM] ItemClump sample: {0} x{1} at {2} frozen={3} found={4}",
                    coll.Code, rootStack.StackSize, root.ServerPos.XYZ, frozen, found?.Length ?? -1);
            }

            if (found == null || found.Length < 2) { dbgSearchLt2++; return; }

            group.Clear();
            group.Add(root);
            for (int i = 0; i < found.Length && group.Count < maxGroupSize; i++)
            {
                if (found[i] is not EntityItem other || other == root) continue;
                if (!IsValid(other) || (!frozen && !IsSettled(other))) continue;
                group.Add(other);
            }

            if (group.Count < 2) { dbgGroupLt2++; group.Clear(); return; }

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
                    dbgMerged++;
                }

                ItemStack left = donor.Slot.Itemstack;
                if (left == null || left.StackSize <= 0)
                {
                    donor.Die(EnumDespawnReason.Removed);
                    dbgKilled++;
                }
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

        private static bool NoPlayerNear(EntityPos ep)
        {
            Vec3d pos = ep.XYZ;
            int r = frozenRange;
            foreach (IPlayer player in sapi.World.AllOnlinePlayers)
            {
                EntityPlayer eplr = player.Entity;
                if (eplr != null && eplr.Pos.InRangeOf(pos, r * r, r))
                    return false;
            }
            return true;
        }

        private static double SqDist(double x1, double y1, double z1, double x2, double y2, double z2)
        {
            double dx = x1 - x2, dy = y1 - y2, dz = z1 - z2;
            return dx * dx + dy * dy + dz * dz;
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
            if (raw == null) return [];

            return raw
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .Select(entry => AddDefaultDomain(entry.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        // Код без домена считаем ванильным: "gear-rusty" -> "game:gear-rusty".
        // Паттерны-регулярки ("@...") не трогаем.
        private static string AddDefaultDomain(string code)
        {
            return code[0] == '@' || code.Contains(':') ? code : "game:" + code;
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