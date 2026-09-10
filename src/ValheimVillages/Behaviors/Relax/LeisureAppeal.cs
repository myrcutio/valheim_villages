using UnityEngine;
using ValheimVillages.Enums;
using ValheimVillages.Schemas;

namespace ValheimVillages.Behaviors.Relax
{
    /// <summary>
    ///     Scores an idle spot for one villager: <c>Σ driveᵢ · gainᵢ(spot)</c>, then modulated
    ///     by the spot's comfort, its shelter fit for the current weather, how recently this
    ///     villager was there, and who else is already standing at it.
    ///
    ///     <para>This is the "needs" half of idle selection. The spatial half stays where it
    ///     was — <see cref="RelaxBehavior" /> still resolves an approach and rejects anything
    ///     unreachable — so a wildly appealing spot on the far side of a wall never wins.</para>
    /// </summary>
    public static class LeisureAppeal
    {
        /// <summary>Weight of the spot's own comfort rating on the final score.</summary>
        public const float ComfortWeight = 0.45f;

        /// <summary>Bonus multiplier for a sheltered spot when it is wet, cold, or dark.</summary>
        public const float ShelterBonus = 1.6f;

        /// <summary>Penalty multiplier for an exposed spot when it is wet, cold, or dark.</summary>
        public const float ExposedPenalty = 0.55f;

        /// <summary>How much a second villager already at a spot boosts its Social appeal.</summary>
        public const float CompanyBonusPerVillager = 0.35f;

        /// <summary>Cap on the company bonus, so a crowd doesn't become a black hole.</summary>
        public const int MaxCompanyCounted = 3;

        /// <summary>Seconds after a visit before a spot feels novel again.</summary>
        public const float NoveltyRecoverySeconds = 240f;

        /// <summary>
        ///     How much a spot of the given kind serves the given drive, in [0,1].
        ///     This is the whole personality of the system — a fire is warm and a bit social,
        ///     a table is social and restful, the farm and the animals are interesting but
        ///     neither warm nor restful.
        /// </summary>
        public static float Gain(LocationType type, Drive drive)
        {
            switch (type)
            {
                case LocationType.Fire:
                    return drive switch
                    {
                        Drive.Warmth => 1.0f,
                        Drive.Social => 0.5f,
                        Drive.Rest => 0.4f,
                        _ => 0f,
                    };

                case LocationType.HotTub:
                    return drive switch
                    {
                        Drive.Warmth => 1.0f,
                        Drive.Rest => 0.9f,
                        Drive.Social => 0.3f,
                        _ => 0f,
                    };

                case LocationType.Table:
                    return drive switch
                    {
                        Drive.Social => 1.0f,
                        Drive.Rest => 0.5f,
                        Drive.Warmth => 0.2f,
                        _ => 0f,
                    };

                case LocationType.Chair:
                    return drive switch
                    {
                        Drive.Rest => 1.0f,
                        Drive.Social => 0.2f,
                        _ => 0f,
                    };

                case LocationType.Shelter:
                    return drive switch
                    {
                        Drive.Warmth => 0.7f,
                        Drive.Rest => 0.5f,
                        _ => 0f,
                    };

                case LocationType.Farm:
                    return drive switch
                    {
                        Drive.Curiosity => 0.9f,
                        _ => 0f,
                    };

                case LocationType.Animals:
                    return drive switch
                    {
                        Drive.Curiosity => 1.0f,
                        Drive.Social => 0.4f,
                        _ => 0f,
                    };

                default:
                    return 0f;
            }
        }

        /// <summary>True if this spot kind is somewhere a villager would idle at all.</summary>
        public static bool IsLeisureType(LocationType type)
        {
            switch (type)
            {
                case LocationType.Fire:
                case LocationType.HotTub:
                case LocationType.Table:
                case LocationType.Chair:
                case LocationType.Shelter:
                case LocationType.Farm:
                case LocationType.Animals:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        ///     Appeal of one spot to one villager. Higher wins. Returns 0 for spot kinds that
        ///     serve nothing, so they are never chosen.
        /// </summary>
        /// <param name="companyCount">
        ///     Villagers already lingering at this spot, excluding the one choosing.
        /// </param>
        public static float Score(KnownLocation spot, DriveState drives, int companyCount, float now)
        {
            if (spot == null || drives == null) return 0f;
            if (!IsLeisureType(spot.Type)) return 0f;

            // Needs term: how well this spot serves what the villager currently wants.
            var need = 0f;
            foreach (Drive drive in System.Enum.GetValues(typeof(Drive)))
            {
                var gain = Gain(spot.Type, drive);
                if (gain <= 0f) continue;

                var pressure = drives.Get(drive);
                if (drive == Drive.Social && companyCount > 0)
                {
                    // Company makes a social spot more attractive the more the villager
                    // wants company — this is what makes them cluster rather than spread.
                    var company = Mathf.Min(companyCount, MaxCompanyCounted);
                    pressure = Mathf.Clamp01(pressure + company * CompanyBonusPerVillager * pressure);
                }

                need += pressure * gain;
            }

            if (need <= 0f) return 0f;

            // Comfort term: Valheim already rates these pieces, so a banner-lit hearth beats
            // a lone campfire without us re-deriving it.
            var score = need * (1f + ComfortWeight * Mathf.Clamp01(spot.ComfortValue / 10f));

            // Weather term: sheltered spots win when it is wet, cold, or dark.
            if (VillagerDrives.IsCold())
                score *= spot.HasShelter ? ShelterBonus : ExposedPenalty;

            // Novelty term: a spot just visited is dull until it recovers.
            if (spot.LastVisitedAt > 0f)
            {
                var since = now - spot.LastVisitedAt;
                if (since < NoveltyRecoverySeconds)
                    score *= Mathf.Lerp(0.35f, 1f, since / NoveltyRecoverySeconds);
            }

            return score;
        }
    }
}
