using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.Characters;

namespace LiveAppearanceFramework.Framework
{
    /// <summary>The result of a dry run of <see cref="NPC.ChooseAppearance"/>.</summary>
    internal sealed class ResolveResult
    {
        /// <summary>The winning Appearance entry, or null (no entry applies → default textures).</summary>
        public CharacterAppearanceData Winner { get; init; }

        public string WinnerId => this.Winner?.Id;

        /// <summary>The location the dry run was made for.</summary>
        public GameLocation Location { get; init; }

        /// <summary>The NPC's texture name (usually their internal name).</summary>
        public string TextureName { get; init; }

        public bool IslandAttire { get; init; }

        /// <summary>The portrait forced by the location's <c>UniquePortrait</c> map property, if any.</summary>
        public string MapPortrait { get; init; }

        /// <summary>The sprite forced by the location's <c>UniqueSprite</c> map property, if any.</summary>
        public string MapSprite { get; init; }

        /// <summary>How many entries tied for the winning precedence.</summary>
        public int Candidates { get; init; }
    }

    /// <summary>One line of <c>laf_why</c>: how the game's selection loop treated an Appearance entry.</summary>
    internal sealed class EntryTrace
    {
        public CharacterAppearanceData Entry { get; init; }
        public string Status { get; set; }
        public bool IsCandidate { get; set; }
    }

    /// <summary>
    /// A copy of the selection part of <see cref="NPC.ChooseAppearance"/> (verified against the 1.6.15 DLL) that loads
    /// nothing and changes nothing. The order of evaluation is identical to vanilla, so the day-seeded random used by
    /// conditions and by the weighted tie-break gives the same answer.
    /// </summary>
    internal static class AppearanceResolver
    {
        private static IReflectionHelper Reflection;
        private static readonly HashSet<string> Resolving = new(StringComparer.Ordinal);

        public static void Init(IReflectionHelper reflection)
        {
            Reflection = reflection;
        }

        /// <summary>Whether the dry run of this NPC is running right now (to catch circular conditions).</summary>
        public static bool IsResolving(string npcName) => npcName != null && Resolving.Contains(npcName);

        /// <summary>The private <c>NPC.isWearingIslandAttire</c> field, used by vanilla to filter entries.</summary>
        public static bool IsWearingIslandAttire(NPC npc)
        {
            return Reflection.GetField<bool>(npc, "isWearingIslandAttire").GetValue();
        }

        /// <summary>The private <c>NPC.portrait</c> field. Reading <see cref="NPC.Portrait"/> would re-pick the appearance when it's null.</summary>
        public static Texture2D GetLoadedPortrait(NPC npc)
        {
            return Reflection.GetField<Texture2D>(npc, "portrait").GetValue();
        }

        /// <summary>Dry-run the game's appearance choice for this NPC. Returns null if the game wouldn't choose (no location, simple NPC).</summary>
        /// <param name="npc">The NPC.</param>
        /// <param name="trace">If set, receives how each entry was treated (for <c>laf_why</c>).</param>
        public static ResolveResult Resolve(NPC npc, List<EntryTrace> trace = null)
        {
            if (npc == null || npc.SimpleNonVillagerNPC)
                return null;

            GameLocation location = npc.currentLocation;
            if (location == null)
                return null;

            if (!Resolving.Add(npc.Name))
                return null; // circular: a condition of this NPC asked for this NPC's appearance

            try
            {
                string textureName = npc.getTextureName();

                // UniquePortrait / UniqueSprite map properties win (vanilla checks them first).
                string mapPortrait = null;
                if (location.TryGetMapProperty("UniquePortrait", out string uniquePortraits) && ArgUtility.SplitBySpace(uniquePortraits).Contains(npc.Name))
                {
                    string asset = "Portraits\\" + textureName + "_" + location.Name;
                    if (AssetExists(asset))
                        mapPortrait = asset;
                }

                string mapSprite = null;
                if (location.TryGetMapProperty("UniqueSprite", out string uniqueSprites) && ArgUtility.SplitBySpace(uniqueSprites).Contains(npc.Name))
                {
                    string asset = "Characters\\" + textureName + "_" + location.Name;
                    if (AssetExists(asset))
                        mapSprite = asset;
                }

                bool island = IsWearingIslandAttire(npc);

                if (mapPortrait != null && mapSprite != null)
                {
                    return new ResolveResult
                    {
                        Location = location,
                        TextureName = textureName,
                        IslandAttire = island,
                        MapPortrait = mapPortrait,
                        MapSprite = mapSprite
                    };
                }

                CharacterAppearanceData winner = null;
                int candidateCount = 0;

                CharacterData data = npc.IsMonster ? null : npc.GetData();
                if (data?.Appearance?.Count > 0)
                {
                    List<CharacterAppearanceData> candidates = new();
                    List<EntryTrace> candidateTraces = new();
                    int totalWeight = 0;
                    Random random = Utility.CreateDaySaveRandom(Game1.hash.GetDeterministicHashCode(npc.Name));
                    Season season = location.GetSeason();
                    bool isOutdoors = location.IsOutdoors;
                    int precedence = int.MaxValue;

                    // Same loop as vanilla, statement for statement.
                    foreach (CharacterAppearanceData option in data.Appearance)
                    {
                        EntryTrace line = null;
                        if (trace != null)
                        {
                            line = new EntryTrace { Entry = option };
                            trace.Add(line);
                        }

                        if (option.Precedence > precedence)
                        {
                            if (line != null)
                                line.Status = "skipped: a lower Precedence already matched (condition not checked)";
                            continue;
                        }
                        if (option.IsIslandAttire != island)
                        {
                            if (line != null)
                                line.Status = option.IsIslandAttire ? "skipped: island attire entry, NPC isn't in island attire" : "skipped: NPC is in island attire";
                            continue;
                        }

                        bool seasonOk = !option.Season.HasValue || option.Season.Value == season;
                        bool placeOk = isOutdoors ? option.Outdoors : option.Indoors;
                        if (!seasonOk || !placeOk)
                        {
                            if (line != null)
                                line.Status = !seasonOk ? $"filtered: Season is {option.Season}, location is {season}" : (isOutdoors ? "filtered: Outdoors is false" : "filtered: Indoors is false");
                            continue;
                        }

                        if (!GameStateQuery.CheckConditions(option.Condition, location, null, null, null, random))
                        {
                            if (line != null)
                                line.Status = "condition is false";
                            continue;
                        }

                        if (option.Precedence < precedence)
                        {
                            precedence = option.Precedence;
                            candidates.Clear();
                            totalWeight = 0;
                            foreach (EntryTrace old in candidateTraces)
                            {
                                old.IsCandidate = false;
                                old.Status = $"matched, but a lower Precedence ({precedence}) matched later";
                            }
                            candidateTraces.Clear();
                        }

                        candidates.Add(option);
                        totalWeight += option.Weight;
                        if (line != null)
                        {
                            line.IsCandidate = true;
                            line.Status = "matched";
                            candidateTraces.Add(line);
                        }
                    }

                    candidateCount = candidates.Count;
                    switch (candidates.Count)
                    {
                        case 0:
                            break;

                        case 1:
                            winner = candidates[0];
                            break;

                        default:
                            {
                                winner = candidates[candidates.Count - 1];
                                int cursor = Utility.CreateDaySaveRandom(Game1.hash.GetDeterministicHashCode(npc.Name)).Next(totalWeight + 1);
                                foreach (CharacterAppearanceData option in candidates)
                                {
                                    cursor -= option.Weight;
                                    if (cursor <= 0)
                                    {
                                        winner = option;
                                        break;
                                    }
                                }
                                break;
                            }
                    }
                }

                return new ResolveResult
                {
                    Winner = winner,
                    Location = location,
                    TextureName = textureName,
                    IslandAttire = island,
                    MapPortrait = mapPortrait,
                    MapSprite = mapSprite,
                    Candidates = candidateCount
                };
            }
            finally
            {
                Resolving.Remove(npc.Name);
            }
        }

        /// <summary>The portrait and sprite assets vanilla would end up loading for this result (with its fallbacks).</summary>
        public static (string Portrait, string Sprite) GetExpectedAssets(ResolveResult result)
        {
            return (
                GetExpectedAsset(result, "Portraits/", result.MapPortrait, result.Winner?.Portrait),
                GetExpectedAsset(result, "Characters/", result.MapSprite, result.Winner?.Sprite)
            );
        }

        private static string GetExpectedAsset(ResolveResult result, string folder, string mapAsset, string entryAsset)
        {
            if (mapAsset != null)
                return mapAsset;

            string defaultAsset = folder + result.TextureName;
            if (entryAsset != null && entryAsset != defaultAsset && AssetExists(entryAsset))
                return entryAsset;

            if (result.IslandAttire && AssetExists(defaultAsset + "_Beach"))
                return defaultAsset + "_Beach";

            return defaultAsset;
        }

        /// <summary>Whether the NPC's loaded textures already are the ones this result would load.</summary>
        public static bool TexturesMatch(NPC npc, ResolveResult result)
        {
            (string portrait, string sprite) = GetExpectedAssets(result);
            return PortraitMatches(npc, portrait) && SpriteMatches(npc, sprite);
        }

        private static bool PortraitMatches(NPC npc, string expected)
        {
            if (npc.Name is "Raccoon" or "MrsRaccoon")
                return true; // vanilla never loads portraits for them

            Texture2D loaded = GetLoadedPortrait(npc);
            return loaded != null && !loaded.IsDisposed && SameAsset(loaded.Name, expected);
        }

        private static bool SpriteMatches(NPC npc, string expected)
        {
            AnimatedSprite sprite = npc.Sprite;
            if (sprite?.spriteTexture == null || sprite.spriteTexture.IsDisposed)
                return false;

            return SameAsset(GetSpriteAsset(npc), expected) || SameAsset(sprite.spriteTexture.Name, expected);
        }

        /// <summary>The sprite asset this client shows (farmhands keep their own choice in <c>overrideTextureName</c>).</summary>
        public static string GetSpriteAsset(NPC npc)
        {
            return npc.Sprite?.overrideTextureName ?? npc.Sprite?.textureName.Value;
        }

        public static bool SameAsset(string a, string b)
        {
            if (a == null || b == null)
                return false;
            return string.Equals(a.Replace('\\', '/').Trim(), b.Replace('\\', '/').Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool AssetExists(string assetName)
        {
            try
            {
                return Game1.content.DoesAssetExist<Texture2D>(assetName);
            }
            catch
            {
                return false;
            }
        }
    }
}
