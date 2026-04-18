# ThinkPadKbBacklight

Tiny Windows tray app that keeps the ThinkPad keyboard backlight **on while you're using the machine**, then turns it off after an idle timeout. Any keyboard, mouse, or trackpad activity brings it back and restarts the timer.

Targets recent ThinkPads (X1 Carbon Gen 9, etc.). Tries two backends automatically:

1. **Lenovo Vantage's `Keyboard_Core.dll`** (if present — searched under `Program Files`, `Program Files (x86)`, and `WindowsApps`)
2. **Legacy `\\.\IBMPmDrv`** kernel driver (still present on many recent ThinkPads, including X1 Carbon Gen 9)

Whichever one works wins. The diagnostic report tells you which backend is active.

## What this is vs. what it isn't

- ✅ Idle-off + activity-wake timer for the keyboard backlight
- ✅ Configurable timeout, "on" level (1 = low, 2 = high), and a pause toggle
- ✅ First-run diagnostic dump so we can debug without a round trip
- ❌ Not an ambient light / auto screen brightness tool (that was the extra feature in the 2020 pspatel repo; left out here on purpose)

## Requirements

- Windows 10 / 11 (including LTSC / IoT Enterprise)
- .NET Framework 4.8 (preinstalled on Win10 1903+)
- Either Lenovo Vantage / ThinkPad Hotkey Features installed **or** the legacy Lenovo Power Manager driver (ships with factory Windows images; exposes `\\.\IBMPmDrv`)

## Install

1. Download `ThinkPadKbBacklight.exe` from the Releases page (or the Actions artifact).
2. Put it anywhere, e.g. `%LocalAppData%\ThinkPadKbBacklight\`.
3. Double-click to run. A tray icon appears.
4. On **first run**, a diagnostic report is written to the Desktop and opened in Notepad. During this, the backlight will cycle off → low → high → off. That's expected — it's confirming what levels actually work.
5. To autostart, press `Win+R` → `shell:startup` → drop a shortcut in the folder.

> SmartScreen: the binary is unsigned, so Windows will warn on first run. Click "More info" → "Run anyway" if you trust the source.

## Tray menu

- **Status** — current state
- **Pause** — stop reacting to activity and leave the backlight at your "on" level (useful for presentations)
- **Timeout** — pick 10 s … 10 min
- **Remember previous level** — when on (default), the app reads the current backlight level right before turning off, then restores to that on wake. Lets you set a dimmer level via `Fn+Space` and have it stick. When off, wake always goes to the `OnLevel` in config.
- **Ignore external input** — when on, only the built-in keyboard / TrackPoint / TouchPad reset the idle timer. External USB mice and keyboards are ignored, so scrolling an external mouse while reading will not wake the backlight. Off by default (uses Windows' system-wide idle timer, which sees any input).
- **Run diagnostics…** — regenerate the report on the Desktop
- **Open config folder** — shows `%AppData%\ThinkPadKbBacklight\config.json`
- **Exit**

## Config file

`%AppData%\ThinkPadKbBacklight\config.json`:

```json
{
  "TimeoutSeconds": 30,
  "OnLevel": 2,
  "OffLevel": 0,
  "Paused": false,
  "RestorePreviousLevel": true,
  "IgnoreExternalDevices": false,
  "InternalDeviceMarkers": null
}
```

Levels: 0 = off, 1 = low, 2 = high. Edit and relaunch.

`RestorePreviousLevel` controls whether the app tracks the level you had before idle and restores to it on wake. `OnLevel` is only used when this is `false` (or as the initial wake level at startup if the backlight is currently off).

`IgnoreExternalDevices` flips the idle monitor between two modes:

- **off** (default): `GetLastInputInfo` — the system-wide idle clock. Any input, anywhere, counts.
- **on**: RawInput-based, per-device. Only devices whose raw-input path matches one of the `InternalDeviceMarkers` substrings (case-insensitive) reset the idle timer.

`InternalDeviceMarkers` is a list of substrings matched against the raw-input device name. `null` (default) means use the built-in list: `["ACPI\\", "LEN", "VID_17EF", "ELAN", "SYNA"]`. That covers PS/2 built-in keyboard + TrackPoint, Lenovo-branded HIDs, and Synaptics/Elan touchpads (PnP IDs like `ELAN0672`, `SYNA3299`), which is what ships on modern ThinkPads. Run diagnostics to see how your devices classify, and add markers if something is misclassified.

## Diagnostics report

On first run (or via the tray menu), the app writes `ThinkPadKbBacklight-diagnostic-*.txt` to the Desktop. It records:

- Windows version, model, BIOS
- Where `Keyboard_Core.dll` lives (searches Program Files, Program Files (x86), WindowsApps)
- Whether the legacy `\\.\IBMPmDrv` device opens
- Raw input devices (keyboard/mouse) visible to the OS
- Current backlight level (if readable)
- Off → low → high → off cycle test with OK/FAIL per step

If the backlight did **not** visibly change during the cycle test, send the report — it tells us exactly what to try next.

## Build from source

Windows + Visual Studio 2022 (or Build Tools) with the .NET desktop workload:

```powershell
msbuild ThinkPadKbBacklight.sln /p:Configuration=Release /p:Platform=x86
```

Output: `ThinkPadKbBacklight\bin\Release\ThinkPadKbBacklight.exe`

CI builds on every push to `main`; tagging `vX.Y.Z` creates a GitHub Release with the zipped binary attached.

## Why not just use / fix the original repo?

[pspatel321/auto-backlight-for-thinkpad](https://github.com/pspatel321/auto-backlight-for-thinkpad) last shipped in May 2020. Its open [PR #8](https://github.com/pspatel321/auto-backlight-for-thinkpad/pull/8) fixes raw-input device detection on newer ThinkPads, but the underlying backlight control still goes through the legacy `IbmPmDrv` kernel driver that ships with old Power Manager — no longer installed on Gen 9+. This project sidesteps that by going through the Vantage DLL instead, which is the path Lenovo's own software uses on modern models.

## License

GPL-3.0. See [LICENSE](LICENSE).
