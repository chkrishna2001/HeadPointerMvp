# HeadPointerMvp — project state

Last updated 2026-10-19 by "5x5 grid, dictation research"

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

- Modifier+letter keys by voice, phonetic ("rama control tango" = Ctrl+T; bare letters t/b were confused, also with alt/shift/control shift/control alt); letters need a modifier. Compiles; NOT yet verified by real mic (app was running, exe locked).

- Voice reliability (from logs: "rama" heard, then the command after a pause was a miss, because the grammar only held whole "rama X" phrases): grammar now also holds "rama" alone and every command bare (bare ones act only while awake, 3 s follow-up window); wake word exempt from the confidence filter (threshold 0.3); gate floor 450->300 RMS plus one-chunk pre-roll so word onsets ("k" of click) survive. NOT yet verified by real mic.
- Feedback sounds now differ in character (`FeedbackSounds.cs`): rising whoop = wake, dry tick = command ran, Windows "Speech Misrecognition" wav = no command.
- Built-in app commands (`AppCommands` in VoiceCommandService): close window/app (Alt+F4), switch window, task view, minimize, maximize, snap left/right, show desktop, start menu, search, new/close/reopen/next/previous tab, go back/forward, refresh, address bar, copy/cut/paste/undo/redo/select all/save/find, top/bottom. User `VoiceMacros` override them.

- Grid option A (user picked, trial; revert if it does not help): first level is 4x4 named by NATO words alpha..papa (voice words or keyboard A-P except D/R), deeper levels stay 3x3 digits. Digits at level 1 and words deeper are ignored. NOT yet verified by mic.

## In progress
Nothing. Camera is not used (CameraTrackingEnabled=false, no CameraBridge process); the user keeps the shutter closed and works by voice.

## Remaining
- Decide with the user (discuss BEFORE building; they dislike unrequested designs): how to keep spoken commands out of an external dictation tool. Options: per-app commands keyed by foreground process ("start/stop dictating" -> shortcut or UIA button click; probe UIA names in Claude desktop/Slack first), a silent hotkey, or built-in Vosk dictation mode (PARKED by user).
- Claude desktop (Windows) has no dictation shortcut (checked settings, config files, docs 2026-10-19); Claude UI buttons are invisible to UIA. User chose to do nothing: use the grid. Options if revisited: Win+H macro, or a saved-position click (saved spots were rejected earlier).
- Possibly remove the camera/head-tracking code (user undecided; ask).
- Tune noise gate and confidence filter by real use (`MinSpeechRms`, `SpeechOverNoise`, `MinWordConfidence` in VoiceCommandService.cs; log shows `dropped low-confidence words`).
- Grid-mode grammar is loose (includes wake-prefixed phrases); tighten if stray words fire.
- Step 2: UIA 'show numbers' mode; 4x4 first grid level; Settings UI for wake word/macros/mic; model auto-download; README still describes SAPI.
- Mic input level must stay ~60-65% (100% clipped and wrecked accuracy); the app never changes system settings.

## Last session
Fixed from the user's report: confusing beeps, commands needing 3 repeats (esp. click), no window/app commands. Changes above (sounds, grammar, gate, AppCommands). Build clean; deployed copy NOT republished and nothing verified by mic yet; changes uncommitted (also holds the earlier phonetic-letter work).

## Next task
Publish, then check by mic: "rama click" once and "rama" (pause) "click" both work; log shows no unexpected `Voice phrases dropped`; the three sounds are distinguishable. Then discuss grid speed (Remaining) and the dictation-leak options with the user before coding either.

## Decisions & gotchas
- Swallowed Ctrl+Alt+G still lets the remote see Ctrl+Alt down/up (only the G is eaten); harmless so far.
- Vosk grammar is a word loop: unknown speech maps to [unk] and is dropped, so stray digits in conversation don't fire in everyday mode.
- Hotkey strings use `D1` not `1` (`Enum.TryParse("1")` yields Keys.LButton).
- Grid keyboard input is a WH_KEYBOARD_LL hook installed only while the grid is open; it ignores keys with Ctrl/Alt/Win held.
- Speech events arrive on a background thread; AppHost posts to the captured UI SynchronizationContext before touching forms/hooks.
- Looking at the keyboard fooled the old yaw-nearest router; typing-activity freeze is the main fix, pitch freeze the backup.
- Grid first level is 5x5 (alpha..zulu by voice; keyboard A-W, not D/R-conflicting ones); deeper levels 3x3 digits. Deployed 2026-10-19, not yet verified by mic.
- Deployed copy: `dotnet publish src/HeadPointerMvp/HeadPointerMvp.csproj -c Release -o C:\Users\chkri\Apps\HeadPointer` (stop the running exe first); autostarts via HKCU Run value `HeadPointer`. Dev builds in bin\Debug don't touch it; settings/logs/models are shared in %LocalAppData%\HeadPointerMvp, so quit the deployed app before testing a dev build.
