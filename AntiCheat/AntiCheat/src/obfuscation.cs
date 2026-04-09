using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Common.Database;
using Vintagestory.Server;

namespace OreObfuscator.src
{
    [HarmonyPatch(typeof(ServerChunk), "ToPacket")]
    public class Obfuscation
    {
        public static void Postfix(ServerChunk __instance, Packet_ServerChunk __result, int posX, int posY, int posZ)
        {
            if (__result.Empty == 1) return;
            __result.SetBlocks(ServerChunkCache.GetOrObfuscate(__instance, posX, posY, posZ));
        }
    }

    [HarmonyPatch(typeof(ServerChunk), "MarkModified")]
    public class ServerChunk_MarkModified_Patch
    {
        public static void Postfix(ServerChunk __instance) => ServerChunkCache.MarkDirty(__instance);
    }

    public static class ServerChunkCache
    {
        private static ConditionalWeakTable<ServerChunk, CacheEntry> cache = new();
        private static bool[] IsBlockTransparent;
        private static bool initialized = false;

        private static MethodInfo? unpackMethod;
        private static MethodInfo? getBlocksCompressedGetter;
        private static MethodInfo? createNewMethod;
        private static MethodInfo? compressIntoMethod;
        private static MethodInfo? freeMethod;
        private static FieldInfo? chunkdataVersionField;
        private static FieldInfo? chunkDataPoolField;

        public class CacheEntry
        {
            public byte[]? ObfuscatedData;
            public bool IsDirty;
        }

        public static void MarkDirty(ServerChunk chunk)
        {
            if (cache.TryGetValue(chunk, out var entry)) entry.IsDirty = true;
        }

        public static byte[] GetOrObfuscate(ServerChunk chunk, int posX, int posY, int posZ)
        {
            if (!cache.TryGetValue(chunk, out CacheEntry? entry))
            {
                entry = new CacheEntry { IsDirty = true };
                cache.Add(chunk, entry);
            }

            if (entry.IsDirty)
            {
                entry.ObfuscatedData = ObfuscateChunk(chunk, posX, posY, posZ);
                entry.IsDirty = false;
            }
            return entry.ObfuscatedData;
        }

        public static byte[] ObfuscateChunk(ServerChunk chunk, int posX, int posY, int posZ)
        {
            ServerMain serverMain = (ServerMain)OreObfuscatorModSystem.ServerApi.World;

            IWorldChunk iChunk = chunk;
            if (iChunk.Data == null) unpackMethod?.Invoke(chunk, null);

            ChunkData sourceData = iChunk.Data as ChunkData;
            if (sourceData == null) return (byte[])getBlocksCompressedGetter.Invoke(chunk, null);

            object dataPool = chunkDataPoolField.GetValue(serverMain);
            ChunkData obfData = (ChunkData)createNewMethod.Invoke(null, new object[] { 32, dataPool });

            // Neighbors: +X, -X, +Y, -Y, +Z, -Z
            ChunkData[] neighbors = {
                GetCachedChunkData(serverMain, posX + 1, posY, posZ),
                GetCachedChunkData(serverMain, posX - 1, posY, posZ),
                GetCachedChunkData(serverMain, posX, posY + 1, posZ),
                GetCachedChunkData(serverMain, posX, posY - 1, posZ),
                GetCachedChunkData(serverMain, posX, posY, posZ + 1),
                GetCachedChunkData(serverMain, posX, posY, posZ - 1)
            };

            const int SZ = 32;
            const int SZ2 = 1024;
            int[] offsets = { 1, -1, SZ2, -SZ2, SZ, -SZ };

            for (int i = 0; i < 32768; i++)
            {
                int blockId = sourceData.GetBlockIdUnsafe(i);

                if (OreObfuscatorModSystem.RockBlockIds.Contains(blockId))
                {
                    int x = i & 31;
                    int z = (i >> 5) & 31;
                    int y = i >> 10;

                    bool isVisible = false;
                    for (int d = 0; d < 6; d++)
                    {
                        bool isEdge = (d == 0 && x == 31) || (d == 1 && x == 0) ||
                                      (d == 2 && y == 31) || (d == 3 && y == 0) ||
                                      (d == 4 && z == 31) || (d == 5 && z == 0);

                        if (!isEdge)
                        {
                            if (IsBlockTransparent[sourceData.GetBlockIdUnsafe(i + offsets[d])])
                            {
                                isVisible = true; break;
                            }
                        }
                        else
                        {
                            ChunkData nData = neighbors[d];
                            if (nData == null || IsBlockTransparent[nData.GetBlockIdUnsafe(GetWrappedIndex(x, y, z, d))])
                            {
                                isVisible = true; break;
                            }
                        }
                    }

                    if (!isVisible && OreObfuscatorModSystem.RockToOresMap.TryGetValue(blockId, out List<int> ores))
                    {
                        int h = ((posX * 32 + x) * 73856093) ^ ((posY * 32 + y) * 19349663) ^ ((posZ * 32 + z) * 83492791);
                        obfData?.SetBlockUnsafe(i, ores[Math.Abs(h) % ores.Count]);
                    }
                    else obfData?.SetBlockUnsafe(i, blockId);
                }
                else obfData?.SetBlockUnsafe(i, blockId);
            }

            int version = (int)chunkdataVersionField.GetValue(chunk);
            object[] compressArgs = new object[] { null, null, null, null, version };
            compressIntoMethod.Invoke(obfData, compressArgs);

            freeMethod?.Invoke(dataPool, new object[] { obfData });
            return (byte[])compressArgs[0];
        }

        private static int GetWrappedIndex(int x, int y, int z, int d)
        {
            if (d == 0) return 0 + z * 32 + y * 1024;      // +X -> Neighbor's X=0
            if (d == 1) return 31 + z * 32 + y * 1024;     // -X -> Neighbor's X=31
            if (d == 2) return x + z * 32 + 0 * 1024;      // +Y -> Neighbor's Y=0
            if (d == 3) return x + z * 32 + 31 * 1024;     // -Y -> Neighbor's Y=31
            if (d == 4) return x + 0 * 32 + y * 1024;      // +Z -> Neighbor's Z=0
            return x + 31 * 32 + y * 1024;                 // -Z -> Neighbor's Z=31
        }

        public static void InitCaches()
        {
            if (initialized) return;
            var api = OreObfuscatorModSystem.ServerApi;

            IsBlockTransparent = new bool[api.World.Blocks.Count];
            for (int i = 0; i < api.World.Blocks.Count; i++)
            {
                var b = api.World.Blocks[i];
                IsBlockTransparent[i] = b == null || b.Id == 0 || !b.SideOpaque.All;
            }

            unpackMethod = AccessTools.Method(typeof(ServerChunk), "Unpack");
            getBlocksCompressedGetter = AccessTools.PropertyGetter(typeof(ServerChunk), "blocksCompressed");
            chunkdataVersionField = AccessTools.Field(typeof(ServerChunk), "chunkdataVersion");
            chunkDataPoolField = AccessTools.Field(typeof(ServerMain), "serverChunkDataPool");
            createNewMethod = AccessTools.Method(typeof(ChunkData), "CreateNew");
            compressIntoMethod = AccessTools.Method(typeof(ChunkData), "CompressInto");

            if (chunkDataPoolField != null)
            {
                freeMethod = AccessTools.Method(chunkDataPoolField.FieldType, "Free");
            }

            initialized = true;
        }

        private static ChunkData? GetCachedChunkData(ServerMain server, int cx, int cy, int cz)
        {
            if (server.WorldMap == null) return null;
            return server.GetLoadedChunk(server.WorldMap.ChunkIndex3D(new ChunkPos(cx, cy, cz)))?.Data as ChunkData;
        }

        public static void OnChunkColumnUnloaded(Vec3i chunkPos)
        {
            if (OreObfuscatorModSystem.ServerApi?.World is not ServerMain server) return;

            int mapHeightInChunks = server.WorldMap.MapSizeY / GlobalConstants.ChunkSize;

            for (int cy = 0; cy < mapHeightInChunks; cy++)
            {
                long index = server.WorldMap.ChunkIndex3D(new ChunkPos(chunkPos.X, cy, chunkPos.Z));

                if (server.GetLoadedChunk(index) is ServerChunk chunk)
                {
                    if (cache.TryGetValue(chunk, out var entry))
                    {
                        entry.ObfuscatedData = null;
                        cache.Remove(chunk);
                    }
                }
            }
        }

        public static void Dispose() => cache.Clear();
    }
}