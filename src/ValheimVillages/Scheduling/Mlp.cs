using System;

namespace ValheimVillages.Scheduling
{
    /// <summary>
    ///     Minimal feed-forward net: <c>inputs → hidden (ReLU) → 1 (linear)</c>.
    ///     Row-major weight arrays and a single preallocated hidden-activation scratch
    ///     buffer — zero per-call allocation. Sized for a handful of inputs and ~16
    ///     hidden units, a forward pass is a few hundred multiply-adds (microseconds).
    ///
    ///     <para>
    ///     An all-zero weight set (the untrained default) makes <see cref="Forward" />
    ///     return <c>m_b2</c> == 0, so an untrained reranker is driven entirely by its
    ///     closed-form utility and the net contributes nothing until trained.
    ///     </para>
    /// </summary>
    public sealed class Mlp
    {
        private readonly int m_in;
        private readonly int m_hidden;
        private readonly float[] m_w1; // [hidden * in], row-major (row = one hidden unit)
        private readonly float[] m_b1; // [hidden]
        private readonly float[] m_w2; // [hidden]
        private float m_b2;
        private readonly float[] m_h; // [hidden] activation scratch
        private readonly float[] m_dz; // [hidden] backprop delta scratch

        public Mlp(int inputs, int hidden)
        {
            if (inputs <= 0) throw new ArgumentOutOfRangeException(nameof(inputs));
            if (hidden <= 0) throw new ArgumentOutOfRangeException(nameof(hidden));
            m_in = inputs;
            m_hidden = hidden;
            m_w1 = new float[hidden * inputs];
            m_b1 = new float[hidden];
            m_w2 = new float[hidden];
            m_h = new float[hidden];
            m_dz = new float[hidden];
        }

        public int InputCount => m_in;
        public int HiddenCount => m_hidden;

        public float Forward(float[] x)
        {
            if (x == null || x.Length != m_in)
                throw new ArgumentException($"expected {m_in} inputs, got {x?.Length ?? 0}");

            var sum2 = m_b2;
            for (var j = 0; j < m_hidden; j++)
            {
                var acc = m_b1[j];
                var rowBase = j * m_in;
                for (var i = 0; i < m_in; i++)
                    acc += m_w1[rowBase + i] * x[i];
                var a = acc > 0f ? acc : 0f; // ReLU
                m_h[j] = a;
                sum2 += m_w2[j] * a;
            }

            return sum2;
        }

        // --- Training: SGD on squared error against a scalar target ---

        /// <summary>
        ///     True while every weight is zero — the untrained default, where
        ///     <see cref="Forward" /> returns 0 and the reranker runs on its closed form alone.
        /// </summary>
        public bool IsUntrained()
        {
            foreach (var w in m_w1)
                if (w != 0f) return false;
            foreach (var b in m_b1)
                if (b != 0f) return false;
            foreach (var w in m_w2)
                if (w != 0f) return false;
            return m_b2 == 0f;
        }

        /// <summary>
        ///     Break the all-zero symmetry so gradients can flow. An all-zero net is a DEAD start,
        ///     not merely a neutral one: with <c>w2 = 0</c> the hidden deltas are
        ///     <c>e·w2 = 0</c>, and with <c>b1 = 0</c> the ReLU outputs are 0, so w1, b1 and w2 all
        ///     have exactly zero gradient forever and only the output bias could ever move.
        ///     Called lazily on the first <see cref="Train" />, which preserves the useful property
        ///     that a model nobody has trained contributes exactly nothing.
        /// </summary>
        public void InitializeForTraining(int seed)
        {
            var rng = new Random(seed);
            // He-style scale for ReLU: sqrt(2/fan_in).
            var scale = (float)Math.Sqrt(2.0 / m_in);
            for (var i = 0; i < m_w1.Length; i++)
                m_w1[i] = (float)(rng.NextDouble() * 2.0 - 1.0) * scale;
            for (var j = 0; j < m_b1.Length; j++)
                m_b1[j] = 0.01f; // small positive: keeps ReLUs alive at init
            for (var j = 0; j < m_w2.Length; j++)
                m_w2[j] = (float)(rng.NextDouble() * 2.0 - 1.0) * 0.1f;
            m_b2 = 0f;
        }

        /// <summary>
        ///     One SGD step minimizing <c>0.5·(forward(x) − target)²</c>. Returns the prediction
        ///     from BEFORE the update, so a caller can log the error it corrected.
        /// </summary>
        public float Train(float[] x, float target, float learningRate)
        {
            var pred = Forward(x); // also fills m_h with this input's activations
            var e = pred - target;

            // Hidden deltas must be computed against the PRE-update w2, so do them first.
            for (var j = 0; j < m_hidden; j++)
                m_dz[j] = m_h[j] > 0f ? e * m_w2[j] : 0f; // ReLU mask: h>0 <=> pre-activation>0

            for (var j = 0; j < m_hidden; j++)
                m_w2[j] -= learningRate * e * m_h[j];
            m_b2 -= learningRate * e;

            for (var j = 0; j < m_hidden; j++)
            {
                var dz = m_dz[j];
                if (dz == 0f) continue;
                m_b1[j] -= learningRate * dz;
                var rowBase = j * m_in;
                for (var i = 0; i < m_in; i++)
                    m_w1[rowBase + i] -= learningRate * dz * x[i];
            }

            return pred;
        }

        // --- Flat weight (de)serialization: layout is W1 | b1 | W2 | b2 ---

        public int WeightCount => m_w1.Length + m_b1.Length + m_w2.Length + 1;

        public void LoadWeights(float[] flat)
        {
            if (flat == null || flat.Length != WeightCount)
                throw new ArgumentException($"expected {WeightCount} weights, got {flat?.Length ?? 0}");
            var o = 0;
            Array.Copy(flat, o, m_w1, 0, m_w1.Length);
            o += m_w1.Length;
            Array.Copy(flat, o, m_b1, 0, m_b1.Length);
            o += m_b1.Length;
            Array.Copy(flat, o, m_w2, 0, m_w2.Length);
            o += m_w2.Length;
            m_b2 = flat[o];
        }

        public float[] SaveWeights()
        {
            var flat = new float[WeightCount];
            var o = 0;
            Array.Copy(m_w1, 0, flat, o, m_w1.Length);
            o += m_w1.Length;
            Array.Copy(m_b1, 0, flat, o, m_b1.Length);
            o += m_b1.Length;
            Array.Copy(m_w2, 0, flat, o, m_w2.Length);
            o += m_w2.Length;
            flat[o] = m_b2;
            return flat;
        }
    }
}
