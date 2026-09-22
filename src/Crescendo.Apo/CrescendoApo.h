//
// CrescendoApo.h -- The system-effect APO that Windows loads into audiodg.exe.
//
// Registered as a Mode Effect (MFX) by default, which places it after the audio
// engine has mixed every application stream but before the endpoint stage. That
// is the one spot where a single instance can amplify the whole system.
//
// The APO interfaces are implemented directly rather than through
// CBaseAudioProcessingObject: that base class pulls in a WDK-only symbol
// (CreateAudioMediaTypeFromUncompressedAudioFormat) which is not redistributed
// with the Windows SDK. Implementing the three interfaces by hand is a few
// hundred lines and removes the WDK from the build entirely.
//
// Everything the UI can change arrives through a shared-memory block. The audio
// thread only ever reads that block; it never waits on the UI, and the UI never
// waits on the audio thread.
//
#pragma once

#include <atlbase.h>
#include <atlcom.h>
#include <windows.h>
#include <audioenginebaseapo.h>
#include <baseaudioprocessingobject.h>   // AERT_Allocate/AERT_Free + CRegAPOProperties

#include "CrescendoAbi.h"
#include "dsp/Processor.h"

// Defined once in Guids.cpp. Keeping the definition out of this header lets ATL
// be included normally -- initguid.h redefines DEFINE_GUID and would strip the
// declarations atlbase.h relies on (GUID_NULL and friends).
EXTERN_C const GUID CLSID_CrescendoApo;

class ATL_NO_VTABLE CCrescendoApo :
    public CComObjectRootEx<CComMultiThreadModel>,
    public CComCoClass<CCrescendoApo, &CLSID_CrescendoApo>,
    public IAudioProcessingObject,
    public IAudioProcessingObjectRT,
    public IAudioProcessingObjectConfiguration,
    public IAudioSystemEffects
{
public:
    CCrescendoApo();
    virtual ~CCrescendoApo();

    // Registration is performed by the Crescendo app against the endpoint that
    // the user picked, not by regsvr32, so ATL needs no .rgs script here.
    DECLARE_NO_REGISTRY()
    DECLARE_PROTECT_FINAL_CONSTRUCT()

    BEGIN_COM_MAP(CCrescendoApo)
        COM_INTERFACE_ENTRY(IAudioProcessingObject)
        COM_INTERFACE_ENTRY(IAudioProcessingObjectConfiguration)
        COM_INTERFACE_ENTRY(IAudioProcessingObjectRT)
        COM_INTERFACE_ENTRY(IAudioSystemEffects)
    END_COM_MAP()

    // ---- IAudioProcessingObject -------------------------------------------
    STDMETHOD(Reset)(void) override;
    STDMETHOD(GetLatency)(HNSTIME* pTime) override;
    STDMETHOD(GetRegistrationProperties)(APO_REG_PROPERTIES** ppRegProps) override;
    STDMETHOD(Initialize)(UINT32 cbDataSize, BYTE* pbyData) override;
    STDMETHOD(IsInputFormatSupported)(
        IAudioMediaType* pOutputFormat,
        IAudioMediaType* pRequestedInputFormat,
        IAudioMediaType** ppSupportedInputFormat) override;
    STDMETHOD(IsOutputFormatSupported)(
        IAudioMediaType* pInputFormat,
        IAudioMediaType* pRequestedOutputFormat,
        IAudioMediaType** ppSupportedOutputFormat) override;
    STDMETHOD(GetInputChannelCount)(UINT32* pu32ChannelCount) override;

    // ---- IAudioProcessingObjectRT -----------------------------------------
    STDMETHOD_(void, APOProcess)(
        UINT32 u32NumInputConnections,
        APO_CONNECTION_PROPERTY** ppInputConnections,
        UINT32 u32NumOutputConnections,
        APO_CONNECTION_PROPERTY** ppOutputConnections) override;
    STDMETHOD_(UINT32, CalcInputFrames)(UINT32 u32OutputFrameCount) override;
    STDMETHOD_(UINT32, CalcOutputFrames)(UINT32 u32InputFrameCount) override;

    // ---- IAudioProcessingObjectConfiguration ------------------------------
    STDMETHOD(LockForProcess)(
        UINT32 u32NumInputConnections,
        APO_CONNECTION_DESCRIPTOR** ppInputConnections,
        UINT32 u32NumOutputConnections,
        APO_CONNECTION_DESCRIPTOR** ppOutputConnections) override;
    STDMETHOD(UnlockForProcess)(void) override;

private:
    // Accepts only what this DSP chain can process losslessly: uncompressed
    // 32-bit float, 1..8 channels, at a sane frame rate.
    HRESULT ValidateFormat(IAudioMediaType* pType, UNCOMPRESSEDAUDIOFORMAT& out) const;
    HRESULT CheckFormatPair(IAudioMediaType* pOpposite, IAudioMediaType* pRequested,
                            IAudioMediaType** ppSupported) const;

    // The UI may start after the APO is already live, so a small background
    // thread keeps retrying the shared-memory attach instead of the audio thread
    // ever touching the object manager.
    static DWORD WINAPI AttachThreadProc(LPVOID param);
    void AttachLoop();
    void StartAttachThread();
    void StopAttachThread();
    bool TryAttachMappings();
    void DetachMappings();

    cres::Processor m_processor;

    CRITICAL_SECTION m_lock;
    bool m_lockValid = false;

    UNCOMPRESSEDAUDIOFORMAT m_format = {};
    UINT32 m_channels = 2;
    UINT32 m_sampleRate = 48000;
    bool m_initialized = false;
    bool m_locked = false;
    bool m_prepared = false;
    bool m_silentHandled = false;

    HANDLE m_configMap = nullptr;
    HANDLE m_meterMap = nullptr;
    // Written by the attach thread, read by the audio thread. Aligned pointer
    // writes are atomic on every architecture Windows targets.
    CrescendoConfig* volatile m_config = nullptr;
    CrescendoMeter* volatile m_meter = nullptr;

    HANDLE m_attachThread = nullptr;
    HANDLE m_attachStop = nullptr;
};

OBJECT_ENTRY_AUTO(CLSID_CrescendoApo, CCrescendoApo)
