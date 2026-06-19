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
