namespace ValheimVillages.Items.WorkOrders
{
    /// <summary>
    ///     Turns an item PREFAB name into the name the player sees on the item.
    ///
    ///     <para>Anything a player reads should be in the game's words. "CookedLoxMeat" in a
    ///     blocked-order message is code leaking into the UI — and worse, it is not what the
    ///     item is called in their inventory, so it reads as a different thing entirely.</para>
    /// </summary>
    public static class ItemDisplay
    {
        public static string Name(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return "?";

            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            var token = prefab != null
                ? prefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_name
                : null;

            return string.IsNullOrEmpty(token) || Localization.instance == null
                ? prefabName
                : Localization.instance.Localize(token);
        }
    }
}
