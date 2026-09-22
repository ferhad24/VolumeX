//
// Watchdog.h -- Decides whether the UI has stopped feeding the engine.
//
// Kept apart from the APO so the arithmetic can be tested: it has to survive
// GetTickCount wrapping every 49.7 days, and a stamp that is a millisecond
// *ahead* of the reader's clock (the writer and reader sample the tick on
// different threads, and an earlier UI rounded stamps up to keep them odd).
//
// The age is taken as a signed difference. Treated as unsigned, a stamp 1 ms
// in the future reads as 4 294 967 295 ms old -- which made the engine bypass
// on every other block and crackle.
//
#pragma once
#include <stdint.h>

namespace cres
{
    // stamp == 0 means the UI does not run a watchdog at all.
    inline bool UiWatchdogExpired(uint32_t now, uint32_t stamp, uint32_t timeoutMs)
    {
        if (stamp == 0) return false;
        const int32_t age = static_cast<int32_t>(now - stamp);
        return age > static_cast<int32_t>(timeoutMs);
    }
}
