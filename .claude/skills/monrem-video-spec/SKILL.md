---
name: monrem-video-spec
description: Use when working on the MonRem video module (virtual monitor streaming, H.264, TCP 28252, TLS-PSK, input injection, rotation, tray/console functions). Explains where the spec, reference server and evidence live and the clean-room rules.
---

# MonRem video module

## Source of truth
- `docs/video/SPEC.md`: wire protocol (sections 12-15), console functions (16), tray (17). Read the section for the feature first.
- `docs/video/HANDOFF.md`: current state and the ordered checklist of what is missing.
- `docs/video/keymap.csv`: Android/iOS key code to scan code table.

## Behavioural oracle
`tools/video-oracle/ref_server/` (Python) works with the real Android phone. Run it with Windows Python (WSL2 only forwards loopback). Use it to confirm behaviour before changing the C# module.

## Evidence (read-only)
`research/vendor-reference/` holds decompiled code. Use `research/vendor-reference/README.md` for the feature-to-file map. Facts (registry names, GUIDs, enum values, command lines) may be reused. Never copy control flow or code into the product, never commit the folder.

## Where code goes
Module `src/host/MonRem.Host.Video` (to be created). Settings, status, diagnostics and tray functions go into the MonRem host and app, not a separate application. Write `docs/video/DESIGN.md` before any code.

## Rules
- Never cite or reintroduce MonRem's old video design (SudoVDA, capture spikes, UDP media).
- When a SPEC detail turns out wrong, fix SPEC first, then the code.
- Captures contain the user's screen: delete them by exact name afterwards.
