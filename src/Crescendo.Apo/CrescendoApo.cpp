//
// CrescendoApo.cpp -- APO implementation.
//

#include "CrescendoApo.h"

#include <ks.h>
#include <ksmedia.h>
#include <xmmintrin.h>
#include <pmmintrin.h>

namespace
{
    // Advertised to the audio engine. MaxInstances is unbounded because Windows
    // may build one graph per signal-processing mode on the same endpoint.
    CRegAPOProperties<1> const g_RegProperties(
        CLSID_CrescendoApo,
        L"Crescendo Audio Engine",
        L"Copyright (c) Crescendo",
        1, 0,
        __uuidof(IAudioSystemEffects));

    constexpr DWORD kAttachPollMs = 400;

    // The audio engine hosts this DLL inside audiodg.exe, a service-context
    // process. It can open an existing Global\ object but is not guaranteed the
    // privilege needed to create one, so the APO only ever opens.
    HANDLE OpenMapping(LPCWSTR name, DWORD access)
    {
        return OpenFileMappingW(access, FALSE, name);
    }
}

CCrescendoApo::CCrescendoApo()
{
    m_lockValid = (InitializeCriticalSectionEx(&m_lock, 0, 0) != FALSE);
}

CCrescendoApo::~CCrescendoApo()
{
    StopAttachThread();
    DetachMappings();
    m_processor.Release();

    if (m_lockValid)
    {
        DeleteCriticalSection(&m_lock);
        m_lockValid = false;
    }
}

// -----------------------------------------------------------------------------
// Format negotiation
// -----------------------------------------------------------------------------

HRESULT CCrescendoApo::ValidateFormat(IAudioMediaType* pType, UNCOMPRESSEDAUDIOFORMAT& out) const
{
    if (!pType) return E_POINTER;

    BOOL isCompressed = TRUE;
    HRESULT hr = pType->IsCompressedFormat(&isCompressed);
    if (FAILED(hr)) return hr;
    if (isCompressed) return APOERR_FORMAT_NOT_SUPPORTED;

    hr = pType->GetUncompressedAudioFormat(&out);
    if (FAILED(hr)) return hr;

    // 32-bit float is what the audio engine hands to system effects, and it is
    // the only layout this chain processes without a conversion pass.
    if (out.guidFormatType != KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
        return APOERR_FORMAT_NOT_SUPPORTED;
    if (out.dwBytesPerSampleContainer != 4 || out.dwValidBitsPerSample != 32)
        return APOERR_FORMAT_NOT_SUPPORTED;
    if (out.dwSamplesPerFrame == 0 || out.dwSamplesPerFrame > CRESCENDO_MAX_CHANNELS)
        return APOERR_FORMAT_NOT_SUPPORTED;
    if (out.fFramesPerSecond < 8000.0f || out.fFramesPerSecond > 384000.0f)
        return APOERR_FORMAT_NOT_SUPPORTED;

    return S_OK;
}

HRESULT CCrescendoApo::CheckFormatPair(IAudioMediaType* pOpposite,
                                       IAudioMediaType* pRequested,
                                       IAudioMediaType** ppSupported) const
{
    if (ppSupported) *ppSupported = nullptr;
    if (!pRequested) return E_POINTER;

    UNCOMPRESSEDAUDIOFORMAT requested = {};
    HRESULT hr = ValidateFormat(pRequested, requested);
    if (FAILED(hr)) return hr;

    // Crescendo is a 1:1 effect: it never resamples and never changes the
    // channel count, so both sides of the connection have to agree.
    if (pOpposite)
    {
        UNCOMPRESSEDAUDIOFORMAT opposite = {};
        hr = ValidateFormat(pOpposite, opposite);
        if (FAILED(hr)) return hr;

        if (opposite.dwSamplesPerFrame != requested.dwSamplesPerFrame ||
            opposite.fFramesPerSecond != requested.fFramesPerSecond)
        {
            return APOERR_FORMAT_NOT_SUPPORTED;
        }
    }

    if (ppSupported)
    {
        *ppSupported = pRequested;
        pRequested->AddRef();
    }
    return S_OK;
}

// -----------------------------------------------------------------------------
// IAudioProcessingObject
// -----------------------------------------------------------------------------

STDMETHODIMP CCrescendoApo::Initialize(UINT32 /*cbDataSize*/, BYTE* /*pbyData*/)
{
    // Crescendo carries no per-endpoint init payload: everything it needs comes
    // from shared memory, which keeps the registration footprint minimal.
    m_initialized = true;
    return S_OK;
}

STDMETHODIMP CCrescendoApo::IsInputFormatSupported(
    IAudioMediaType* pOutputFormat,
    IAudioMediaType* pRequestedInputFormat,
    IAudioMediaType** ppSupportedInputFormat)
{
    return CheckFormatPair(pOutputFormat, pRequestedInputFormat, ppSupportedInputFormat);
}

STDMETHODIMP CCrescendoApo::IsOutputFormatSupported(
    IAudioMediaType* pInputFormat,
    IAudioMediaType* pRequestedOutputFormat,
    IAudioMediaType** ppSupportedOutputFormat)
{
    return CheckFormatPair(pInputFormat, pRequestedOutputFormat, ppSupportedOutputFormat);
}

STDMETHODIMP CCrescendoApo::GetInputChannelCount(UINT32* pu32ChannelCount)
{
    if (!pu32ChannelCount) return E_POINTER;
    if (!m_locked) return APOERR_ALREADY_UNLOCKED;
    *pu32ChannelCount = m_channels;
    return S_OK;
}

STDMETHODIMP CCrescendoApo::GetRegistrationProperties(APO_REG_PROPERTIES** ppRegProps)
{
    if (!ppRegProps) return E_POINTER;
    *ppRegProps = nullptr;

    const APO_REG_PROPERTIES* src = g_RegProperties;
    const SIZE_T cb = sizeof(APO_REG_PROPERTIES) +
                      (src->u32NumAPOInterfaces > 0 ? (src->u32NumAPOInterfaces - 1) * sizeof(IID) : 0);

    APO_REG_PROPERTIES* copy = static_cast<APO_REG_PROPERTIES*>(CoTaskMemAlloc(cb));
    if (!copy) return E_OUTOFMEMORY;

    memcpy(copy, src, cb);
    *ppRegProps = copy;
    return S_OK;
}

STDMETHODIMP CCrescendoApo::Reset(void)
{
    if (m_prepared)
        m_processor.ResetState();
    return S_OK;
}

STDMETHODIMP CCrescendoApo::GetLatency(HNSTIME* pTime)
{
    if (!pTime) return E_POINTER;

    // Report the look-ahead delay so the engine keeps audio and video in sync.
    HNSTIME latency = 0;
    if (m_prepared && m_sampleRate)
    {
        const UINT32 samples = m_processor.LatencySamples();
        latency = static_cast<HNSTIME>((static_cast<double>(samples) / m_sampleRate) * 10000000.0);
    }
    *pTime = latency;
    return S_OK;
}

// -----------------------------------------------------------------------------
// IAudioProcessingObjectConfiguration
// -----------------------------------------------------------------------------

STDMETHODIMP CCrescendoApo::LockForProcess(
    UINT32 u32NumInputConnections,
    APO_CONNECTION_DESCRIPTOR** ppInputConnections,
    UINT32 u32NumOutputConnections,
    APO_CONNECTION_DESCRIPTOR** ppOutputConnections)
{
    if (!m_lockValid) return E_FAIL;
    if (!m_initialized) return APOERR_NOT_INITIALIZED;
    if (u32NumInputConnections != 1 || u32NumOutputConnections != 1)
        return APOERR_NUM_CONNECTIONS_INVALID;
    if (!ppInputConnections || !ppOutputConnections ||
        !ppInputConnections[0] || !ppOutputConnections[0])
        return E_POINTER;

    EnterCriticalSection(&m_lock);

    HRESULT hr = S_OK;
    do
    {
        if (m_locked) { hr = APOERR_APO_LOCKED; break; }

        UNCOMPRESSEDAUDIOFORMAT inFmt = {};
        UNCOMPRESSEDAUDIOFORMAT outFmt = {};

        hr = ValidateFormat(ppInputConnections[0]->pFormat, inFmt);
        if (FAILED(hr)) break;
        hr = ValidateFormat(ppOutputConnections[0]->pFormat, outFmt);
        if (FAILED(hr)) break;

        if (inFmt.dwSamplesPerFrame != outFmt.dwSamplesPerFrame ||
            inFmt.fFramesPerSecond != outFmt.fFramesPerSecond)
        {
            hr = APOERR_FORMAT_NOT_SUPPORTED;
            break;
        }

        if (ppInputConnections[0]->u32MaxFrameCount != ppOutputConnections[0]->u32MaxFrameCount)
        {
            hr = APOERR_INVALID_OUTPUT_MAXFRAMECOUNT;
            break;
        }

        m_format = inFmt;
        m_channels = inFmt.dwSamplesPerFrame;
        m_sampleRate = static_cast<UINT32>(inFmt.fFramesPerSecond + 0.5f);

        hr = m_processor.Prepare(m_sampleRate, m_channels);
        if (FAILED(hr)) break;

        m_prepared = true;
        m_locked = true;
        m_silentHandled = false;
    } while (false);

    LeaveCriticalSection(&m_lock);

    if (SUCCEEDED(hr))
    {
        TryAttachMappings();
        StartAttachThread();
    }
    return hr;
}

STDMETHODIMP CCrescendoApo::UnlockForProcess(void)
{
    StopAttachThread();

    if (m_lockValid) EnterCriticalSection(&m_lock);

    m_locked = false;
    m_prepared = false;
    m_processor.Release();

    if (m_lockValid) LeaveCriticalSection(&m_lock);

    DetachMappings();
    return S_OK;
}

// -----------------------------------------------------------------------------
// IAudioProcessingObjectRT -- real-time path
// -----------------------------------------------------------------------------

STDMETHODIMP_(UINT32) CCrescendoApo::CalcInputFrames(UINT32 u32OutputFrameCount)
{
    return u32OutputFrameCount;   // 1:1, no resampling
}

STDMETHODIMP_(UINT32) CCrescendoApo::CalcOutputFrames(UINT32 u32InputFrameCount)
{
    return u32InputFrameCount;
}

STDMETHODIMP_(void) CCrescendoApo::APOProcess(
    UINT32 u32NumInputConnections,
    APO_CONNECTION_PROPERTY** ppInputConnections,
    UINT32 u32NumOutputConnections,
    APO_CONNECTION_PROPERTY** ppOutputConnections)
{
    ASSERT_REALTIME();

    if (u32NumInputConnections != 1 || u32NumOutputConnections != 1) return;
    if (!ppInputConnections || !ppOutputConnections) return;

    APO_CONNECTION_PROPERTY* in = ppInputConnections[0];
    APO_CONNECTION_PROPERTY* out = ppOutputConnections[0];
    if (!in || !out || !m_prepared) return;

    const UINT32 frames = in->u32ValidFrameCount;
    float* dst = reinterpret_cast<float*>(out->pBuffer);
    const float* src = reinterpret_cast<const float*>(in->pBuffer);

    switch (in->u32BufferFlags)
    {
    case BUFFER_VALID:
    {
        if (!dst || !src || frames == 0)
        {
            out->u32ValidFrameCount = frames;
            out->u32BufferFlags = in->u32BufferFlags;
            return;
        }

        if (dst != src)
            memcpy(dst, src, static_cast<size_t>(frames) * m_channels * sizeof(float));

        // Biquad tails decay through denormal territory; without flush-to-zero a
        // quiet passage can cost an order of magnitude more CPU than a loud one.
        const unsigned int savedCsr = _mm_getcsr();
        _mm_setcsr(savedCsr | 0x8040u);   // FTZ | DAZ

        // Watchdog: a UI that stops stamping (crashed, killed, or exited
        // without a clean shutdown) must not leave the system boosted.
        // GetTickCount reads shared user data -- no syscall, safe here -- and
        // unsigned subtraction survives the 49-day wrap.
        bool uiGone = false;
        if (const CrescendoConfig* cfg = m_config)
        {
            const uint32_t stamp = cfg->uiHeartbeatMs;
            uiGone = stamp != 0 && (static_cast<uint32_t>(GetTickCount()) - stamp) > CRESCENDO_UI_TIMEOUT_MS;
        }

        m_processor.Process(dst, frames, m_config, m_meter, uiGone);

        _mm_setcsr(savedCsr);

        m_silentHandled = false;
        out->u32ValidFrameCount = frames;
        out->u32BufferFlags = BUFFER_VALID;
        break;
    }

    case BUFFER_SILENT:
    default:
        // Drop the filter and look-ahead memory once, so resuming playback does
        // not replay a stale tail, then pass the silence straight through and
        // let the engine keep its power optimisations.
        if (!m_silentHandled)
        {
            m_processor.ResetState();
            m_silentHandled = true;
        }
        out->u32ValidFrameCount = frames;
        out->u32BufferFlags = BUFFER_SILENT;
        break;
    }
}

// -----------------------------------------------------------------------------
// Shared-memory attachment
// -----------------------------------------------------------------------------

bool CCrescendoApo::TryAttachMappings()
{
    if (!m_config)
    {
        HANDLE h = OpenMapping(CRESCENDO_CONFIG_MAP, FILE_MAP_READ);
        if (h)
        {
            void* view = MapViewOfFile(h, FILE_MAP_READ, 0, 0, sizeof(CrescendoConfig));
            CrescendoConfig* cfg = reinterpret_cast<CrescendoConfig*>(view);
            if (cfg && cfg->magic == CRESCENDO_CONFIG_MAGIC)
            {
                m_configMap = h;
                m_config = cfg;
            }
            else
            {
                if (view) UnmapViewOfFile(view);
                CloseHandle(h);
            }
        }
    }

    if (!m_meter)
    {
        HANDLE h = OpenMapping(CRESCENDO_METER_MAP, FILE_MAP_READ | FILE_MAP_WRITE);
        if (h)
        {
            void* view = MapViewOfFile(h, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(CrescendoMeter));
            CrescendoMeter* meter = reinterpret_cast<CrescendoMeter*>(view);
            if (meter && meter->magic == CRESCENDO_METER_MAGIC)
            {
                m_meterMap = h;
                m_meter = meter;
            }
            else
            {
                if (view) UnmapViewOfFile(view);
                CloseHandle(h);
            }
        }
    }

    return m_config != nullptr;
}

void CCrescendoApo::DetachMappings()
{
    CrescendoConfig* cfg = m_config;
    CrescendoMeter* meter = m_meter;
    m_config = nullptr;
    m_meter = nullptr;

    if (cfg) UnmapViewOfFile(cfg);
    if (meter) UnmapViewOfFile(meter);

    if (m_configMap) { CloseHandle(m_configMap); m_configMap = nullptr; }
    if (m_meterMap) { CloseHandle(m_meterMap); m_meterMap = nullptr; }
}

DWORD WINAPI CCrescendoApo::AttachThreadProc(LPVOID param)
{
    static_cast<CCrescendoApo*>(param)->AttachLoop();
    return 0;
}

void CCrescendoApo::AttachLoop()
{
    for (;;)
    {
        if (WaitForSingleObject(m_attachStop, kAttachPollMs) == WAIT_OBJECT_0)
            return;

        if (m_config && m_meter)
            continue;   // attached; the thread stays alive only to be shut down cleanly

        TryAttachMappings();
    }
}

void CCrescendoApo::StartAttachThread()
{
    if (m_attachThread) return;

    m_attachStop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!m_attachStop) return;

    m_attachThread = CreateThread(nullptr, 0, AttachThreadProc, this, 0, nullptr);
    if (!m_attachThread)
    {
        CloseHandle(m_attachStop);
        m_attachStop = nullptr;
    }
}

void CCrescendoApo::StopAttachThread()
{
    if (m_attachStop) SetEvent(m_attachStop);

    if (m_attachThread)
    {
        WaitForSingleObject(m_attachThread, 3000);
        CloseHandle(m_attachThread);
        m_attachThread = nullptr;
    }

    if (m_attachStop)
    {
        CloseHandle(m_attachStop);
        m_attachStop = nullptr;
    }
}
