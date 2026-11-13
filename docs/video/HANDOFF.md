# Handoff: spacedesk replica (saved 2026-11-13)

Read this first in a new session opened in this folder.

## Goal
This is the **video part of MonRem (msfsControl)**, repo `E:\!Projetos\msfsControl` (WSL `/mnt/e/!Projetos/msfsControl`). It was reorganized on 2026-11-13 (see `docs/estrutura.md`): spec and handoff in `docs/video/`, reference server and test scripts in `tools/video-oracle/`, decompiled evidence in `research/vendor-reference/` (gitignored), future module in `src/host/MonRem.Host.Video`. Before the reorganization everything sat in a top-level `video/` folder. The standalone copy in `~/Projetos/remote-video-replica` is the origin of the import (2026-11-13) and is no longer the working copy.

What to build: the remote-video module of `MonRem.Host`, clean-room from `SPEC.md`: virtual monitor frames -> H.264 -> transport of the SPEC (TCP 28252, TLS 1.3 PSK, ACK flow control) so the existing spacedesk client apps work as viewers, plus input injection and rotation. Settings, connection status, diagnostics and tray functions (SPEC 16, 17) go into the MonRem host and app, not into a separate application. MonRem's repo no longer holds any video design (its spikes, driver submodule, UDP media/input protocol were removed on 2026-11-13); the video module is defined only by this SPEC. VideoWall is out of scope.

The owner delegated all video decisions to the assistant ("o vídeo é seu"): decide and report, do not ask. Never cite or reintroduce MonRem's old video design (SudoVDA, capture spikes, UDP media): the owner called it wrong and had it removed.

## State
- Wire protocol: verified with 3 live captures and a live test with the user's Android phone (SPEC sections 12-15). Reference server in `tools/video-oracle/ref_server/` works with the phone.
- Console analysis (SPEC 16): settings, boot toggle, per-client status, diagnostics and troubleshoot are all registry + SCM + WMI + child processes; there is no IPC to the service.
- Tray analysis (SPEC 17): user-session helper linked to the service by a named pipe (8-byte header, 16 message ids); keyboard injection, display rotation, balloons and message boxes go through it.
- Decompiled code for all three binaries is in `research/vendor-reference/` (see `research/vendor-reference/README.md` for the feature-to-file map). Evidence only, never copy it into product code; the root `.gitignore` keeps it out of git.
- Correction already applied: `ServerStartType` 0/absent = automatic, 1 = manual.

## Open items
- Windows-client capture done (SPEC 15.7): keyboard uses VK in int 2, scan code in int 3, int 4=2 means key up; no types 6/8/9 from Windows.
- TLS-PSK transport solved (SPEC 15.9): `ref_server --psk <EncryptionPassword>`; `phone_test.ps1` (copy in C:\temp) runs the phone test and restores the service. Android keyboard decoded (15.9). Rotation solved from the service code (SPEC 15.10): value = DISPLAYCONFIG_ROTATION, frame size never changes, Windows rotates the desktop inside the frame; `ref_server` now rotates its test pattern that way. Confirmed live with the phone (15.10.1 item 3). `tools/video-oracle/ref_server/proxy.py` + `proxy_test.ps1` (TLS-PSK logging proxy in front of the vendor service) exist but were never run against the phone.
- Keyboard closed: SPEC 15.6, 15.7, 15.9, 15.11 and `keymap.csv` (Android/iOS key code -> scan code table from the service). Types 6 (stream state 1/2) and 8 (client close with reason) are known from the handler code (15.8) but not confirmed live.
- Tray pipe payload layouts for messages 1, 2, 3, 7, 9 and icon ids (only if compatibility with the vendor service is wanted).
- (Settings reload resolved statically, SPEC 16.6c: live reload via registry watch; a live test would confirm which values restart the listener.)
- `tools/video-oracle/ref_server/server.py` now has `--logfile`; it already logs unknown client message types (ints + body hex). Run it with Windows Python and use the phone for keyboard, rotation and resolution change to fill type 6, 9, 11.

## What is missing (checklist, in order)
1. **DONE (commit 41d8d26): commit the MonRem cleanup.** In `E:\!Projetos\msfsControl` the removal of all old video content is staged/modified but NOT committed (spikes, `driver/` submodule, `.gitmodules`, Vortice pins, `media_header.h`, `input.proto`, video messages in `control.proto`, regenerated `Generated/*.g.cs`, code and doc edits; `video/` is untracked). 111 tests pass; `MonRem.Host.Msfs` is untouched but could not be compiled on Linux (no MSFS SDK): build it on Windows or let CI do it. Use `git -c core.fileMode=false -c core.autocrlf=true status` (CRLF/mode noise otherwise). Never commit `video/reference/`. Kept on purpose: ECDH session secret and an empty `Capabilities` message (owner may ask to remove).
2. **SPEC section for the service -> display-driver interface (frame source).** Not documented yet. Read `research/vendor-reference/service/functions` (`ALDeviceControlIddCx`, `ALMonitorIddCx`, `ALThreadMonitorIddcx`): IOCTLs 0x226002 init, 0x22408a EDID set, monitor plug, 0x224086 unplug, 0x22414a frame events subscribe, 0x22600a acquire next frame, 0x22600e update regions, 0x226006 swapchain events, 0x2260ce cursor, 0x2260c2 render adapter, 0x226052/0x226056 persistence. Then analyse the installed driver `C:\Windows\System32\drivers\UMDF\spacedeskDisplayUmode1_4.dll` (not analysed). Re-check SPEC section 1 (Desktop Duplication) against it. This answers how the host gets frames from a virtual monitor; no other source is valid.
3. **`docs/video/DESIGN.md`** before any code: module `src/host/MonRem.Host.Video`, frame source, H.264 encoder (Media Foundation; Annex B, ACK window 2 per SPEC 12-15), TCP 28252 + TLS 1.3 PSK transport, UDP discovery (308-byte reply), input injection (SPEC 15.8-15.11, `keymap.csv`), rotation (15.10: keep frame size, rotate content).
4. **Implementation** of the module and its tests, using `tools/video-oracle/ref_server/` as the behavioural oracle (Python, verified live with the Android phone).
5. **Settings/status/diagnostics/tray functions** (SPEC 16, 17) inside the MonRem host and app: registry-equivalent settings with hot reload (16.6c), per-client status, diagnostics, tray-side actions (keyboard injection, display rotation via SetDisplayConfig, balloons).
6. **Optional live confirmations:** types 6 and 8 from a real client; hot-reload of settings; run `tools/video-oracle/ref_server/proxy.py` against the vendor service; tray pipe payload layouts for messages 1, 2, 3, 7, 9 (only for vendor-service compatibility).

## Environment facts
- WSL2 only forwards loopback: a LAN-facing server must run with Windows Python (`C:\Users\augus\miniconda3\python.exe`).
- `pktmon` needs an admin PowerShell. All three captures were deleted; new ones contain the user's screen image and must be deleted by exact name afterwards.
- Check `Get-Service spacedeskService` is Running before any capture.
- No ffmpeg or tshark; pcapng was parsed with a small Python script.
- Original session transcript: `~/.claude/projects/-home-augusto-Projetos-Rea/8d3cd044-b9ff-4249-a208-66c9aa7a55dc.jsonl`.
