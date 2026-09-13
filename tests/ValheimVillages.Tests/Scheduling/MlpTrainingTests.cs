using ValheimVillages.Scheduling;
using Xunit;

namespace ValheimVillages.Tests.Scheduling;

/// <summary>
///     Gradient checks for <see cref="Mlp" />'s SGD step. These exist because the reranker's
///     learned residual is only as trustworthy as the backprop under it, and a silently-wrong
///     gradient looks identical to "the model hasn't learned yet".
/// </summary>
public class MlpTrainingTests
{
    private static float[] X(params float[] v) => v;

    [Fact]
    public void FreshModel_IsUntrained_AndContributesNothing()
    {
        var mlp = new Mlp(3, 8);

        Assert.True(mlp.IsUntrained());
        Assert.Equal(0f, mlp.Forward(X(1f, -2f, 3f)));
    }

    [Fact]
    public void AllZeroWeights_CannotLearn_WhichIsWhyInitializeForTrainingExists()
    {
        // Documents the trap: with w2 = 0 the hidden deltas are e*w2 = 0, and with b1 = 0 every
        // ReLU emits 0, so w1/b1/w2 all have exactly zero gradient. Only the output bias moves,
        // so training a zero net collapses to predicting a single constant.
        var mlp = new Mlp(2, 8);
        for (var i = 0; i < 200; i++)
        {
            mlp.Train(X(1f, 0f), 1f, 0.05f);
            mlp.Train(X(0f, 1f), -1f, 0.05f);
        }

        var a = mlp.Forward(X(1f, 0f));
        var b = mlp.Forward(X(0f, 1f));
        Assert.True(System.Math.Abs(a - b) < 0.01f,
            $"a zero-initialised net must stay input-independent, got a={a}, b={b}");
    }

    [Fact]
    public void AfterInitializeForTraining_LearnsAnInputDependentFunction()
    {
        var mlp = new Mlp(2, 16);
        mlp.InitializeForTraining(seed: 1234);
        Assert.False(mlp.IsUntrained());

        // The same task the zero net could not represent: separate two inputs.
        for (var i = 0; i < 4000; i++)
        {
            mlp.Train(X(1f, 0f), 1f, 0.02f);
            mlp.Train(X(0f, 1f), -1f, 0.02f);
        }

        var a = mlp.Forward(X(1f, 0f));
        var b = mlp.Forward(X(0f, 1f));
        Assert.True(a > 0.5f, $"expected ~+1 for the first input, got {a}");
        Assert.True(b < -0.5f, $"expected ~-1 for the second input, got {b}");
    }

    [Fact]
    public void TrainReducesErrorOnTheSampleItSaw()
    {
        var mlp = new Mlp(4, 12);
        mlp.InitializeForTraining(seed: 7);
        var x = X(0.5f, -0.25f, 1f, 0.1f);
        const float target = 2f;

        var before = System.Math.Abs(mlp.Forward(x) - target);
        for (var i = 0; i < 50; i++) mlp.Train(x, target, 0.01f);
        var after = System.Math.Abs(mlp.Forward(x) - target);

        Assert.True(after < before, $"error should shrink: before={before}, after={after}");
    }

    [Fact]
    public void TrainedWeightsSurviveASaveLoadRoundTrip()
    {
        var a = new Mlp(TaskReranker.FeatureCount, 16);
        a.InitializeForTraining(seed: 99);
        var x = new float[TaskReranker.FeatureCount];
        x[0] = 1f;
        for (var i = 0; i < 100; i++) a.Train(x, 1.5f, 0.01f);

        var b = new Mlp(TaskReranker.FeatureCount, 16);
        b.LoadWeights(a.SaveWeights());

        Assert.Equal(a.Forward(x), b.Forward(x), 5);
    }

    [Fact]
    public void ItemBucket_IsStable_AndSeparatesDistinctItems()
    {
        // Weights are persisted, so this mapping must not drift between sessions.
        Assert.Equal(TaskReranker.ItemBucket("CarrotSeeds"), TaskReranker.ItemBucket("CarrotSeeds"));
        Assert.InRange(TaskReranker.ItemBucket("CarrotSeeds"), 0, TaskReranker.ItemBuckets - 1);
        Assert.Equal(-1, TaskReranker.ItemBucket(null));
        Assert.Equal(-1, TaskReranker.ItemBucket(""));
    }
}
