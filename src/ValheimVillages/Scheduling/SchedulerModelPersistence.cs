using System;
using System.Globalization;
using System.Text;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Scheduling
{
    /// <summary>
    ///     Serializes the per-village scheduler model (MLP weights + reranker settings)
    ///     to the village's durable ZDO blob via <see cref="Village.GetBlob" /> /
    ///     <see cref="Village.SetBlob" /> — the same generic-blob seam the station data uses.
    ///
    ///     <para>
    ///     Format v1: <c>v1|hidden|w0,w1,…|perHop,sharpness,etaNorm,slackNorm</c>.
    ///     An absent/blank blob yields a fresh zero-weight model — untrained, so the
    ///     reranker runs on its closed-form utility alone until weights are learned.
    ///     </para>
    /// </summary>
    public static class SchedulerModelPersistence
    {
        public const string BlobKey = "vv_village_scheduler";

        /// <summary>
        ///     Bumped whenever <see cref="TaskReranker.FeatureCount" /> changes — persisted weights
        ///     are sized to the feature vector, so an older blob is unloadable by construction.
        /// </summary>
        public const string BlobVersion = "v2";

        /// <summary>Default hidden width for a freshly created model.</summary>
        public const int DefaultHidden = 16;

        public static (Mlp mlp, RerankSettings settings) LoadOrCreate(Village village)
        {
            var blob = village?.GetBlob(BlobKey);
            if (string.IsNullOrEmpty(blob))
                return (new Mlp(TaskReranker.FeatureCount, DefaultHidden), new RerankSettings());

            var parts = blob.Split('|');

            // A model trained against a different feature width cannot be loaded — the weights
            // are literally the wrong shape. Discard it LOUDLY and start fresh rather than
            // throwing (which would wedge every scheduler tick for this village) or silently
            // mis-loading. Retraining is the correct outcome of changing the feature set.
            if (parts.Length == 4 && parts[0] != BlobVersion && parts[0].StartsWith("v"))
            {
                Plugin.Log?.LogWarning(
                    $"[Scheduler] model blob for village {village.VillageId} is {parts[0]}, expected " +
                    $"{BlobVersion} (feature vector changed to {TaskReranker.FeatureCount} inputs). " +
                    "Discarding the old weights and starting from an untrained model.");
                return (new Mlp(TaskReranker.FeatureCount, DefaultHidden), new RerankSettings());
            }

            if (parts.Length != 4 || parts[0] != BlobVersion)
                throw new FormatException(
                    $"[Scheduler] unrecognized model blob for village {village.VillageId}");

            var hidden = int.Parse(parts[1], CultureInfo.InvariantCulture);
            var mlp = new Mlp(TaskReranker.FeatureCount, hidden);
            mlp.LoadWeights(ParseFloats(parts[2]));

            var cfg = ParseFloats(parts[3]);
            var settings = new RerankSettings
            {
                PerHopSeconds = cfg[0],
                SlackSharpness = cfg[1],
                EtaNorm = cfg[2],
                SlackNorm = cfg[3],
            };
            return (mlp, settings);
        }

        public static void Save(Village village, Mlp mlp, RerankSettings s)
        {
            if (village == null || mlp == null || s == null) return;

            var sb = new StringBuilder();
            sb.Append(BlobVersion).Append('|').Append(mlp.HiddenCount).Append('|');
            sb.Append(JoinFloats(mlp.SaveWeights())).Append('|');
            sb.Append(s.PerHopSeconds.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(s.SlackSharpness.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(s.EtaNorm.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(s.SlackNorm.ToString("R", CultureInfo.InvariantCulture));
            village.SetBlob(BlobKey, sb.ToString());
        }

        private static float[] ParseFloats(string csv)
        {
            var tok = csv.Split(',');
            var arr = new float[tok.Length];
            for (var i = 0; i < tok.Length; i++)
                arr[i] = float.Parse(tok[i], CultureInfo.InvariantCulture);
            return arr;
        }

        private static string JoinFloats(float[] a)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < a.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(a[i].ToString("R", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }
    }
}
