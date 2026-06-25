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

            ApplyDevice(rule.DeviceId, rule.Device, rule.AlternateDeviceId, rule.AlternateDevice, rule.Name, foreground, force);
        }

        private void ApplyDevice(string deviceId, string deviceMatch, string ruleName, string foreground, bool force)
        {
            ApplyDevice(deviceId, deviceMatch, null, null, ruleName, foreground, force);
        }

        private void ApplyDevice(string deviceId, string deviceMatch, string alternateDeviceId, string alternateDeviceMatch, string ruleName, string foreground, bool force)
        {
            var device = _audio.FindRenderDevice(deviceId, deviceMatch);
            if (device == null)
            {
                if (!string.IsNullOrWhiteSpace(alternateDeviceId) || !string.IsNullOrWhiteSpace(alternateDeviceMatch))
                {
                    device = _audio.FindRenderDevice(alternateDeviceId, alternateDeviceMatch);
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
                return;
            }

            if (!force && IsSwitchCooldownActive())
            {
                SetStatus("Cooldown active. Current: " + CurrentDeviceName());
                return;
            }

            if (!force && string.Equals(_lastAppliedDeviceId, device.Id, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Rule: " + ruleName + " -> " + device.Name);
                return;
            }

            _audio.SetDefaultRenderDevice(device.Id);
            _lastAppliedDeviceId = device.Id;
            _lastSwitchAt = DateTimeOffset.Now;
            AddSwitchHistory(ruleName, device.Name, foreground, force);
            SetStatus("Rule: " + ruleName + " -> " + device.Name);
            ShowNotification("Audio device changed", device.Name + " (" + ruleName + ")");
            Log("Applied rule='" + ruleName + "' foreground='" + (foreground ?? "unknown") + "' device='" + device.Name + "'" + (force ? " force=true" : ""));
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
            if (string.IsNullOrWhiteSpace(normalized) || _config.ProcessExitAction == ProcessExitAction.None)
            {
                return;
            }

            var matchingRules = _config.Rules.Where(rule => rule.MatchesRunningProcess(normalized)).ToList();
            if (matchingRules.Count == 0)
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
                ScheduleProcessExitRestore(normalized, delay);
                return;
            }

            RestoreAfterProcessStop(normalized);
        }

        private void ScheduleProcessExitRestore(string processName, int delayMilliseconds)
        {
            var restoreTimer = new System.Windows.Forms.Timer { Interval = delayMilliseconds };
            restoreTimer.Tick += delegate
            {
                restoreTimer.Stop();
                restoreTimer.Dispose();
                if (!IsProcessRunning(processName))
                {
                    RestoreAfterProcessStop(processName);
                }
            };
            restoreTimer.Start();
        }

        private void RestoreAfterProcessStop(string normalized)
        {
            string previousDeviceId;
            AudioRule previousRule = null;
            previousDeviceId = null;
            if (_config.ProcessExitAction == ProcessExitAction.PreviousDevice)
            {
                foreach (var rule in _config.Rules)
                {
                    if (rule.MatchesRunningProcess(normalized) &&
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

            if (_config.ProcessExitAction == ProcessExitAction.FallbackDevice && _config.HasFallbackDevice)
            {
                ApplyDevice(_config.FallbackDeviceId, _config.FallbackDevice, "restore fallback after " + normalized, normalized, true);
            }
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
            var backupPath = _configPath + ".bak";
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
        public int SwitchCooldownMilliseconds { get; set; }
        public ProcessExitAction ProcessExitAction { get; set; }
        public List<AudioRule> Rules { get; set; }

        public bool HasFallbackDevice
        {
            get { return !string.IsNullOrWhiteSpace(FallbackDevice) || !string.IsNullOrWhiteSpace(FallbackDeviceId); }
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
                SwitchCooldownMilliseconds = (int?)root.Attribute("switchCooldownMilliseconds") ?? 0,
                ProcessExitAction = ProcessExitActions.Parse((string)root.Attribute("processExitAction")),
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
                new XAttribute("switchCooldownMilliseconds", SwitchCooldownMilliseconds),
                new XAttribute("processExitAction", ProcessExitActions.ToConfigValue(ProcessExitAction)));

            foreach (var rule in Rules)
            {
                var element = new XElement("Rule",
                    new XAttribute("name", rule.Name ?? ""),
                    new XAttribute("enabled", rule.Enabled ? "true" : "false"),
                    new XAttribute("windowTitle", rule.WindowTitle ?? ""),
                    new XAttribute("device", rule.Device ?? ""),
                    new XAttribute("deviceId", rule.DeviceId ?? ""),
                    new XAttribute("alternateDevice", rule.AlternateDevice ?? ""),
                    new XAttribute("alternateDeviceId", rule.AlternateDeviceId ?? ""),
                    new XAttribute("retryCount", rule.RetryCount),
                    new XAttribute("retryDelayMilliseconds", rule.RetryDelayMilliseconds),
                    new XAttribute("exitDelayMilliseconds", rule.ExitDelayMilliseconds));

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
                SwitchCooldownMilliseconds = SwitchCooldownMilliseconds,
                ProcessExitAction = ProcessExitAction,
                Rules = Rules.Select(r => r.Clone()).ToList()
            };
        }

        public static readonly string DefaultXml =
@"<?xml version=""1.0"" encoding=""utf-8""?>
<PrimaryAudioSwitcher pollMilliseconds=""1000"" fallbackDevice="""" fallbackDeviceId="""" log=""true"" notifications=""false"" paused=""false"" processStartWatcher=""true"" deviceChangeWatcher=""true"" switchCooldownMilliseconds=""0"" processExitAction=""fallback"">
  <!-- device is matched by substring against active Windows render device friendly names. -->
  <Rule name=""Game foreground"" enabled=""true"" foregroundProcess=""Game"" windowTitle="""" device=""Speakers"" deviceId="""" alternateDevice="""" alternateDeviceId="""" retryCount=""3"" retryDelayMilliseconds=""500"" exitDelayMilliseconds=""0"" />
  <Rule name=""Discord running"" enabled=""true"" runningProcess=""Discord"" windowTitle="""" device=""Headset"" deviceId="""" alternateDevice="""" alternateDeviceId="""" retryCount=""3"" retryDelayMilliseconds=""500"" exitDelayMilliseconds=""0"" />
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
    }

    internal sealed class AudioRule
    {
        public string Name { get; set; }
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

        public bool HasAlternateDevice
        {
            get { return !string.IsNullOrWhiteSpace(AlternateDevice) || !string.IsNullOrWhiteSpace(AlternateDeviceId); }
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
                ExitDelayMilliseconds = (int?)element.Attribute("exitDelayMilliseconds") ?? 0
            };
        }

        public AudioRule Clone()
        {
            return new AudioRule
            {
                Name = Name,
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
                ExitDelayMilliseconds = ExitDelayMilliseconds
            };
        }

        public XElement ToXml()
        {
            var element = new XElement("Rule",
                new XAttribute("name", Name ?? ""),
                new XAttribute("enabled", Enabled ? "true" : "false"),
                new XAttribute("windowTitle", WindowTitle ?? ""),
                new XAttribute("device", Device ?? ""),
                new XAttribute("deviceId", DeviceId ?? ""),
                new XAttribute("alternateDevice", AlternateDevice ?? ""),
                new XAttribute("alternateDeviceId", AlternateDeviceId ?? ""),
                new XAttribute("retryCount", RetryCount),
                new XAttribute("retryDelayMilliseconds", RetryDelayMilliseconds),
                new XAttribute("exitDelayMilliseconds", ExitDelayMilliseconds));

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
            return (Enabled ? "" : "[disabled] ") + (Name ?? "unnamed") + " [" + mode + ": " + (process ?? "") + "] -> " + (Device ?? "");
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
        private readonly ListBox _rules = new ListBox();
        private readonly TextBox _name = new TextBox();
        private readonly ComboBox _mode = new ComboBox();
        private readonly ComboBox _process = new ComboBox();
        private readonly TextBox _windowTitle = new TextBox();
        private readonly ComboBox _device = new ComboBox();
        private readonly ComboBox _alternateDevice = new ComboBox();
        private readonly ComboBox _fallback = new ComboBox();
        private readonly ComboBox _exitAction = new ComboBox();
        private readonly NumericUpDown _poll = new NumericUpDown();
        private readonly NumericUpDown _cooldown = new NumericUpDown();
        private readonly NumericUpDown _startRetryCount = new NumericUpDown();
        private readonly NumericUpDown _startRetryDelay = new NumericUpDown();
        private readonly NumericUpDown _exitDelay = new NumericUpDown();
        private readonly CheckBox _log = new CheckBox();
        private readonly CheckBox _processStartWatcher = new CheckBox();
        private readonly CheckBox _deviceChangeWatcher = new CheckBox();
        private readonly CheckBox _startup = new CheckBox();
        private readonly CheckBox _paused = new CheckBox();
        private readonly CheckBox _notifications = new CheckBox();
        private readonly CheckBox _enabled = new CheckBox();
        private readonly Label _currentDevice = new Label();
        private readonly Label _matchingRules = new Label();
        private readonly ListBox _history = new ListBox();
        private readonly IReadOnlyList<SwitchHistoryItem> _switchHistory;

        public SettingsForm(AppConfig config, AudioDeviceManager audio, IReadOnlyList<SwitchHistoryItem> switchHistory)
        {
            _audio = audio;
            _switchHistory = switchHistory;
            Config = config.Clone();

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

            _rules.Dock = DockStyle.Fill;
            _rules.SelectedIndexChanged += (sender, args) => LoadSelectedRule();
            root.Controls.Add(_rules, 0, 0);
            _enabled.Checked = true;

            var editor = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 11,
                Padding = new Padding(10, 0, 0, 0)
            };
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            for (var i = 0; i < 11; i++)
            {
                editor.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 9 ? 70 : 34));
            }
            root.Controls.Add(editor, 1, 0);

            AddLabel(editor, "Rule name", 0);
            editor.Controls.Add(_name, 1, 0);
            _name.Dock = DockStyle.Fill;
            _enabled.Text = "Enabled";
            _enabled.Dock = DockStyle.Fill;
            editor.Controls.Add(_enabled, 2, 0);

            AddLabel(editor, "Match type", 1);
            _mode.DropDownStyle = ComboBoxStyle.DropDownList;
            _mode.Items.Add("Foreground app");
            _mode.Items.Add("Running process");
            _mode.SelectedIndex = 0;
            editor.Controls.Add(_mode, 1, 1);
            editor.SetColumnSpan(_mode, 2);
            _mode.Dock = DockStyle.Fill;

            AddLabel(editor, "Process", 2);
            _process.DropDownStyle = ComboBoxStyle.DropDown;
            editor.Controls.Add(_process, 1, 2);
            _process.Dock = DockStyle.Fill;
            editor.Controls.Add(MakeButton("Browse exe", BrowseProcessFile), 2, 2);

            AddLabel(editor, "Title contains", 3);
            editor.Controls.Add(_windowTitle, 1, 3);
            editor.SetColumnSpan(_windowTitle, 2);
            _windowTitle.Dock = DockStyle.Fill;

            AddLabel(editor, "Audio device", 4);
            _device.DropDownStyle = ComboBoxStyle.DropDown;
            editor.Controls.Add(_device, 1, 4);
            _device.Dock = DockStyle.Fill;
            editor.Controls.Add(MakeButton("Refresh", RefreshLists), 2, 4);

            AddLabel(editor, "Alt device", 5);
            _alternateDevice.DropDownStyle = ComboBoxStyle.DropDown;
            editor.Controls.Add(_alternateDevice, 1, 5);
            editor.SetColumnSpan(_alternateDevice, 2);
            _alternateDevice.Dock = DockStyle.Fill;

            AddLabel(editor, "On exit delay", 6);
            _exitDelay.Minimum = 0;
            _exitDelay.Maximum = 60000;
            _exitDelay.Increment = 250;
            editor.Controls.Add(_exitDelay, 1, 6);
            editor.SetColumnSpan(_exitDelay, 2);

            AddLabel(editor, "Rule retry", 7);
            _startRetryCount.Minimum = 0;
            _startRetryCount.Maximum = 20;
            _startRetryCount.Increment = 1;
            editor.Controls.Add(_startRetryCount, 1, 7);
            _startRetryDelay.Minimum = 100;
            _startRetryDelay.Maximum = 10000;
            _startRetryDelay.Increment = 100;
            editor.Controls.Add(_startRetryDelay, 2, 7);

            var ruleButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            ruleButtons.Controls.Add(MakeButton("Add", AddRule));
            ruleButtons.Controls.Add(MakeButton("Update", UpdateRule));
            ruleButtons.Controls.Add(MakeButton("Delete", DeleteRule));
            ruleButtons.Controls.Add(MakeButton("Duplicate", DuplicateRule));
            ruleButtons.Controls.Add(MakeButton("Up", MoveRuleUp));
            ruleButtons.Controls.Add(MakeButton("Down", MoveRuleDown));
            ruleButtons.Controls.Add(MakeButton("Use current", UseCurrentForeground));
            ruleButtons.Controls.Add(MakeButton("Export", ExportRule));
            ruleButtons.Controls.Add(MakeButton("Import", ImportRule));
            ruleButtons.Controls.Add(MakeButton("Test", TestRule));
            ruleButtons.Controls.Add(MakeButton("Validate", ValidateRules));
            editor.Controls.Add(ruleButtons, 1, 9);
            editor.SetColumnSpan(ruleButtons, 2);
        }

        private void BuildGlobalTab(TabPage tab)
        {
            var editor = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 8,
                Padding = new Padding(10)
            };
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            for (var i = 0; i < 8; i++)
            {
                editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
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
            _poll.Value = Math.Max(_poll.Minimum, Math.Min(_poll.Maximum, Config.PollMilliseconds));
            _cooldown.Value = Math.Max(_cooldown.Minimum, Math.Min(_cooldown.Maximum, Config.SwitchCooldownMilliseconds));
            _processStartWatcher.Checked = Config.ProcessStartWatcherEnabled;
            _deviceChangeWatcher.Checked = Config.DeviceChangeWatcherEnabled;
            _paused.Checked = Config.Paused;
            _notifications.Checked = Config.NotificationsEnabled;
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
            _rules.SelectedIndex = _rules.Items.Count - 1;
        }

        private void UpdateRule(object sender, EventArgs args)
        {
            if (_rules.SelectedIndex < 0)
            {
                return;
            }

            var rule = ReadEditorRule();
            if (rule == null)
            {
                return;
            }

            Config.Rules[_rules.SelectedIndex] = rule;
            var selected = _rules.SelectedIndex;
            ReloadRuleList();
            _rules.SelectedIndex = selected;
        }

        private void DeleteRule(object sender, EventArgs args)
        {
            if (_rules.SelectedIndex < 0)
            {
                return;
            }

            var selected = _rules.SelectedIndex;
            Config.Rules.RemoveAt(selected);
            ReloadRuleList();
            if (_rules.Items.Count > 0)
            {
                _rules.SelectedIndex = Math.Min(selected, _rules.Items.Count - 1);
            }
        }

        private void DuplicateRule(object sender, EventArgs args)
        {
            if (_rules.SelectedIndex < 0)
            {
                return;
            }

            var copy = Config.Rules[_rules.SelectedIndex].Clone();
            copy.Name = (copy.Name ?? "unnamed") + " copy";
            var target = _rules.SelectedIndex + 1;
            Config.Rules.Insert(target, copy);
            ReloadRuleList();
            _rules.SelectedIndex = target;
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
                _rules.SelectedIndex = _rules.Items.Count - 1;
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
            var selected = _rules.SelectedIndex;
            var target = selected + direction;
            if (selected < 0 || target < 0 || target >= Config.Rules.Count)
            {
                return;
            }

            var item = Config.Rules[selected];
            Config.Rules.RemoveAt(selected);
            Config.Rules.Insert(target, item);
            ReloadRuleList();
            _rules.SelectedIndex = target;
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

            _audio.SetDefaultRenderDevice(device.Id);
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
                if (_audio.FindRenderDevice(rule.DeviceId, rule.Device) == null)
                {
                    messages.Add("Primary device not found for rule: " + (rule.Name ?? "unnamed"));
                }
                if (rule.HasAlternateDevice && _audio.FindRenderDevice(rule.AlternateDeviceId, rule.AlternateDevice) == null)
                {
                    messages.Add("Alternate device not found for rule: " + (rule.Name ?? "unnamed"));
                }
            }

            if (Config.HasFallbackDevice && _audio.FindRenderDevice(Config.FallbackDeviceId, Config.FallbackDevice) == null)
            {
                messages.Add("Fallback device was not found.");
            }

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
            Config.Paused = _paused.Checked;
            Config.ProcessStartWatcherEnabled = _processStartWatcher.Checked;
            Config.DeviceChangeWatcherEnabled = _deviceChangeWatcher.Checked;
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
                ExitDelayMilliseconds = (int)_exitDelay.Value
            };
        }

        private void LoadSelectedRule()
        {
            if (_rules.SelectedIndex < 0)
            {
                return;
            }

            var rule = Config.Rules[_rules.SelectedIndex];
            _name.Text = rule.Name ?? "";
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
        }

        private void ReloadRuleList()
        {
            var matching = GetMatchingRuleIndexes();
            _rules.Items.Clear();
            for (var i = 0; i < Config.Rules.Count; i++)
            {
                _rules.Items.Add((matching.Contains(i) ? ">> " : "") + Config.Rules[i]);
            }
            RefreshStatusSummaries(matching);
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
                if (Config.Rules[i].IsMatch(foreground, running))
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

            builder.AppendLine("Matching rules:");
            foreach (var rule in config.Rules)
            {
                if (rule.IsMatch(foreground, running))
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
            var devices = ListRenderDevices();
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
            var policy = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(ComIds.PolicyConfigClient));
            try
            {
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.eConsole));
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.eMultimedia));
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.eCommunications));
            }
            finally
            {
                Marshal.ReleaseComObject(policy);
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
