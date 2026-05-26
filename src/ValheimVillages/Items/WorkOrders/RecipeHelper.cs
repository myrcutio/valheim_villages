using System;
using System.Reflection;

namespace ValheimVillages.Items.WorkOrders
{
    /// <summary>
    ///     Helper for accessing the currently selected recipe from InventoryGui.
    ///     Uses reflection to access private InventoryGui fields without
    ///     depending on assembly_guiutils.
    /// </summary>
    public static class RecipeHelper
    {
        private static readonly FieldInfo s_selectedRecipeField =
            typeof(InventoryGui).GetField("m_selectedRecipe",
                BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        ///     Gets the currently selected recipe from InventoryGui.
        ///     Returns null if no recipe is selected or the GUI is not available.
        /// </summary>
        public static Recipe GetSelectedRecipe()
        {
            var gui = InventoryGui.instance;
            if (gui == null || s_selectedRecipeField == null)
                return null;

            try
            {
                var pair = s_selectedRecipeField.GetValue(gui);
                if (pair == null) return null;

                // RecipeDataPair is a struct with a Recipe property
                var recipeProp = pair.GetType().GetProperty("Recipe",
                    BindingFlags.Public | BindingFlags.Instance);
                return recipeProp?.GetValue(pair) as Recipe;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Failed to get selected recipe: {ex.Message}");
                return null;
            }
        }
    }
}