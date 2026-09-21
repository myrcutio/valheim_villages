using UnityEngine;

namespace ValheimVillages.Behaviors.Repair
{
    /// <summary>
    ///     How damaged a piece actually is, measured the way <see cref="WearNTear.Repair" />
    ///     measures it.
    ///
    ///     <para><b>Not <c>WearNTear.GetHealthPercentage()</c>.</b> That returns a cached
    ///     <c>m_healthPercentage</c> written in exactly two places: Awake, and the owner's
    ///     <c>RPC_HealthChanged</c> broadcast. Awake computes it as
    ///     <c>zdoHealth / m_health</c> — but reads the ZDO BEFORE the world-level multiplier
    ///     inflates <c>m_health</c>:</para>
    ///     <code>
    ///     float num = zdo.GetFloat(s_health, m_health);            // base max
    ///     if (Game.m_worldLevel > 0)
    ///         m_health += worldLevel * multiplier * m_health;      // max scaled UP
    ///     m_healthPercentage = Clamp01(num / m_health);            // base / scaled &lt; 1
    ///     </code>
    ///     <para>So on any world past world level 0, a perfectly intact piece reports a
    ///     health percentage of <c>1/(1+level*multiplier)</c> forever — and it cannot be
    ///     repaired, because <c>Repair()</c> compares the ZDO value against the SCALED max
    ///     (defaulting to it), sees full health, and returns false without touching anything.
    ///     Measured on a live server: every intact piece in the village read 0.50, the
    ///     carpenter was dispatched at one every tick, repaired nothing, and flooded the log
    ///     with a thousand assignments for the same two metres of wall.</para>
    ///
    ///     <para>Reading the ZDO against the same <c>m_health</c> that <c>Repair()</c> uses
    ///     makes "damaged" mean "repairing this would do something", which is the only
    ///     definition that cannot disagree with the action.</para>
    /// </summary>
    internal static class PieceHealth
    {
        /// <summary>Health as a 0..1 fraction of this piece's current maximum. 1 = intact.</summary>
        public static float Fraction(WearNTear wnt)
        {
            if (wnt == null || wnt.m_health <= 0f) return 1f;

            var nview = wnt.GetComponent<ZNetView>();
            var zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo == null) return 1f;

            return Mathf.Clamp01(zdo.GetFloat(ZDOVars.s_health, wnt.m_health) / wnt.m_health);
        }
    }
}
