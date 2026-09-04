# Generation absolute-age watchdog

Last updated: 2026-09-04 after PR #220

## Purpose

The normal reply pipeline keeps its 50-second AI cancellation budget. This watchdog is an independent last-resort safety barrier for failure modes where a downstream provider ignores cancellation, ThreadPool timer continuations are delayed, or a generation moves through a short-lived state between watchdog polling intervals.

## Current contract

- A dedicated background `Thread` polls generation state every 250ms, so the watchdog does not depend on ThreadPool timer scheduling.
- Generation watches are registered from the accepted buyer action lifetime, using `BuyerActionAccepted` evidence rather than waiting for a sample that happens to observe `Generating`.
- The watch registry persists independently of the bounded `RecentEvents` diagnostic ring. A busy session cannot disable the deadline merely by pushing the accepted event out of the 64-entry event history.
- The 55-second absolute-age limit covers the active end-to-end generation lifecycle, including `Observed`, `Coalescing`, `Processing`, `Generating`, `Ready`, `Sending`, and `Waiting`.
- `Completed`, `Cancelled`, and `Failed` are terminal states and remove the watch.
- If an active generation exceeds the limit, `BuyerSessionAgent.Cancel` removes it from `ActiveGenerations` and cancels its token.
- The reply/send pipeline subsequently fails the generation lease/currentness checks, so a late provider or local Knowledge V2 result cannot legitimately continue into a new send.
- Accepted-event timestamps are normalized when source timing is missing or implausible so an external clock anomaly cannot create an unbounded watch.
- Human seller replies remain observational evidence and do not by themselves cancel an unrelated generation.
- Watchdog logs contain lifecycle identifiers, generation id, elapsed milliseconds, limit and reason; reply body is not required for deadline enforcement.

## Why PR #220 changed the original design

The first watchdog version started timing only after a 250ms poll observed `Generating`. Production analysis found two bypasses:

1. high-density session events could evict the generation's accepted event from the bounded diagnostic ring; and
2. Knowledge V2 local direct answers could move through `Generating -> Ready` between two polls, so the watchdog never registered that generation.

The current design anchors the deadline to the accepted buyer action and keeps a persistent generation watch until terminal state.

## Related send-side bounds

The generation deadline is not a substitute for send-path bounds. PR #220 also added:

- a 1.5-second maximum wait to enter the CDP execute single-flight gate; and
- a 9-second total wall-clock deadline for active-buyer confirmation before send.

These prevent a valid generation from spending minutes waiting in CDP/session-confirmation infrastructure.

This watchdog remains a safety backstop, not a replacement for the normal 50-second cancellation path or delivery verification.
