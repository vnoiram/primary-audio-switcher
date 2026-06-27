using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;

namespace PrimaryAudioSwitcher
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, "PrimaryAudioSwitcher.SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("Primary Audio Switcher is already running.", "Primary Audio Switcher",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApplicationContext(args));
            }
        }
    }

    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly string _configPath;
        private readonly AudioDeviceManager _audio = new AudioDeviceManager();
        private readonly NotifyIcon _notifyIcon;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Control _uiThread = new Control();
        private ManagementEventWatcher _processStartWatcher;
        private ManagementEventWatcher _processStopWatcher;
        private ManagementEventWatcher _deviceChangeWatcher;
        private readonly Dictionary<string, string> _previousDeviceByRule = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<SwitchHistoryItem> _switchHistory = new List<SwitchHistoryItem>();
        private AppConfig _config;
        private string _lastAppliedDeviceId;
        private string _lastStatus = "Starting";
        private DateTimeOffset _lastSwitchAt = DateTimeOffset.MinValue;
        private bool _paused;

        public TrayApplicationContext(string[] args)
        {
            _configPath = GetConfigPath(args);
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath));
            if (!File.Exists(_configPath))
            {
                File.WriteAllText(_configPath, AppConfig.DefaultXml, Encoding.UTF8);
            }

            _config = AppConfig.Load(_configPath);
            _uiThread.CreateControl();
            if (_uiThread.Handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to initialize UI message target.");
            }
            _notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "Primary Audio Switcher",
                Visible = true,
                ContextMenuStrip = BuildMenu()
            };

            _timer = new System.Windows.Forms.Timer { Interval = Math.Max(250, _config.PollMilliseconds) };
            _timer.Tick += (sender, eventArgs) => EvaluateRules();
            _timer.Start();
            _paused = _config.Paused;
            HotKeyManager.Register(_uiThread, TogglePause, ManualEvaluate, CycleOutputDevice, CycleActiveProfile);
            StartWatchers();
            EvaluateRules();
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            var status = new ToolStripMenuItem("Status: Starting") { Enabled = false, Name = "status" };
            menu.Items.Add(status);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Pause automation", null, (sender, args) => TogglePause()).Name = "pause";
            menu.Items.Add("Re-evaluate now", null, (sender, args) => ManualEvaluate());
            menu.Items.Add("Preview rule match", null, (sender, args) => PreviewRuleMatch());
            menu.Items.Add("Cycle active profile", null, (sender, args) => CycleActiveProfile());
            menu.Items.Add("Cycle output device", null, (sender, args) => CycleOutputDevice());
            menu.Items.Add("Settings", null, (sender, args) => OpenSettings());
            menu.Items.Add("Reload config", null, (sender, args) => ReloadConfig());
            menu.Items.Add("Open config", null, (sender, args) => Process.Start("notepad.exe", _configPath));
            menu.Items.Add("Open config folder", null, (sender, args) => Process.Start("explorer.exe", Path.GetDirectoryName(_configPath)));
            menu.Items.Add("View log", null, (sender, args) => ViewLog());
            menu.Items.Add("Diagnostics", null, (sender, args) => ViewDiagnostics());
            menu.Items.Add("Write device list to log", null, (sender, args) => WriteDeviceList());
            menu.Items.Add("Export config", null, (sender, args) => ExportConfig());
            menu.Items.Add("Import config", null, (sender, args) => ImportConfig());
            menu.Items.Add("Restore config backup", null, (sender, args) => RestoreConfigBackup());
            menu.Items.Add("About", null, (sender, args) => ShowAbout());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (sender, args) => ExitThread());
            return menu;
        }

        private void EvaluateRules()
        {
            try
            {
                if (_paused)
                {
                    SetStatus("Paused. Current: " + CurrentDeviceName());
                    return;
                }

                var foregroundInfo = ForegroundWindowReader.GetForegroundWindowInfo();
                var foreground = foregroundInfo.ProcessName;
                var running = new HashSet<string>(
                    Process.GetProcesses()
                        .Select(p => SafeProcessName(p))
                        .Where(name => !string.IsNullOrWhiteSpace(name)),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var rule in _config.Rules)
                {
                    if (!_config.IsRuleInActiveProfile(rule))
                    {
                        continue;
                    }

                    if (rule.IsTemporarilyDisabled)
                    {
                        continue;
                    }

                    if (!rule.IsMatch(foregroundInfo, running))
                    {
                        continue;
                    }

                    ApplyDevice(rule, foreground, false, false);
                    return;
                }

                if (_config.HasFallbackDevice)
                {
                    ApplyDevice(_config.FallbackDeviceId, _config.FallbackDevice, "fallback", foreground, false);
                    return;
                }

                SetStatus("No matching rule. Foreground: " + (foreground ?? "unknown"));
            }
            catch (Exception ex)
            {
                Log("ERROR " + ex);
                SetStatus("Error: " + ex.Message);
            }
        }

        private void ApplyDevice(AudioRule rule, string foreground, bool force, bool rememberPrevious)
        {
            if (rememberPrevious)
            {
                var current = _audio.GetDefaultRenderDevice();
                if (current != null)
                {
                    var process = rule.TargetProcess;
                    if (!string.IsNullOrWhiteSpace(process) && !_previousDeviceByRule.ContainsKey(rule.Name ?? ""))
                    {
                        _previousDeviceByRule[rule.Name ?? ""] = current.Id;
                    }
                }
            }

            if (ApplyDevice(rule.DeviceId, rule.Device, rule.AlternateDeviceId, rule.AlternateDevice, rule.Name, foreground, force))
            {
                _audio.ApplySessionSettings(rule.TargetProcesses, rule.SessionVolumeEnabled, rule.SessionVolumePercent, rule.SessionMuteEnabled, rule.SessionMuted);
            }
        }

        private bool ApplyDevice(string deviceId, string deviceMatch, string ruleName, string foreground, bool force)
        {
            return ApplyDevice(deviceId, deviceMatch, null, null, ruleName, foreground, force);
        }

        private bool ApplyDevice(string deviceId, string deviceMatch, string alternateDeviceId, string alternateDeviceMatch, string ruleName, string foreground, bool force)
        {
            var device = _audio.FindRenderDevice(deviceId, deviceMatch, _config.DeviceAliases);
            if (device == null)
            {
                if (!string.IsNullOrWhiteSpace(alternateDeviceId) || !string.IsNullOrWhiteSpace(alternateDeviceMatch))
                {
                    device = _audio.FindRenderDevice(alternateDeviceId, alternateDeviceMatch, _config.DeviceAliases);
                    if (device != null)
                    {
                        Log("Using alternate device for rule '" + ruleName + "': " + device.Name);
                    }
                }
            }
            if (device == null)
            {
                SetStatus("Device not found: " + deviceMatch);
                Log("Device not found for rule '" + ruleName + "': " + deviceMatch);
                ShowFailureNotification("Audio device not found", (deviceMatch ?? "unknown") + " (" + ruleName + ")");
                return false;
            }

            if (!force && IsSwitchCooldownActive())
            {
                SetStatus("Cooldown active. Current: " + CurrentDeviceName());
                return false;
            }

            if (!force && string.Equals(_lastAppliedDeviceId, device.Id, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Rule: " + ruleName + " -> " + device.Name);
                return true;
            }

            if (!_config.AnyOutputRoleEnabled)
            {
                SetStatus("No output roles enabled");
                ShowFailureNotification("Audio switch skipped", "No output roles are enabled.");
                return false;
            }

            try
            {
                _audio.SetDefaultRenderDevice(device.Id, _config.RoleConsole, _config.RoleMultimedia, _config.RoleCommunications);
                _lastAppliedDeviceId = device.Id;
                _lastSwitchAt = DateTimeOffset.Now;
                AddSwitchHistory(ruleName, device.Name, foreground, force);
                SetStatus("Rule: " + ruleName + " -> " + device.Name);
                ShowNotification("Audio device changed", "Rule: " + ruleName + Environment.NewLine + "Device: " + device.Name + Environment.NewLine + "Trigger: " + (foreground ?? "unknown"));
                Log("Applied rule='" + ruleName + "' foreground='" + (foreground ?? "unknown") + "' device='" + device.Name + "'" + (force ? " force=true" : ""));
                return true;
            }
            catch (Exception ex)
            {
                SetStatus("Switch failed: " + ex.Message);
                Log("Switch failed for rule '" + ruleName + "': " + ex);
                ShowFailureNotification("Audio switch failed", device.Name + " (" + ruleName + ")");
                return false;
            }
        }

        private void StartWatchers()
        {
            StartProcessStartWatcher();
            StartDeviceChangeWatcher();
        }

        private void StartProcessStartWatcher()
        {
            StopProcessStartWatcher();
            if (!_config.ProcessStartWatcherEnabled)
            {
                return;
            }

            try
            {
                _processStartWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
                _processStartWatcher.EventArrived += ProcessStartWatcherOnEventArrived;
                _processStartWatcher.Start();
                _processStopWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace"));
                _processStopWatcher.EventArrived += ProcessStopWatcherOnEventArrived;
                _processStopWatcher.Start();
                Log("Process start watcher enabled");
            }
            catch (Exception ex)
            {
                Log("Process start watcher failed: " + ex);
                SetStatus("WMI watcher failed: " + ex.Message);
            }
        }

        private void StopProcessStartWatcher()
        {
            if (_processStartWatcher == null)
            {
                StopWatcher(ref _processStopWatcher, ProcessStopWatcherOnEventArrived);
                return;
            }

            StopWatcher(ref _processStartWatcher, ProcessStartWatcherOnEventArrived);
            StopWatcher(ref _processStopWatcher, ProcessStopWatcherOnEventArrived);
        }

        private static void StopWatcher(ref ManagementEventWatcher watcher, EventArrivedEventHandler handler)
        {
            if (watcher == null)
            {
                return;
            }

            try
            {
                watcher.EventArrived -= handler;
                watcher.Stop();
            }
            catch
            {
            }
            finally
            {
                watcher.Dispose();
                watcher = null;
            }
        }

        private void StartDeviceChangeWatcher()
        {
            StopWatcher(ref _deviceChangeWatcher, DeviceChangeWatcherOnEventArrived);
            if (!_config.DeviceChangeWatcherEnabled)
            {
                return;
            }

            try
            {
                _deviceChangeWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent"));
                _deviceChangeWatcher.EventArrived += DeviceChangeWatcherOnEventArrived;
                _deviceChangeWatcher.Start();
                Log("Device change watcher enabled");
            }
            catch (Exception ex)
            {
                Log("Device change watcher failed: " + ex);
            }
        }

        private void DeviceChangeWatcherOnEventArrived(object sender, EventArrivedEventArgs args)
        {
            if (_uiThread.IsDisposed)
            {
                return;
            }

            try
            {
                _uiThread.BeginInvoke((MethodInvoker)delegate
                {
                    Log("Device change event received");
                    _lastAppliedDeviceId = null;
                    EvaluateRules();
                });
            }
            catch
            {
            }
        }

        private void ProcessStartWatcherOnEventArrived(object sender, EventArrivedEventArgs args)
        {
            var value = args.NewEvent.Properties["ProcessName"].Value;
            var processName = value == null ? null : value.ToString();
            if (string.IsNullOrWhiteSpace(processName) || _uiThread.IsDisposed)
            {
                return;
            }

            try
            {
                _uiThread.BeginInvoke((MethodInvoker)delegate { EvaluateProcessStart(processName); });
            }
            catch
            {
            }
        }

        private void ProcessStopWatcherOnEventArrived(object sender, EventArrivedEventArgs args)
        {
            var value = args.NewEvent.Properties["ProcessName"].Value;
            var processName = value == null ? null : value.ToString();
            if (string.IsNullOrWhiteSpace(processName) || _uiThread.IsDisposed)
            {
                return;
            }

            try
            {
                _uiThread.BeginInvoke((MethodInvoker)delegate { EvaluateProcessStop(processName); });
            }
            catch
            {
            }
        }

        private void EvaluateProcessStart(string processName)
        {
            if (_paused)
            {
                return;
            }

            var normalized = NormalizeProcess(processName);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            foreach (var rule in _config.Rules)
            {
                if (!_config.IsRuleInActiveProfile(rule))
                {
                    continue;
                }

                if (rule.IsTemporarilyDisabled)
                {
                    continue;
                }

                if (!rule.MatchesRunningProcess(normalized))
                {
                    continue;
                }

                Log("Process start matched rule='" + rule.Name + "' process='" + normalized + "'");
                ApplyDevice(rule, normalized, true, true);
                ScheduleProcessStartRetries(rule, normalized);
                return;
            }
        }

        private void EvaluateProcessStop(string processName)
        {
            if (_paused)
            {
                return;
            }

            var normalized = NormalizeProcess(processName);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            var matchingRules = _config.Rules
                .Where(rule => _config.IsRuleInActiveProfile(rule) && !rule.IsTemporarilyDisabled && rule.MatchesRunningProcess(normalized))
                .ToList();
            if (matchingRules.Count == 0)
            {
                return;
            }

            var exitAction = ResolveExitAction(matchingRules);
            if (exitAction == ProcessExitAction.None)
            {
                return;
            }

            if (matchingRules.Any(rule => rule.RunningProcesses.Any(process =>
                    !string.Equals(process, normalized, StringComparison.OrdinalIgnoreCase) && IsProcessRunning(process))))
            {
                Log("Process stop ignored because related process is still running: " + normalized);
                return;
            }

            var delay = matchingRules.Max(rule => Math.Max(0, rule.ExitDelayMilliseconds));
            if (delay > 0)
            {
                ScheduleProcessExitRestore(normalized, delay, exitAction);
                return;
            }

            RestoreAfterProcessStop(normalized, exitAction);
        }

        private void ScheduleProcessExitRestore(string processName, int delayMilliseconds, ProcessExitAction exitAction)
        {
            var restoreTimer = new System.Windows.Forms.Timer { Interval = delayMilliseconds };
            restoreTimer.Tick += delegate
            {
                restoreTimer.Stop();
                restoreTimer.Dispose();
                if (!IsProcessRunning(processName))
                {
                    RestoreAfterProcessStop(processName, exitAction);
                }
            };
            restoreTimer.Start();
        }

        private void RestoreAfterProcessStop(string normalized, ProcessExitAction exitAction)
        {
            string previousDeviceId;
            AudioRule previousRule = null;
            previousDeviceId = null;
            if (exitAction == ProcessExitAction.PreviousDevice)
            {
                foreach (var rule in _config.Rules)
                {
                    if (!rule.IsTemporarilyDisabled && rule.MatchesRunningProcess(normalized) &&
                        _previousDeviceByRule.TryGetValue(rule.Name ?? "", out previousDeviceId))
                    {
                        previousRule = rule;
                        break;
                    }
                }
            }
            if (previousRule != null)
            {
                ApplyDevice(previousDeviceId, null, "restore previous after " + normalized, normalized, true);
                _previousDeviceByRule.Remove(previousRule.Name ?? "");
                return;
            }

            if (exitAction == ProcessExitAction.FallbackDevice && _config.HasFallbackDevice)
            {
                ApplyDevice(_config.FallbackDeviceId, _config.FallbackDevice, "restore fallback after " + normalized, normalized, true);
            }
        }

        private ProcessExitAction ResolveExitAction(IReadOnlyList<AudioRule> matchingRules)
        {
            foreach (var rule in matchingRules)
            {
                if (rule.ExitActionOverride.HasValue)
                {
                    return rule.ExitActionOverride.Value;
                }
            }

            return _config.ProcessExitAction;
        }

        private bool IsSwitchCooldownActive()
        {
            return _config.SwitchCooldownMilliseconds > 0 &&
                   DateTimeOffset.Now - _lastSwitchAt < TimeSpan.FromMilliseconds(_config.SwitchCooldownMilliseconds);
        }

        private void ScheduleProcessStartRetries(AudioRule rule, string processName)
        {
            var retryCount = Math.Max(0, rule.RetryCount);
            var retryDelay = Math.Max(100, rule.RetryDelayMilliseconds);
            for (var i = 1; i <= retryCount; i++)
            {
                var retryIndex = i;
                var retryRule = rule.Clone();
                var retryTimer = new System.Windows.Forms.Timer { Interval = retryDelay * retryIndex };
                retryTimer.Tick += delegate
                {
                    retryTimer.Stop();
                    retryTimer.Dispose();
                    if (IsProcessRunning(processName))
                    {
                        ApplyDevice(retryRule.DeviceId, retryRule.Device, retryRule.AlternateDeviceId, retryRule.AlternateDevice, retryRule.Name + " (process start retry " + retryIndex + ")", processName, true);
                    }
                };
                retryTimer.Start();
            }
        }

        private void ManualEvaluate()
        {
            _lastAppliedDeviceId = null;
            EvaluateRules();
        }

        private void PreviewRuleMatch()
        {
            MessageBox.Show(RulePreview.Build(_config, _audio), "Primary Audio Switcher Preview", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void CycleActiveProfile()
        {
            var profiles = _config.ProfileNames.ToList();
            if (profiles.Count == 0)
            {
                return;
            }

            var current = profiles.FindIndex(profile => profile.Equals(_config.ActiveProfile ?? "Default", StringComparison.OrdinalIgnoreCase));
            _config.ActiveProfile = profiles[(current + 1 + profiles.Count) % profiles.Count];
            _config.Save(_configPath);
            SetStatus("Profile: " + _config.ActiveProfile);
            EvaluateRules();
        }

        private void CycleOutputDevice()
        {
            try
            {
                var devices = _audio.ListRenderDevices();
                if (devices.Count == 0)
                {
                    return;
                }

                var current = _audio.GetDefaultRenderDevice();
                var currentIndex = current == null ? -1 : devices.FindIndex(device => device.Id.Equals(current.Id, StringComparison.OrdinalIgnoreCase));
                var next = devices[(currentIndex + 1 + devices.Count) % devices.Count];
                ApplyDevice(next.Id, next.Name, "manual cycle", "tray", true);
            }
            catch (Exception ex)
            {
                Log("Manual device cycle failed: " + ex);
                ShowFailureNotification("Audio device cycle failed", ex.Message);
            }
        }

        private void ReloadConfig()
        {
            try
            {
                _config = AppConfig.Load(_configPath);
                _timer.Interval = Math.Max(250, _config.PollMilliseconds);
                StartWatchers();
                _lastAppliedDeviceId = null;
                _paused = _config.Paused;
                SetStatus("Config reloaded");
                EvaluateRules();
            }
            catch (Exception ex)
            {
                Log("Config reload failed: " + ex);
                MessageBox.Show(ex.Message, "Primary Audio Switcher config error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenSettings()
        {
            using (var form = new SettingsForm(_config, _audio, _switchHistory))
            {
                if (form.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                form.Config.Save(_configPath);
                _config = form.Config;
                _timer.Interval = Math.Max(250, _config.PollMilliseconds);
                StartWatchers();
                _lastAppliedDeviceId = null;
                _paused = _config.Paused;
                SetStatus("Settings saved");
                EvaluateRules();
            }
        }

        private void TogglePause()
        {
            _paused = !_paused;
            _config.Paused = _paused;
            _config.Save(_configPath);
            SetPauseMenuText();
            SetStatus(_paused ? "Paused. Current: " + CurrentDeviceName() : "Resumed");
            if (!_paused)
            {
                EvaluateRules();
            }
        }

        private void SetPauseMenuText()
        {
            var item = _notifyIcon.ContextMenuStrip.Items["pause"] as ToolStripMenuItem;
            if (item != null)
            {
                item.Text = _paused ? "Resume automation" : "Pause automation";
            }
        }

        private void ExportConfig()
        {
            try
            {
                using (var dialog = new SaveFileDialog())
                {
                    dialog.Filter = "XML config (*.xml)|*.xml|All files (*.*)|*.*";
                    dialog.FileName = "primary-audio-switcher-config.xml";
                    if (dialog.ShowDialog() == DialogResult.OK)
                    {
                        _config.Save(_configPath);
                        File.Copy(_configPath, dialog.FileName, true);
                        SetStatus("Config exported");
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Config export failed: " + ex);
                MessageBox.Show(ex.Message, "Primary Audio Switcher export error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ImportConfig()
        {
            try
            {
                using (var dialog = new OpenFileDialog())
                {
                    dialog.Filter = "XML config (*.xml)|*.xml|All files (*.*)|*.*";
                    if (dialog.ShowDialog() != DialogResult.OK)
                    {
                        return;
                    }

                    var imported = AppConfig.Load(dialog.FileName);
                    imported.Save(_configPath);
                    _config = imported;
                    _timer.Interval = Math.Max(250, _config.PollMilliseconds);
                    _paused = _config.Paused;
                    StartWatchers();
                    SetPauseMenuText();
                    SetStatus("Config imported");
                    EvaluateRules();
                }
            }
            catch (Exception ex)
            {
                Log("Config import failed: " + ex);
                MessageBox.Show(ex.Message, "Primary Audio Switcher import error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void WriteDeviceList()
        {
            var lines = _audio.ListRenderDevices()
                .Select(d => d.Name + Environment.NewLine + "  " + d.Id);
            Log("Active render devices:" + Environment.NewLine + string.Join(Environment.NewLine, lines));
            SetStatus("Device list written to log");
        }

        private void ViewLog()
        {
            var path = LogPath();
            using (var form = new LogViewerForm(path))
            {
                form.ShowDialog();
            }
        }

        private void ViewDiagnostics()
        {
            using (var form = new DiagnosticsForm(_config, _audio, _lastStatus))
            {
                form.ShowDialog();
            }
        }

        private void RestoreConfigBackup()
        {
            var backupPath = ConfigBackups.LatestBackupPath(_configPath);
            if (!File.Exists(backupPath))
            {
                MessageBox.Show("Backup config was not found.", "Primary Audio Switcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                File.Copy(backupPath, _configPath, true);
                _config = AppConfig.Load(_configPath);
                _paused = _config.Paused;
                _timer.Interval = Math.Max(250, _config.PollMilliseconds);
                StartWatchers();
                SetStatus("Config backup restored");
                EvaluateRules();
            }
            catch (Exception ex)
            {
                Log("Config backup restore failed: " + ex);
                MessageBox.Show(ex.Message, "Primary Audio Switcher restore error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowAbout()
        {
            MessageBox.Show(
                "Primary Audio Switcher" + Environment.NewLine +
                "Windows tray app for rule-based default audio device switching." + Environment.NewLine +
                Environment.NewLine +
                "Dependencies: .NET Framework / Windows Core Audio / WMI",
                "About Primary Audio Switcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void SetStatus(string status)
        {
            _lastStatus = status;
            _notifyIcon.Text = ("Primary Audio Switcher - " + status).Substring(0, Math.Min(63, ("Primary Audio Switcher - " + status).Length));
            var item = _notifyIcon.ContextMenuStrip.Items["status"] as ToolStripMenuItem;
            if (item != null)
            {
                item.Text = "Status: " + status + " | Current: " + CurrentDeviceName();
            }
            SetPauseMenuText();
        }

        private string CurrentDeviceName()
        {
            try
            {
                var current = _audio.GetDefaultRenderDevice();
                return current == null ? "unknown" : current.Name;
            }
            catch
            {
                return "unknown";
            }
        }

        private void ShowNotification(string title, string text)
        {
            if (!_config.NotificationsEnabled)
            {
                return;
            }

            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = text;
            _notifyIcon.ShowBalloonTip(2000);
        }

        private void ShowFailureNotification(string title, string text)
        {
            if (!_config.NotifyFailures)
            {
                return;
            }

            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = text;
            _notifyIcon.ShowBalloonTip(3000);
        }

        private void AddSwitchHistory(string ruleName, string deviceName, string trigger, bool forced)
        {
            _switchHistory.Insert(0, new SwitchHistoryItem(DateTimeOffset.Now, ruleName, deviceName, trigger, forced));
            while (_switchHistory.Count > 50)
            {
                _switchHistory.RemoveAt(_switchHistory.Count - 1);
            }
        }

        private void Log(string message)
        {
            if (!_config.LogEnabled)
            {
                return;
            }

            File.AppendAllText(LogPath(), DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
        }

        private string LogPath()
        {
            return Path.Combine(Path.GetDirectoryName(_configPath), "primary-audio-switcher.log");
        }

        protected override void ExitThreadCore()
        {
            HotKeyManager.Unregister(_uiThread);
            StopProcessStartWatcher();
            StopWatcher(ref _deviceChangeWatcher, DeviceChangeWatcherOnEventArrived);
            _timer.Stop();
            _timer.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _uiThread.Dispose();
            base.ExitThreadCore();
        }

        private static bool IsProcessRunning(string processName)
        {
            var normalized = NormalizeProcess(processName);
            return Process.GetProcesses()
                .Select(p => SafeProcessName(p))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Any(name => string.Equals(NormalizeProcess(name), normalized, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeProcess(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(trimmed)
                : trimmed;
        }

        private static string SafeProcessName(Process process)
        {
            try
            {
                return process.ProcessName;
            }
            catch
            {
                return null;
            }
            finally
            {
                process.Dispose();
            }
        }

        private static string GetConfigPath(string[] args)
        {
            var explicitPath = args
                .FirstOrDefault(arg => arg.StartsWith("--config=", StringComparison.OrdinalIgnoreCase));
            if (explicitPath != null)
            {
                return Path.GetFullPath(explicitPath.Split(new[] { '=' }, 2)[1]);
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PrimaryAudioSwitcher",
                "config.xml");
        }
    }

    internal sealed class AppConfig
    {
        public int PollMilliseconds { get; set; }
        public string FallbackDevice { get; set; }
        public string FallbackDeviceId { get; set; }
        public bool LogEnabled { get; set; }
        public bool NotificationsEnabled { get; set; }
        public bool Paused { get; set; }
        public bool ProcessStartWatcherEnabled { get; set; }
        public bool DeviceChangeWatcherEnabled { get; set; }
        public bool NotifyFailures { get; set; }
        public bool RoleConsole { get; set; }
        public bool RoleMultimedia { get; set; }
        public bool RoleCommunications { get; set; }
        public string ActiveProfile { get; set; }
        public int SwitchCooldownMilliseconds { get; set; }
        public ProcessExitAction ProcessExitAction { get; set; }
        public List<DeviceAlias> DeviceAliases { get; set; }
        public List<AudioRule> Rules { get; set; }

        public bool HasFallbackDevice
        {
            get { return !string.IsNullOrWhiteSpace(FallbackDevice) || !string.IsNullOrWhiteSpace(FallbackDeviceId); }
        }

        public bool AnyOutputRoleEnabled
        {
            get { return RoleConsole || RoleMultimedia || RoleCommunications; }
        }

        public bool IsRuleInActiveProfile(AudioRule rule)
        {
            var active = string.IsNullOrWhiteSpace(ActiveProfile) ? "Default" : ActiveProfile;
            var profile = string.IsNullOrWhiteSpace(rule.Profile) ? "Default" : rule.Profile;
            return active.Equals("All", StringComparison.OrdinalIgnoreCase) ||
                   profile.Equals(active, StringComparison.OrdinalIgnoreCase);
        }

        public IEnumerable<string> ProfileNames
        {
            get
            {
                return Rules
                    .Select(rule => string.IsNullOrWhiteSpace(rule.Profile) ? "Default" : rule.Profile)
                    .Concat(new[] { "Default", "All" })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(profile => profile.Equals("All", StringComparison.OrdinalIgnoreCase) ? "" : profile, StringComparer.OrdinalIgnoreCase);
            }
        }

        public static AppConfig Load(string path)
        {
            var document = XmlFiles.LoadSecure(path);
            var root = document.Root;
            if (root == null || root.Name != "PrimaryAudioSwitcher")
            {
                throw new InvalidOperationException("Root element must be <PrimaryAudioSwitcher>.");
            }

            return new AppConfig
            {
                PollMilliseconds = (int?)root.Attribute("pollMilliseconds") ?? 1000,
                FallbackDevice = (string)root.Attribute("fallbackDevice"),
                FallbackDeviceId = (string)root.Attribute("fallbackDeviceId"),
                LogEnabled = ((string)root.Attribute("log") ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase),
                NotificationsEnabled = ((string)root.Attribute("notifications") ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase),
                Paused = ((string)root.Attribute("paused") ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase),
                ProcessStartWatcherEnabled = ((string)root.Attribute("processStartWatcher") ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase),
                DeviceChangeWatcherEnabled = ((string)root.Attribute("deviceChangeWatcher") ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase),
                NotifyFailures = ((string)root.Attribute("notifyFailures") ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase),
                RoleConsole = ((string)root.Attribute("roleConsole") ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase),
                RoleMultimedia = ((string)root.Attribute("roleMultimedia") ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase),
                RoleCommunications = ((string)root.Attribute("roleCommunications") ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase),
                ActiveProfile = (string)root.Attribute("activeProfile") ?? "Default",
                SwitchCooldownMilliseconds = (int?)root.Attribute("switchCooldownMilliseconds") ?? 0,
                ProcessExitAction = ProcessExitActions.Parse((string)root.Attribute("processExitAction")),
                DeviceAliases = root.Elements("DeviceAlias").Select(DeviceAlias.FromXml).ToList(),
                Rules = root.Elements("Rule")
                    .Select(rule => AudioRule.FromXml(
                        rule,
                        (int?)root.Attribute("processStartRetryCount") ?? 3,
                        (int?)root.Attribute("processStartRetryDelayMilliseconds") ?? 500))
                    .ToList()
            };
        }

        public void Save(string path)
        {
            var root = new XElement("PrimaryAudioSwitcher",
                new XAttribute("pollMilliseconds", PollMilliseconds),
                new XAttribute("fallbackDevice", FallbackDevice ?? ""),
                new XAttribute("fallbackDeviceId", FallbackDeviceId ?? ""),
                new XAttribute("log", LogEnabled ? "true" : "false"),
                new XAttribute("notifications", NotificationsEnabled ? "true" : "false"),
                new XAttribute("paused", Paused ? "true" : "false"),
                new XAttribute("processStartWatcher", ProcessStartWatcherEnabled ? "true" : "false"),
                new XAttribute("deviceChangeWatcher", DeviceChangeWatcherEnabled ? "true" : "false"),
                new XAttribute("notifyFailures", NotifyFailures ? "true" : "false"),
                new XAttribute("roleConsole", RoleConsole ? "true" : "false"),
                new XAttribute("roleMultimedia", RoleMultimedia ? "true" : "false"),
                new XAttribute("roleCommunications", RoleCommunications ? "true" : "false"),
                new XAttribute("activeProfile", ActiveProfile ?? "Default"),
                new XAttribute("switchCooldownMilliseconds", SwitchCooldownMilliseconds),
                new XAttribute("processExitAction", ProcessExitActions.ToConfigValue(ProcessExitAction)));

            foreach (var alias in DeviceAliases ?? new List<DeviceAlias>())
            {
                root.Add(alias.ToXml());
            }

            foreach (var rule in Rules)
            {
                var element = new XElement("Rule",
                    new XAttribute("name", rule.Name ?? ""),
                    new XAttribute("profile", rule.Profile ?? "Default"),
                    new XAttribute("enabled", rule.Enabled ? "true" : "false"),
                    new XAttribute("windowTitle", rule.WindowTitle ?? ""),
                    new XAttribute("device", rule.Device ?? ""),
                    new XAttribute("deviceId", rule.DeviceId ?? ""),
                    new XAttribute("alternateDevice", rule.AlternateDevice ?? ""),
                    new XAttribute("alternateDeviceId", rule.AlternateDeviceId ?? ""),
                    new XAttribute("retryCount", rule.RetryCount),
                    new XAttribute("retryDelayMilliseconds", rule.RetryDelayMilliseconds),
                    new XAttribute("exitDelayMilliseconds", rule.ExitDelayMilliseconds),
                    new XAttribute("exitAction", ProcessExitActions.ToConfigValue(rule.ExitActionOverride)),
                    new XAttribute("disabledUntilUtc", rule.DisabledUntilUtc.HasValue ? rule.DisabledUntilUtc.Value.UtcDateTime.ToString("O") : ""),
                    new XAttribute("sessionVolumeEnabled", rule.SessionVolumeEnabled ? "true" : "false"),
                    new XAttribute("sessionVolumePercent", rule.SessionVolumePercent),
                    new XAttribute("sessionMuteEnabled", rule.SessionMuteEnabled ? "true" : "false"),
                    new XAttribute("sessionMuted", rule.SessionMuted ? "true" : "false"));

                if (!string.IsNullOrWhiteSpace(rule.ForegroundProcess))
                {
                    element.SetAttributeValue("foregroundProcess", rule.ForegroundProcess);
                }
                else if (!string.IsNullOrWhiteSpace(rule.RunningProcess))
                {
                    element.SetAttributeValue("runningProcess", rule.RunningProcess);
                }

                root.Add(element);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", true);
                ConfigBackups.CreateTimestampedBackup(path, 5);
            }
            new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root).Save(path);
        }

        public AppConfig Clone()
        {
            return new AppConfig
            {
                PollMilliseconds = PollMilliseconds,
                FallbackDevice = FallbackDevice,
                FallbackDeviceId = FallbackDeviceId,
                LogEnabled = LogEnabled,
                NotificationsEnabled = NotificationsEnabled,
                Paused = Paused,
                ProcessStartWatcherEnabled = ProcessStartWatcherEnabled,
                DeviceChangeWatcherEnabled = DeviceChangeWatcherEnabled,
                NotifyFailures = NotifyFailures,
                RoleConsole = RoleConsole,
                RoleMultimedia = RoleMultimedia,
                RoleCommunications = RoleCommunications,
                ActiveProfile = ActiveProfile,
                SwitchCooldownMilliseconds = SwitchCooldownMilliseconds,
                ProcessExitAction = ProcessExitAction,
                DeviceAliases = (DeviceAliases ?? new List<DeviceAlias>()).Select(alias => alias.Clone()).ToList(),
                Rules = Rules.Select(r => r.Clone()).ToList()
            };
        }

        public static readonly string DefaultXml =
@"<?xml version=""1.0"" encoding=""utf-8""?>
<PrimaryAudioSwitcher pollMilliseconds=""1000"" fallbackDevice="""" fallbackDeviceId="""" log=""true"" notifications=""false"" notifyFailures=""true"" paused=""false"" processStartWatcher=""true"" deviceChangeWatcher=""true"" roleConsole=""true"" roleMultimedia=""true"" roleCommunications=""true"" activeProfile=""Default"" switchCooldownMilliseconds=""0"" processExitAction=""fallback"">
  <DeviceAlias alias=""Headset"" device=""Headset"" deviceId="""" />
  <!-- device is matched by substring against active Windows render device friendly names. -->
  <Rule name=""Game foreground"" profile=""Default"" enabled=""true"" foregroundProcess=""Game"" windowTitle="""" device=""Speakers"" deviceId="""" alternateDevice="""" alternateDeviceId="""" retryCount=""3"" retryDelayMilliseconds=""500"" exitDelayMilliseconds=""0"" sessionVolumeEnabled=""false"" sessionVolumePercent=""100"" sessionMuteEnabled=""false"" sessionMuted=""false"" />
  <Rule name=""Discord running"" profile=""Default"" enabled=""true"" runningProcess=""Discord"" windowTitle="""" device=""Headset"" deviceId="""" alternateDevice="""" alternateDeviceId="""" retryCount=""3"" retryDelayMilliseconds=""500"" exitDelayMilliseconds=""0"" sessionVolumeEnabled=""false"" sessionVolumePercent=""100"" sessionMuteEnabled=""false"" sessionMuted=""false"" />
</PrimaryAudioSwitcher>
";
    }

    internal static class XmlFiles
    {
        public static XDocument LoadSecure(string path)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = 1024 * 1024
            };

            using (var stream = File.OpenRead(path))
            using (var reader = XmlReader.Create(stream, settings))
            {
                return XDocument.Load(reader, LoadOptions.None);
            }
        }
    }

    internal enum ProcessExitAction
    {
        None,
        FallbackDevice,
        PreviousDevice
    }

    internal static class ProcessExitActions
    {
        public static ProcessExitAction Parse(string value)
        {
            if (value != null && value.Equals("previous", StringComparison.OrdinalIgnoreCase))
            {
                return ProcessExitAction.PreviousDevice;
            }

            if (value != null && value.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return ProcessExitAction.None;
            }

            return ProcessExitAction.FallbackDevice;
        }

        public static ProcessExitAction? ParseNullable(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Equals("global", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return Parse(value);
        }

        public static string ToConfigValue(ProcessExitAction action)
        {
            if (action == ProcessExitAction.PreviousDevice)
            {
                return "previous";
            }

            if (action == ProcessExitAction.None)
            {
                return "none";
            }

            return "fallback";
        }

        public static string ToConfigValue(ProcessExitAction? action)
        {
            return action.HasValue ? ToConfigValue(action.Value) : "global";
        }
    }

    internal sealed class DeviceAlias
    {
        public string Alias { get; set; }
        public string Device { get; set; }
        public string DeviceId { get; set; }

        public static DeviceAlias FromXml(XElement element)
        {
            return new DeviceAlias
            {
                Alias = (string)element.Attribute("alias") ?? "",
                Device = (string)element.Attribute("device") ?? "",
                DeviceId = (string)element.Attribute("deviceId") ?? ""
            };
        }

        public XElement ToXml()
        {
            return new XElement("DeviceAlias",
                new XAttribute("alias", Alias ?? ""),
                new XAttribute("device", Device ?? ""),
                new XAttribute("deviceId", DeviceId ?? ""));
        }

        public DeviceAlias Clone()
        {
            return new DeviceAlias { Alias = Alias, Device = Device, DeviceId = DeviceId };
        }
    }

    internal sealed class AudioRule
    {
        public string Name { get; set; }
        public string Profile { get; set; }
        public string ForegroundProcess { get; set; }
        public string RunningProcess { get; set; }
        public string WindowTitle { get; set; }
        public bool Enabled { get; set; }
        public string Device { get; set; }
        public string DeviceId { get; set; }
        public string AlternateDevice { get; set; }
        public string AlternateDeviceId { get; set; }
        public int RetryCount { get; set; }
        public int RetryDelayMilliseconds { get; set; }
        public int ExitDelayMilliseconds { get; set; }
        public ProcessExitAction? ExitActionOverride { get; set; }
        public DateTimeOffset? DisabledUntilUtc { get; set; }
        public bool DisabledUntilRestart { get; set; }
        public bool SessionVolumeEnabled { get; set; }
        public int SessionVolumePercent { get; set; }
        public bool SessionMuteEnabled { get; set; }
        public bool SessionMuted { get; set; }

        public bool HasAlternateDevice
        {
            get { return !string.IsNullOrWhiteSpace(AlternateDevice) || !string.IsNullOrWhiteSpace(AlternateDeviceId); }
        }

        public bool IsTemporarilyDisabled
        {
            get
            {
                return DisabledUntilRestart ||
                       (DisabledUntilUtc.HasValue && DisabledUntilUtc.Value > DateTimeOffset.UtcNow);
            }
        }

        public string TargetProcess
        {
            get
            {
                return !string.IsNullOrWhiteSpace(RunningProcess)
                    ? RunningProcess
                    : ForegroundProcess;
            }
        }

        public bool IsMatch(ForegroundWindowInfo foreground, HashSet<string> runningProcesses)
        {
            if (!string.IsNullOrWhiteSpace(ForegroundProcess))
            {
                return Enabled &&
                       MatchesAnyProcess(ForegroundProcess, foreground.ProcessName) &&
                       MatchesWindowTitle(foreground.Title);
            }

            if (!string.IsNullOrWhiteSpace(RunningProcess))
            {
                return Enabled && runningProcesses.Any(process => MatchesAnyProcess(RunningProcess, process));
            }

            return false;
        }

        public bool MatchesRunningProcess(string processName)
        {
            return Enabled && !string.IsNullOrWhiteSpace(RunningProcess) && MatchesAnyProcess(RunningProcess, processName);
        }

        public IReadOnlyList<string> RunningProcesses
        {
            get { return SplitProcesses(RunningProcess); }
        }

        public IReadOnlyList<string> TargetProcesses
        {
            get { return SplitProcesses(TargetProcess); }
        }

        public static AudioRule FromXml(XElement element, int defaultRetryCount, int defaultRetryDelayMilliseconds)
        {
            var device = (string)element.Attribute("device");
            var deviceId = (string)element.Attribute("deviceId");
            if (string.IsNullOrWhiteSpace(device) && string.IsNullOrWhiteSpace(deviceId))
            {
                throw new InvalidOperationException("Rule is missing required device or deviceId attribute.");
            }

            return new AudioRule
            {
                Name = (string)element.Attribute("name") ?? "unnamed",
                Profile = (string)element.Attribute("profile") ?? "Default",
                Enabled = !(((string)element.Attribute("enabled") ?? "true").Equals("false", StringComparison.OrdinalIgnoreCase)),
                ForegroundProcess = NormalizeProcess((string)element.Attribute("foregroundProcess")),
                RunningProcess = NormalizeProcess((string)element.Attribute("runningProcess")),
                WindowTitle = (string)element.Attribute("windowTitle"),
                Device = device,
                DeviceId = deviceId,
                AlternateDevice = (string)element.Attribute("alternateDevice"),
                AlternateDeviceId = (string)element.Attribute("alternateDeviceId"),
                RetryCount = (int?)element.Attribute("retryCount") ?? defaultRetryCount,
                RetryDelayMilliseconds = (int?)element.Attribute("retryDelayMilliseconds") ?? defaultRetryDelayMilliseconds,
                ExitDelayMilliseconds = (int?)element.Attribute("exitDelayMilliseconds") ?? 0,
                ExitActionOverride = ProcessExitActions.ParseNullable((string)element.Attribute("exitAction")),
                DisabledUntilUtc = ParseDateTimeOffset((string)element.Attribute("disabledUntilUtc")),
                SessionVolumeEnabled = ((string)element.Attribute("sessionVolumeEnabled") ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase),
                SessionVolumePercent = (int?)element.Attribute("sessionVolumePercent") ?? 100,
                SessionMuteEnabled = ((string)element.Attribute("sessionMuteEnabled") ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase),
                SessionMuted = ((string)element.Attribute("sessionMuted") ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase)
            };
        }

        public AudioRule Clone()
        {
            return new AudioRule
            {
                Name = Name,
                Profile = Profile,
                Enabled = Enabled,
                ForegroundProcess = ForegroundProcess,
                RunningProcess = RunningProcess,
                WindowTitle = WindowTitle,
                Device = Device,
                DeviceId = DeviceId,
                AlternateDevice = AlternateDevice,
                AlternateDeviceId = AlternateDeviceId,
                RetryCount = RetryCount,
                RetryDelayMilliseconds = RetryDelayMilliseconds,
                ExitDelayMilliseconds = ExitDelayMilliseconds,
                ExitActionOverride = ExitActionOverride,
                DisabledUntilUtc = DisabledUntilUtc,
                DisabledUntilRestart = DisabledUntilRestart,
                SessionVolumeEnabled = SessionVolumeEnabled,
                SessionVolumePercent = SessionVolumePercent,
                SessionMuteEnabled = SessionMuteEnabled,
                SessionMuted = SessionMuted
            };
        }

        public XElement ToXml()
        {
            var element = new XElement("Rule",
                new XAttribute("name", Name ?? ""),
                new XAttribute("profile", Profile ?? "Default"),
                new XAttribute("enabled", Enabled ? "true" : "false"),
                new XAttribute("windowTitle", WindowTitle ?? ""),
                new XAttribute("device", Device ?? ""),
                new XAttribute("deviceId", DeviceId ?? ""),
                new XAttribute("alternateDevice", AlternateDevice ?? ""),
                new XAttribute("alternateDeviceId", AlternateDeviceId ?? ""),
                new XAttribute("retryCount", RetryCount),
                new XAttribute("retryDelayMilliseconds", RetryDelayMilliseconds),
                new XAttribute("exitDelayMilliseconds", ExitDelayMilliseconds),
                new XAttribute("exitAction", ProcessExitActions.ToConfigValue(ExitActionOverride)),
                new XAttribute("disabledUntilUtc", DisabledUntilUtc.HasValue ? DisabledUntilUtc.Value.UtcDateTime.ToString("O") : ""),
                new XAttribute("sessionVolumeEnabled", SessionVolumeEnabled ? "true" : "false"),
                new XAttribute("sessionVolumePercent", SessionVolumePercent),
                new XAttribute("sessionMuteEnabled", SessionMuteEnabled ? "true" : "false"),
                new XAttribute("sessionMuted", SessionMuted ? "true" : "false"));

            if (!string.IsNullOrWhiteSpace(ForegroundProcess))
            {
                element.SetAttributeValue("foregroundProcess", ForegroundProcess);
            }
            else if (!string.IsNullOrWhiteSpace(RunningProcess))
            {
                element.SetAttributeValue("runningProcess", RunningProcess);
            }

            return element;
        }

        public override string ToString()
        {
            var mode = !string.IsNullOrWhiteSpace(ForegroundProcess) ? "foreground" : "running";
            var process = !string.IsNullOrWhiteSpace(ForegroundProcess) ? ForegroundProcess : RunningProcess;
            var prefix = IsTemporarilyDisabled ? "[temp disabled] " : Enabled ? "" : "[disabled] ";
            return prefix + (Name ?? "unnamed") + " [" + (Profile ?? "Default") + " #" + mode + ": " + (process ?? "") + "] -> " + (Device ?? "");
        }

        private static DateTimeOffset? ParseDateTimeOffset(string value)
        {
            DateTimeOffset parsed;
            if (DateTimeOffset.TryParse(value, out parsed))
            {
                return parsed.ToUniversalTime();
            }

            return null;
        }

        private bool MatchesWindowTitle(string actualTitle)
        {
            return string.IsNullOrWhiteSpace(WindowTitle) ||
                   (!string.IsNullOrWhiteSpace(actualTitle) &&
                    actualTitle.IndexOf(WindowTitle, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool MatchesAnyProcess(string expectedList, string actual)
        {
            return SplitProcesses(expectedList).Any(expected => MatchesProcess(expected, actual));
        }

        private static bool MatchesProcess(string expected, string actual)
        {
            return !string.IsNullOrWhiteSpace(actual) &&
                   actual.Equals(NormalizeProcess(expected), StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> SplitProcesses(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new List<string>();
            }

            return value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeProcess)
                .Where(process => !string.IsNullOrWhiteSpace(process))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string NormalizeProcess(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return string.Join(";", value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item =>
                {
                    var trimmed = item.Trim();
                    return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? Path.GetFileNameWithoutExtension(trimmed)
                        : trimmed;
                })
                .Where(item => !string.IsNullOrWhiteSpace(item)));
        }
    }

    internal sealed class SwitchHistoryItem
    {
        public SwitchHistoryItem(DateTimeOffset timestamp, string ruleName, string deviceName, string trigger, bool forced)
        {
            Timestamp = timestamp;
            RuleName = ruleName;
            DeviceName = deviceName;
            Trigger = trigger;
            Forced = forced;
        }

        public DateTimeOffset Timestamp { get; private set; }
        public string RuleName { get; private set; }
        public string DeviceName { get; private set; }
        public string Trigger { get; private set; }
        public bool Forced { get; private set; }

        public override string ToString()
        {
            return Timestamp.ToString("yyyy-MM-dd HH:mm:ss") +
                   " | " + (RuleName ?? "") +
                   " -> " + (DeviceName ?? "") +
                   " | " + (Trigger ?? "unknown") +
                   (Forced ? " | forced" : "");
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly AudioDeviceManager _audio;
        private AppConfig _originalConfig;
        private readonly ListBox _rules = new ListBox();
        private readonly List<int> _visibleRuleIndexes = new List<int>();
        private readonly TextBox _name = new TextBox();
        private readonly TextBox _profile = new TextBox();
        private readonly ComboBox _activeProfile = new ComboBox();
        private readonly TextBox _ruleFilter = new TextBox();
        private readonly ComboBox _mode = new ComboBox();
        private readonly ComboBox _process = new ComboBox();
        private readonly TextBox _windowTitle = new TextBox();
        private readonly ComboBox _device = new ComboBox();
        private readonly ComboBox _alternateDevice = new ComboBox();
        private readonly ComboBox _fallback = new ComboBox();
        private readonly ComboBox _exitAction = new ComboBox();
        private readonly ComboBox _ruleExitAction = new ComboBox();
        private readonly TextBox _deviceAliases = new TextBox();
        private readonly NumericUpDown _poll = new NumericUpDown();
        private readonly NumericUpDown _cooldown = new NumericUpDown();
        private readonly NumericUpDown _startRetryCount = new NumericUpDown();
        private readonly NumericUpDown _startRetryDelay = new NumericUpDown();
        private readonly NumericUpDown _exitDelay = new NumericUpDown();
        private readonly CheckBox _log = new CheckBox();
        private readonly CheckBox _processStartWatcher = new CheckBox();
        private readonly CheckBox _deviceChangeWatcher = new CheckBox();
        private readonly CheckBox _notifyFailures = new CheckBox();
        private readonly CheckBox _roleConsole = new CheckBox();
        private readonly CheckBox _roleMultimedia = new CheckBox();
        private readonly CheckBox _roleCommunications = new CheckBox();
        private readonly CheckBox _startup = new CheckBox();
        private readonly CheckBox _paused = new CheckBox();
        private readonly CheckBox _notifications = new CheckBox();
        private readonly CheckBox _enabled = new CheckBox();
        private readonly CheckBox _sessionVolumeEnabled = new CheckBox();
        private readonly NumericUpDown _sessionVolume = new NumericUpDown();
        private readonly CheckBox _sessionMuteEnabled = new CheckBox();
        private readonly CheckBox _sessionMuted = new CheckBox();
        private readonly Label _currentDevice = new Label();
        private readonly Label _matchingRules = new Label();
        private readonly ListBox _history = new ListBox();
        private readonly IReadOnlyList<SwitchHistoryItem> _switchHistory;

        public SettingsForm(AppConfig config, AudioDeviceManager audio, IReadOnlyList<SwitchHistoryItem> switchHistory)
        {
            _audio = audio;
            _switchHistory = switchHistory;
            Config = config.Clone();
            _originalConfig = config.Clone();

            Text = "Primary Audio Switcher Settings";
            StartPosition = FormStartPosition.CenterScreen;
            Width = 760;
            Height = 560;
            MinimumSize = new Size(720, 520);

            BuildUi();
            LoadDeviceList();
            LoadProcessList();
            LoadConfig();
        }

        public AppConfig Config { get; private set; }

        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(10)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            Controls.Add(root);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            root.Controls.Add(tabs, 0, 0);

            var rulesTab = new TabPage("Rules");
            var globalTab = new TabPage("Global");
            var statusTab = new TabPage("Status");
            tabs.TabPages.Add(rulesTab);
            tabs.TabPages.Add(globalTab);
            tabs.TabPages.Add(statusTab);

            BuildRulesTab(rulesTab);
            BuildGlobalTab(globalTab);
            BuildStatusTab(statusTab);

            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft
            };
            var save = MakeButton("Save", SaveAndClose);
            save.DialogResult = DialogResult.None;
            bottom.Controls.Add(save);
            bottom.Controls.Add(MakeButton("Cancel", delegate { DialogResult = DialogResult.Cancel; Close(); }));
            root.Controls.Add(bottom, 0, 1);
        }

        private void BuildRulesTab(TabPage tab)
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(10)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
            tab.Controls.Add(root);

            var listPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1
            };
            listPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            listPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(listPanel, 0, 0);
            _ruleFilter.Dock = DockStyle.Fill;
            _ruleFilter.TextChanged += delegate { ReloadRuleList(); };
            listPanel.Controls.Add(_ruleFilter, 0, 0);
            _rules.Dock = DockStyle.Fill;
            _rules.SelectedIndexChanged += (sender, args) => LoadSelectedRule();
            listPanel.Controls.Add(_rules, 0, 1);
            _enabled.Checked = true;

            var editor = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 14,
                Padding = new Padding(10, 0, 0, 0)
            };
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            for (var i = 0; i < 14; i++)
            {
                editor.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 12 ? 70 : 34));
            }
            root.Controls.Add(editor, 1, 0);

            AddLabel(editor, "Rule name", 0);
            editor.Controls.Add(_name, 1, 0);
            _name.Dock = DockStyle.Fill;
            _enabled.Text = "Enabled";
            _enabled.Dock = DockStyle.Fill;
            editor.Controls.Add(_enabled, 2, 0);

            AddLabel(editor, "Profile", 1);
            editor.Controls.Add(_profile, 1, 1);
            editor.SetColumnSpan(_profile, 2);
            _profile.Dock = DockStyle.Fill;

            AddLabel(editor, "Match type", 2);
            _mode.DropDownStyle = ComboBoxStyle.DropDownList;
            _mode.Items.Add("Foreground app");
            _mode.Items.Add("Running process");
            _mode.SelectedIndex = 0;
            editor.Controls.Add(_mode, 1, 2);
            editor.SetColumnSpan(_mode, 2);
            _mode.Dock = DockStyle.Fill;

            AddLabel(editor, "Process", 3);
            _process.DropDownStyle = ComboBoxStyle.DropDown;
            editor.Controls.Add(_process, 1, 3);
            _process.Dock = DockStyle.Fill;
            editor.Controls.Add(MakeButton("Browse exe", BrowseProcessFile), 2, 3);

            AddLabel(editor, "Title contains", 4);
            editor.Controls.Add(_windowTitle, 1, 4);
            editor.SetColumnSpan(_windowTitle, 2);
            _windowTitle.Dock = DockStyle.Fill;

            AddLabel(editor, "Audio device", 5);
            _device.DropDownStyle = ComboBoxStyle.DropDown;
            editor.Controls.Add(_device, 1, 5);
            _device.Dock = DockStyle.Fill;
            editor.Controls.Add(MakeButton("Refresh", RefreshLists), 2, 5);

            AddLabel(editor, "Alt device", 6);
            _alternateDevice.DropDownStyle = ComboBoxStyle.DropDown;
            editor.Controls.Add(_alternateDevice, 1, 6);
            editor.SetColumnSpan(_alternateDevice, 2);
            _alternateDevice.Dock = DockStyle.Fill;

            AddLabel(editor, "On exit delay", 7);
            _exitDelay.Minimum = 0;
            _exitDelay.Maximum = 60000;
            _exitDelay.Increment = 250;
            editor.Controls.Add(_exitDelay, 1, 7);
            _ruleExitAction.DropDownStyle = ComboBoxStyle.DropDownList;
            _ruleExitAction.Items.Add("Use global");
            _ruleExitAction.Items.Add("Fallback device");
            _ruleExitAction.Items.Add("Previous device");
            _ruleExitAction.Items.Add("Do nothing");
            _ruleExitAction.SelectedIndex = 0;
            editor.Controls.Add(_ruleExitAction, 2, 7);

            AddLabel(editor, "Rule retry", 8);
            _startRetryCount.Minimum = 0;
            _startRetryCount.Maximum = 20;
            _startRetryCount.Increment = 1;
            editor.Controls.Add(_startRetryCount, 1, 8);
            _startRetryDelay.Minimum = 100;
            _startRetryDelay.Maximum = 10000;
            _startRetryDelay.Increment = 100;
            editor.Controls.Add(_startRetryDelay, 2, 8);

            AddLabel(editor, "Session", 9);
            _sessionVolumeEnabled.Text = "Volume";
            _sessionVolumeEnabled.Dock = DockStyle.Left;
            _sessionVolume.Minimum = 0;
            _sessionVolume.Maximum = 100;
            _sessionVolume.Value = 100;
            var sessionPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            sessionPanel.Controls.Add(_sessionVolumeEnabled);
            sessionPanel.Controls.Add(_sessionVolume);
            _sessionMuteEnabled.Text = "Mute";
            sessionPanel.Controls.Add(_sessionMuteEnabled);
            _sessionMuted.Text = "Muted";
            sessionPanel.Controls.Add(_sessionMuted);
            editor.Controls.Add(sessionPanel, 1, 9);
            editor.SetColumnSpan(sessionPanel, 2);

            var ruleButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            ruleButtons.Controls.Add(MakeButton("Add", AddRule));
            ruleButtons.Controls.Add(MakeButton("Update", UpdateRule));
            ruleButtons.Controls.Add(MakeButton("Delete", DeleteRule));
            ruleButtons.Controls.Add(MakeButton("Duplicate", DuplicateRule));
            ruleButtons.Controls.Add(MakeButton("Up", MoveRuleUp));
            ruleButtons.Controls.Add(MakeButton("Down", MoveRuleDown));
            ruleButtons.Controls.Add(MakeButton("Disable 30m", DisableRuleFor30Minutes));
            ruleButtons.Controls.Add(MakeButton("Disable run", DisableRuleUntilRestart));
            ruleButtons.Controls.Add(MakeButton("Enable temp", ClearTemporaryDisable));
            ruleButtons.Controls.Add(MakeButton("Use current", UseCurrentForeground));
            ruleButtons.Controls.Add(MakeButton("Export", ExportRule));
            ruleButtons.Controls.Add(MakeButton("Import", ImportRule));
            ruleButtons.Controls.Add(MakeButton("Dry run", PreviewRules));
            ruleButtons.Controls.Add(MakeButton("Test", TestRule));
            ruleButtons.Controls.Add(MakeButton("Validate", ValidateRules));
            editor.Controls.Add(ruleButtons, 1, 12);
            editor.SetColumnSpan(ruleButtons, 2);
        }

        private void BuildGlobalTab(TabPage tab)
        {
            var editor = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 13,
                Padding = new Padding(10)
            };
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            for (var i = 0; i < 13; i++)
            {
                editor.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 10 ? 82 : 36));
            }
            tab.Controls.Add(editor);

            AddLabel(editor, "Fallback", 0);
            _fallback.DropDownStyle = ComboBoxStyle.DropDown;
            editor.Controls.Add(_fallback, 1, 0);
            editor.SetColumnSpan(_fallback, 2);
            _fallback.Dock = DockStyle.Fill;

            AddLabel(editor, "On exit", 1);
            _exitAction.DropDownStyle = ComboBoxStyle.DropDownList;
            _exitAction.Items.Add("Fallback device");
            _exitAction.Items.Add("Previous device");
            _exitAction.Items.Add("Do nothing");
            editor.Controls.Add(_exitAction, 1, 1);
            editor.SetColumnSpan(_exitAction, 2);
            _exitAction.Dock = DockStyle.Fill;

            AddLabel(editor, "Poll ms", 2);
            _poll.Minimum = 250;
            _poll.Maximum = 60000;
            _poll.Increment = 250;
            editor.Controls.Add(_poll, 1, 2);
            _log.Text = "Enable log";
            _log.Dock = DockStyle.Fill;
            editor.Controls.Add(_log, 2, 2);

            AddLabel(editor, "Cooldown", 3);
            _cooldown.Minimum = 0;
            _cooldown.Maximum = 60000;
            _cooldown.Increment = 250;
            editor.Controls.Add(_cooldown, 1, 3);

            AddLabel(editor, "Watchers", 4);
            _processStartWatcher.Text = "Process start";
            _processStartWatcher.Dock = DockStyle.Fill;
            editor.Controls.Add(_processStartWatcher, 1, 4);
            _deviceChangeWatcher.Text = "Device changes";
            _deviceChangeWatcher.Dock = DockStyle.Fill;
            editor.Controls.Add(_deviceChangeWatcher, 2, 4);

            AddLabel(editor, "Startup", 5);
            _startup.Text = "Start with Windows";
            _startup.Dock = DockStyle.Fill;
            editor.Controls.Add(_startup, 1, 5);
            _notifications.Text = "Notifications";
            _notifications.Dock = DockStyle.Fill;
            editor.Controls.Add(_notifications, 2, 5);

            AddLabel(editor, "Paused", 6);
            _paused.Text = "Pause automation";
            _paused.Dock = DockStyle.Fill;
            editor.Controls.Add(_paused, 1, 6);
            editor.SetColumnSpan(_paused, 2);

            AddLabel(editor, "Profile", 7);
            _activeProfile.DropDownStyle = ComboBoxStyle.DropDown;
            _activeProfile.Dock = DockStyle.Fill;
            editor.Controls.Add(_activeProfile, 1, 7);
            editor.SetColumnSpan(_activeProfile, 2);

            AddLabel(editor, "Output roles", 8);
            var rolePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            _roleConsole.Text = "Console";
            _roleMultimedia.Text = "Multimedia";
            _roleCommunications.Text = "Communications";
            rolePanel.Controls.Add(_roleConsole);
            rolePanel.Controls.Add(_roleMultimedia);
            rolePanel.Controls.Add(_roleCommunications);
            editor.Controls.Add(rolePanel, 1, 8);
            editor.SetColumnSpan(rolePanel, 2);

            AddLabel(editor, "Failures", 9);
            _notifyFailures.Text = "Notify switch failures";
            _notifyFailures.Dock = DockStyle.Fill;
            editor.Controls.Add(_notifyFailures, 1, 9);
            editor.SetColumnSpan(_notifyFailures, 2);

            AddLabel(editor, "Aliases", 10);
            _deviceAliases.Multiline = true;
            _deviceAliases.ScrollBars = ScrollBars.Vertical;
            _deviceAliases.Dock = DockStyle.Fill;
            editor.Controls.Add(_deviceAliases, 1, 10);
            editor.SetColumnSpan(_deviceAliases, 2);

            var undoPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            undoPanel.Controls.Add(MakeButton("Undo changes", UndoChanges));
            editor.Controls.Add(undoPanel, 1, 11);
            editor.SetColumnSpan(undoPanel, 2);
        }

        private void BuildStatusTab(TabPage tab)
        {
            var editor = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 4,
                Padding = new Padding(10)
            };
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tab.Controls.Add(editor);

            AddLabel(editor, "Current", 0);
            _currentDevice.Dock = DockStyle.Fill;
            _currentDevice.TextAlign = ContentAlignment.MiddleLeft;
            editor.Controls.Add(_currentDevice, 1, 0);
            editor.SetColumnSpan(_currentDevice, 2);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            buttons.Controls.Add(MakeButton("Refresh", RefreshLists));
            buttons.Controls.Add(MakeButton("View log", ViewLog));
            buttons.Controls.Add(MakeButton("Save report", SaveDiagnosticsReport));
            buttons.Controls.Add(MakeButton("Clear history", ClearHistory));
            editor.Controls.Add(buttons, 1, 1);
            editor.SetColumnSpan(buttons, 2);

            AddLabel(editor, "Matching", 2);
            _matchingRules.Dock = DockStyle.Fill;
            _matchingRules.TextAlign = ContentAlignment.MiddleLeft;
            editor.Controls.Add(_matchingRules, 1, 2);
            editor.SetColumnSpan(_matchingRules, 2);

            _history.Dock = DockStyle.Fill;
            editor.Controls.Add(_history, 0, 3);
            editor.SetColumnSpan(_history, 3);
        }

        private static void AddLabel(TableLayoutPanel panel, string text, int row)
        {
            panel.Controls.Add(new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, row);
        }

        private static Button MakeButton(string text, EventHandler handler)
        {
            var button = new Button
            {
                Text = text,
                Width = 105,
                Height = 28
            };
            button.Click += handler;
            return button;
        }

        private void LoadConfig()
        {
            RefreshProfileChoices();
            _poll.Value = Math.Max(_poll.Minimum, Math.Min(_poll.Maximum, Config.PollMilliseconds));
            _cooldown.Value = Math.Max(_cooldown.Minimum, Math.Min(_cooldown.Maximum, Config.SwitchCooldownMilliseconds));
            _processStartWatcher.Checked = Config.ProcessStartWatcherEnabled;
            _deviceChangeWatcher.Checked = Config.DeviceChangeWatcherEnabled;
            _paused.Checked = Config.Paused;
            _notifications.Checked = Config.NotificationsEnabled;
            _notifyFailures.Checked = Config.NotifyFailures;
            _roleConsole.Checked = Config.RoleConsole;
            _roleMultimedia.Checked = Config.RoleMultimedia;
            _roleCommunications.Checked = Config.RoleCommunications;
            _activeProfile.Text = string.IsNullOrWhiteSpace(Config.ActiveProfile) ? "Default" : Config.ActiveProfile;
            _deviceAliases.Text = FormatAliases(Config.DeviceAliases);
            _exitAction.SelectedIndex = Config.ProcessExitAction == ProcessExitAction.PreviousDevice
                ? 1
                : Config.ProcessExitAction == ProcessExitAction.None ? 2 : 0;
            _startup.Checked = StartupManager.IsEnabled();
            SelectDevice(_fallback, Config.FallbackDeviceId, Config.FallbackDevice);
            _log.Checked = Config.LogEnabled;
            RefreshCurrentDeviceLabel();
            ReloadRuleList();
            if (_rules.Items.Count > 0)
            {
                _rules.SelectedIndex = 0;
            }
        }

        private void LoadDeviceList()
        {
            var previousDevice = _device.Text;
            var previousAlternate = _alternateDevice.Text;
            var previousFallback = _fallback.Text;
            _device.Items.Clear();
            _alternateDevice.Items.Clear();
            _fallback.Items.Clear();
            _alternateDevice.Items.Add("");
            _fallback.Items.Add("");

            try
            {
                foreach (var device in _audio.ListRenderDevices())
                {
                    var item = new DeviceChoice(device);
                    _device.Items.Add(item);
                    _alternateDevice.Items.Add(new DeviceChoice(device));
                    _fallback.Items.Add(item);
                }
            }
            catch
            {
                // The manual text field still works if Core Audio enumeration fails.
            }

            _device.Text = previousDevice;
            _alternateDevice.Text = previousAlternate;
            _fallback.Text = previousFallback;
        }

        private void LoadProcessList()
        {
            var previous = _process.Text;
            _process.Items.Clear();
            foreach (var name in Process.GetProcesses()
                .Select(p => SafeProcessName(p))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                _process.Items.Add(name);
            }
            _process.Text = previous;
        }

        private void RefreshLists(object sender, EventArgs args)
        {
            LoadDeviceList();
            LoadProcessList();
            RefreshCurrentDeviceLabel();
            ReloadRuleList();
        }

        private void ViewLog(object sender, EventArgs args)
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PrimaryAudioSwitcher",
                "primary-audio-switcher.log");
            using (var form = new LogViewerForm(path))
            {
                form.ShowDialog(this);
            }
        }

        private void SaveDiagnosticsReport(object sender, EventArgs args)
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "Text report (*.txt)|*.txt|All files (*.*)|*.*";
                dialog.FileName = "primary-audio-switcher-diagnostics.txt";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    File.WriteAllText(dialog.FileName, DiagnosticsReport.Build(Config, _audio, "settings report"));
                }
            }
        }

        private void ClearHistory(object sender, EventArgs args)
        {
            var list = _switchHistory as List<SwitchHistoryItem>;
            if (list != null)
            {
                list.Clear();
                ReloadRuleList();
            }
        }

        private void BrowseProcessFile(object sender, EventArgs args)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*";
                dialog.Title = "Select application executable";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    var selected = Path.GetFileNameWithoutExtension(dialog.FileName);
                    _process.Text = string.IsNullOrWhiteSpace(_process.Text)
                        ? selected
                        : _process.Text.TrimEnd(';', ' ') + ";" + selected;
                }
            }
        }

        private void AddRule(object sender, EventArgs args)
        {
            var rule = ReadEditorRule();
            if (rule == null)
            {
                return;
            }

            Config.Rules.Add(rule);
            ReloadRuleList();
            SelectRuleByConfigIndex(Config.Rules.Count - 1);
        }

        private void UpdateRule(object sender, EventArgs args)
        {
            var configIndex = GetSelectedRuleIndex();
            if (configIndex < 0)
            {
                return;
            }

            var rule = ReadEditorRule();
            if (rule == null)
            {
                return;
            }

            Config.Rules[configIndex] = rule;
            ReloadRuleList();
            SelectRuleByConfigIndex(configIndex);
        }

        private void DeleteRule(object sender, EventArgs args)
        {
            var configIndex = GetSelectedRuleIndex();
            if (configIndex < 0)
            {
                return;
            }

            Config.Rules.RemoveAt(configIndex);
            ReloadRuleList();
            if (_rules.Items.Count > 0)
            {
                var nextIndex = _visibleRuleIndexes.FindIndex(index => index >= configIndex);
                _rules.SelectedIndex = nextIndex >= 0 ? nextIndex : _rules.Items.Count - 1;
            }
        }

        private void DuplicateRule(object sender, EventArgs args)
        {
            var configIndex = GetSelectedRuleIndex();
            if (configIndex < 0)
            {
                return;
            }

            var copy = Config.Rules[configIndex].Clone();
            copy.Name = (copy.Name ?? "unnamed") + " copy";
            var target = configIndex + 1;
            Config.Rules.Insert(target, copy);
            ReloadRuleList();
            SelectRuleByConfigIndex(target);
        }

        private void UseCurrentForeground(object sender, EventArgs args)
        {
            var foreground = ForegroundWindowReader.GetForegroundWindowInfo();
            if (string.IsNullOrWhiteSpace(foreground.ProcessName))
            {
                MessageBox.Show(this, "Foreground process was not available.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _mode.SelectedIndex = 0;
            _process.Text = foreground.ProcessName;
            _windowTitle.Text = foreground.Title ?? "";
            if (string.IsNullOrWhiteSpace(_name.Text))
            {
                _name.Text = foreground.ProcessName;
            }
        }

        private void ExportRule(object sender, EventArgs args)
        {
            var rule = ReadEditorRule();
            if (rule == null)
            {
                return;
            }

            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "XML rule (*.xml)|*.xml|All files (*.*)|*.*";
                dialog.FileName = (rule.Name ?? "rule") + ".xml";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    new XDocument(new XDeclaration("1.0", "utf-8", "yes"), rule.ToXml()).Save(dialog.FileName);
                }
            }
        }

        private void ImportRule(object sender, EventArgs args)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "XML rule (*.xml)|*.xml|All files (*.*)|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                var document = XmlFiles.LoadSecure(dialog.FileName);
                if (document.Root == null || document.Root.Name != "Rule")
                {
                    MessageBox.Show(this, "Rule XML must have a <Rule> root element.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var rule = AudioRule.FromXml(document.Root, 3, 500);
                Config.Rules.Add(rule);
                ReloadRuleList();
                SelectRuleByConfigIndex(Config.Rules.Count - 1);
            }
        }

        private void MoveRuleUp(object sender, EventArgs args)
        {
            MoveSelectedRule(-1);
        }

        private void MoveRuleDown(object sender, EventArgs args)
        {
            MoveSelectedRule(1);
        }

        private void MoveSelectedRule(int direction)
        {
            var selected = GetSelectedRuleIndex();
            var target = selected + direction;
            if (selected < 0 || target < 0 || target >= Config.Rules.Count)
            {
                return;
            }

            var item = Config.Rules[selected];
            Config.Rules.RemoveAt(selected);
            Config.Rules.Insert(target, item);
            ReloadRuleList();
            SelectRuleByConfigIndex(target);
        }

        private void TestRule(object sender, EventArgs args)
        {
            var rule = ReadEditorRule();
            if (rule == null)
            {
                return;
            }

            var device = _audio.FindRenderDevice(rule.DeviceId, rule.Device) ??
                         _audio.FindRenderDevice(rule.AlternateDeviceId, rule.AlternateDevice);
            if (device == null)
            {
                MessageBox.Show(this, "Audio device was not found.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!_roleConsole.Checked && !_roleMultimedia.Checked && !_roleCommunications.Checked)
            {
                MessageBox.Show(this, "Enable at least one output role.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _audio.SetDefaultRenderDevice(device.Id, _roleConsole.Checked, _roleMultimedia.Checked, _roleCommunications.Checked);
            _audio.ApplySessionSettings(rule.TargetProcesses, rule.SessionVolumeEnabled, rule.SessionVolumePercent, rule.SessionMuteEnabled, rule.SessionMuted);
            RefreshCurrentDeviceLabel();
            MessageBox.Show(this, "Switched to " + device.Name + ".", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ValidateRules(object sender, EventArgs args)
        {
            var messages = new List<string>();
            if (Config.Rules.Count == 0)
            {
                messages.Add("No rules are configured.");
            }
            if (Config.Rules.Count > 0 && Config.Rules.All(rule => !rule.Enabled))
            {
                messages.Add("All rules are disabled.");
            }

            Config.DeviceAliases = ParseAliases(_deviceAliases.Text);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in Config.Rules)
            {
                if (!string.IsNullOrWhiteSpace(rule.Name) && !names.Add(rule.Name))
                {
                    messages.Add("Duplicate rule name: " + rule.Name);
                }
                if (string.IsNullOrWhiteSpace(rule.ForegroundProcess) && string.IsNullOrWhiteSpace(rule.RunningProcess))
                {
                    messages.Add("Rule has no process: " + (rule.Name ?? "unnamed"));
                }
                if (_audio.FindRenderDevice(rule.DeviceId, rule.Device, Config.DeviceAliases) == null)
                {
                    messages.Add("Primary device not found for rule: " + (rule.Name ?? "unnamed"));
                }
                if (rule.HasAlternateDevice && _audio.FindRenderDevice(rule.AlternateDeviceId, rule.AlternateDevice, Config.DeviceAliases) == null)
                {
                    messages.Add("Alternate device not found for rule: " + (rule.Name ?? "unnamed"));
                }
            }

            if (Config.HasFallbackDevice && _audio.FindRenderDevice(Config.FallbackDeviceId, Config.FallbackDevice, Config.DeviceAliases) == null)
            {
                messages.Add("Fallback device was not found.");
            }

            messages.AddRange(RuleAnalyzer.FindWarnings(Config));

            MessageBox.Show(
                this,
                messages.Count == 0 ? "No validation issues found." : string.Join(Environment.NewLine, messages),
                "Rule validation",
                MessageBoxButtons.OK,
                messages.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void SaveAndClose(object sender, EventArgs args)
        {
            Config.PollMilliseconds = (int)_poll.Value;
            Config.SwitchCooldownMilliseconds = (int)_cooldown.Value;
            var fallback = GetSelectedDevice(_fallback);
            Config.FallbackDevice = fallback.Name;
            Config.FallbackDeviceId = fallback.Id;
            Config.LogEnabled = _log.Checked;
            Config.NotificationsEnabled = _notifications.Checked;
            Config.NotifyFailures = _notifyFailures.Checked;
            Config.Paused = _paused.Checked;
            Config.ProcessStartWatcherEnabled = _processStartWatcher.Checked;
            Config.DeviceChangeWatcherEnabled = _deviceChangeWatcher.Checked;
            Config.RoleConsole = _roleConsole.Checked;
            Config.RoleMultimedia = _roleMultimedia.Checked;
            Config.RoleCommunications = _roleCommunications.Checked;
            Config.ActiveProfile = string.IsNullOrWhiteSpace(_activeProfile.Text) ? "Default" : _activeProfile.Text.Trim();
            Config.DeviceAliases = ParseAliases(_deviceAliases.Text);
            Config.ProcessExitAction = _exitAction.SelectedIndex == 1
                ? ProcessExitAction.PreviousDevice
                : _exitAction.SelectedIndex == 2 ? ProcessExitAction.None : ProcessExitAction.FallbackDevice;
            StartupManager.SetEnabled(_startup.Checked);
            DialogResult = DialogResult.OK;
            Close();
        }

        private AudioRule ReadEditorRule()
        {
            var processName = NormalizeProcess(_process.Text);
            var device = GetSelectedDevice(_device);
            var alternateDevice = GetSelectedDevice(_alternateDevice);
            if (string.IsNullOrWhiteSpace(processName))
            {
                MessageBox.Show(this, "Select or enter a process.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            if (string.IsNullOrWhiteSpace(device.Name) && string.IsNullOrWhiteSpace(device.Id))
            {
                MessageBox.Show(this, "Select or enter an audio device.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            return new AudioRule
            {
                Name = string.IsNullOrWhiteSpace(_name.Text) ? processName : _name.Text.Trim(),
                Profile = string.IsNullOrWhiteSpace(_profile.Text) ? "Default" : _profile.Text.Trim(),
                Enabled = _enabled.Checked,
                ForegroundProcess = _mode.SelectedIndex == 0 ? processName : null,
                RunningProcess = _mode.SelectedIndex == 1 ? processName : null,
                WindowTitle = _windowTitle.Text.Trim(),
                Device = device.Name,
                DeviceId = device.Id,
                AlternateDevice = alternateDevice.Name,
                AlternateDeviceId = alternateDevice.Id,
                RetryCount = (int)_startRetryCount.Value,
                RetryDelayMilliseconds = (int)_startRetryDelay.Value,
                ExitDelayMilliseconds = (int)_exitDelay.Value,
                ExitActionOverride = ReadRuleExitAction(),
                SessionVolumeEnabled = _sessionVolumeEnabled.Checked,
                SessionVolumePercent = (int)_sessionVolume.Value,
                SessionMuteEnabled = _sessionMuteEnabled.Checked,
                SessionMuted = _sessionMuted.Checked
            };
        }

        private void LoadSelectedRule()
        {
            var configIndex = GetSelectedRuleIndex();
            if (configIndex < 0)
            {
                return;
            }

            var rule = Config.Rules[configIndex];
            _name.Text = rule.Name ?? "";
            _profile.Text = string.IsNullOrWhiteSpace(rule.Profile) ? "Default" : rule.Profile;
            _enabled.Checked = rule.Enabled;
            _windowTitle.Text = rule.WindowTitle ?? "";
            if (!string.IsNullOrWhiteSpace(rule.ForegroundProcess))
            {
                _mode.SelectedIndex = 0;
                _process.Text = rule.ForegroundProcess;
            }
            else
            {
                _mode.SelectedIndex = 1;
                _process.Text = rule.RunningProcess ?? "";
            }
            SelectDevice(_device, rule.DeviceId, rule.Device);
            SelectDevice(_alternateDevice, rule.AlternateDeviceId, rule.AlternateDevice);
            _startRetryCount.Value = Math.Max(_startRetryCount.Minimum, Math.Min(_startRetryCount.Maximum, rule.RetryCount));
            _startRetryDelay.Value = Math.Max(_startRetryDelay.Minimum, Math.Min(_startRetryDelay.Maximum, rule.RetryDelayMilliseconds));
            _exitDelay.Value = Math.Max(_exitDelay.Minimum, Math.Min(_exitDelay.Maximum, rule.ExitDelayMilliseconds));
            _ruleExitAction.SelectedIndex = rule.ExitActionOverride == ProcessExitAction.FallbackDevice
                ? 1
                : rule.ExitActionOverride == ProcessExitAction.PreviousDevice ? 2 : rule.ExitActionOverride == ProcessExitAction.None ? 3 : 0;
            _sessionVolumeEnabled.Checked = rule.SessionVolumeEnabled;
            _sessionVolume.Value = Math.Max(_sessionVolume.Minimum, Math.Min(_sessionVolume.Maximum, rule.SessionVolumePercent));
            _sessionMuteEnabled.Checked = rule.SessionMuteEnabled;
            _sessionMuted.Checked = rule.SessionMuted;
        }

        private void ReloadRuleList()
        {
            var matching = GetMatchingRuleIndexes();
            var filter = (_ruleFilter.Text ?? "").Trim();
            _rules.Items.Clear();
            _visibleRuleIndexes.Clear();
            for (var i = 0; i < Config.Rules.Count; i++)
            {
                if (!RuleMatchesFilter(Config.Rules[i], filter))
                {
                    continue;
                }

                _visibleRuleIndexes.Add(i);
                _rules.Items.Add((matching.Contains(i) ? ">> " : "") + (i + 1).ToString("00") + ". " + Config.Rules[i]);
            }
            RefreshStatusSummaries(matching);
            RefreshProfileChoices();
        }

        private void DisableRuleFor30Minutes(object sender, EventArgs args)
        {
            var configIndex = GetSelectedRuleIndex();
            if (configIndex < 0)
            {
                return;
            }

            Config.Rules[configIndex].DisabledUntilUtc = DateTimeOffset.UtcNow.AddMinutes(30);
            Config.Rules[configIndex].DisabledUntilRestart = false;
            ReloadRuleList();
            SelectRuleByConfigIndex(configIndex);
        }

        private void DisableRuleUntilRestart(object sender, EventArgs args)
        {
            var configIndex = GetSelectedRuleIndex();
            if (configIndex < 0)
            {
                return;
            }

            Config.Rules[configIndex].DisabledUntilRestart = true;
            Config.Rules[configIndex].DisabledUntilUtc = null;
            ReloadRuleList();
            SelectRuleByConfigIndex(configIndex);
        }

        private void ClearTemporaryDisable(object sender, EventArgs args)
        {
            var configIndex = GetSelectedRuleIndex();
            if (configIndex < 0)
            {
                return;
            }

            Config.Rules[configIndex].DisabledUntilRestart = false;
            Config.Rules[configIndex].DisabledUntilUtc = null;
            ReloadRuleList();
            SelectRuleByConfigIndex(configIndex);
        }

        private void PreviewRules(object sender, EventArgs args)
        {
            Config.ActiveProfile = string.IsNullOrWhiteSpace(_activeProfile.Text) ? "Default" : _activeProfile.Text.Trim();
            Config.DeviceAliases = ParseAliases(_deviceAliases.Text);
            MessageBox.Show(this, RulePreview.Build(Config, _audio), "Rule preview", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private HashSet<int> GetMatchingRuleIndexes()
        {
            var result = new HashSet<int>();
            var foreground = ForegroundWindowReader.GetForegroundWindowInfo();
            var running = new HashSet<string>(
                Process.GetProcesses()
                    .Select(p => SafeProcessName(p))
                    .Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < Config.Rules.Count; i++)
            {
                if (Config.IsRuleInActiveProfile(Config.Rules[i]) && Config.Rules[i].IsMatch(foreground, running))
                {
                    result.Add(i);
                }
            }

            return result;
        }

        private void RefreshStatusSummaries(HashSet<int> matching)
        {
            if (matching.Count == 0)
            {
                _matchingRules.Text = "none";
            }
            else
            {
                _matchingRules.Text = string.Join(", ", matching.Select(index => Config.Rules[index].Name ?? "unnamed"));
            }

            _history.Items.Clear();
            foreach (var item in _switchHistory)
            {
                _history.Items.Add(item.ToString());
            }
        }

        private int GetSelectedRuleIndex()
        {
            return _rules.SelectedIndex >= 0 && _rules.SelectedIndex < _visibleRuleIndexes.Count
                ? _visibleRuleIndexes[_rules.SelectedIndex]
                : -1;
        }

        private void SelectRuleByConfigIndex(int configIndex)
        {
            var visibleIndex = _visibleRuleIndexes.IndexOf(configIndex);
            if (visibleIndex >= 0)
            {
                _rules.SelectedIndex = visibleIndex;
            }
        }

        private static bool RuleMatchesFilter(AudioRule rule, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                return true;
            }

            return ContainsIgnoreCase(rule.Name, filter) ||
                   ContainsIgnoreCase(rule.Profile, filter) ||
                   ContainsIgnoreCase(rule.ForegroundProcess, filter) ||
                   ContainsIgnoreCase(rule.RunningProcess, filter) ||
                   ContainsIgnoreCase(rule.WindowTitle, filter) ||
                   ContainsIgnoreCase(rule.Device, filter) ||
                   ContainsIgnoreCase(rule.AlternateDevice, filter);
        }

        private static bool ContainsIgnoreCase(string value, string filter)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void RefreshProfileChoices()
        {
            var current = string.IsNullOrWhiteSpace(_activeProfile.Text) ? Config.ActiveProfile : _activeProfile.Text;
            var profiles = Config.Rules
                .Select(rule => string.IsNullOrWhiteSpace(rule.Profile) ? "Default" : rule.Profile)
                .Concat(new[] { "Default", "All" })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(profile => profile.Equals("All", StringComparison.OrdinalIgnoreCase) ? "" : profile, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _activeProfile.Items.Clear();
            foreach (var profile in profiles)
            {
                _activeProfile.Items.Add(profile);
            }
            _activeProfile.Text = string.IsNullOrWhiteSpace(current) ? "Default" : current;
        }

        private ProcessExitAction? ReadRuleExitAction()
        {
            if (_ruleExitAction.SelectedIndex == 1)
            {
                return ProcessExitAction.FallbackDevice;
            }
            if (_ruleExitAction.SelectedIndex == 2)
            {
                return ProcessExitAction.PreviousDevice;
            }
            if (_ruleExitAction.SelectedIndex == 3)
            {
                return ProcessExitAction.None;
            }

            return null;
        }

        private static string FormatAliases(IReadOnlyList<DeviceAlias> aliases)
        {
            if (aliases == null || aliases.Count == 0)
            {
                return "";
            }

            return string.Join(Environment.NewLine, aliases.Select(alias =>
                (alias.Alias ?? "") + "=" + (alias.Device ?? "") + (string.IsNullOrWhiteSpace(alias.DeviceId) ? "" : "|" + alias.DeviceId)));
        }

        private static List<DeviceAlias> ParseAliases(string text)
        {
            var result = new List<DeviceAlias>();
            foreach (var line in (text ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(new[] { '=' }, 2);
                if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]))
                {
                    continue;
                }

                var target = parts[1].Split(new[] { '|' }, 2);
                result.Add(new DeviceAlias
                {
                    Alias = parts[0].Trim(),
                    Device = target[0].Trim(),
                    DeviceId = target.Length > 1 ? target[1].Trim() : ""
                });
            }

            return result;
        }

        private void UndoChanges(object sender, EventArgs args)
        {
            Config = _originalConfig.Clone();
            LoadConfig();
        }

        private static string SafeProcessName(Process process)
        {
            try
            {
                return process.ProcessName;
            }
            catch
            {
                return null;
            }
            finally
            {
                process.Dispose();
            }
        }

        private static string NormalizeProcess(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(trimmed)
                : trimmed;
        }

        private void SelectDevice(ComboBox combo, string deviceId, string deviceName)
        {
            foreach (var item in combo.Items)
            {
                var device = item as DeviceChoice;
                if (device != null && !string.IsNullOrWhiteSpace(deviceId) &&
                    string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }

            combo.Text = deviceName ?? "";
        }

        private static DeviceSelection GetSelectedDevice(ComboBox combo)
        {
            var selected = combo.SelectedItem as DeviceChoice;
            if (selected != null && string.Equals(combo.Text, selected.Name, StringComparison.OrdinalIgnoreCase))
            {
                return new DeviceSelection(selected.Id, selected.Name);
            }

            return new DeviceSelection(null, combo.Text.Trim());
        }

        private void RefreshCurrentDeviceLabel()
        {
            try
            {
                var current = _audio.GetDefaultRenderDevice();
                _currentDevice.Text = current == null ? "unknown" : current.Name;
            }
            catch
            {
                _currentDevice.Text = "unknown";
            }
        }
    }

    internal sealed class DeviceChoice
    {
        public DeviceChoice(AudioDevice device)
        {
            Id = device.Id;
            Name = device.Name;
        }

        public string Id { get; private set; }
        public string Name { get; private set; }

        public override string ToString()
        {
            return Name;
        }
    }

    internal sealed class LogViewerForm : Form
    {
        private readonly string _path;
        private readonly TextBox _text = new TextBox();

        public LogViewerForm(string path)
        {
            _path = path;
            Text = "Primary Audio Switcher Log";
            StartPosition = FormStartPosition.CenterParent;
            Width = 900;
            Height = 560;
            MinimumSize = new Size(640, 360);
            BuildUi();
            LoadLog();
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1,
                Padding = new Padding(10)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            Controls.Add(root);

            _text.Dock = DockStyle.Fill;
            _text.Multiline = true;
            _text.ReadOnly = true;
            _text.ScrollBars = ScrollBars.Both;
            _text.WordWrap = false;
            _text.Font = new Font(FontFamily.GenericMonospace, 9f);
            root.Controls.Add(_text, 0, 0);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft
            };
            buttons.Controls.Add(MakeButton("Close", delegate { Close(); }));
            buttons.Controls.Add(MakeButton("Refresh", delegate { LoadLog(); }));
            buttons.Controls.Add(MakeButton("Clear", delegate { ClearLog(); }));
            root.Controls.Add(buttons, 0, 1);
        }

        private static Button MakeButton(string text, EventHandler handler)
        {
            var button = new Button
            {
                Text = text,
                Width = 90,
                Height = 28
            };
            button.Click += handler;
            return button;
        }

        private void LoadLog()
        {
            try
            {
                _text.Text = File.Exists(_path) ? File.ReadAllText(_path) : "";
                _text.SelectionStart = _text.TextLength;
                _text.ScrollToCaret();
            }
            catch (Exception ex)
            {
                _text.Text = ex.Message;
            }
        }

        private void ClearLog()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(_path, "");
                LoadLog();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Log clear error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    internal sealed class DiagnosticsForm : Form
    {
        private readonly AppConfig _config;
        private readonly AudioDeviceManager _audio;
        private readonly string _status;
        private readonly TextBox _text = new TextBox();

        public DiagnosticsForm(AppConfig config, AudioDeviceManager audio, string status)
        {
            _config = config.Clone();
            _audio = audio;
            _status = status;
            Text = "Primary Audio Switcher Diagnostics";
            StartPosition = FormStartPosition.CenterParent;
            Width = 900;
            Height = 640;
            MinimumSize = new Size(700, 420);
            BuildUi();
            RefreshDiagnostics();
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1,
                Padding = new Padding(10)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            Controls.Add(root);

            _text.Dock = DockStyle.Fill;
            _text.Multiline = true;
            _text.ReadOnly = true;
            _text.ScrollBars = ScrollBars.Both;
            _text.WordWrap = false;
            _text.Font = new Font(FontFamily.GenericMonospace, 9f);
            root.Controls.Add(_text, 0, 0);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft
            };
            buttons.Controls.Add(MakeButton("Close", delegate { Close(); }));
            buttons.Controls.Add(MakeButton("Refresh", delegate { RefreshDiagnostics(); }));
            root.Controls.Add(buttons, 0, 1);
        }

        private static Button MakeButton(string text, EventHandler handler)
        {
            var button = new Button
            {
                Text = text,
                Width = 90,
                Height = 28
            };
            button.Click += handler;
            return button;
        }

        private void RefreshDiagnostics()
        {
            _text.Text = DiagnosticsReport.Build(_config, _audio, _status);
        }
    }

    internal static class DiagnosticsReport
    {
        public static string Build(AppConfig config, AudioDeviceManager audio, string status)
        {
            var builder = new StringBuilder();
            var foreground = ForegroundWindowReader.GetForegroundWindowInfo();
            var running = new HashSet<string>(
                Process.GetProcesses()
                    .Select(p => SafeProcessName(p))
                    .Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);

            builder.AppendLine("Status: " + status);
            builder.AppendLine("Foreground process: " + (foreground.ProcessName ?? "unknown"));
            builder.AppendLine("Foreground title: " + (foreground.Title ?? ""));
            builder.AppendLine("Current default render: " + CurrentDeviceName(audio));
            builder.AppendLine();
            builder.AppendLine("Preview:");
            builder.AppendLine(RulePreview.Build(config, audio).TrimEnd());
            builder.AppendLine();

            builder.AppendLine("Matching rules:");
            foreach (var rule in config.Rules)
            {
                if (config.IsRuleInActiveProfile(rule) && rule.IsMatch(foreground, running))
                {
                    builder.AppendLine("  " + rule);
                }
            }
            builder.AppendLine();

            builder.AppendLine("Rules:");
            foreach (var rule in config.Rules)
            {
                builder.AppendLine("  " + rule);
            }
            builder.AppendLine();

            builder.AppendLine("Rule warnings:");
            var warnings = RuleAnalyzer.FindWarnings(config).ToList();
            if (warnings.Count == 0)
            {
                builder.AppendLine("  none");
            }
            else
            {
                foreach (var warning in warnings)
                {
                    builder.AppendLine("  " + warning);
                }
            }
            builder.AppendLine();

            builder.AppendLine("Render devices:");
            try
            {
                foreach (var device in audio.ListRenderDevices())
                {
                    builder.AppendLine("  " + device.Name);
                    builder.AppendLine("    " + device.Id);
                }
            }
            catch (Exception ex)
            {
                builder.AppendLine("  " + ex.Message);
            }
            builder.AppendLine();

            builder.AppendLine("Running processes:");
            foreach (var process in running.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine("  " + process);
            }

            return builder.ToString();
        }

        private static string CurrentDeviceName(AudioDeviceManager audio)
        {
            try
            {
                var current = audio.GetDefaultRenderDevice();
                return current == null ? "unknown" : current.Name;
            }
            catch
            {
                return "unknown";
            }
        }

        private static string SafeProcessName(Process process)
        {
            try
            {
                return process.ProcessName;
            }
            catch
            {
                return null;
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    internal sealed class DeviceSelection
    {
        public DeviceSelection(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; private set; }
        public string Name { get; private set; }
    }

    internal static class ConfigBackups
    {
        public static void CreateTimestampedBackup(string path, int keepCount)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                var directory = Path.GetDirectoryName(path);
                var backupPath = Path.Combine(directory, "config." + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + ".bak.xml");
                File.Copy(path, backupPath, true);
                foreach (var oldBackup in Directory.GetFiles(directory, "config.*.bak.xml")
                    .OrderByDescending(File.GetCreationTimeUtc)
                    .Skip(Math.Max(1, keepCount)))
                {
                    File.Delete(oldBackup);
                }
            }
            catch
            {
            }
        }

        public static string LatestBackupPath(string path)
        {
            var directory = Path.GetDirectoryName(path);
            var latest = Directory.Exists(directory)
                ? Directory.GetFiles(directory, "config.*.bak.xml")
                    .OrderByDescending(File.GetCreationTimeUtc)
                    .FirstOrDefault()
                : null;

            return latest ?? path + ".bak";
        }
    }

    internal static class RuleAnalyzer
    {
        public static IEnumerable<string> FindWarnings(AppConfig config)
        {
            var warnings = new List<string>();
            for (var i = 0; i < config.Rules.Count; i++)
            {
                var current = config.Rules[i];
                if (!current.Enabled)
                {
                    continue;
                }

                for (var j = 0; j < i; j++)
                {
                    var previous = config.Rules[j];
                    if (!previous.Enabled)
                    {
                        continue;
                    }

                    if (RulesOverlap(previous, current))
                    {
                        warnings.Add("Rule may be hidden by higher priority rule: " + (current.Name ?? "unnamed") + " after " + (previous.Name ?? "unnamed"));
                        break;
                    }
                }
            }

            foreach (var group in config.Rules
                .Where(rule => rule.Enabled)
                .GroupBy(rule => (rule.Profile ?? "Default") + "|" + (rule.ForegroundProcess ?? "") + "|" + (rule.RunningProcess ?? "") + "|" + (rule.WindowTitle ?? ""), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1))
            {
                warnings.Add("Duplicate match condition: " + string.Join(", ", group.Select(rule => rule.Name ?? "unnamed")));
            }

            return warnings;
        }

        private static bool RulesOverlap(AudioRule a, AudioRule b)
        {
            if (!string.Equals(a.Profile ?? "Default", b.Profile ?? "Default", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(a.ForegroundProcess) && !string.IsNullOrWhiteSpace(b.ForegroundProcess))
            {
                return ProcessesOverlap(a.ForegroundProcess, b.ForegroundProcess) &&
                       (string.IsNullOrWhiteSpace(a.WindowTitle) ||
                        string.IsNullOrWhiteSpace(b.WindowTitle) ||
                        a.WindowTitle.IndexOf(b.WindowTitle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        b.WindowTitle.IndexOf(a.WindowTitle, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (!string.IsNullOrWhiteSpace(a.RunningProcess) && !string.IsNullOrWhiteSpace(b.RunningProcess))
            {
                return ProcessesOverlap(a.RunningProcess, b.RunningProcess);
            }

            return false;
        }

        private static bool ProcessesOverlap(string left, string right)
        {
            var leftSet = SplitProcesses(left);
            var rightSet = SplitProcesses(right);
            return leftSet.Any(process => rightSet.Contains(process, StringComparer.OrdinalIgnoreCase));
        }

        private static List<string> SplitProcesses(string value)
        {
            return (value ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(process => process.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileNameWithoutExtension(process.Trim())
                    : process.Trim())
                .Where(process => !string.IsNullOrWhiteSpace(process))
                .ToList();
        }
    }

    internal static class RulePreview
    {
        public static string Build(AppConfig config, AudioDeviceManager audio)
        {
            var foreground = ForegroundWindowReader.GetForegroundWindowInfo();
            var running = new HashSet<string>(
                Process.GetProcesses()
                    .Select(p => SafeProcessName(p))
                    .Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);
            var builder = new StringBuilder();
            builder.AppendLine("Active profile: " + (config.ActiveProfile ?? "Default"));
            builder.AppendLine("Foreground: " + (foreground.ProcessName ?? "unknown"));
            builder.AppendLine("Title: " + (foreground.Title ?? ""));
            builder.AppendLine();

            foreach (var rule in config.Rules)
            {
                if (!config.IsRuleInActiveProfile(rule) || rule.IsTemporarilyDisabled)
                {
                    continue;
                }

                if (rule.IsMatch(foreground, running))
                {
                    var device = audio.FindRenderDevice(rule.DeviceId, rule.Device, config.DeviceAliases) ??
                                 audio.FindRenderDevice(rule.AlternateDeviceId, rule.AlternateDevice, config.DeviceAliases);
                    builder.AppendLine("Matched rule: " + (rule.Name ?? "unnamed"));
                    builder.AppendLine("Target device: " + (device == null ? "not found" : device.Name));
                    builder.AppendLine("Exit action: " + ProcessExitActions.ToConfigValue(rule.ExitActionOverride ?? config.ProcessExitAction));
                    return builder.ToString();
                }
            }

            builder.AppendLine("No rule matched.");
            if (config.HasFallbackDevice)
            {
                var fallback = audio.FindRenderDevice(config.FallbackDeviceId, config.FallbackDevice, config.DeviceAliases);
                builder.AppendLine("Fallback device: " + (fallback == null ? "not found" : fallback.Name));
            }

            return builder.ToString();
        }

        private static string SafeProcessName(Process process)
        {
            try
            {
                return process.ProcessName;
            }
            catch
            {
                return null;
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    internal static class HotKeyManager
    {
        private const int WmHotKey = 0x0312;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private static HotKeyMessageFilter _filter;

        public static void Register(Control target, Action togglePause, Action evaluate, Action cycleDevice, Action cycleProfile)
        {
            Unregister(target);
            _filter = new HotKeyMessageFilter(target.Handle, togglePause, evaluate, cycleDevice, cycleProfile);
            Application.AddMessageFilter(_filter);
            RegisterHotKey(target.Handle, 1, ModControl | ModAlt, (uint)Keys.P);
            RegisterHotKey(target.Handle, 2, ModControl | ModAlt, (uint)Keys.R);
            RegisterHotKey(target.Handle, 3, ModControl | ModAlt, (uint)Keys.D);
            RegisterHotKey(target.Handle, 4, ModControl | ModAlt, (uint)Keys.O);
        }

        public static void Unregister(Control target)
        {
            if (_filter != null)
            {
                Application.RemoveMessageFilter(_filter);
                _filter = null;
            }

            if (target != null && target.IsHandleCreated)
            {
                for (var id = 1; id <= 4; id++)
                {
                    UnregisterHotKey(target.Handle, id);
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private sealed class HotKeyMessageFilter : IMessageFilter
        {
            private readonly IntPtr _handle;
            private readonly Action _togglePause;
            private readonly Action _evaluate;
            private readonly Action _cycleDevice;
            private readonly Action _cycleProfile;

            public HotKeyMessageFilter(IntPtr handle, Action togglePause, Action evaluate, Action cycleDevice, Action cycleProfile)
            {
                _handle = handle;
                _togglePause = togglePause;
                _evaluate = evaluate;
                _cycleDevice = cycleDevice;
                _cycleProfile = cycleProfile;
            }

            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != WmHotKey || m.HWnd != _handle)
                {
                    return false;
                }

                var id = m.WParam.ToInt32();
                if (id == 1) _togglePause();
                if (id == 2) _evaluate();
                if (id == 3) _cycleDevice();
                if (id == 4) _cycleProfile();
                return true;
            }
        }
    }

    internal static class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "PrimaryAudioSwitcher";

        public static bool IsEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                var value = key == null ? null : key.GetValue(ValueName) as string;
                return !string.IsNullOrWhiteSpace(value);
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true) ??
                             Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (enabled)
                {
                    key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }
    }

    internal static class ForegroundWindowReader
    {
        public static string GetForegroundProcessName()
        {
            return GetForegroundWindowInfo().ProcessName;
        }

        public static ForegroundWindowInfo GetForegroundWindowInfo()
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return new ForegroundWindowInfo(null, null);
            }

            uint processId;
            GetWindowThreadProcessId(hwnd, out processId);
            if (processId == 0)
            {
                return new ForegroundWindowInfo(null, ReadWindowTitle(hwnd));
            }

            try
            {
                using (var process = Process.GetProcessById((int)processId))
                {
                    return new ForegroundWindowInfo(process.ProcessName, ReadWindowTitle(hwnd));
                }
            }
            catch
            {
                return new ForegroundWindowInfo(null, ReadWindowTitle(hwnd));
            }
        }

        private static string ReadWindowTitle(IntPtr hwnd)
        {
            var length = GetWindowTextLength(hwnd);
            if (length <= 0)
            {
                return "";
            }

            var builder = new StringBuilder(length + 1);
            GetWindowText(hwnd, builder, builder.Capacity);
            return builder.ToString();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);
    }

    internal sealed class ForegroundWindowInfo
    {
        public ForegroundWindowInfo(string processName, string title)
        {
            ProcessName = processName;
            Title = title;
        }

        public string ProcessName { get; private set; }
        public string Title { get; private set; }
    }

    internal sealed class AudioDeviceManager
    {
        public List<AudioDevice> ListRenderDevices()
        {
            var result = new List<AudioDevice>();
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(ComIds.MMDeviceEnumerator));
            IMMDeviceCollection collection = null;

            try
            {
                Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.Active, out collection));
                uint count;
                Marshal.ThrowExceptionForHR(collection.GetCount(out count));
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    try
                    {
                        Marshal.ThrowExceptionForHR(collection.Item(i, out device));
                        result.Add(new AudioDevice(ReadId(device), ReadFriendlyName(device)));
                    }
                    finally
                    {
                        if (device != null)
                        {
                            Marshal.ReleaseComObject(device);
                        }
                    }
                }
            }
            finally
            {
                if (collection != null)
                {
                    Marshal.ReleaseComObject(collection);
                }
                Marshal.ReleaseComObject(enumerator);
            }

            return result.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public AudioDevice FindRenderDevice(string deviceId, string deviceMatch)
        {
            return FindRenderDevice(deviceId, deviceMatch, null);
        }

        public AudioDevice FindRenderDevice(string deviceId, string deviceMatch, IReadOnlyList<DeviceAlias> aliases)
        {
            var devices = ListRenderDevices();
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                var exact = devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    return exact;
                }
            }

            if (!string.IsNullOrWhiteSpace(deviceMatch) && aliases != null)
            {
                var alias = aliases.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Alias) &&
                    item.Alias.Equals(deviceMatch, StringComparison.OrdinalIgnoreCase));
                if (alias != null)
                {
                    var aliased = FindInDevices(devices, alias.DeviceId, alias.Device);
                    if (aliased != null)
                    {
                        return aliased;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(deviceMatch))
            {
                return null;
            }

            return FindInDevices(devices, null, deviceMatch);
        }

        private static AudioDevice FindInDevices(IEnumerable<AudioDevice> devices, string deviceId, string deviceMatch)
        {
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                var exact = devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    return exact;
                }
            }

            if (string.IsNullOrWhiteSpace(deviceMatch))
            {
                return null;
            }

            return devices.FirstOrDefault(d => d.Name.IndexOf(deviceMatch, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                               d.Id.IndexOf(deviceMatch, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public AudioDevice GetDefaultRenderDevice()
        {
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(ComIds.MMDeviceEnumerator));
            IMMDevice device = null;

            try
            {
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device));
                return new AudioDevice(ReadId(device), ReadFriendlyName(device));
            }
            finally
            {
                if (device != null)
                {
                    Marshal.ReleaseComObject(device);
                }
                Marshal.ReleaseComObject(enumerator);
            }
        }

        public void SetDefaultRenderDevice(string deviceId)
        {
            SetDefaultRenderDevice(deviceId, true, true, true);
        }

        public void SetDefaultRenderDevice(string deviceId, bool console, bool multimedia, bool communications)
        {
            var policy = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(ComIds.PolicyConfigClient));
            try
            {
                if (console)
                {
                    Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.eConsole));
                }
                if (multimedia)
                {
                    Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.eMultimedia));
                }
                if (communications)
                {
                    Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.eCommunications));
                }
            }
            finally
            {
                Marshal.ReleaseComObject(policy);
            }
        }

        public void ApplySessionSettings(IReadOnlyList<string> processNames, bool setVolume, int volumePercent, bool setMute, bool muted)
        {
            if ((processNames == null || processNames.Count == 0) || (!setVolume && !setMute))
            {
                return;
            }

            var targets = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
            var volume = Math.Max(0, Math.Min(100, volumePercent)) / 100f;
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(ComIds.MMDeviceEnumerator));
            IMMDeviceCollection collection = null;

            try
            {
                Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.Active, out collection));
                uint deviceCount;
                Marshal.ThrowExceptionForHR(collection.GetCount(out deviceCount));
                for (uint deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
                {
                    IMMDevice device = null;
                    IAudioSessionManager2 manager = null;
                    IAudioSessionEnumerator sessions = null;
                    try
                    {
                        Marshal.ThrowExceptionForHR(collection.Item(deviceIndex, out device));
                        var managerIid = typeof(IAudioSessionManager2).GUID;
                        object managerObject;
                        Marshal.ThrowExceptionForHR(device.Activate(ref managerIid, ClsCtx.All, IntPtr.Zero, out managerObject));
                        manager = (IAudioSessionManager2)managerObject;
                        Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out sessions));
                        int sessionCount;
                        Marshal.ThrowExceptionForHR(sessions.GetCount(out sessionCount));
                        for (var sessionIndex = 0; sessionIndex < sessionCount; sessionIndex++)
                        {
                            IAudioSessionControl2 control = null;
                            try
                            {
                                Marshal.ThrowExceptionForHR(sessions.GetSession(sessionIndex, out control));
                                uint processId;
                                Marshal.ThrowExceptionForHR(control.GetProcessId(out processId));
                                if (processId == 0)
                                {
                                    continue;
                                }

                                using (var process = Process.GetProcessById((int)processId))
                                {
                                    if (!targets.Contains(process.ProcessName))
                                    {
                                        continue;
                                    }
                                }

                                var simpleVolume = (ISimpleAudioVolume)control;
                                var eventContext = Guid.Empty;
                                if (setVolume)
                                {
                                    Marshal.ThrowExceptionForHR(simpleVolume.SetMasterVolume(volume, ref eventContext));
                                }
                                if (setMute)
                                {
                                    Marshal.ThrowExceptionForHR(simpleVolume.SetMute(muted, ref eventContext));
                                }
                            }
                            catch
                            {
                            }
                            finally
                            {
                                if (control != null)
                                {
                                    Marshal.ReleaseComObject(control);
                                }
                            }
                        }
                    }
                    finally
                    {
                        if (sessions != null)
                        {
                            Marshal.ReleaseComObject(sessions);
                        }
                        if (manager != null)
                        {
                            Marshal.ReleaseComObject(manager);
                        }
                        if (device != null)
                        {
                            Marshal.ReleaseComObject(device);
                        }
                    }
                }
            }
            finally
            {
                if (collection != null)
                {
                    Marshal.ReleaseComObject(collection);
                }
                Marshal.ReleaseComObject(enumerator);
            }
        }

        private static string ReadId(IMMDevice device)
        {
            IntPtr idPtr;
            Marshal.ThrowExceptionForHR(device.GetId(out idPtr));
            try
            {
                return Marshal.PtrToStringUni(idPtr) ?? "";
            }
            finally
            {
                Marshal.FreeCoTaskMem(idPtr);
            }
        }

        private static string ReadFriendlyName(IMMDevice device)
        {
            IPropertyStore store = null;
            try
            {
                Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0, out store));
                var key = PropertyKeys.PkeyDeviceFriendlyName;
                PropVariant prop;
                Marshal.ThrowExceptionForHR(store.GetValue(ref key, out prop));
                try
                {
                    return prop.Value ?? "";
                }
                finally
                {
                    PropVariantClear(ref prop);
                }
            }
            finally
            {
                if (store != null)
                {
                    Marshal.ReleaseComObject(store);
                }
            }
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pvar);
    }

    internal sealed class AudioDevice
    {
        public AudioDevice(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; private set; }
        public string Name { get; private set; }
    }

    internal static class ComIds
    {
        public static readonly Guid MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        public static readonly Guid PolicyConfigClient = new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-C0F2EBB11C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        int GetCount(out uint count);
        int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        int Activate(ref Guid iid, ClsCtx clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
        int OpenPropertyStore(int storageAccess, out IPropertyStore properties);
        int GetId(out IntPtr id);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        int GetCount(out uint propertyCount);
        int GetAt(uint propertyIndex, out PropertyKey key);
        int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr audioSessionGuid, uint streamFlags, out IAudioSessionControl2 sessionControl);
        int GetSimpleAudioVolume(IntPtr audioSessionGuid, uint streamFlags, out ISimpleAudioVolume audioVolume);
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
        int RegisterSessionNotification(IntPtr sessionNotification);
        int UnregisterSessionNotification(IntPtr sessionNotification);
        int RegisterDuckNotification(string sessionId, IntPtr duckNotification);
        int UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        int GetCount(out int sessionCount);
        int GetSession(int sessionIndex, out IAudioSessionControl2 session);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        int GetState(out int state);
        int GetDisplayName(out IntPtr displayName);
        int SetDisplayName(string displayName, Guid eventContext);
        int GetIconPath(out IntPtr iconPath);
        int SetIconPath(string iconPath, Guid eventContext);
        int GetGroupingParam(out Guid groupingParam);
        int SetGroupingParam(Guid groupingParam, Guid eventContext);
        int RegisterAudioSessionNotification(IntPtr notifications);
        int UnregisterAudioSessionNotification(IntPtr notifications);
        int GetSessionIdentifier(out IntPtr sessionIdentifier);
        int GetSessionInstanceIdentifier(out IntPtr sessionInstanceIdentifier);
        int GetProcessId(out uint processId);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, ref Guid eventContext);
        int GetMasterVolume(out float level);
        int SetMute(bool isMuted, ref Guid eventContext);
        int GetMute(out bool isMuted);
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        int GetMixFormat();
        int GetDeviceFormat();
        int ResetDeviceFormat();
        int SetDeviceFormat();
        int GetProcessingPeriod();
        int SetProcessingPeriod();
        int GetShareMode();
        int SetShareMode();
        int GetPropertyValue();
        int SetPropertyValue();
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        int SetEndpointVisibility();
    }

    internal enum EDataFlow
    {
        eRender,
        eCapture,
        eAll
    }

    internal enum ERole
    {
        eConsole,
        eMultimedia,
        eCommunications
    }

    [Flags]
    internal enum DeviceState
    {
        Active = 0x00000001
    }

    [Flags]
    internal enum ClsCtx
    {
        InprocServer = 0x1,
        InprocHandler = 0x2,
        LocalServer = 0x4,
        RemoteServer = 0x10,
        All = InprocServer | InprocHandler | LocalServer | RemoteServer
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FmtId;
        public uint Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort ValueType;
        private readonly ushort _reserved1;
        private readonly ushort _reserved2;
        private readonly ushort _reserved3;
        private readonly IntPtr _value;
        private readonly int _value2;

        public string Value
        {
            get { return ValueType == 31 ? Marshal.PtrToStringUni(_value) : null; }
        }
    }

    internal static class PropertyKeys
    {
        public static readonly PropertyKey PkeyDeviceFriendlyName = new PropertyKey
        {
            FmtId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            Pid = 14
        };
    }
}
