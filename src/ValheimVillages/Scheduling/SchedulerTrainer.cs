using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Scheduling
{
    /// <summary>
    ///     Online learning for the reranker's residual. Each dispatch is one training sample:
    ///     the feature vector that won, and whether the pick actually converted into work.
    ///
    ///     <para>
    ///     TARGET. The reranker scores <c>U = closed + residual</c>, where <c>closed</c> is the
    ///     hand-written utility. We regress the TOTAL toward the observed reward, so the residual's
    ///     target is <c>reward - closed</c>. That keeps the closed form as a prior and makes the net
    ///     learn only the part the prior gets wrong — which is why an untrained (zero) model is
    ///     exactly the old behaviour rather than a random one.
    ///     </para>
    ///
    ///     <para>
    ///     REWARD. Currently dispatch CONVERSION: 1 if <c>BeginAssignment</c> accepted and work
    ///     started, 0 if the offer fizzled. That directly targets the churn this codebase already
    ///     fights (assign -> "no work payload" -> abandon, every tick). It is deliberately NOT a
    ///     productivity signal: a richer reward (output actually deposited, time-to-complete) needs
    ///     outcome tracking past the dispatch frame and is the obvious next step. Read the learned
    ///     residual as "how likely is this row to turn into real work", not "how valuable is it".
    ///     </para>
    ///
    ///     <para>
    ///     EXPLORATION. There is none — the reranker is greedy. The closed form is what keeps the
    ///     board honest: a starved order's deficit climbs until it wins on the prior alone, so new
    ///     rows do get sampled. Without that the learner could never observe an order it has
    ///     learned to avoid. Worth revisiting if the residual ever grows large enough to overpower
    ///     the deficit term.
    ///     </para>
    /// </summary>
    public static class SchedulerTrainer
    {
        /// <summary>Seeds <see cref="Mlp.InitializeForTraining" /> so a run is reproducible.</summary>
        private const int InitSeed = 20260913;

        /// <summary>
        ///     Apply one SGD step for a dispatch decision and persist the result.
        ///     No-op when training is off, when the pick carried no features (nothing was
        ///     selected), or when the village has no durable carrier to save into.
        /// </summary>
        public static void Learn(
            Village village, Mlp mlp, RerankSettings settings,
            in TaskReranker.RerankPick pick, float reward)
        {
            if (!SchedulerSettings.TrainingEnabled) return;
            if (mlp == null || village == null) return;
            if (pick.Task == null || pick.Features == null) return;

            // A zero net has zero gradient everywhere except the output bias, so it can never
            // learn an input-dependent function. Break the symmetry the first time we actually
            // train — this is what keeps "nobody trained it" identical to the old behaviour.
            if (mlp.IsUntrained()) mlp.InitializeForTraining(InitSeed);

            var target = reward - pick.Closed;
            var before = mlp.Train(pick.Features, target, SchedulerSettings.LearningRate);

            if (SchedulerSettings.LogTraining)
                Plugin.Log?.LogInfo(
                    $"[SchedulerTrain] {pick.Task.Kind}" +
                    (pick.Task.TargetItemPrefab != null ? $":{pick.Task.TargetItemPrefab}" : "") +
                    $" reward={reward:F2} closed={pick.Closed:F3} target={target:F3} " +
                    $"residual={before:F3} err={before - target:F3}");

            SchedulerModelPersistence.Save(village, mlp, settings);
        }
    }
}
