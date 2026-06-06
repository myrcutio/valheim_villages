using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ValheimVillages.Villager
{
    /// <summary>
    ///     Shared, player-safe teardown of the native engine components a villager NPC
    ///     inherits from its base prefab (Dverger <c>MonsterAI</c> / <c>NpcTalk</c> /
    ///     <c>Tameable</c>). Used by both the spawn path (<c>VillagerPawnPatch</c>) and
    ///     the restore path (<c>VillagerRestoration</c>) so they tear down identically.
    ///
    ///     <para>Two invariants every caller depends on:</para>
    ///     <list type="number">
    ///         <item>
    ///             NEVER mutate or destroy a <see cref="Character" /> that IS — or is
    ///             parented under — a real <see cref="Player" />. A prefab/ZDO lookup
    ///             that resolves to the player must be a no-op, not a component strip or
    ///             faction flip. <see cref="IsPlayerOwned(GameObject)" /> is the gate.
    ///         </item>
    ///         <item>
    ///             Removing a native <see cref="MonsterAI" /> is not just
    ///             <c>DestroyImmediate</c>. Its <c>Awake</c> already registered RPCs
    ///             (<c>Alert</c> / <c>OnNearProjectileHit</c> / <c>SetAggravated</c>) on
    ///             the shared <see cref="ZNetView" /> and bound <c>OnDamaged</c>/
    ///             <c>OnDeath</c> onto the <see cref="Character" />. Those survive the
    ///             destroy: the RPCs collide when our <c>VillagerAI</c> (also a
    ///             <c>BaseAI</c>) re-registers them — aborting <c>BaseAI.Awake</c> before
    ///             <c>m_character</c> is set — and the dangling delegates NRE on the next
    ///             hit. <see cref="Strip" /> unregisters/detaches them first.
    ///         </item>
    ///     </list>
    /// </summary>
    public static class NativeNpcStripper
    {
        // RPC names BaseAI.Awake registers on the ZNetView. A second BaseAI (our
        // VillagerAI) re-registering any of these throws "same key already added", so
        // they must be cleared before the replacement AI Awakes.
        private static readonly string[] BaseAiRpcs = { "Alert", "OnNearProjectileHit", "SetAggravated" };

        /// <summary>
        ///     True when <paramref name="go" /> is a player character or sits underneath
        ///     one in the hierarchy. Any villager-targeted mutate/destroy must veto on this.
        /// </summary>
        public static bool IsPlayerOwned(GameObject go)
        {
            return go != null && go.GetComponentInParent<Player>() != null;
        }

        /// <summary>
        ///     True when <paramref name="character" /> is, or is a child of, a player.
        ///     Overload for callers that already hold the resolved <c>m_character</c>.
        /// </summary>
        public static bool IsPlayerOwned(Character character)
        {
            return character != null && character.GetComponentInParent<Player>() != null;
        }

        /// <summary>
        ///     Strip the native Dverger AI/talk/tame components and undo the side effects
        ///     their Awake left on the shared ZNetView/Character, so a fresh VillagerAI can
        ///     re-register cleanly. No-op (logged loud) on a player-owned object.
        /// </summary>
        public static void Strip(GameObject go)
        {
            if (go == null) return;

            if (IsPlayerOwned(go))
            {
                Plugin.Log?.LogError(
                    $"[NativeNpcStripper] Refusing to strip native components on player-owned object " +
                    $"'{go.name}' — a villager lookup resolved to the player character.");
                return;
            }

            var character = go.GetComponent<Character>();
            var nview = go.GetComponent<ZNetView>();

            // Always clear the BaseAI RPC slots before a new BaseAI Awakes — a prior
            // (already-destroyed) MonsterAI or VillagerAI may have left them registered.
            UnregisterBaseAiRpcs(nview);
            // Drop any OnDamaged/OnDeath handlers whose target is already dead, so they
            // can't fire on a destroyed component and NRE on its transform.
            PruneDeadHandlers(character);

            var monsterAI = go.GetComponent<MonsterAI>();
            if (monsterAI != null)
            {
                DetachHandlers(character, monsterAI);
                Object.DestroyImmediate(monsterAI);
            }

            var tameable = go.GetComponent<Tameable>();
            if (tameable != null)
            {
                DetachHandlers(character, tameable);
                Object.DestroyImmediate(tameable);
            }

            var npcTalk = go.GetComponent<NpcTalk>();
            if (npcTalk != null)
                Object.DestroyImmediate(npcTalk);
        }

        private static void UnregisterBaseAiRpcs(ZNetView nview)
        {
            if (nview == null) return;
            foreach (var rpc in BaseAiRpcs)
                nview.Unregister(rpc);
        }

        /// <summary>Remove handlers bound by <paramref name="component" /> from the character delegates.</summary>
        private static void DetachHandlers(Character character, object component)
        {
            if (character == null || component == null) return;
            character.m_onDamaged = (Action<float, Character>)Prune(character.m_onDamaged, component);
            character.m_onDeath = (Action)Prune(character.m_onDeath, component);
        }

        /// <summary>Remove handlers whose target is an already-destroyed Unity object.</summary>
        private static void PruneDeadHandlers(Character character)
        {
            if (character == null) return;
            character.m_onDamaged = (Action<float, Character>)Prune(character.m_onDamaged, null);
            character.m_onDeath = (Action)Prune(character.m_onDeath, null);
        }

        /// <summary>
        ///     Strip from a multicast delegate every handler whose target is
        ///     <paramref name="component" /> (when non-null) or an already-destroyed
        ///     UnityEngine.Object. Preserves unrelated handlers (e.g. CharacterDrop loot).
        /// </summary>
        private static Delegate Prune(Delegate multicast, object component)
        {
            if (multicast == null) return null;
            var result = multicast;
            foreach (var handler in multicast.GetInvocationList())
            {
                var target = handler.Target;
                var deadUnityObject = target is Object unityTarget && unityTarget == null;
                if ((component != null && ReferenceEquals(target, component)) || deadUnityObject)
                    result = Delegate.Remove(result, handler);
            }

            return result;
        }
    }
}
