# HeadPointerMvp — project state

Last updated 2026-10-16 by "voice tuning: wake window, forced utterance end, mic level"

## Purpose & shape
Windows tray app (WinForms, .NET 8) for multi-monitor, low-mouse use. Voice (Vosk offline, closed grammar, NAudio capture) and hotkeys pick a monitor; webcam head yaw (CameraBridge process, OpenCvSharp+YuNet → UDP 127.0.0.1:11573) can too but is OFF by default (user: camera isn't best); a numbered zoom grid points/clicks anywhere.
Build: `dotnet build HeadPointerMvp.sln`. Settings: `%LocalAppData%\HeadPointerMvp\settings.json`, logs in `logs\`. See README.md for calibration and voice details.

## Done
- Head → monitor routing with per-monitor yaw calibration, dwell, cooldown.
- Bridge yaw/pitch now measured along/across the eye line (roll-proof); pitch is sent.
- Router freezes head moves while typing/clicking (GetLastInputInfo, `TypingFreezeMilliseconds`), while pitch deviates from a running baseline (`PitchFreezeUnits`), and 4 s after any explicit move; 35/65 hysteresis between calibrated monitors.
- Ctrl+Alt+1..9 jump to monitor N; Ctrl+Alt+G opens the grid (`GridHotkey`). Hotkeys register individually (one clash no longer drops the rest).
- Voice monitor commands and grid work even when 'Enable tracking' is off (that toggle now gates head moves only).
- `GridPointerService`: 3x3 numbered, repeatedly-zooming overlay. Digits 1-9 zoom, Enter/Space click, R right-click, D double-click, Backspace/Z back, Tab next monitor, Esc cancel. Voice: "grid", then "one".."nine", "click", "right click", "double click", "back", "cancel", "next monitor".

- Triggers that work with a full-screen remote desktop (Windows App, cloud PC on the right monitor) focused: `TriggerHookService` (low-level keyboard hook for Ctrl+Alt+G / Ctrl+Alt+1..9, swallows the keys; mouse side button `GridMouseButton`, default XButton2, swallowed system-wide; every hit logs `[trigger] ...`), and `HeadTiltGesture` (tilt head >15 deg for 1.2 s toggles the grid). Voice "grid" is the third path.

- Voice moved from SAPI (rejected ~half of commands) to Vosk `vosk-model-small-en-us-0.15` (model NOT in git: lives in `%LocalAppData%\HeadPointerMvp\models\`; re-download from alphacephei.com/vosk/models if missing). Two grammars: everyday ("grid", "monitor N") and grid mode (digits, click, right/double click, back, cancel, next monitor), chainable in one breath ("five five five click"). Verified offline with synthetic speech; not yet by real mic.
- Camera tracking and head tilt default off (user's settings.json updated).

- Wake-word voice: everything is spoken as `<wake word> <command>` (default "rama", `VoiceWakeWord`, may be two words, must be in the Vosk vocab - checked at start via FindWord). Commands: show grid, monitor N, `press [control|alt|shift|control shift] <enter|tab|escape|space|back space|delete|arrows|home|end|page up/down>`, click, right click, double click, scroll up/down, plus `VoiceMacros` (phrase -> chord, in settings.json). Several commands chain after one wake word. Speech without the wake word is ignored, so dictation is untouched; grid mode is the only part that needs no wake word (digits, click, back, cancel). Keys are injected with SendInput (flagged injected; our hooks pass those through).

- Voice tuning (all from real logs): wake word opens a 5 s window and half-heard commands carry over (the engine splits at every pause); utterances are force-closed when partial text is stable 0.7 s (noisy mic never reached silence); grid mode also accepts wake-prefixed commands; 'tap'/'enter' also click in grid mode; log lines show peak and live input level. Verified by user: 'rama show grid', digits, 'click' -> real click, at 65% mic level.

## In progress
Nothing. NOT yet verified on screen: grid rendering, digit/Enter/voice flow, hysteresis/typing-freeze feel. Build is clean (0 warnings).

## Remaining
- Step 2: UIA 'show numbers' mode (local apps, e.g. Claude desktop sidebar); 4x4 first grid level.
- Step 3: voice macros (phrase -> key chord, JSON-configured) for session switching; find Claude desktop's own session shortcuts.
- Verify the grid and freeze behaviour by hand, then tune `TypingFreezeMilliseconds`, `PitchFreezeUnits`.
- Recalibrate monitors in Settings (yaw formula changed slightly).
- Settings UI for the new settings (currently JSON-only).
- "Show numbers" mode: UI Automation clickable elements labelled with numbers (the other half of Apple Voice Control).
- Mic input level must stay ~60-65% (100% clipped and wrecked accuracy); the app never changes system settings. Wrong-word noise in grid mode ('tab', 'page') is harmless but shows the merged grammar is loose.
- Settings UI for wake word/macros (JSON-only now). Claude desktop's own session-switch shortcuts still unknown: add as VoiceMacros once found.
- Tune Vosk by real use (`[voice] heard` lines); consider a different wake word if 'rama' mis-fires.
- Model auto-download/installer step; README still describes SAPI.

## Last session
User confirmed mouse button, Ctrl+Alt+G, grid work with the cloud PC focused; voice (SAPI) was flaky. Swapped to Vosk, then added the wake word; builds clean, app starts and listens on the Logi headset mic. Offline TTS test showed wake-word phrases parse and non-wake speech is ignored. Never inject keys from the agent: the user works in a live remote session.

## Next task
Real-use test with Windows App focused: 'rama press tab', 'rama scroll down', 'rama monitor two', 'rama next tab'; then Step 2 (show numbers) and Step 3 (find Claude desktop session shortcuts -> VoiceMacros). Done when each works within 2 tries; done when log shows `[voice] heard` and `[grid] Left click` and the click lands within a few px. Then Step 2/3 (show numbers, voice macros).

## Decisions & gotchas
- Swallowed Ctrl+Alt+G still lets the remote see Ctrl+Alt down/up (only the G is eaten); harmless so far.
- Vosk grammar is a word loop: unknown speech maps to [unk] and is dropped, so stray digits in conversation don't fire in everyday mode.
- Hotkey strings use `D1` not `1` (`Enum.TryParse("1")` yields Keys.LButton).
- Grid keyboard input is a WH_KEYBOARD_LL hook installed only while the grid is open; it ignores keys with Ctrl/Alt/Win held.
- Speech events arrive on a background thread; AppHost posts to the captured UI SynchronizationContext before touching forms/hooks.
- Looking at the keyboard fooled the old yaw-nearest router; typing-activity freeze is the main fix, pitch freeze the backup.
