//
// Limiter.h -- Look-ahead brickwall peak limiter.
//
// Why look-ahead: a boost of up to +14 dB drives ordinary programme material
// well past 0 dBFS. A feedback limiter only reacts after the peak has clipped.
// Here the signal is delayed while the gain is computed from future samples,
// so the attenuation is in place by the time the peak arrives.
//
// The gain path, and why it holds the ceiling on its own:
//
//   t(n)  per-sample target: ceiling / |peak|, or 1
//   M(j)  = min t over the window [j, j+L-1]          (monotonic deque, exact O(1))
//   R(j)  = M(j) when M falls, otherwise rises toward M with the release time;
//           so R(j) <= M(j) always
//   G(k)  = mean of R(j) for j in [k-L+1, k]          (boxcar of length L)
//
// Every window [j, j+L-1] with j in [k-L+1, k] contains k, so M(j) <= t(k),
// hence R(j) <= t(k), hence their mean G(k) <= t(k). The applied gain can never
// exceed what sample k needs: the ceiling is met by construction, not by the
// safety clamp that follows. The boxcar also shapes the attack into a smooth
// L-sample ramp, which is what keeps heavy limiting free of crackle.
//
// An earlier version smoothed toward M with a one-pole attack instead. A
// one-pole never quite arrives, so under 10+ dB of limiting it overshot by a
// few percent and the clamp after it clipped audibly.
//
// Latency is L - 1 samples. All buffers come from AERT_Allocate (non-paged),
// as required for APO code on the audio engine real-time thread.
//
#pragma once
#include <cmath>
#include <cstring>
#include "Biquad.h"

namespace cres
{
    class Limiter
    {
    public:
        // Called from the configuration thread only.
        HRESULT Prepare(uint32_t sampleRate, uint32_t channels, float maxLookaheadMs)
        {
            Release();

            m_sampleRate = sampleRate ? sampleRate : 48000;
            m_channels = channels ? channels : 2;
            if (m_channels > CRESCENDO_MAX_CHANNELS) m_channels = CRESCENDO_MAX_CHANNELS;

            m_capacity = static_cast<uint32_t>(m_sampleRate * (maxLookaheadMs / 1000.0f)) + 2;
            if (m_capacity < 8) m_capacity = 8;

            const size_t delayBytes = static_cast<size_t>(m_capacity) * m_channels * sizeof(float);
            const size_t valBytes = static_cast<size_t>(m_capacity) * sizeof(float);
            const size_t idxBytes = static_cast<size_t>(m_capacity) * sizeof(uint32_t);

            HRESULT hr = AERT_Allocate(delayBytes, reinterpret_cast<void**>(&m_delay));
            if (FAILED(hr)) return hr;
            hr = AERT_Allocate(valBytes, reinterpret_cast<void**>(&m_dequeVal));
            if (FAILED(hr)) { Release(); return hr; }
            hr = AERT_Allocate(idxBytes, reinterpret_cast<void**>(&m_dequeIdx));
            if (FAILED(hr)) { Release(); return hr; }
            hr = AERT_Allocate(valBytes, reinterpret_cast<void**>(&m_box));
            if (FAILED(hr)) { Release(); return hr; }

            memset(m_delay, 0, delayBytes);
            memset(m_dequeVal, 0, valBytes);
            memset(m_dequeIdx, 0, idxBytes);

            m_lookahead = 0;
            SetParams(-0.3f, 120.0f, 5.0f);
            Reset();
            return S_OK;
        }

        void Release()
        {
            if (m_delay) { AERT_Free(m_delay); m_delay = nullptr; }
            if (m_dequeVal) { AERT_Free(m_dequeVal); m_dequeVal = nullptr; }
            if (m_dequeIdx) { AERT_Free(m_dequeIdx); m_dequeIdx = nullptr; }
            if (m_box) { AERT_Free(m_box); m_box = nullptr; }
            m_capacity = 0;
        }

        // Safe to call between blocks; clamps everything into the prepared range.
        void SetParams(float ceilingDb, float releaseMs, float lookaheadMs)
        {
            if (!(ceilingDb <= 0.0f)) ceilingDb = 0.0f;    // also catches NaN
            if (ceilingDb < -12.0f) ceilingDb = -12.0f;
            m_ceiling = std::pow(10.0f, ceilingDb / 20.0f);

            if (!(releaseMs >= 5.0f)) releaseMs = 5.0f;
            if (releaseMs > 2000.0f) releaseMs = 2000.0f;

            if (!(lookaheadMs >= 0.2f)) lookaheadMs = 0.2f;

            uint32_t look = static_cast<uint32_t>(m_sampleRate * (lookaheadMs / 1000.0f));
            if (look < 2) look = 2;
            if (m_capacity && look > m_capacity - 1) look = m_capacity - 1;

            if (look != m_lookahead)
            {
                m_lookahead = look;
                ResetWindow();
            }

            const float releaseSamples = m_sampleRate * (releaseMs / 1000.0f);
            m_releaseCoef = std::exp(-1.0f / (releaseSamples > 1.0f ? releaseSamples : 1.0f));
        }

        void Reset()
        {
            if (m_delay && m_capacity)
                memset(m_delay, 0, static_cast<size_t>(m_capacity) * m_channels * sizeof(float));
            m_writePos = 0;
            ResetWindow();
        }

        float Ceiling() const { return m_ceiling; }
        uint32_t LookaheadSamples() const { return m_lookahead; }

        /// Delay the limiter adds to the signal, as reported to Windows.
        uint32_t LatencySamples() const { return m_lookahead > 0 ? m_lookahead - 1 : 0; }

        bool IsReady() const { return m_delay != nullptr && m_box != nullptr && m_capacity > 0; }

        // Real-time path. In-place over an interleaved block.
        // Returns the deepest gain reduction applied in this block, in dB (>= 0).
        float Process(float* buffer, uint32_t frames, uint32_t channels)
        {
            if (!IsReady() || channels != m_channels) return 0.0f;

            const uint32_t length = m_lookahead;
            const uint32_t delay = length - 1;
            const double inverseLength = 1.0 / length;
            float minGain = 1.0f;

            for (uint32_t f = 0; f < frames; ++f)
            {
                float* frame = buffer + static_cast<size_t>(f) * channels;

                // Linked peak across channels, so the stereo image never shifts.
                float peak = 0.0f;
                for (uint32_t c = 0; c < channels; ++c)
                {
                    const float a = std::fabs(frame[c]);
                    if (a > peak) peak = a;
                }

                const float target = (peak > m_ceiling) ? (m_ceiling / peak) : 1.0f;
                PushTarget(target);

                // M: the minimum over the window, instant down, released up.
                const float windowMin = WindowMin();
                m_release = (windowMin < m_release)
                    ? windowMin
                    : windowMin + (m_release - windowMin) * m_releaseCoef;

                // G: boxcar over the last L released values.
                m_boxSum += static_cast<double>(m_release) - m_box[m_boxPos];
                m_box[m_boxPos] = m_release;
                if (++m_boxPos == length)
                {
                    m_boxPos = 0;
                    // Re-sum once per window so float rounding cannot drift.
                    double exact = 0.0;
                    for (uint32_t i = 0; i < length; ++i) exact += m_box[i];
                    m_boxSum = exact;
                }
                const float gain = static_cast<float>(m_boxSum * inverseLength);
                if (gain < minGain) minGain = gain;

                // The sample this gain belongs to entered L - 1 samples ago.
                const uint32_t readPos = (m_writePos + m_capacity - delay) % m_capacity;
                const float* delayed = m_delay + static_cast<size_t>(readPos) * channels;

                float out[CRESCENDO_MAX_CHANNELS];
                for (uint32_t c = 0; c < channels; ++c)
                    out[c] = delayed[c];

                float* slot = m_delay + static_cast<size_t>(m_writePos) * channels;
                for (uint32_t c = 0; c < channels; ++c)
                    slot[c] = frame[c];
                m_writePos = (m_writePos + 1) % m_capacity;

                for (uint32_t c = 0; c < channels; ++c)
                    frame[c] = out[c] * gain;
            }

            if (minGain >= 1.0f) return 0.0f;
            if (minGain < 1e-6f) minGain = 1e-6f;
            return -20.0f * std::log10(minGain);
        }

    private:
        void ResetWindow()
        {
            m_head = 0;
            m_tail = 0;
            m_count = 0;
            m_sampleIndex = 0;
            m_release = 1.0f;

            // An empty boxcar reads as "no reduction".
            m_boxPos = 0;
            m_boxSum = static_cast<double>(m_lookahead);
            if (m_box)
            {
                for (uint32_t i = 0; i < m_capacity; ++i) m_box[i] = 1.0f;
            }
        }

        // Monotonic deque: values increase from head to tail, so the head is
        // always the minimum of the last m_lookahead targets.
        void PushTarget(float target)
        {
            while (m_count > 0)
            {
                const uint32_t tailSlot = (m_tail + m_capacity - 1) % m_capacity;
                if (m_dequeVal[tailSlot] >= target)
                {
                    m_tail = tailSlot;
                    --m_count;
                }
                else break;
            }

            m_dequeVal[m_tail] = target;
            m_dequeIdx[m_tail] = m_sampleIndex;
            m_tail = (m_tail + 1) % m_capacity;
            ++m_count;

            while (m_count > 0 && m_sampleIndex - m_dequeIdx[m_head] >= m_lookahead)
            {
                m_head = (m_head + 1) % m_capacity;
                --m_count;
            }

            ++m_sampleIndex;
        }

        float WindowMin() const
        {
            return (m_count > 0) ? m_dequeVal[m_head] : 1.0f;
        }

        float* m_delay = nullptr;
        float* m_dequeVal = nullptr;
        uint32_t* m_dequeIdx = nullptr;
        float* m_box = nullptr;

        uint32_t m_capacity = 0;
        uint32_t m_channels = 2;
        uint32_t m_sampleRate = 48000;
        uint32_t m_lookahead = 0;
        uint32_t m_writePos = 0;

        uint32_t m_head = 0, m_tail = 0, m_count = 0;
        uint32_t m_sampleIndex = 0;

        uint32_t m_boxPos = 0;
        double m_boxSum = 0.0;

        float m_ceiling = 0.966f;
        float m_release = 1.0f;
        float m_releaseCoef = 0.999f;
    };
}
