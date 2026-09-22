//
// com_tests.cpp -- Checks the COM shell of CrescendoApo.dll.
//
// This does not prove Windows will load the APO into audiodg -- that also needs
// registration and the unsigned-APO policy -- but it does prove the DLL exports
// what the audio engine asks for, and that the object it hands back implements
// every interface a system effect must expose.
//

#include <cstdio>
#include <windows.h>
#include <audioenginebaseapo.h>

namespace
{
    int g_failures = 0;
    int g_checks = 0;

    void Check(bool condition, const char* what)
    {
        ++g_checks;
        printf(condition ? "  [ ok ] %s\n" : "  [FAIL] %s\n", what);
        if (!condition) ++g_failures;
    }

    // {8B3F5D2A-7C14-4E9B-A6D3-2F81C0E5B740}
    const CLSID kClsid = { 0x8b3f5d2a, 0x7c14, 0x4e9b, { 0xa6, 0xd3, 0x2f, 0x81, 0xc0, 0xe5, 0xb7, 0x40 } };

    using DllGetClassObjectFn = HRESULT(STDAPICALLTYPE*)(REFCLSID, REFIID, LPVOID*);
}

int wmain(int argc, wchar_t** argv)
{
    printf("Crescendo COM tests\n");
    printf("===================\n\n");

    if (argc < 2)
    {
        printf("usage: com_tests <path to CrescendoApo.dll>\n");
        return 2;
    }

    HMODULE library = LoadLibraryW(argv[1]);
    Check(library != nullptr, "CrescendoApo.dll loads");
    if (!library) return 1;

    auto getClassObject = reinterpret_cast<DllGetClassObjectFn>(GetProcAddress(library, "DllGetClassObject"));
    Check(getClassObject != nullptr, "DllGetClassObject is exported");
    Check(GetProcAddress(library, "DllCanUnloadNow") != nullptr, "DllCanUnloadNow is exported");
    if (!getClassObject) return 1;

    CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    IClassFactory* factory = nullptr;
    HRESULT hr = getClassObject(kClsid, IID_IClassFactory, reinterpret_cast<void**>(&factory));
    Check(SUCCEEDED(hr) && factory, "the class factory for the Crescendo CLSID is available");

    if (factory)
    {
        IAudioProcessingObject* apo = nullptr;
        hr = factory->CreateInstance(nullptr, __uuidof(IAudioProcessingObject), reinterpret_cast<void**>(&apo));
        Check(SUCCEEDED(hr) && apo, "the factory creates an IAudioProcessingObject");

        if (apo)
        {
            IAudioProcessingObjectRT* rt = nullptr;
            IAudioProcessingObjectConfiguration* configuration = nullptr;
            IAudioSystemEffects* effects = nullptr;

            Check(SUCCEEDED(apo->QueryInterface(__uuidof(IAudioProcessingObjectRT),
                  reinterpret_cast<void**>(&rt))), "it exposes IAudioProcessingObjectRT");
            Check(SUCCEEDED(apo->QueryInterface(__uuidof(IAudioProcessingObjectConfiguration),
                  reinterpret_cast<void**>(&configuration))), "it exposes IAudioProcessingObjectConfiguration");
            Check(SUCCEEDED(apo->QueryInterface(__uuidof(IAudioSystemEffects),
                  reinterpret_cast<void**>(&effects))), "it exposes IAudioSystemEffects (marks it a system effect)");

            APO_REG_PROPERTIES* properties = nullptr;
            hr = apo->GetRegistrationProperties(&properties);
            Check(SUCCEEDED(hr) && properties, "it reports its registration properties");
            if (properties)
            {
                Check(IsEqualGUID(properties->clsid, kClsid), "the reported CLSID matches the one the installer registers");
                Check(properties->u32MaxInputConnections == 1 && properties->u32MaxOutputConnections == 1,
                      "it declares a single input and output connection");
                CoTaskMemFree(properties);
            }

            HNSTIME latency = -1;
            Check(SUCCEEDED(apo->GetLatency(&latency)) && latency == 0, "latency is zero before a format is locked");

            // Locking without Initialize must fail cleanly rather than crash
            // the audio engine.
            if (configuration)
            {
                Check(FAILED(configuration->LockForProcess(0, nullptr, 0, nullptr)),
                      "LockForProcess rejects an invalid call instead of crashing");
            }

            if (rt) rt->Release();
            if (configuration) configuration->Release();
            if (effects) effects->Release();
            apo->Release();
        }
        factory->Release();
    }

    CoUninitialize();
    FreeLibrary(library);

    printf("\n%d checks, %d failures\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
