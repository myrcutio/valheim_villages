using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ValheimVillages.Items.VirtualRecipes
{
    // Debug: set to true to log cultivator piece discovery to BepInEx log
    internal static class CultivatorDiscoveryDebug
    {
        public const bool Enabled = true;
    }
    /// <summary>
    /// Discovers planting recipes from the cultivator's piece table so the farmer
    /// can offer them as work orders. Recipe = what the cultivator needs (inputs) + what it shows (output).
    /// Excludes terrain (cultivate, grass/replant), tree saplings, and "seed" placeholder pieces
    /// (plant crop to get seeds); only seed→crop recipes are included. Supports mod-added plantable types.
    /// </summary>
    public static class CultivatorRecipeDiscovery
    {
        private static readonly FieldInfo s_buildPieces = typeof(ItemDrop.ItemData.SharedData)
            .GetField("m_buildPieces", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo s_mPieces = typeof(PieceTable)
            .GetField("m_pieces", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo s_mAvailablePieces = typeof(PieceTable)
            .GetField("m_availablePieces", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// Returns true if the given text (e.g. piece name, output item, ingredient name)
        /// should be excluded: if its lowercased form contains any exclusion substring.
        /// </summary>
        public static bool MatchesExclusion(string text, IReadOnlyList<string> exclusionSubstringsLower)
        {
            if (string.IsNullOrEmpty(text) || exclusionSubstringsLower == null) return false;
            var lower = text.ToLowerInvariant();
            for (int i = 0; i < exclusionSubstringsLower.Count; i++)
            {
                var ex = exclusionSubstringsLower[i];
                if (!string.IsNullOrEmpty(ex) && lower.Contains(ex))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns virtual recipe entries for all cultivator planting pieces that pass the filter.
        /// exclusionSubstringsLower: substrings (lowercased) to exclude; if piece name contains any, it is skipped.
        /// </summary>
        public static List<VirtualRecipeEntry> GetPlantingRecipes(HashSet<string> existingOutputs, IReadOnlyList<string> exclusionSubstringsLower)
        {
            var list = new List<VirtualRecipeEntry>();
            var objectDB = ObjectDB.instance;
            if (objectDB == null) return list;

            var cultivatorPrefab = objectDB.GetItemPrefab("Cultivator");
            if (cultivatorPrefab == null) return list;

            var itemDrop = cultivatorPrefab.GetComponent<ItemDrop>();
            if (itemDrop?.m_itemData?.m_shared == null) return list;

            var buildPieces = s_buildPieces?.GetValue(itemDrop.m_itemData.m_shared) as PieceTable;
            if (buildPieces == null) return list;

            var pieceGos = GetPieceGameObjects(buildPieces, out int fromMCount, out int fromAvailableCount);
            if (CultivatorDiscoveryDebug.Enabled && ValheimVillages.Plugin.Log != null)
            {
                ValheimVillages.Plugin.Log.LogInfo($"[Valheim Villages] Cultivator discovery: m_pieces count={fromMCount}, m_availablePieces total pieces={fromAvailableCount}, merged unique={pieceGos?.Count ?? 0}");
            }
            if (pieceGos == null || pieceGos.Count == 0) return list;

            foreach (var pieceGo in pieceGos)
            {
                if (pieceGo == null) continue;
                var piece = pieceGo.GetComponent<Piece>();
                if (piece == null)
                {
                    if (CultivatorDiscoveryDebug.Enabled && ValheimVillages.Plugin.Log != null)
                        ValheimVillages.Plugin.Log.LogDebug($"[Valheim Villages] Piece skip (no Piece component): {pieceGo.name}");
                    continue;
                }
                var pieceName = piece.m_name ?? pieceGo.name;
                if (MatchesExclusion(pieceName, exclusionSubstringsLower))
                {
                    if (CultivatorDiscoveryDebug.Enabled && ValheimVillages.Plugin.Log != null)
                        ValheimVillages.Plugin.Log.LogDebug($"[Valheim Villages] Piece filtered out (piece name): name='{pieceName}' (gameObject='{pieceGo.name}')");
                    continue;
                }

                string outputItemName = ResolveOutputItemName(pieceGo, piece);
                if (string.IsNullOrEmpty(outputItemName))
                {
                    if (CultivatorDiscoveryDebug.Enabled && ValheimVillages.Plugin.Log != null)
                        ValheimVillages.Plugin.Log.LogDebug($"[Valheim Villages] Piece no output: name='{pieceName}' gameObject='{pieceGo.name}'");
                    continue;
                }
                if (MatchesExclusion(outputItemName, exclusionSubstringsLower))
                {
                    if (CultivatorDiscoveryDebug.Enabled && ValheimVillages.Plugin.Log != null)
                        ValheimVillages.Plugin.Log.LogDebug($"[Valheim Villages] Piece filtered out (output): output={outputItemName} name='{pieceName}'");
                    continue;
                }
                if (existingOutputs != null && existingOutputs.Contains(outputItemName))
                {
                    if (CultivatorDiscoveryDebug.Enabled && ValheimVillages.Plugin.Log != null)
                        ValheimVillages.Plugin.Log.LogDebug($"[Valheim Villages] Piece skipped (already in JSON): output={outputItemName} name='{pieceName}'");
                    continue;
                }

                var inputs = GetInputsFromPiece(piece);
                if (inputs == null || inputs.Length == 0)
                {
                    if (CultivatorDiscoveryDebug.Enabled && ValheimVillages.Plugin.Log != null)
                        ValheimVillages.Plugin.Log.LogDebug($"[Valheim Villages] Piece no inputs: name='{pieceName}' output={outputItemName}");
                    continue;
                }
                bool inputExcluded = false;
                for (int ii = 0; ii < inputs.Length; ii++)
                {
                    if (MatchesExclusion(inputs[ii].item, exclusionSubstringsLower))
                    {
                        inputExcluded = true;
                        if (CultivatorDiscoveryDebug.Enabled && ValheimVillages.Plugin.Log != null)
                            ValheimVillages.Plugin.Log.LogDebug($"[Valheim Villages] Piece filtered out (ingredient): input={inputs[ii].item} name='{pieceName}'");
                        break;
                    }
                }
                if (inputExcluded) continue;

                if (CultivatorDiscoveryDebug.Enabled && ValheimVillages.Plugin.Log != null)
                    ValheimVillages.Plugin.Log.LogInfo($"[Valheim Villages] Cultivator recipe added: '{pieceName}' -> {outputItemName} (inputs: {string.Join(", ", System.Array.ConvertAll(inputs, i => $"{i.item}x{i.amount}"))})");

                // Register the piece prefab so farming NPCs can plant it
                PlantPieceRegistry.Register(outputItemName, pieceGo);

                list.Add(new VirtualRecipeEntry
                {
                    output = outputItemName,
                    outputAmount = 1,
                    inputs = inputs,
                    minStationLevel = 1,
                    physicalStation = "farm"
                });
            }

            if (CultivatorDiscoveryDebug.Enabled && ValheimVillages.Plugin.Log != null)
                ValheimVillages.Plugin.Log.LogInfo($"[Valheim Villages] Cultivator discovery total recipes added: {list.Count}");
            return list;
        }

        /// <summary>Get all piece GameObjects from the table; merge m_pieces and m_availablePieces so mod-added plantable types are included.</summary>
        private static List<GameObject> GetPieceGameObjects(PieceTable table, out int fromMPiecesCount, out int fromAvailablePiecesCount)
        {
            fromMPiecesCount = 0;
            fromAvailablePiecesCount = 0;
            var seen = new HashSet<GameObject>();
            var result = new List<GameObject>();

            var fromM = s_mPieces?.GetValue(table) as List<GameObject>;
            if (fromM != null)
            {
                fromMPiecesCount = fromM.Count;
                foreach (var go in fromM)
                {
                    if (go != null && seen.Add(go))
                        result.Add(go);
                }
            }

            var available = s_mAvailablePieces?.GetValue(table) as List<List<Piece>>;
            if (available != null)
            {
                foreach (var category in available)
                {
                    if (category == null) continue;
                    foreach (var p in category)
                    {
                        if (p == null || p.gameObject == null) continue;
                        fromAvailablePiecesCount++;
                        if (seen.Add(p.gameObject))
                            result.Add(p.gameObject);
                    }
                }
            }

            if (CultivatorDiscoveryDebug.Enabled && ValheimVillages.Plugin.Log != null)
            {
                var names = new List<string>();
                foreach (var go in result)
                {
                    if (go == null) continue;
                    var piece = go.GetComponent<Piece>();
                    var name = piece != null ? (piece.m_name ?? go.name) : go.name;
                    names.Add(name);
                }
                ValheimVillages.Plugin.Log.LogInfo($"[Valheim Villages] Cultivator piece list (all): [{string.Join(", ", names)}]");
            }
            return result;
        }

        private static string ResolveOutputItemName(GameObject pieceGo, Piece piece)
        {
            // Prefer: Plant.m_grownPrefabs[0] -> Pickable.m_itemPrefab (harvest drop)
            var plant = pieceGo.GetComponent<Plant>();
            if (plant != null)
            {
                var grownPrefabs = plant.m_grownPrefabs;
                if (grownPrefabs != null && grownPrefabs.Length > 0)
                {
                    for (int i = 0; i < grownPrefabs.Length; i++)
                    {
                        var grown = grownPrefabs[i];
                        if (grown == null) continue;
                        var pickable = grown.GetComponent<Pickable>();
                        if (pickable == null) continue;
                        var itemPrefab = pickable.m_itemPrefab;
                        if (itemPrefab != null)
                        {
                            var drop = itemPrefab.GetComponent<ItemDrop>();
                            if (drop != null)
                                return itemPrefab.name;
                        }
                    }
                }
            }

            // Fallback: some mod pieces might have Pickable on the piece itself
            var pickableOnPiece = pieceGo.GetComponent<Pickable>();
            if (pickableOnPiece != null && pickableOnPiece.m_itemPrefab != null)
                return pickableOnPiece.m_itemPrefab.name;

            // Fallback: output = first resource item (e.g. "plant mushroom" -> Mushroom)
            if (piece.m_resources != null && piece.m_resources.Length > 0 && piece.m_resources[0].m_resItem != null)
                return piece.m_resources[0].m_resItem.gameObject.name;

            return null;
        }

        private static VirtualRecipeInput[] GetInputsFromPiece(Piece piece)
        {
            if (piece.m_resources == null || piece.m_resources.Length == 0) return null;
            var inputs = new List<VirtualRecipeInput>();
            foreach (var req in piece.m_resources)
            {
                if (req.m_resItem == null) continue;
                var name = req.m_resItem.gameObject.name;
                if (string.IsNullOrEmpty(name)) continue;
                inputs.Add(new VirtualRecipeInput { item = name, amount = req.m_amount });
            }
            return inputs.Count > 0 ? inputs.ToArray() : null;
        }
    }
}
