# HeadPointerMvp — project state

Last updated 2026-10-16 by "voice noise robustness, feedback beeps, key shorthand"

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

- Noise robustness: prefers `vosk-model-en-us-0.22-lgraph` (~128 MB, in models folder, falls back to small); noise gate (adaptive floor, gated audio becomes silence); per-word confidence filter (0.4, dropped words logged). Built OK but NOT yet verified by real mic (app was running, exe locked: close it, rebuild, test).
- Voice feedback: wake word -> rising beep, command ran -> double chirp (NAudio tones on the default output; Console.Beep was inaudible; tray balloons rejected as annoying), wake without command in 5 s -> low buzz (`VoiceCommandService.Feedback` -> `AppHost.VoiceFeedback`). Keys also work without "press" ("rama alt tab"), since the recognizer often drops "press". Not yet verified by ear.

## In progress
Nothing. Camera is not used (CameraTrackingEnabled=false, no CameraBridge process); the user keeps the shutter closed and works by voice.

## Remaining
- Decide with the user (discuss BEFORE building; they dislike unrequested designs): how to keep spoken commands out of an external dictation tool. Options: per-app commands keyed by foreground process ("start/stop dictating" -> shortcut or UIA button click; probe UIA names in Claude desktop/Slack first), a silent hotkey, or built-in Vosk dictation mode (PARKED by user).
- Possibly remove the camera/head-tracking code (user undecided; ask).
- Tune noise gate and confidence filter by real use (`MinSpeechRms`, `SpeechOverNoise`, `MinWordConfidence` in VoiceCommandService.cs; log shows `dropped low-confidence words`).
- Grid-mode grammar is loose (includes wake-prefixed phrases); tighten if stray words fire.
- Step 2: UIA 'show numbers' mode; 4x4 first grid level; Settings UI for wake word/macros/mic; model auto-download; README still describes SAPI.
- Mic input level must stay ~60-65% (100% clipped and wrecked accuracy); the app never changes system settings.

## Last session
Downloaded vosk-model-en-us-0.22-lgraph (128 MB, in the models folder; code prefers it, falls back to small). Added noise gate, per-word confidence filter, audible feedback (rising beep = wake word heard, double chirp = command ran, low buzz = no command in 5 s; NAudio tones, tray balloons rejected as annoying), and key commands without "press" ("rama alt tab" works). Saved-spots idea was built, rejected by the user and reverted. Verified by user: beeps audible, commands run. Build clean.

## Next task
Discuss the dictation/command-leak options above with the user and agree one before coding. Done when the user picks a design and a first command works in Claude desktop within 2 tries.

## Decisions & gotchas
- Swallowed Ctrl+Alt+G still lets the remote see Ctrl+Alt down/up (only the G is eaten); harmless so far.
- Vosk grammar is a word loop: unknown speech maps to [unk] and is dropped, so stray digits in conversation don't fire in everyday mode.
- Hotkey strings use `D1` not `1` (`Enum.TryParse("1")` yields Keys.LButton).
- Grid keyboard input is a WH_KEYBOARD_LL hook installed only while the grid is open; it ignores keys with Ctrl/Alt/Win held.
- Speech events arrive on a background thread; AppHost posts to the captured UI SynchronizationContext before touching forms/hooks.
- Looking at the keyboard fooled the old yaw-nearest router; typing-activity freeze is the main fix, pitch freeze the backup.
