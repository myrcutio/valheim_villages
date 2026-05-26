using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Items.VirtualRecipes
{
    /// <summary>
    ///     Maps output item prefab names (e.g., "Carrot") to their cultivator piece
    ///     GameObjects (e.g., the "sapling_carrot" piece). Built during cultivator
    ///     recipe discovery so farming NPCs can look up which piece to instantiate.
    /// </summary>
    public static class PlantPieceRegistry
    {
        private static readonly Dictionary<string, GameObject> _outputToPiece = new();

        /// <summary>
        ///     Register a mapping from output item prefab name to cultivator piece prefab.
        ///     Called during CultivatorRecipeDiscovery.
        /// </summary>
        public static void Register(string outputItemName, GameObject piecePrefab)
        {
            if (string.IsNullOrEmpty(outputItemName) || piecePrefab == null) return;
            _outputToPiece[outputItemName] = piecePrefab;
            Plugin.Log?.LogDebug(
                $"PlantPieceRegistry: {outputItemName} -> {piecePrefab.name}");
        }

        /// <summary>
        ///     Get the cultivator piece prefab for the given output item, or null if not found.
        /// </summary>
        public static GameObject GetPiecePrefab(string outputItemName)
        {
            if (string.IsNullOrEmpty(outputItemName)) return null;
            return _outputToPiece.TryGetValue(outputItemName, out var prefab) ? prefab : null;
        }

        /// <summary>
        ///     Returns true if the given output item has a registered plant piece.
        /// </summary>
        public static bool IsFarmingOutput(string outputItemName)
        {
            return !string.IsNullOrEmpty(outputItemName) && _outputToPiece.ContainsKey(outputItemName);
        }

        /// <summary>
        ///     Clear all registrations (for hot reload).
        /// </summary>
        [RegisterCleanup]
        public static void Clear()
        {
            _outputToPiece.Clear();
        }
    }
}