# HeadPointerMvp — project state

Last updated 2026-10-16 by "remote-desktop-proof triggers: low-level hooks, mouse side button, head tilt"

## Purpose & shape
Windows tray app (WinForms, .NET 8) for multi-monitor, low-mouse use. Webcam head yaw (CameraBridge process, OpenCvSharp+YuNet → UDP 127.0.0.1:11573) picks a monitor; voice (System.Speech/SAPI, NAudio capture) and hotkeys do the same; a numbered zoom grid points/clicks anywhere.
Build: `dotnet build HeadPointerMvp.sln`. Settings: `%LocalAppData%\HeadPointerMvp\settings.json`, logs in `logs\`. See README.md for calibration and voice details.

## Done
- Head → monitor routing with per-monitor yaw calibration, dwell, cooldown.
- Bridge yaw/pitch now measured along/across the eye line (roll-proof); pitch is sent.
- Router freezes head moves while typing/clicking (GetLastInputInfo, `TypingFreezeMilliseconds`), while pitch deviates from a running baseline (`PitchFreezeUnits`), and 4 s after any explicit move; 35/65 hysteresis between calibrated monitors.
- Ctrl+Alt+1..9 jump to monitor N; Ctrl+Alt+G opens the grid (`GridHotkey`). Hotkeys register individually (one clash no longer drops the rest).
- Voice monitor commands and grid work even when 'Enable tracking' is off (that toggle now gates head moves only).
- `GridPointerService`: 3x3 numbered, repeatedly-zooming overlay. Digits 1-9 zoom, Enter/Space click, R right-click, D double-click, Backspace/Z back, Tab next monitor, Esc cancel. Voice: "grid", then "one".."nine", "click", "right click", "double click", "back", "cancel", "next monitor".

- Triggers that work with a full-screen remote desktop (Windows App, cloud PC on the right monitor) focused: `TriggerHookService` (low-level keyboard hook for Ctrl+Alt+G / Ctrl+Alt+1..9, swallows the keys; mouse side button `GridMouseButton`, default XButton2, swallowed system-wide; every hit logs `[trigger] ...`), and `HeadTiltGesture` (tilt head >15 deg for 1.2 s toggles the grid). Voice "grid" is the third path.

## In progress
Nothing. NOT yet verified on screen: grid rendering, digit/Enter/voice flow, hysteresis/typing-freeze feel. Build is clean (0 warnings).

## Remaining
- FIRST: test which triggers survive with Windows App focused (check `[trigger]` lines in the log). If the chord never appears, rely on mouse button/tilt/voice.
- Step 2: UIA 'show numbers' mode (local apps, e.g. Claude desktop sidebar); 4x4 first grid level.
- Step 3: voice macros (phrase -> key chord, JSON-configured) for session switching; find Claude desktop's own session shortcuts.
- Verify the grid and freeze behaviour by hand, then tune `TypingFreezeMilliseconds`, `PitchFreezeUnits`.
- Recalibrate monitors in Settings (yaw formula changed slightly).
- Settings UI for the new settings (currently JSON-only).
- "Show numbers" mode: UI Automation clickable elements labelled with numbers (the other half of Apple Voice Control).
- Voice accuracy: SAPI logs show many empty rejects; consider Windows Voice Access (Win+Ctrl+S) or a Vosk/Whisper engine.

## Last session
Added hook-based triggers and head tilt; solution builds, 0 warnings. Not run against the real remote session. Never inject keys from the agent: the user works in a live remote session.

## Next task
User runs the app with Windows App focused and tries: mouse side button, Ctrl+Alt+G, head tilt, voice 'grid'; then 5,5,5, Enter. Done when `[trigger]` and `[grid] Left click` lines appear and the click lands within a few px.

## Decisions & gotchas
- Swallowed Ctrl+Alt+G still lets the remote see Ctrl+Alt down/up (only the G is eaten); harmless so far.
- Hotkey strings use `D1` not `1` (`Enum.TryParse("1")` yields Keys.LButton).
- Grid keyboard input is a WH_KEYBOARD_LL hook installed only while the grid is open; it ignores keys with Ctrl/Alt/Win held.
- Speech events arrive on a background thread; AppHost posts to the captured UI SynchronizationContext before touching forms/hooks.
- Looking at the keyboard fooled the old yaw-nearest router; typing-activity freeze is the main fix, pitch freeze the backup.
