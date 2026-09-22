//
// Limiter.h -- Look-ahead brickwall peak limiter.
//
// Why look-ahead: a boost of up to +14 dB will drive ordinary program material
// well past 0 dBFS. A feedback limiter only reacts after the peak has already
// clipped. Here the signal is delayed by the look-ahead window while the gain
// curve is computed from the *future* samples, so the attenuation is fully in
// place by the time the peak arrives -- no clipping, no pumping artefacts.
//
// The window minimum is tracked with a monotonic deque, giving an exact O(1)
// sliding minimum. That is what turns the ceiling into a true guarantee rather
// than an approximation.
//
// All buffers come from AERT_Allocate (non-paged), as required for APO code
// running on the audio engine real-time thread.
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

            memset(m_delay, 0, delayBytes);
            memset(m_dequeVal, 0, valBytes);
            memset(m_dequeIdx, 0, idxBytes);

            SetParams(-0.3f, 120.0f, 5.0f);
            Reset();
            return S_OK;
        }

        void Release()
        {
            if (m_delay) { AERT_Free(m_delay); m_delay = nullptr; }
            if (m_dequeVal) { AERT_Free(m_dequeVal); m_dequeVal = nullptr; }
            if (m_dequeIdx) { AERT_Free(m_dequeIdx); m_dequeIdx = nullptr; }
            m_capacity = 0;
        }

        // Safe to call between blocks; clamps everything into the prepared range.
        void SetParams(float ceilingDb, float releaseMs, float lookaheadMs)
        {
            if (ceilingDb > 0.0f) ceilingDb = 0.0f;
            if (ceilingDb < -12.0f) ceilingDb = -12.0f;
            m_ceiling = std::pow(10.0f, ceilingDb / 20.0f);

            if (releaseMs < 5.0f) releaseMs = 5.0f;
            if (releaseMs > 2000.0f) releaseMs = 2000.0f;

            if (lookaheadMs < 0.2f) lookaheadMs = 0.2f;

            uint32_t look = static_cast<uint32_t>(m_sampleRate * (lookaheadMs / 1000.0f));
            if (look < 2) look = 2;
            if (m_capacity && look > m_capacity - 1) look = m_capacity - 1;

            if (look != m_lookahead)
            {
                m_lookahead = look;
                ResetWindow();
            }

            // One-pole coefficients. Attack is tied to the look-ahead window so the
            // smoothed gain has essentially converged by the time the peak lands.
            const float attackSamples = static_cast<float>(m_lookahead) / 4.6f;
            m_attackCoef = std::exp(-1.0f / (attackSamples > 1.0f ? attackSamples : 1.0f));
            const float releaseSamples = m_sampleRate * (releaseMs / 1000.0f);
            m_releaseCoef = std::exp(-1.0f / (releaseSamples > 1.0f ? releaseSamples : 1.0f));
        }

        void Reset()
        {
            if (m_delay && m_capacity)
                memset(m_delay, 0, static_cast<size_t>(m_capacity) * m_channels * sizeof(float));
            m_writePos = 0;
            m_gain = 1.0f;
            ResetWindow();
        }

        float Ceiling() const { return m_ceiling; }
        uint32_t LookaheadSamples() const { return m_lookahead; }
        bool IsReady() const { return m_delay != nullptr && m_capacity > 0; }

        // Real-time path. In-place over an interleaved block.
        // Returns the deepest gain reduction applied in this block, in dB (>= 0).
        float Process(float* buffer, uint32_t frames, uint32_t channels)
        {
            if (!IsReady() || channels != m_channels) return 0.0f;

            float minGain = 1.0f;

            for (uint32_t f = 0; f < frames; ++f)
            {
                float* frame = buffer + static_cast<size_t>(f) * channels;

                // Peak across the channel set -- linking the channels keeps the
                // stereo image from shifting under gain reduction.
                float peak = 0.0f;
                for (uint32_t c = 0; c < channels; ++c)
                {
                    const float a = std::fabs(frame[c]);
                    if (a > peak) peak = a;
                }

                const float target = (peak > m_ceiling) ? (m_ceiling / peak) : 1.0f;

                // Push the newest target into the sliding-minimum window.
                PushTarget(target);

                // Pull the delayed sample out before overwriting the slot.
                const uint32_t readPos = (m_writePos + m_capacity - m_lookahead) % m_capacity;
                const float* delayed = m_delay + static_cast<size_t>(readPos) * channels;

                float out[CRESCENDO_MAX_CHANNELS];
                for (uint32_t c = 0; c < channels; ++c)
                    out[c] = delayed[c];

                float* slot = m_delay + static_cast<size_t>(m_writePos) * channels;
                for (uint32_t c = 0; c < channels; ++c)
                    slot[c] = frame[c];

                m_writePos = (m_writePos + 1) % m_capacity;

                // Smooth toward the window minimum: fast going down, gentle coming back.
                const float want = WindowMin();
                const float coef = (want < m_gain) ? m_attackCoef : m_releaseCoef;
                m_gain = want + (m_gain - want) * coef;

                if (m_gain < minGain) minGain = m_gain;

                for (uint32_t c = 0; c < channels; ++c)
                    frame[c] = out[c] * m_gain;
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
            m_gain = 1.0f;
        }

        // Monotonic deque: values increase from head to tail, so the head is always
        // the minimum of the last m_lookahead targets.
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

            // Retire entries that have fallen out of the window.
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

        uint32_t m_capacity = 0;
        uint32_t m_channels = 2;
        uint32_t m_sampleRate = 48000;
        uint32_t m_lookahead = 0;
        uint32_t m_writePos = 0;

        uint32_t m_head = 0, m_tail = 0, m_count = 0;
        uint32_t m_sampleIndex = 0;

        float m_ceiling = 0.966f;
        float m_gain = 1.0f;
        float m_attackCoef = 0.0f;
        float m_releaseCoef = 0.999f;
    };
}
