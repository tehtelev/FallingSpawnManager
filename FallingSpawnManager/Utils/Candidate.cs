using Vintagestory.API.Common;

namespace FSMMgr.Utils
{
    public sealed class Candidate
    {
        public EntityItem Entity;
        public long EntityId;
        public long DueMs;
        public int Attempts;
        public bool Cancelled;

        // Позиция на прошлой проверке: если предмет «висит» без опоры и не сдвинулся,
        // физика его не считает (он вне зоны симуляции).
        public bool HasLastPos;
        public double LastX, LastY, LastZ;
    }
}
