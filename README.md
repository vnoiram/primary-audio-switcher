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

- Select `Foreground app` or `Running process`.
- Pick a process from the currently running process list.
- Use `Browse exe` to select a local executable file.
- Pick an active render audio device from the device list, or type a device-name substring.

## Config

Example:

```xml
<?xml version="1.0" encoding="utf-8"?>
<PrimaryAudioSwitcher pollMilliseconds="1000" fallbackDevice="" log="true">
  <Rule name="Game foreground" foregroundProcess="Game.exe" device="Speakers" />
  <Rule name="Discord running" runningProcess="Discord.exe" device="Headset" />
</PrimaryAudioSwitcher>
```

Rules are evaluated top to bottom. `foregroundProcess` has priority over `runningProcess` inside a rule. Process names may include or omit `.exe`.

The settings window saves process names without `.exe`, which matches how Windows reports `Process.ProcessName`.

`device` is matched by substring against active Windows render device friendly names or endpoint IDs. Use the tray menu item `Write device list to log`; it writes active render device names to:

```text
%APPDATA%\PrimaryAudioSwitcher\primary-audio-switcher.log
```

`fallbackDevice` is optional. If set, the app switches to it when no rule matches.

## Notes

Changing the Windows default audio endpoint uses the same private Core Audio policy COM interface commonly used by audio switcher tools. It is not a documented Microsoft API, so Windows updates could theoretically change behavior.
