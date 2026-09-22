//
// Dll.cpp -- COM entry points for the Crescendo APO.
//
// Deliberately minimal. The audio engine only ever needs DllGetClassObject and
// DllCanUnloadNow; registration is written by the Crescendo app, which knows
// which endpoint the user selected.
//

#include "CrescendoApo.h"

class CCrescendoModule : public CAtlDllModuleT<CCrescendoModule>
{
};

CCrescendoModule _AtlModule;

extern "C" BOOL WINAPI DllMain(HINSTANCE hInstance, DWORD dwReason, LPVOID lpReserved)
{
    if (dwReason == DLL_PROCESS_ATTACH)
    {
        // No per-thread state, and audiodg creates threads freely.
        DisableThreadLibraryCalls(hInstance);
    }
    return _AtlModule.DllMain(dwReason, lpReserved);
}

STDAPI DllCanUnloadNow(void)
{
    return _AtlModule.DllCanUnloadNow();
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv)
{
    return _AtlModule.DllGetClassObject(rclsid, riid, ppv);
}
