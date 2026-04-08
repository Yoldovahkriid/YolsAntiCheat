using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace AntiCheat
{
    public class AntiCheatModSystem : ModSystem
    {
        public static ICoreServerAPI ServerApi;
        public static HashSet<int> OreBlockIds = new();
        public static HashSet<int> RockBlockIds = new();
        // Maps rock type to possible ores that can spawn in it
        public static Dictionary<int, List<int>> RockToOresMap = new();

        private HarmonyLib.Harmony harmony;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            ServerApi = api;

            harmony = new HarmonyLib.Harmony("antixray");
            harmony.PatchAll();

            api.Event.DidBreakBlock += OnBlockBroken;
        }

        public override void AssetsFinalize(ICoreAPI api)
        {
            if (api.Side != EnumAppSide.Server) return;

            // First pass: collect all ores and rocks
            foreach (var block in api.World.Blocks)
            {
                if (block?.Code == null) continue;
                string path = block.Code.Path;

                if (path.StartsWith("ore-"))
                {
                    OreBlockIds.Add(block.Id);
                }
                else if (path.StartsWith("rock-"))
                {
                    RockBlockIds.Add(block.Id);
                }
            }

            // Second pass: map ores to their rock types
            foreach (var block in api.World.Blocks)
            {
                if (block?.Code == null) continue;
                string path = block.Code.Path;

                if (path.StartsWith("ore-"))
                {
                    // Extract rock type from ore name (e.g., "ore-poor-quartz-granite" -> "granite")
                    string[] parts = path.Split('-');
                    if (parts.Length >= 3)
                    {
                        string rockType = parts[parts.Length - 1];

                        // Find the corresponding rock block
                        var rockBlock = api.World.Blocks.FirstOrDefault(b =>
                            b?.Code != null && b.Code.Path == $"rock-{rockType}");

                        if (rockBlock != null)
                        {
                            if (!RockToOresMap.ContainsKey(rockBlock.Id))
                            {
                                RockToOresMap[rockBlock.Id] = new List<int>();
                            }
                            RockToOresMap[rockBlock.Id].Add(block.Id);
                        }
                    }
                }
            }

            api.Logger.Notification($"[AntiXray] Loaded {OreBlockIds.Count} ore types, {RockBlockIds.Count} rock types");
            api.Logger.Notification($"[AntiXray] Mapped {RockToOresMap.Count} rock-to-ore relationships");
        }

        private void OnBlockBroken(IServerPlayer byPlayer, int oldBlockId, BlockSelection blockSel)
        {
            BlockPos pos = blockSel.Position;
            IBlockAccessor blockAccessor = ServerApi.World.BlockAccessor;

            foreach (BlockFacing face in BlockFacing.ALLFACES)
            {
                BlockPos neighborPos = pos.AddCopy(face);
                Block neighborBlock = blockAccessor.GetBlock(neighborPos);

                // If the neighbor block is a rock, mark it dirty to force a re-send to clients (this avoids sending the full chunk so we dont have to recompress)
                if (neighborBlock != null && RockBlockIds.Contains(neighborBlock.Id))
                {
                    blockAccessor.MarkBlockDirty(neighborPos);
                }
            }
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll("antixray");
        }
    }
}