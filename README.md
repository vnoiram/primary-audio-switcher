# Primary Audio Switcher

Windows tray application that monitors the foreground app and running processes, then changes the Windows default render audio device.

It uses Win32 foreground-window APIs and Windows Core Audio COM. No NuGet packages are required.

## Build

### Visual Studio

Open:

```text
PrimaryAudioSwitcher.sln
```

Then build the `Release` configuration. The project targets .NET Framework 4.8 and builds as a Windows tray app.

### PowerShell

Run from PowerShell:

```powershell
.\scripts\build.ps1
```

Output:

```text
dist\PrimaryAudioSwitcher.exe
```

## Run

Start:

```powershell
.\dist\PrimaryAudioSwitcher.exe
```

The first run writes a config file here:

```text
%APPDATA%\PrimaryAudioSwitcher\config.xml
```

Right-click the tray icon to reload or open the config.

Use `Settings` from the tray menu to edit rules. For each rule you can:

Settings are split into tabs:

- `Rules`: rule list and per-rule matching/device behavior.
- `Global`: fallback, exit behavior, polling, watchers, startup, pause, and notifications.
- `Status`: current default render device and log access.

- Select `Foreground app` or `Running process`.
- Pick a process from the currently running process list.
- Use `Browse exe` to select a local executable file.
- Pick an active render audio device from the device list, or type a device-name substring.
- Enable `Use WMI start event` to switch immediately when a configured running-process rule starts.
- Set retry count and retry delay per rule so the selected default device is re-applied shortly after launch.
- Use `Test` to immediately switch to the selected rule's device.
- Choose what happens when a watched process exits: fallback device, previous device, or no action.
- Enable `Start with Windows` to register the app under the current user's Run key.
- See the current default render device in the settings window and tray status.
- Move rules up or down to control priority.
- Pause or resume automation from the tray menu or settings window.
- Enable optional tray balloon notifications when the app changes the default device.
- Export or import the XML config from the tray menu.
- Disable a rule without deleting it.
- Configure an alternate audio device per rule for disconnected primary devices.
- Delay process-exit restore per rule.
- Set a global switch cooldown to avoid rapid device changes while foreground apps change.
- Open the built-in log viewer from the tray menu or settings window.
- Match foreground rules by optional window title substring.
- Enter multiple process names in one rule with `;`, for example `launcher;game`.
- Open `Diagnostics` from the tray menu to inspect foreground process/title, active devices, running processes, and matching rules.
- Restore the previous `config.xml.bak` from the tray menu.
- Re-evaluate rules automatically when Windows reports device connection changes.
- Duplicate rules from the `Rules` tab.
- Fill a rule from the current foreground app with `Use current`.
- Export or import a single rule as XML.
- See currently matching rules highlighted with `>>` in the rule list.
- Review recent switch history in the `Status` tab.

## Config

Example:

```xml
<?xml version="1.0" encoding="utf-8"?>
<PrimaryAudioSwitcher pollMilliseconds="1000" fallbackDevice="" fallbackDeviceId="" log="true" notifications="false" paused="false" processStartWatcher="true" deviceChangeWatcher="true" switchCooldownMilliseconds="0" processExitAction="fallback">
  <Rule name="Game foreground" enabled="true" foregroundProcess="Game.exe" windowTitle="" device="Speakers" deviceId="" alternateDevice="" alternateDeviceId="" retryCount="3" retryDelayMilliseconds="500" exitDelayMilliseconds="0" />
  <Rule name="Discord running" enabled="true" runningProcess="Discord.exe" windowTitle="" device="Headset" deviceId="" alternateDevice="" alternateDeviceId="" retryCount="3" retryDelayMilliseconds="500" exitDelayMilliseconds="0" />
</PrimaryAudioSwitcher>
```

Rules are evaluated top to bottom. `foregroundProcess` has priority over `runningProcess` inside a rule. Process names may include or omit `.exe`.

The settings window saves process names without `.exe`, which matches how Windows reports `Process.ProcessName`.

Process fields accept multiple names separated by `;` or `,`. `windowTitle` is optional and only applies to foreground rules.

`deviceId` is matched first. If it is empty or the endpoint no longer exists, `device` is matched by substring against active Windows render device friendly names or endpoint IDs. Use the tray menu item `Write device list to log`; it writes active render device names to:

If the primary device cannot be found, `alternateDeviceId` / `alternateDevice` is tried before giving up.

```text
%APPDATA%\PrimaryAudioSwitcher\primary-audio-switcher.log
```

`fallbackDevice` is optional. If set, the app switches to it when no rule matches.

When `processStartWatcher` is enabled, the app subscribes to `Win32_ProcessStartTrace` and `Win32_ProcessStopTrace` through WMI. This is not process injection; it only receives Windows process notifications. Start events apply only to `runningProcess` rules, then retry the same default-device change for apps that bind audio shortly after launch.

When `deviceChangeWatcher` is enabled, the app subscribes to `Win32_DeviceChangeEvent` and re-runs rule evaluation after device connection changes.

`processExitAction` supports:

- `fallback`: switch to the configured fallback device.
- `previous`: switch back to the default device that was active before the matched process-start rule.
- `none`: do nothing.

`switchCooldownMilliseconds` applies to normal polling/foreground switches. WMI process-start switches and retries bypass it so launch-time audio binding is not delayed.

## Notes

Changing the Windows default audio endpoint uses the same private Core Audio policy COM interface commonly used by audio switcher tools. It is not a documented Microsoft API, so Windows updates could theoretically change behavior.
