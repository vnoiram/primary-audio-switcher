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

- `Rules`: rule list, search/filter, profile, per-rule matching, device behavior, and app session volume/mute.
- `Global`: fallback, exit behavior, polling, watchers, startup, pause, profiles, output roles, failure notifications, and undo.
- `Status`: current default render device and log access.

- Select `Foreground app` or `Running process`.
- Assign rules to a profile and choose the active profile from `Global`; `All` evaluates every profile.
- Temporarily disable a rule for 30 minutes or until the app exits.
- Pick a process from the currently running process list.
- Use `Browse exe` to select a local executable file.
- Pick an active render audio device from the device list, or type a device-name substring.
- Define device aliases in `Global` with `Alias=Device substring` lines, then use the alias as a rule device.
- Choose which Windows default endpoint roles are changed: Console, Multimedia, and/or Communications.
- Enable switch-failure notifications separately from normal change notifications.
- Suppress notifications during quiet hours.
- Rotate the log when it reaches the configured size.
- Filter rules by name, profile, process, title, or device.
- Undo settings-window changes back to the values loaded when the dialog opened.
- Set per-app audio session volume and mute state when a rule applies.
- Use `Dry run` or tray `Preview rule match` to see which rule would apply without switching.
- Validate rules to detect duplicate match conditions and lower-priority rules hidden by earlier rules.
- Override exit restore behavior per rule: global setting, fallback, previous device, or no action.
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
- Restore the newest timestamped config backup from the tray menu. The app keeps the latest five `config.*.bak.xml` files.
- Re-evaluate rules automatically when Windows reports device connection changes.
- Re-evaluate rules manually from the tray menu.
- Select or cycle the current output device or active profile from the tray menu.
- Use global hotkeys: `Ctrl+Alt+P` pause/resume, `Ctrl+Alt+R` re-evaluate, `Ctrl+Alt+D` cycle device, `Ctrl+Alt+O` cycle profile.
- Duplicate rules from the `Rules` tab.
- Fill a rule from the current foreground app with `Use current`.
- Export or import a single rule as XML.
- See currently matching rules highlighted with `>>` in the rule list.
- Review recent switch history in the `Status` tab.
- Validate rules from the `Rules` tab to catch missing processes, duplicate names, and missing devices.
- Save a diagnostics report from the `Status` tab.
- Clear switch history from the `Status` tab.
- Open the config folder directly from the tray menu.
- Open an `About` dialog from the tray menu.

## Config

Example:

```xml
<?xml version="1.0" encoding="utf-8"?>
<PrimaryAudioSwitcher pollMilliseconds="1000" fallbackDevice="" fallbackDeviceId="" log="true" logMaxKilobytes="1024" notifications="false" notificationQuietHours="false" notificationQuietStart="22:00" notificationQuietEnd="07:00" notifyFailures="true" paused="false" processStartWatcher="true" deviceChangeWatcher="true" roleConsole="true" roleMultimedia="true" roleCommunications="true" activeProfile="Default" switchCooldownMilliseconds="0" processExitAction="fallback">
  <DeviceAlias alias="Headset" device="Headset" deviceId="" />
  <Rule name="Game foreground" profile="Default" enabled="true" foregroundProcess="Game.exe" windowTitle="" device="Speakers" deviceId="" alternateDevice="" alternateDeviceId="" retryCount="3" retryDelayMilliseconds="500" exitDelayMilliseconds="0" exitAction="global" disabledUntilUtc="" sessionVolumeEnabled="false" sessionVolumePercent="100" sessionMuteEnabled="false" sessionMuted="false" />
  <Rule name="Discord running" profile="Default" enabled="true" runningProcess="Discord.exe" windowTitle="" device="Headset" deviceId="" alternateDevice="" alternateDeviceId="" retryCount="3" retryDelayMilliseconds="500" exitDelayMilliseconds="0" sessionVolumeEnabled="false" sessionVolumePercent="100" sessionMuteEnabled="false" sessionMuted="false" />
</PrimaryAudioSwitcher>
```

Rules in the active profile are evaluated top to bottom. `activeProfile="All"` evaluates every rule. `foregroundProcess` has priority over `runningProcess` inside a rule. Process names may include or omit `.exe`.

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

`roleConsole`, `roleMultimedia`, and `roleCommunications` control which Windows default render roles are updated. At least one role must be enabled for switching to occur.

Per-rule `sessionVolumeEnabled` / `sessionVolumePercent` and `sessionMuteEnabled` / `sessionMuted` are applied to matching process audio sessions after a successful device switch. Windows only exposes sessions that already exist, so apps may need to be producing audio before session volume or mute can be changed.

`DeviceAlias` maps a stable alias to a device substring or endpoint ID. Rules can use the alias in `device` or `alternateDevice`, which helps when Windows device names change.

Per-rule `exitAction` accepts `global`, `fallback`, `previous`, or `none`. `disabledUntilUtc` can temporarily suppress a rule until a UTC timestamp; the Settings window also supports a restart-only temporary disable that is not written to XML.

`notificationQuietHours`, `notificationQuietStart`, and `notificationQuietEnd` suppress tray balloons during the configured local-time window. `logMaxKilobytes` rotates the app log to `.1` before appending once the file grows past the limit.

Config saves are written through a temporary file and then replaced, so interruption during save is less likely to leave a partial XML file.

## Notes

Changing the Windows default audio endpoint uses the same private Core Audio policy COM interface commonly used by audio switcher tools. It is not a documented Microsoft API, so Windows updates could theoretically change behavior.

XML config and rule imports are loaded with DTD processing prohibited, external XML resolution disabled, and a 1 MB document size limit.
