# AI / Agent Development Instructions

Before modifying this repository, read:

- `docs/QIANNIU_CHAT_AUTOMATION_PROGRESS.md`

This document records the verified Qianniu 9.97.56N chat-automation findings, failed experiments, current architecture, safety rules, and next tasks. Do not repeat experiments already marked as ineffective unless new evidence justifies doing so.

## Mandatory constraints

1. Preserve the existing Simplified Chinese / `zh-CN` repair behavior. Do not modify language, locale, startup, or text-repair code while working on chat discovery.
2. Keep discovery work isolated under `tools/qn_discovery_lab` until an end-to-end probe is verified.
3. Prefer Windows UI Automation for the near-term message-read/send path. This is structured control automation, not pixel-coordinate RPA.
4. Do not treat the Windmill `internal.chat.selectAndSendText` call as the main chat send API. It was found under a mini-app `shareTextMsg` path and has not been verified in the `bench_im` renderer.
5. Do not blindly call native C++ functions or dereference memory hits without a verified object pointer, ABI, thread, and execution context.
6. Never use a fixed `AliRender.exe` PID. Resolve the current renderer whose command line contains `--render_id=bench_im`.
7. Real-send probes must default to dry-run, require explicit confirmation, perform only one send attempt, and never automatically retry.
8. The user's PowerShell/PSReadLine crashes on long pasted multi-line commands. Add scripts to the repository or use short terminal commands instead of long here-strings.
