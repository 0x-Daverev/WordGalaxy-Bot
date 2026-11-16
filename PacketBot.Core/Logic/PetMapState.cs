using System;
namespace FunnyProject.Logic
{
    public sealed class PetMapState
    {
        private readonly object sync = new();
        private int petId;
        private bool spawnSeen;
        private int x, y;
        private bool hasPosition;
        public void Spawn(int id)
        {
            if (id <= 0) return;
            lock (sync)
            {
                if (spawnSeen && petId == id) return;
                spawnSeen = true;
                petId = id;
                hasPosition = false;
            }
        }
        public bool ClearIfMatches(int id)
        {
            lock (sync)
            {
                if (!spawnSeen || petId != id) return false;
                spawnSeen = false;
                petId = 0;
                hasPosition = false;
                return true;
            }
        }
        public void Clear()
        {
            lock (sync)
            {
                spawnSeen = false;
                petId = 0;
                hasPosition = false;
            }
        }
        public bool TryMove(int id, int destX, int destY, bool isKnownPlayer)
        {
            lock (sync)
            {
                if (!spawnSeen || id <= 0 || destX < 0 || destY < 0 ||
                    destX > 21000 || destY > 13000) return false;
                if (id != petId) return false;
                x = destX;
                y = destY;
                hasPosition = true;
                return true;
            }
        }
        public bool TryGetPosition(out int px, out int py)
        {
            lock (sync)
            {
                px = x; py = y;
                return spawnSeen && hasPosition;
            }
        }
    }
}
