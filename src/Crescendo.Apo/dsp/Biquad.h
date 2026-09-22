//
// Biquad.h -- RBJ cookbook biquad, transposed direct form II.
//
// Coefficient maths runs on the configuration thread; ProcessSample() is the
// only thing the real-time thread touches and it neither allocates nor branches
// on anything but its own state.
//
#pragma once
#include <cmath>

namespace cres
{
    constexpr float kPi = 3.14159265358979323846f;

    enum class FilterKind
    {
        Peaking,
        LowShelf,
        HighShelf,
        HighPass
    };

    struct BiquadCoeffs
    {
        float b0 = 1.0f, b1 = 0.0f, b2 = 0.0f, a1 = 0.0f, a2 = 0.0f;

        bool IsIdentity() const
        {
            return b0 == 1.0f && b1 == 0.0f && b2 == 0.0f && a1 == 0.0f && a2 == 0.0f;
        }
    };

    // Per-channel filter memory. Kept separate from the coefficients so a single
    // coefficient set can drive every channel without duplication.
    struct BiquadState
    {
        float z1 = 0.0f, z2 = 0.0f;

        void Reset() { z1 = 0.0f; z2 = 0.0f; }
    };

    inline BiquadCoeffs DesignBiquad(FilterKind kind, float sampleRate, float freqHz, float q, float gainDb)
    {
        BiquadCoeffs c;

        // Guard against nonsense coming from a corrupted config block.
        if (sampleRate < 8000.0f) sampleRate = 48000.0f;
        if (q < 0.1f) q = 0.1f;
        if (q > 18.0f) q = 18.0f;

        const float nyquist = sampleRate * 0.5f;
        if (freqHz < 10.0f) freqHz = 10.0f;
        if (freqHz > nyquist * 0.98f) freqHz = nyquist * 0.98f;

        const float a = std::pow(10.0f, gainDb / 40.0f);   // shelf/peak amplitude
        const float w0 = 2.0f * kPi * freqHz / sampleRate;
        const float cosw = std::cos(w0);
        const float sinw = std::sin(w0);

        float b0, b1, b2, a0, a1, a2;

        switch (kind)
        {
        case FilterKind::Peaking:
        {
            const float alpha = sinw / (2.0f * q);
            b0 = 1.0f + alpha * a;
            b1 = -2.0f * cosw;
            b2 = 1.0f - alpha * a;
            a0 = 1.0f + alpha / a;
            a1 = -2.0f * cosw;
            a2 = 1.0f - alpha / a;
            break;
        }
        case FilterKind::LowShelf:
        {
            const float alpha = sinw / 2.0f * std::sqrt((a + 1.0f / a) * (1.0f / q - 1.0f) + 2.0f);
            const float twoSqrtAAlpha = 2.0f * std::sqrt(a) * alpha;
            b0 = a * ((a + 1.0f) - (a - 1.0f) * cosw + twoSqrtAAlpha);
            b1 = 2.0f * a * ((a - 1.0f) - (a + 1.0f) * cosw);
            b2 = a * ((a + 1.0f) - (a - 1.0f) * cosw - twoSqrtAAlpha);
            a0 = (a + 1.0f) + (a - 1.0f) * cosw + twoSqrtAAlpha;
            a1 = -2.0f * ((a - 1.0f) + (a + 1.0f) * cosw);
            a2 = (a + 1.0f) + (a - 1.0f) * cosw - twoSqrtAAlpha;
            break;
        }
        case FilterKind::HighShelf:
        {
            const float alpha = sinw / 2.0f * std::sqrt((a + 1.0f / a) * (1.0f / q - 1.0f) + 2.0f);
            const float twoSqrtAAlpha = 2.0f * std::sqrt(a) * alpha;
            b0 = a * ((a + 1.0f) + (a - 1.0f) * cosw + twoSqrtAAlpha);
            b1 = -2.0f * a * ((a - 1.0f) + (a + 1.0f) * cosw);
            b2 = a * ((a + 1.0f) + (a - 1.0f) * cosw - twoSqrtAAlpha);
            a0 = (a + 1.0f) - (a - 1.0f) * cosw + twoSqrtAAlpha;
            a1 = 2.0f * ((a - 1.0f) - (a + 1.0f) * cosw);
            a2 = (a + 1.0f) - (a - 1.0f) * cosw - twoSqrtAAlpha;
            break;
        }
        case FilterKind::HighPass:
        default:
        {
            const float alpha = sinw / (2.0f * q);
            b0 = (1.0f + cosw) / 2.0f;
            b1 = -(1.0f + cosw);
            b2 = (1.0f + cosw) / 2.0f;
            a0 = 1.0f + alpha;
            a1 = -2.0f * cosw;
            a2 = 1.0f - alpha;
            break;
        }
        }

        if (a0 == 0.0f) return c;   // identity

        const float inv = 1.0f / a0;
        c.b0 = b0 * inv;
        c.b1 = b1 * inv;
        c.b2 = b2 * inv;
        c.a1 = a1 * inv;
        c.a2 = a2 * inv;
        return c;
    }

    // Transposed direct form II: numerically well behaved in single precision
    // and only two state words per channel.
    inline float ProcessSample(const BiquadCoeffs& c, BiquadState& s, float x)
    {
        const float y = c.b0 * x + s.z1;
        s.z1 = c.b1 * x - c.a1 * y + s.z2;
        s.z2 = c.b2 * x - c.a2 * y;
        return y;
    }
}
