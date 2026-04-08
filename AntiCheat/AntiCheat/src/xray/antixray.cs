using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Threading;
using Vintagestory.API.Server;
using Vintagestory.Common;

namespace AntiCheat
{
    [HarmonyPatch(typeof(ChunkData), "CompressInto")]
    public class ChunkDataCompressPatch
    {
        [ThreadStatic]
        private static Dictionary<int, int> _restoreCache;

        public static void Prefix(ChunkData __instance, out Dictionary<int, int> __state)
        {
            __state = null;

            // Skip if the method is run on the SaveWorld thread to avoid save corruption
            string threadName = Thread.CurrentThread.Name;
            if (threadName == "SaveWorld" || threadName == null)
            {
                return;
            }

            if (__instance.blocksLayer == null) return;

            int[] palette = __instance.blocksLayer.palette;
            int paletteCount = __instance.blocksLayer.paletteCount;
            if (palette == null) return;

            // Check if the chunk contains any ores before doing expensive processing
            bool hasOre = false;
            for (int i = 0; i < paletteCount; i++)
            {
                if (AntiCheatModSystem.OreBlockIds.Contains(palette[i])) { hasOre = true; break; }
            }
            if (!hasOre) return;

            if (_restoreCache == null) _restoreCache = new Dictionary<int, int>();
            _restoreCache.Clear();
            __state = _restoreCache;

            for (int y = 0; y < 32; y++)
            {
                for (int z = 0; z < 32; z++)
                {
                    for (int x = 0; x < 32; x++)
                    {
                        int index = (y * 32 + z) * 32 + x;
                        int blockId = __instance.GetSolidBlock(index);

                        if (AntiCheatModSystem.OreBlockIds.Contains(blockId))
                        {
                            // If the ore is completely buried, swap it with granite
                            if (!IsExposed(__instance, x, y, z))
                            {
                                __state[index] = blockId;
                                __instance.SetBlockUnsafe(index, AntiCheatModSystem.FakeBlockId);
                            }
                        }
                    }
                }
            }
        }

        public static void Postfix(ChunkData __instance, Dictionary<int, int> __state)
        {
            if (__state == null || __state.Count == 0) return;

            foreach (var kvp in __state)
            {
                __instance.SetBlockUnsafe(kvp.Key, kvp.Value);
            }
            __state = null;
        }

        private static bool IsExposed(ChunkData chunk, int x, int y, int z)
        {
            // Treat chunk edges as exposed to avoid cross-chunk lookups
            if (x == 0 || x == 31 || y == 0 || y == 31 || z == 0 || z == 31) return true;

            return IsAir(chunk, x + 1, y, z) || IsAir(chunk, x - 1, y, z) ||
                   IsAir(chunk, x, y + 1, z) || IsAir(chunk, x, y - 1, z) ||
                   IsAir(chunk, x, y, z + 1) || IsAir(chunk, x, y, z - 1);
        }

        private static bool IsAir(ChunkData chunk, int x, int y, int z)
        {
            return chunk.GetSolidBlock((y * 32 + z) * 32 + x) == 0;
        }
    }
}