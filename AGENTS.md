# AI / Agent Development Instructions

Before modifying this repository, read in order:

1. `docs/PROJECT_HANDOFF_CONTEXT.md`
2. `docs/QIANNIU_CHAT_AUTOMATION_PROGRESS.md`

`PROJECT_HANDOFF_CONTEXT.md` records the full project goal, current repository baseline, code architecture, completed knowledge-base work, confirmed Qianniu automation findings, known problems, unfinished tasks, local synchronization procedure, and the next development plan.

`QIANNIU_CHAT_AUTOMATION_PROGRESS.md` is the detailed evidence log for the verified Qianniu 9.97.56N chat-automation findings, failed experiments, current automation architecture, safety rules, and next tasks.

Do not repeat experiments already marked as ineffective unless new evidence justifies doing so.

## Mandatory constraints

1. Preserve the existing Simplified Chinese / `zh-CN` repair behavior. Do not modify language, locale, startup, or text-repair code while working on chat discovery.
2. Keep discovery work isolated under `tools/qn_discovery_lab` until an end-to-end probe is verified.
3. Prefer Windows UI Automation for the near-term message-read/send path. This is structured control automation, not pixel-coordinate RPA.
4. Do not treat the Windmill `internal.chat.selectAndSendText` call as the main chat send API. It was found under a mini-app `shareTextMsg` path and has not been verified in the `bench_im` renderer.
5. Do not blindly call native C++ functions or dereference memory hits without a verified object pointer, ABI, thread, and execution context.
6. Never use a fixed `AliRender.exe` PID. Resolve the current renderer whose command line contains `--render_id=bench_im`.
7. Real-send probes must default to dry-run, require explicit confirmation, perform only one send attempt, and never automatically retry.
8. The user's PowerShell/PSReadLine crashes on long pasted multi-line commands. Add scripts to the repository or use short terminal commands instead of long here-strings.
9. Do not commit real customer names, account identifiers, chat text, screenshots, or other private conversation data. Use sanitized test fixtures only.
10. Do not integrate the UIA path into the main bot until real-send verification, structured extraction, deduplication, and current-conversation detection are complete.