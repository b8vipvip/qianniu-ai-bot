# Generation absolute-age watchdog

## Purpose

The normal reply pipeline keeps its 50-second AI cancellation budget. This watchdog is an independent last-resort safety barrier for the failure mode where a downstream provider ignores cancellation or ThreadPool timer continuations are delayed.

## Contract

- A dedicated background `Thread` polls generation state every 250ms, so it does not depend on ThreadPool timer scheduling.
- Timing starts only after a generation is first observed in `Generating`.
- If the same generation remains active for more than 55 seconds, `BuyerSessionAgent.Cancel` removes it from `ActiveGenerations` and cancels its token.
- The existing reply pipeline subsequently fails `lease.IsCurrent`, so a late provider result cannot continue into Ready/Sending.
- Human seller replies remain observational evidence and do not trigger cancellation.
- Completed/Cancelled/Failed generation watches are removed.
- Logs contain seller/buyer identifiers, generation id, elapsed milliseconds and limit only; reply body is not logged by this watchdog.

This is a safety backstop, not a replacement for the normal 50-second generation cancellation path.
