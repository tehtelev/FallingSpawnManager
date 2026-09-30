using System.Collections.Generic;

namespace FSMMgr.Utils
{
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
        // массовом обвале и никогда не приземляться сами. Если сущность жива дольше
        // этого времени и ещё не приземлилась, она принудительно укладывается на
        // текущей позиции. 0 отключает таймер полностью.
        public int StuckTimeoutMs = 15000;
        
        // Включает слияние лежащих стаков одного типа.
        public bool ItemClumpEnabled = true;

        // Радиус поиска соседних предметов (блоки): по горизонтали и по вертикали.
        public float ItemClumpRadius = 5.0f;
        public float ItemClumpVerticalRadius = 2.0f;

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

        // Раз в 10 секунд пишет в лог статистику слияния (для диагностики).
        public bool ItemClumpDebug = false;
    }
}