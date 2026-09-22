//
// Guids.cpp -- The single translation unit that emits GUID storage.
//
// Isolated on purpose: initguid.h changes what DEFINE_GUID expands to, and any
// ATL header compiled under that change loses GUID_NULL and CLSID_StdGlobalInterfaceTable.
//

#include <windows.h>
#include <initguid.h>

// {8B3F5D2A-7C14-4E9B-A6D3-2F81C0E5B740} -- Crescendo mode-effect APO
DEFINE_GUID(CLSID_CrescendoApo,
    0x8b3f5d2a, 0x7c14, 0x4e9b, 0xa6, 0xd3, 0x2f, 0x81, 0xc0, 0xe5, 0xb7, 0x40);
