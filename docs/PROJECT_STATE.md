# HeadPointerMvp — project state

Last updated 2026-10-15 by "hands-free pointing: head-jitter fixes, monitor hotkeys, zoom grid"

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

## In progress
Nothing. NOT yet verified on screen: grid rendering, digit/Enter/voice flow, hysteresis/typing-freeze feel. Build is clean (0 warnings).

## Remaining
- Verify the grid and freeze behaviour by hand, then tune `TypingFreezeMilliseconds`, `PitchFreezeUnits`.
- Recalibrate monitors in Settings (yaw formula changed slightly).
- Settings UI for the new settings (currently JSON-only).
- "Show numbers" mode: UI Automation clickable elements labelled with numbers (the other half of Apple Voice Control).
- Voice accuracy: SAPI logs show many empty rejects; consider Windows Voice Access (Win+Ctrl+S) or a Vosk/Whisper engine.

## Last session
Implemented the above; solution builds. Injected-key smoke test did not trigger the hotkey from the agent shell (and was aborted: user was in a live remote session). No commit made.

## Next task
Manually run the app: Ctrl+Alt+G, press 5,5,5, Enter; confirm click lands within a few px. Done when logs show `[grid] Left click at x,y` and the click hits the intended target.

## Decisions & gotchas
- Hotkey strings use `D1` not `1` (`Enum.TryParse("1")` yields Keys.LButton).
- Grid keyboard input is a WH_KEYBOARD_LL hook installed only while the grid is open; it ignores keys with Ctrl/Alt/Win held.
- Speech events arrive on a background thread; AppHost posts to the captured UI SynchronizationContext before touching forms/hooks.
- Looking at the keyboard fooled the old yaw-nearest router; typing-activity freeze is the main fix, pitch freeze the backup.
