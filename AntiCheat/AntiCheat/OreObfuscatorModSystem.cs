using OreObfuscator.src;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace OreObfuscator
{
    public class OreObfuscatorModSystem : ModSystem
    {
        public static ICoreServerAPI ServerApi;
        public static HashSet<int> RockBlockIds = new();
        public static Dictionary<int, List<int>> RockToOresMap = new();

        private HarmonyLib.Harmony harmony;
        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            ServerApi = api;
            harmony = new HarmonyLib.Harmony("oreobfuscator");
            harmony.PatchAll();
            ServerChunkCache.InitCaches();
            api.Event.DidBreakBlock += OnBlockBroken;
            api.Event.ChunkColumnUnloaded += ServerChunkCache.OnChunkColumnUnloaded;
        }

        private void OnBlockBroken(IServerPlayer byPlayer, int oldBlockId, BlockSelection blockSel)
        {
            // Only trigger updates if we broke a block that could reveal hidden ores
            foreach (BlockFacing face in BlockFacing.ALLFACES)
            {
                BlockPos neighborPos = blockSel.Position.AddCopy(face);
                if (RockBlockIds.Contains(ServerApi.World.BlockAccessor.GetBlock(neighborPos).Id))
                {
                    ServerApi.World.BlockAccessor.MarkBlockDirty(neighborPos);
                }
            }
        }

        public override void AssetsFinalize(ICoreAPI api)
        {
            if (api.Side != EnumAppSide.Server) return;

            // 1. Map string codes to IDs in one pass
            var rockCodeToId = new Dictionary<string, int>();
            foreach (var block in api.World.Blocks)
            {
                if (block?.Code == null) continue;
                if (block.Code.Path.StartsWith("rock-"))
                {
                    RockBlockIds.Add(block.Id);
                    rockCodeToId[block.Code.Path] = block.Id;
                }
            }

            // 2. Map ores to rocks using the dictionary
            foreach (var block in api.World.Blocks)
            {
                if (block?.Code == null || !block.Code.Path.StartsWith("ore-")) continue;

                string[] parts = block.Code.Path.Split('-');
                if (parts.Length >= 3)
                {
                    string rockType = parts[^1];
                    if (rockCodeToId.TryGetValue($"rock-{rockType}", out int rockId))
                    {
                        if (!RockToOresMap.TryGetValue(rockId, out var list))
                        {
                            list = new List<int>();
                            RockToOresMap[rockId] = list;
                        }
                        list.Add(block.Id);
                    }
                }
            }
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll("oreobfuscator");
            RockBlockIds.Clear();
            RockToOresMap.Clear();
            ServerChunkCache.Dispose();
        }
    }
}