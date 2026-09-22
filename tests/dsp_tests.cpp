//
// dsp_tests.cpp -- Checks the Crescendo signal chain against the guarantees the
// UI makes about it.
//
// The DSP headers are exercised directly rather than through the COM object:
// instantiating the APO would need the audio engine to hand it an IAudioMediaType
// and a Global\ shared block, neither of which is available outside audiodg.
// The code under test is identical either way.
//
// Build and run: build\run-tests.cmd
//

#include <cstdio>
#include <cmath>
#include <vector>
#include <windows.h>
// Note: windows.h defines `far`/`near` and rpcndr.h defines `small` as `char`,
// so those three words cannot be used as identifiers in this file.
#include <audioenginebaseapo.h>
#include <baseaudioprocessingobject.h>

#include "../src/Crescendo.Apo/dsp/Processor.h"
#include "../src/Crescendo.Apo/dsp/Watchdog.h"

namespace
{
    int g_failures = 0;
    int g_checks = 0;

    void Check(bool condition, const char* what, const char* detail = "")
    {
        ++g_checks;
        if (condition)
        {
            printf("  [ ok ] %s %s\n", what, detail);
        }
        else
        {
            printf("  [FAIL] %s %s\n", what, detail);
            ++g_failures;
        }
    }

    void CheckNear(double actual, double expected, double tolerance, const char* what)
    {
        char detail[160];
        snprintf(detail, sizeof(detail), "(got %.4f, expected %.4f +/- %.4f)", actual, expected, tolerance);
        Check(std::fabs(actual - expected) <= tolerance, what, detail);
    }

    constexpr uint32_t kRate = 48000;
    constexpr uint32_t kChannels = 2;

    CrescendoConfig MakeConfig()
    {
        CrescendoConfig config = {};
        config.magic = CRESCENDO_CONFIG_MAGIC;
        config.structSize = sizeof(CrescendoConfig);
        config.seq = 2;                 // even: a complete, readable snapshot
        config.flags = CRES_FLAG_ENABLED;
        config.boost = 1.0f;
        config.limiterCeilingDb = -0.3f;
        config.limiterReleaseMs = 120.0f;
        config.limiterLookaheadMs = 5.0f;
        config.bandCount = 0;
        return config;
    }

    // Bumping the sequence is what tells the processor to rebuild its filters.
    void Touch(CrescendoConfig& config)
    {
        config.seq += 2;
    }

    std::vector<float> MakeSine(double frequencyHz, double amplitude, uint32_t frames)
    {
        std::vector<float> buffer(static_cast<size_t>(frames) * kChannels);
        for (uint32_t f = 0; f < frames; ++f)
        {
            const double t = static_cast<double>(f) / kRate;
            const float sample = static_cast<float>(amplitude * std::sin(2.0 * 3.14159265358979 * frequencyHz * t));
            buffer[f * kChannels + 0] = sample;
            buffer[f * kChannels + 1] = sample;
        }
        return buffer;
    }

    float PeakOf(const std::vector<float>& buffer)
    {
        float peak = 0.0f;
        for (float sample : buffer)
        {
            const float a = std::fabs(sample);
            if (a > peak) peak = a;
        }
        return peak;
    }

    // RMS over the second half only, so the gain ramp and the limiter's initial
    // settling do not skew the measurement.
    double TailRms(const std::vector<float>& buffer)
    {
        const size_t start = buffer.size() / 2;
        double sum = 0.0;
        for (size_t i = start; i < buffer.size(); ++i)
            sum += static_cast<double>(buffer[i]) * buffer[i];
        return std::sqrt(sum / (buffer.size() - start));
    }

    // Runs a signal through a freshly prepared processor in one-block chunks,
    // the way the audio engine would.
    std::vector<float> Run(CrescendoConfig& config, std::vector<float> signal, uint32_t blockFrames = 480)
    {
        cres::Processor processor;
        if (FAILED(processor.Prepare(kRate, kChannels)))
        {
            printf("  [FAIL] processor.Prepare\n");
            ++g_failures;
            return signal;
        }

        const uint32_t totalFrames = static_cast<uint32_t>(signal.size() / kChannels);
        for (uint32_t offset = 0; offset < totalFrames; offset += blockFrames)
        {
            const uint32_t frames = (totalFrames - offset) < blockFrames ? (totalFrames - offset) : blockFrames;
            processor.Process(signal.data() + static_cast<size_t>(offset) * kChannels, frames, &config, nullptr);
        }

        processor.Release();
        return signal;
    }
}

// -----------------------------------------------------------------------------

static void TestBypass()
{
    printf("bypass\n");

    CrescendoConfig config = MakeConfig();
    config.flags = 0;               // engine off
    config.boost = 4.0f;

    std::vector<float> input = MakeSine(1000.0, 0.5, 4800);
    std::vector<float> output = Run(config, input);

    bool identical = true;
    for (size_t i = 0; i < input.size(); ++i)
    {
        if (input[i] != output[i]) { identical = false; break; }
    }
    Check(identical, "disabled engine passes the signal through untouched");
}

static void TestWatchdogArithmetic()
{
    printf("watchdog arithmetic\n");

    const uint32_t t = CRESCENDO_UI_TIMEOUT_MS;

    // The bug that made the engine flap: a stamp 1 ms ahead of the reader.
    Check(!cres::UiWatchdogExpired(1000, 1001, t), "a stamp 1 ms in the future counts as fresh");
    Check(!cres::UiWatchdogExpired(1000, 1000, t), "a stamp from this millisecond is fresh");
    Check(!cres::UiWatchdogExpired(1000 + t, 1000, t), "exactly at the timeout is still alive");
    Check(cres::UiWatchdogExpired(1001 + t, 1000, t), "one millisecond past the timeout expires");
    Check(cres::UiWatchdogExpired(50000, 1000, t), "a UI silent for 49 s has expired");

    // GetTickCount wraps every 49.7 days.
    Check(!cres::UiWatchdogExpired(5, 0xFFFFFFF0u, t), "fresh across the tick wrap");
    Check(cres::UiWatchdogExpired(t + 100, 0xFFFFFFF0u, t), "expired across the tick wrap");

    Check(!cres::UiWatchdogExpired(123456, 0, t), "stamp 0 (no watchdog) never expires");

    // Every stamp the UI can produce within the last second must read as alive,
    // for every possible reader time -- sampled over the full 32-bit range.
    bool allFresh = true;
    for (uint64_t now = 0; now <= 0xFFFFFFFFull; now += 0x00FFFFFBull)
    {
        for (int32_t offset = -1000; offset <= 5; ++offset)
        {
            const uint32_t stamp = static_cast<uint32_t>(now) - static_cast<uint32_t>(offset);
            if (stamp != 0 && cres::UiWatchdogExpired(static_cast<uint32_t>(now), stamp, t))
            {
                allFresh = false;
                break;
            }
        }
        if (!allFresh) break;
    }
    Check(allFresh, "no recent stamp is ever mistaken for a dead UI, anywhere on the clock");
}

static void TestForcedBypass()
{
    printf("watchdog bypass\n");

    // A fully enabled 500% config: forceBypass (raised when the UI watchdog
    // expires) must still pass the signal through untouched.
    CrescendoConfig config = MakeConfig();
    config.flags = CRES_FLAG_ENABLED | CRES_FLAG_LIMITER | CRES_FLAG_EQ;
    config.boost = 5.0f;

    std::vector<float> input = MakeSine(1000.0, 0.3, 4800);
    std::vector<float> output = input;

    cres::Processor processor;
    processor.Prepare(kRate, kChannels);
    processor.Process(output.data(), 4800, &config, nullptr, true);

    bool identical = true;
    for (size_t i = 0; i < input.size(); ++i)
    {
        if (input[i] != output[i]) { identical = false; break; }
    }
    Check(identical, "an expired UI watchdog bypasses even a 500% config");

    // ...and recovers the moment the UI is back.
    std::vector<float> resumed = MakeSine(1000.0, 0.1, 24000);
    const double reference = TailRms(resumed);
    for (uint32_t offset = 0; offset < 24000; offset += 480)
        processor.Process(resumed.data() + static_cast<size_t>(offset) * kChannels, 480, &config, nullptr, false);
    processor.Release();

    char detail[96];
    snprintf(detail, sizeof(detail), "(gain %.2f)", TailRms(resumed) / reference);
    Check(TailRms(resumed) / reference > 3.0, "boost resumes as soon as the watchdog is fed again", detail);
}

static void TestLinearGain()
{
    printf("gain\n");

    // Limiter off and a quiet source, so the only thing acting on the signal is
    // the boost itself.
    CrescendoConfig config = MakeConfig();
    config.flags = CRES_FLAG_ENABLED;
    config.boost = 2.0f;

    std::vector<float> input = MakeSine(1000.0, 0.1, 24000);
    const double inputRms = TailRms(input);

    std::vector<float> output = Run(config, input);
    const double outputRms = TailRms(output);

    CheckNear(outputRms / inputRms, 2.0, 0.02, "200% doubles the amplitude");

    config.boost = 5.0f;
    Touch(config);
    std::vector<float> loud = Run(config, MakeSine(1000.0, 0.1, 24000));
    CheckNear(TailRms(loud) / inputRms, 5.0, 0.05, "500% is a five-fold linear gain");
}

static void TestLimiterCeiling()
{
    printf("limiter\n");

    CrescendoConfig config = MakeConfig();
    config.flags = CRES_FLAG_ENABLED | CRES_FLAG_LIMITER;
    config.boost = 5.0f;
    config.limiterCeilingDb = -0.3f;

    const float ceiling = std::pow(10.0f, -0.3f / 20.0f);

    // A full-scale sine at 5x is 14 dB of overshoot: the worst case the UI allows.
    std::vector<float> output = Run(config, MakeSine(440.0, 0.9, 48000));
    const float peak = PeakOf(output);

    char detail[128];
    snprintf(detail, sizeof(detail), "(peak %.4f, ceiling %.4f)", peak, ceiling);
    Check(peak <= ceiling + 1e-4f, "nothing exceeds the ceiling at 500% boost", detail);
    Check(peak > ceiling * 0.9f, "the limiter still delivers level, it does not just collapse", detail);

    // Transients are the case a feedback limiter gets wrong.
    std::vector<float> clicks(48000 * kChannels, 0.0f);
    for (uint32_t f = 1000; f < 48000; f += 4800)
    {
        clicks[f * kChannels + 0] = 0.95f;
        clicks[f * kChannels + 1] = 0.95f;
    }
    Touch(config);
    std::vector<float> limited = Run(config, clicks);
    snprintf(detail, sizeof(detail), "(peak %.4f)", PeakOf(limited));
    Check(PeakOf(limited) <= ceiling + 1e-4f, "isolated transients are caught too", detail);

    // A lower ceiling has to be honoured as well.
    config.limiterCeilingDb = -6.0f;
    Touch(config);
    const float lowCeiling = std::pow(10.0f, -6.0f / 20.0f);
    std::vector<float> quiet = Run(config, MakeSine(440.0, 0.9, 48000));
    snprintf(detail, sizeof(detail), "(peak %.4f, ceiling %.4f)", PeakOf(quiet), lowCeiling);
    Check(PeakOf(quiet) <= lowCeiling + 1e-4f, "a -6 dBFS ceiling is honoured", detail);
}

// The chain ends in a hard clamp, so a test of the whole chain can never see
// the limiter overshoot -- the clamp hides it, and clamping is exactly what
// crackles. This drives the Limiter alone and demands it hold the ceiling by
// itself, on the material that stresses it most: loud bass, heavy boost.
static void TestLimiterNeedsNoClamp()
{
    printf("limiter without the safety clamp\n");

    const float ceiling = std::pow(10.0f, -0.3f / 20.0f);

    struct Case { double hz; double amplitude; const char* name; };
    const Case cases[] = {
        { 60.0,  0.9 * 5.0, "60 Hz at 500%" },
        { 440.0, 0.9 * 5.0, "440 Hz at 500%" },
        { 60.0,  0.9 * 2.0, "60 Hz at 200%" },
    };

    for (const Case& c : cases)
    {
        cres::Limiter limiter;
        limiter.Prepare(kRate, kChannels, 20.0f);
        limiter.SetParams(-0.3f, 120.0f, 5.0f);

        std::vector<float> signal = MakeSine(c.hz, c.amplitude, 48000);
        for (uint32_t offset = 0; offset < 48000; offset += 480)
            limiter.Process(signal.data() + static_cast<size_t>(offset) * kChannels, 480, kChannels);
        limiter.Release();

        char what[96], detail[96];
        snprintf(what, sizeof(what), "limiter alone holds the ceiling: %s", c.name);
        snprintf(detail, sizeof(detail), "(peak %.4f, ceiling %.4f)", PeakOf(signal), ceiling);
        Check(PeakOf(signal) <= ceiling + 1e-5f, what, detail);
    }
}

static void TestSafetyClamp()
{
    printf("safety\n");

    // With the limiter disabled the hard clamp is the only thing left.
    CrescendoConfig config = MakeConfig();
    config.flags = CRES_FLAG_ENABLED;       // no CRES_FLAG_LIMITER
    config.boost = 5.0f;

    std::vector<float> output = Run(config, MakeSine(440.0, 0.9, 9600));
    const float ceiling = std::pow(10.0f, -0.3f / 20.0f);

    char detail[128];
    snprintf(detail, sizeof(detail), "(peak %.4f)", PeakOf(output));
    Check(PeakOf(output) <= ceiling + 1e-4f, "output stays bounded even with the limiter off", detail);

    // Soft clip must stay bounded as well, and stay continuous.
    config.flags |= CRES_FLAG_SOFTCLIP;
    Touch(config);
    std::vector<float> soft = Run(config, MakeSine(440.0, 0.9, 9600));
    snprintf(detail, sizeof(detail), "(peak %.4f)", PeakOf(soft));
    Check(PeakOf(soft) <= ceiling + 1e-4f, "soft clip is bounded by the same ceiling", detail);
}

static void TestLookaheadLatency()
{
    printf("latency\n");

    CrescendoConfig config = MakeConfig();
    config.flags = CRES_FLAG_ENABLED | CRES_FLAG_LIMITER;
    config.boost = 1.0f;
    config.limiterLookaheadMs = 5.0f;

    // An impulse at a known position should come out delayed by exactly the
    // look-ahead window -- that is what GetLatency reports to Windows.
    const uint32_t frames = 4800;
    std::vector<float> impulse(static_cast<size_t>(frames) * kChannels, 0.0f);
    impulse[100 * kChannels + 0] = 0.5f;
    impulse[100 * kChannels + 1] = 0.5f;

    cres::Processor processor;
    processor.Prepare(kRate, kChannels);
    const uint32_t reportedLatency = processor.LatencySamples();

    processor.Process(impulse.data(), frames, &config, nullptr);
    processor.Release();

    int foundAt = -1;
    for (uint32_t f = 0; f < frames; ++f)
    {
        if (std::fabs(impulse[f * kChannels]) > 0.01f) { foundAt = static_cast<int>(f); break; }
    }

    // A window of L samples needs L - 1 samples of delay (see Limiter.h).
    const uint32_t expected = static_cast<uint32_t>(kRate * 5.0 / 1000.0) - 1;
    char detail[160];
    snprintf(detail, sizeof(detail), "(impulse at %d, expected %u, reported %u)",
             foundAt, 100 + expected, reportedLatency);
    Check(foundAt >= 0 && std::abs(foundAt - static_cast<int>(100 + expected)) <= 2,
          "the impulse is delayed by exactly the look-ahead", detail);
    Check(reportedLatency == expected, "reported latency matches the actual delay", detail);
}

static void TestEqualizer()
{
    printf("equalizer\n");

    CrescendoConfig config = MakeConfig();
    config.flags = CRES_FLAG_ENABLED | CRES_FLAG_EQ;
    config.boost = 1.0f;
    config.bandCount = 1;
    config.bands[0] = { 1000.0f, 12.0f, 1.41f };

    const double reference = TailRms(MakeSine(1000.0, 0.05, 24000));

    // A +12 dB band should lift its own centre frequency by about 12 dB.
    std::vector<float> boosted = Run(config, MakeSine(1000.0, 0.05, 24000));
    const double boostedDb = 20.0 * std::log10(TailRms(boosted) / reference);
    CheckNear(boostedDb, 12.0, 1.0, "a +12 dB band lifts its centre frequency");

    // ...and leave a decade away essentially alone.
    const double farReference = TailRms(MakeSine(60.0, 0.05, 24000));
    Touch(config);
    std::vector<float> distant = Run(config, MakeSine(60.0, 0.05, 24000));
    const double farDb = 20.0 * std::log10(TailRms(distant) / farReference);
    CheckNear(farDb, 0.0, 1.0, "a band at 1 kHz barely touches 60 Hz");

    // A cut has to work as well as a boost.
    config.bands[0] = { 1000.0f, -12.0f, 1.41f };
    Touch(config);
    std::vector<float> cut = Run(config, MakeSine(1000.0, 0.05, 24000));
    const double cutDb = 20.0 * std::log10(TailRms(cut) / reference);
    CheckNear(cutDb, -12.0, 1.0, "a -12 dB band cuts its centre frequency");
}

static void TestToneAndImaging()
{
    printf("tone and imaging\n");

    // Mono fold: hard-panned content must end up equal in both channels.
    CrescendoConfig config = MakeConfig();
    config.flags = CRES_FLAG_ENABLED | CRES_FLAG_MONO;

    const uint32_t frames = 4800;
    std::vector<float> panned(static_cast<size_t>(frames) * kChannels, 0.0f);
    for (uint32_t f = 0; f < frames; ++f)
        panned[f * kChannels + 0] = 0.4f;       // left only

    std::vector<float> folded = Run(config, panned);
    bool balanced = true;
    for (uint32_t f = frames / 2; f < frames; ++f)
    {
        if (std::fabs(folded[f * kChannels] - folded[f * kChannels + 1]) > 1e-5f) { balanced = false; break; }
    }
    Check(balanced, "mono fold makes both channels identical");

    // Balance: pushing fully right must silence the left channel.
    CrescendoConfig balanceConfig = MakeConfig();
    balanceConfig.flags = CRES_FLAG_ENABLED;
    balanceConfig.balance = 1.0f;

    std::vector<float> stereo = MakeSine(500.0, 0.3, 24000);
    std::vector<float> shifted = Run(balanceConfig, stereo);

    float leftPeak = 0.0f, rightPeak = 0.0f;
    for (uint32_t f = 12000; f < 24000; ++f)
    {
        leftPeak = (std::fabs(shifted[f * kChannels]) > leftPeak) ? std::fabs(shifted[f * kChannels]) : leftPeak;
        rightPeak = (std::fabs(shifted[f * kChannels + 1]) > rightPeak) ? std::fabs(shifted[f * kChannels + 1]) : rightPeak;
    }

    char detail[128];
    snprintf(detail, sizeof(detail), "(left %.4f, right %.4f)", leftPeak, rightPeak);
    Check(leftPeak < 0.001f && rightPeak > 0.25f, "full-right balance silences the left channel", detail);

    // Channel swap.
    CrescendoConfig swapConfig = MakeConfig();
    swapConfig.flags = CRES_FLAG_ENABLED | CRES_FLAG_SWAP_LR;
    std::vector<float> swapped = Run(swapConfig, panned);
    Check(std::fabs(swapped[(frames - 1) * kChannels]) < 1e-5f &&
          std::fabs(swapped[(frames - 1) * kChannels + 1] - 0.4f) < 1e-3f,
          "swap moves left-only content to the right channel");
}

static void TestRobustness()
{
    printf("robustness\n");

    // A corrupted or hostile config block must not produce NaN, infinities or
    // anything above the ceiling -- audiodg would have to kill the graph.
    CrescendoConfig config = MakeConfig();
    config.flags = CRES_FLAG_ENABLED | CRES_FLAG_LIMITER | CRES_FLAG_EQ | CRES_FLAG_TONE;
    config.boost = std::nanf("");
    config.bassDb = 1e9f;
    config.trebleDb = -1e9f;
    config.balance = 42.0f;
    config.limiterCeilingDb = 500.0f;
    config.limiterReleaseMs = -1.0f;
    config.limiterLookaheadMs = 1e6f;
    config.bandCount = CRESCENDO_MAX_BANDS;
    for (uint32_t i = 0; i < CRESCENDO_MAX_BANDS; ++i)
        config.bands[i] = { -5.0f, 1e6f, -3.0f };

    std::vector<float> output = Run(config, MakeSine(1000.0, 0.5, 9600));

    bool finite = true;
    float peak = 0.0f;
    for (float sample : output)
    {
        if (!std::isfinite(sample)) { finite = false; break; }
        const float a = std::fabs(sample);
        if (a > peak) peak = a;
    }

    char detail[128];
    snprintf(detail, sizeof(detail), "(peak %.4f)", peak);
    Check(finite, "a nonsense config produces no NaN or infinity");
    Check(peak <= 1.0f, "a nonsense config still cannot exceed full scale", detail);

    // Silence in, silence out -- no self-oscillation from the filter bank.
    CrescendoConfig loud = MakeConfig();
    loud.flags = CRES_FLAG_ENABLED | CRES_FLAG_LIMITER | CRES_FLAG_EQ;
    loud.boost = 5.0f;
    loud.bandCount = 1;
    loud.bands[0] = { 100.0f, 12.0f, 1.41f };

    std::vector<float> silence(9600 * kChannels, 0.0f);
    std::vector<float> stillSilent = Run(loud, silence);
    char quietDetail[128];
    snprintf(quietDetail, sizeof(quietDetail), "(peak %.8f)", PeakOf(stillSilent));
    Check(PeakOf(stillSilent) < 1e-6f, "silence stays silent at maximum boost", quietDetail);
}

static void TestBlockSizeIndependence()
{
    printf("block sizes\n");

    // The audio engine may hand over any block length; the result must not
    // depend on how the signal was chopped up.
    CrescendoConfig a = MakeConfig();
    a.flags = CRES_FLAG_ENABLED | CRES_FLAG_LIMITER | CRES_FLAG_EQ;
    a.boost = 3.0f;
    a.bandCount = 1;
    a.bands[0] = { 2000.0f, 6.0f, 1.41f };

    CrescendoConfig b = a;

    std::vector<float> source = MakeSine(800.0, 0.4, 24000);
    std::vector<float> shortBlocks = Run(a, source, 160);
    std::vector<float> longBlocks = Run(b, source, 1920);

    double worst = 0.0;
    for (size_t i = 0; i < shortBlocks.size(); ++i)
    {
        const double difference = std::fabs(shortBlocks[i] - longBlocks[i]);
        if (difference > worst) worst = difference;
    }

    char detail[128];
    snprintf(detail, sizeof(detail), "(largest difference %.6f)", worst);
    Check(worst < 1e-4, "160-frame and 1920-frame blocks give the same output", detail);
}


int main()
{
    printf("Crescendo DSP tests\n");
    printf("===================\n\n");

    TestBypass();
    TestWatchdogArithmetic();
    TestForcedBypass();
    TestLinearGain();
    TestLimiterCeiling();
    TestLimiterNeedsNoClamp();
    TestSafetyClamp();
    TestLookaheadLatency();
    TestEqualizer();
    TestToneAndImaging();
    TestRobustness();
    TestBlockSizeIndependence();

    printf("\n%d checks, %d failures\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
