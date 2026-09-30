using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace FSMMgr.Utils
{
    /// <summary>
    /// Данные одной заявки на спавн в очереди (используется и для requestQueue,
    /// и для instantQueue. RetryCount актуален только для requestQueue).
    /// </summary>
    public struct SpawnRequest
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
