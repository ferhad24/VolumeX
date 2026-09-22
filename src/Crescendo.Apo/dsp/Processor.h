//
// Processor.h -- The Crescendo signal chain.
//
// Order of operations, and the reasoning behind it:
//
//   peak meter (in)
//     -> boost * preamp            smoothed per sample, otherwise the slider clicks
//     -> subsonic high-pass        bass boost otherwise pushes DC into the driver
//     -> bass / treble shelves
//     -> 10-band graphic EQ
//     -> stereo stage              mono fold, L/R swap, balance
//     -> output trim
//     -> look-ahead limiter        the last stage that may raise level, so it owns the ceiling
//     -> safety clamp / soft clip  catches anything the limiter could overshoot
//   peak meter (out)
//
// The limiter sits last on purpose: any gain applied after it would break the
// ceiling guarantee.
//
#pragma once
#include <cmath>
#include <cstring>
#include "../CrescendoAbi.h"
#include "Biquad.h"
#include "Limiter.h"

namespace cres
{
    // Local, non-volatile copy of the shared config block.
    struct ConfigSnapshot
    {
        uint32_t seq = 0xFFFFFFFFu;
        uint32_t flags = 0;
        float boost = 1.0f;
        float preampDb = 0.0f;
        float outputTrimDb = 0.0f;
        float bassDb = 0.0f;
        float trebleDb = 0.0f;
        float balance = 0.0f;
        float limiterCeilingDb = -0.3f;
        float limiterReleaseMs = 120.0f;
        float limiterLookaheadMs = 5.0f;
        uint32_t bandCount = 0;
        CrescendoBand bands[CRESCENDO_MAX_BANDS] = {};
    };

    // Reads the shared block without ever blocking the audio thread. Returns false
    // if the writer was mid-update; the caller simply keeps the previous snapshot.
    inline bool TryReadConfig(const CrescendoConfig* src, ConfigSnapshot& dst)
    {
        if (!src || src->magic != CRESCENDO_CONFIG_MAGIC) return false;

        for (int attempt = 0; attempt < 4; ++attempt)
        {
            const uint32_t s1 = src->seq;
            if (s1 & 1u) continue;              // writer is inside the critical section

            ConfigSnapshot tmp;
            tmp.flags = src->flags;
            tmp.boost = src->boost;
            tmp.preampDb = src->preampDb;
            tmp.outputTrimDb = src->outputTrimDb;
            tmp.bassDb = src->bassDb;
            tmp.trebleDb = src->trebleDb;
            tmp.balance = src->balance;
            tmp.limiterCeilingDb = src->limiterCeilingDb;
            tmp.limiterReleaseMs = src->limiterReleaseMs;
            tmp.limiterLookaheadMs = src->limiterLookaheadMs;
            tmp.bandCount = src->bandCount;
            if (tmp.bandCount > CRESCENDO_MAX_BANDS) tmp.bandCount = CRESCENDO_MAX_BANDS;
            for (uint32_t i = 0; i < tmp.bandCount; ++i)
                tmp.bands[i] = src->bands[i];

            const uint32_t s2 = src->seq;
            if (s1 != s2) continue;             // torn read, try again

            tmp.seq = s1;
            dst = tmp;
            return true;
        }
        return false;
    }

    inline float DbToLinear(float db)
    {
        return std::pow(10.0f, db / 20.0f);
    }

    // Cubic soft clipper: transparent below the knee, gently saturating above it.
    // Used instead of a hard clamp when the user prefers colour over precision.
    inline float SoftClip(float x, float ceiling)
    {
        if (ceiling <= 0.0f) return 0.0f;
        const float n = x / ceiling;
        if (n >= 1.5f) return ceiling;
        if (n <= -1.5f) return -ceiling;
        // f(n) = n - (4/27) n^3 saturates smoothly to +/-1 at n = +/-1.5
        return ceiling * (n - 0.148148148f * n * n * n);
    }

    class Processor
    {
    public:
        HRESULT Prepare(uint32_t sampleRate, uint32_t channels)
        {
            m_sampleRate = sampleRate ? sampleRate : 48000;
            m_channels = channels ? channels : 2;
            if (m_channels > CRESCENDO_MAX_CHANNELS) m_channels = CRESCENDO_MAX_CHANNELS;

            // Enough room for the widest look-ahead the UI is allowed to request.
            HRESULT hr = m_limiter.Prepare(m_sampleRate, m_channels, kMaxLookaheadMs);
            if (FAILED(hr)) return hr;

            ResetState();
            m_snapshot.seq = 0xFFFFFFFFu;   // force a rebuild on the first block
            return S_OK;
        }

        void Release()
        {
            m_limiter.Release();
        }

        uint32_t LatencySamples() const
        {
            return m_limiter.IsReady() ? m_limiter.LookaheadSamples() : 0;
        }

        void ResetState()
        {
            for (uint32_t c = 0; c < CRESCENDO_MAX_CHANNELS; ++c)
            {
                m_subsonicState[c].Reset();
                m_bassState[c].Reset();
                m_trebleState[c].Reset();
                for (uint32_t b = 0; b < CRESCENDO_MAX_BANDS; ++b)
                    m_bandState[b][c].Reset();
            }
            m_limiter.Reset();
            m_smoothGain = -1.0f;   // snap to target on the next block
            m_smoothBalL = -1.0f;
            m_smoothBalR = -1.0f;
        }

        // Real-time. Processes an interleaved float block in place and reports
        // levels through the meter block (which may be null). forceBypass is
        // raised by the APO when the UI watchdog has expired.
        void Process(float* buffer, uint32_t frames, const CrescendoConfig* config, CrescendoMeter* meter,
                     bool forceBypass = false)
        {
            if (!buffer || frames == 0) return;

            const uint32_t ch = m_channels;

            ConfigSnapshot prev = m_snapshot;
            if (TryReadConfig(config, m_snapshot) && m_snapshot.seq != prev.seq)
                Rebuild();

            const bool enabled = !forceBypass && (m_snapshot.flags & CRES_FLAG_ENABLED) != 0;

            float peakIn[CRESCENDO_MAX_CHANNELS] = {};
            float peakOut[CRESCENDO_MAX_CHANNELS] = {};

            MeasurePeaks(buffer, frames, ch, peakIn);

            if (!enabled)
            {
                // Bypass still has to flush filter memory, otherwise re-enabling
                // replays a stale tail.
                if (m_wasEnabled)
                {
                    ResetState();
                    m_wasEnabled = false;
                }
                MeasurePeaks(buffer, frames, ch, peakOut);
                WriteMeter(meter, frames, peakIn, peakOut, 0.0f);
                return;
            }
            m_wasEnabled = true;

            const float targetGain = ClampGain(m_snapshot.boost) * DbToLinear(m_snapshot.preampDb);
            if (m_smoothGain < 0.0f) m_smoothGain = targetGain;

            const float trim = DbToLinear(m_snapshot.outputTrimDb);

            // Equal-power-ish balance: attenuate the far side rather than boosting
            // the near one, so balance can never add headroom pressure.
            float balL = 1.0f, balR = 1.0f;
            const float bal = Clamp(m_snapshot.balance, -1.0f, 1.0f);
            if (bal > 0.0f) balL = 1.0f - bal;
            else if (bal < 0.0f) balR = 1.0f + bal;
            if (m_smoothBalL < 0.0f) { m_smoothBalL = balL; m_smoothBalR = balR; }

            const bool useEq = (m_snapshot.flags & CRES_FLAG_EQ) != 0 && m_snapshot.bandCount > 0;
            const bool useTone = (m_snapshot.flags & CRES_FLAG_TONE) != 0;
            const bool useSubsonic = (m_snapshot.flags & CRES_FLAG_SUBSONIC) != 0;
            const bool useMono = (m_snapshot.flags & CRES_FLAG_MONO) != 0;
            const bool useSwap = (m_snapshot.flags & CRES_FLAG_SWAP_LR) != 0 && ch >= 2;
            const bool useLimiter = (m_snapshot.flags & CRES_FLAG_LIMITER) != 0;
            const bool useSoftClip = (m_snapshot.flags & CRES_FLAG_SOFTCLIP) != 0;

            for (uint32_t f = 0; f < frames; ++f)
            {
                float* frame = buffer + static_cast<size_t>(f) * ch;

                m_smoothGain += (targetGain - m_smoothGain) * kGainSmooth;
                m_smoothBalL += (balL - m_smoothBalL) * kGainSmooth;
                m_smoothBalR += (balR - m_smoothBalR) * kGainSmooth;

                for (uint32_t c = 0; c < ch; ++c)
                {
                    float x = frame[c] * m_smoothGain;

                    if (useSubsonic)
                        x = ProcessSample(m_subsonicCoeffs, m_subsonicState[c], x);

                    if (useTone)
                    {
                        if (!m_bassCoeffs.IsIdentity())
                            x = ProcessSample(m_bassCoeffs, m_bassState[c], x);
                        if (!m_trebleCoeffs.IsIdentity())
                            x = ProcessSample(m_trebleCoeffs, m_trebleState[c], x);
                    }

                    if (useEq)
                    {
                        for (uint32_t b = 0; b < m_activeBands; ++b)
                            x = ProcessSample(m_bandCoeffs[b], m_bandState[b][c], x);
                    }

                    frame[c] = x;
                }

                if (ch >= 2)
                {
                    if (useMono)
                    {
                        // -3 dB fold keeps the perceived level steady and avoids
                        // handing the limiter a 6 dB jump on correlated material.
                        const float m = (frame[0] + frame[1]) * 0.7071068f;
                        frame[0] = m;
                        frame[1] = m;
                    }
                    if (useSwap)
                    {
                        const float t = frame[0];
                        frame[0] = frame[1];
                        frame[1] = t;
                    }
                    frame[0] *= m_smoothBalL;
                    frame[1] *= m_smoothBalR;
                }

                for (uint32_t c = 0; c < ch; ++c)
                    frame[c] *= trim;
            }

            float grDb = 0.0f;
            if (useLimiter)
                grDb = m_limiter.Process(buffer, frames, ch);

            // Final safety net. Even with the limiter engaged this costs almost
            // nothing and guarantees nothing leaves the APO above the ceiling.
            const float ceiling = m_limiter.Ceiling();
            const uint32_t total = frames * ch;
            if (useSoftClip)
            {
                for (uint32_t i = 0; i < total; ++i)
                    buffer[i] = SoftClip(buffer[i], ceiling);
            }
            else
            {
                for (uint32_t i = 0; i < total; ++i)
                    buffer[i] = Clamp(buffer[i], -ceiling, ceiling);
            }

            MeasurePeaks(buffer, frames, ch, peakOut);
            WriteMeter(meter, frames, peakIn, peakOut, grDb);
        }

    private:
        static constexpr float kMaxLookaheadMs = 20.0f;
        static constexpr float kGainSmooth = 0.0008f;   // ~25 ms at 48 kHz

        static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        static float ClampGain(float boost)
        {
            if (!(boost > 0.0f)) return 1.0f;    // also catches NaN
            return Clamp(boost, 0.0f, 8.0f);
        }

        static void MeasurePeaks(const float* buffer, uint32_t frames, uint32_t ch, float* peaks)
        {
            for (uint32_t f = 0; f < frames; ++f)
            {
                const float* frame = buffer + static_cast<size_t>(f) * ch;
                for (uint32_t c = 0; c < ch; ++c)
                {
                    const float a = std::fabs(frame[c]);
                    if (a > peaks[c]) peaks[c] = a;
                }
            }
        }

        void Rebuild()
        {
            const float sr = static_cast<float>(m_sampleRate);

            m_subsonicCoeffs = DesignBiquad(FilterKind::HighPass, sr, 20.0f, 0.707f, 0.0f);

            m_bassCoeffs = (m_snapshot.bassDb == 0.0f)
                ? BiquadCoeffs{}
                : DesignBiquad(FilterKind::LowShelf, sr, 100.0f, 0.707f, Clamp(m_snapshot.bassDb, -15.0f, 15.0f));

            m_trebleCoeffs = (m_snapshot.trebleDb == 0.0f)
                ? BiquadCoeffs{}
                : DesignBiquad(FilterKind::HighShelf, sr, 8000.0f, 0.707f, Clamp(m_snapshot.trebleDb, -15.0f, 15.0f));

            // Bands with no gain are dropped entirely rather than run as identity
            // filters -- a flat EQ should cost nothing.
            m_activeBands = 0;
            for (uint32_t i = 0; i < m_snapshot.bandCount && m_activeBands < CRESCENDO_MAX_BANDS; ++i)
            {
                const CrescendoBand& b = m_snapshot.bands[i];
                if (b.gainDb == 0.0f || !(b.freqHz > 0.0f)) continue;
                m_bandCoeffs[m_activeBands] = DesignBiquad(
                    FilterKind::Peaking, sr, b.freqHz,
                    (b.q > 0.0f ? b.q : 1.41f),
                    Clamp(b.gainDb, -15.0f, 15.0f));
                ++m_activeBands;
            }

            m_limiter.SetParams(m_snapshot.limiterCeilingDb,
                                m_snapshot.limiterReleaseMs,
                                Clamp(m_snapshot.limiterLookaheadMs, 0.2f, kMaxLookaheadMs));
        }

        void WriteMeter(CrescendoMeter* meter, uint32_t frames,
                        const float* peakIn, const float* peakOut, float grDb)
        {
            if (!meter || meter->magic != CRESCENDO_METER_MAGIC) return;

            meter->seq = meter->seq + 1;            // odd: update in progress
            meter->channels = m_channels;
            meter->sampleRate = m_sampleRate;
            meter->frameCount = frames;
            for (uint32_t c = 0; c < CRESCENDO_MAX_CHANNELS; ++c)
            {
                meter->peakIn[c] = (c < m_channels) ? peakIn[c] : 0.0f;
                meter->peakOut[c] = (c < m_channels) ? peakOut[c] : 0.0f;
            }
            meter->gainReductionDb = grDb;
            meter->heartbeat = meter->heartbeat + 1;
            meter->seq = meter->seq + 1;            // even: readable again
        }

        Limiter m_limiter;

        ConfigSnapshot m_snapshot;
        uint32_t m_sampleRate = 48000;
        uint32_t m_channels = 2;
        uint32_t m_activeBands = 0;
        bool m_wasEnabled = false;

        float m_smoothGain = -1.0f;
        float m_smoothBalL = -1.0f;
        float m_smoothBalR = -1.0f;

        BiquadCoeffs m_subsonicCoeffs;
        BiquadCoeffs m_bassCoeffs;
        BiquadCoeffs m_trebleCoeffs;
        BiquadCoeffs m_bandCoeffs[CRESCENDO_MAX_BANDS];

        BiquadState m_subsonicState[CRESCENDO_MAX_CHANNELS];
        BiquadState m_bassState[CRESCENDO_MAX_CHANNELS];
        BiquadState m_trebleState[CRESCENDO_MAX_CHANNELS];
        BiquadState m_bandState[CRESCENDO_MAX_BANDS][CRESCENDO_MAX_CHANNELS];
    };
}
