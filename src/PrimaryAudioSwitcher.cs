using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
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
        private AppConfig _config;
        private string _lastAppliedDeviceId;
        private string _lastStatus = "Starting";

        public TrayApplicationContext(string[] args)
        {
            _configPath = GetConfigPath(args);
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath));
            if (!File.Exists(_configPath))
            {
                File.WriteAllText(_configPath, AppConfig.DefaultXml, Encoding.UTF8);
            }

            _config = AppConfig.Load(_configPath);
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
            EvaluateRules();
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            var status = new ToolStripMenuItem("Status: Starting") { Enabled = false, Name = "status" };
            menu.Items.Add(status);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Settings", null, (sender, args) => OpenSettings());
            menu.Items.Add("Reload config", null, (sender, args) => ReloadConfig());
            menu.Items.Add("Open config", null, (sender, args) => Process.Start("notepad.exe", _configPath));
            menu.Items.Add("Write device list to log", null, (sender, args) => WriteDeviceList());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (sender, args) => ExitThread());
            return menu;
        }

        private void EvaluateRules()
        {
            try
            {
                var foreground = ForegroundWindowReader.GetForegroundProcessName();
                var running = new HashSet<string>(
                    Process.GetProcesses()
                        .Select(p => SafeProcessName(p))
                        .Where(name => !string.IsNullOrWhiteSpace(name)),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var rule in _config.Rules)
                {
                    if (!rule.IsMatch(foreground, running))
                    {
                        continue;
                    }

                    ApplyDevice(rule.Device, rule.Name, foreground);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_config.FallbackDevice))
                {
                    ApplyDevice(_config.FallbackDevice, "fallback", foreground);
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

        private void ApplyDevice(string deviceMatch, string ruleName, string foreground)
        {
            var device = _audio.FindRenderDevice(deviceMatch);
            if (device == null)
            {
                SetStatus("Device not found: " + deviceMatch);
                Log("Device not found for rule '" + ruleName + "': " + deviceMatch);
                return;
            }

            if (string.Equals(_lastAppliedDeviceId, device.Id, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Rule: " + ruleName + " -> " + device.Name);
                return;
            }

            _audio.SetDefaultRenderDevice(device.Id);
            _lastAppliedDeviceId = device.Id;
            SetStatus("Rule: " + ruleName + " -> " + device.Name);
            Log("Applied rule='" + ruleName + "' foreground='" + (foreground ?? "unknown") + "' device='" + device.Name + "'");
        }

        private void ReloadConfig()
        {
            try
            {
                _config = AppConfig.Load(_configPath);
                _timer.Interval = Math.Max(250, _config.PollMilliseconds);
                _lastAppliedDeviceId = null;
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
            using (var form = new SettingsForm(_config, _audio))
            {
                if (form.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                form.Config.Save(_configPath);
                _config = form.Config;
                _timer.Interval = Math.Max(250, _config.PollMilliseconds);
                _lastAppliedDeviceId = null;
                SetStatus("Settings saved");
                EvaluateRules();
            }
        }

        private void WriteDeviceList()
        {
            var lines = _audio.ListRenderDevices()
                .Select(d => d.Name + Environment.NewLine + "  " + d.Id);
            Log("Active render devices:" + Environment.NewLine + string.Join(Environment.NewLine, lines));
            SetStatus("Device list written to log");
        }

        private void SetStatus(string status)
        {
            _lastStatus = status;
            _notifyIcon.Text = ("Primary Audio Switcher - " + status).Substring(0, Math.Min(63, ("Primary Audio Switcher - " + status).Length));
            var item = _notifyIcon.ContextMenuStrip.Items["status"] as ToolStripMenuItem;
            if (item != null)
            {
                item.Text = "Status: " + status;
            }
        }

        private void Log(string message)
        {
            if (!_config.LogEnabled)
            {
                return;
            }

            var path = Path.Combine(Path.GetDirectoryName(_configPath), "primary-audio-switcher.log");
            File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
        }

        protected override void ExitThreadCore()
        {
            _timer.Stop();
            _timer.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            base.ExitThreadCore();
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
        public bool LogEnabled { get; set; }
        public List<AudioRule> Rules { get; set; }

        public static AppConfig Load(string path)
        {
            var document = XDocument.Load(path);
            var root = document.Root;
            if (root == null || root.Name != "PrimaryAudioSwitcher")
            {
                throw new InvalidOperationException("Root element must be <PrimaryAudioSwitcher>.");
            }

            return new AppConfig
            {
                PollMilliseconds = (int?)root.Attribute("pollMilliseconds") ?? 1000,
                FallbackDevice = (string)root.Attribute("fallbackDevice"),
                LogEnabled = ((string)root.Attribute("log") ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase),
                Rules = root.Elements("Rule").Select(AudioRule.FromXml).ToList()
            };
        }

        public void Save(string path)
        {
            var root = new XElement("PrimaryAudioSwitcher",
                new XAttribute("pollMilliseconds", PollMilliseconds),
                new XAttribute("fallbackDevice", FallbackDevice ?? ""),
                new XAttribute("log", LogEnabled ? "true" : "false"));

            foreach (var rule in Rules)
            {
                var element = new XElement("Rule",
                    new XAttribute("name", rule.Name ?? ""),
                    new XAttribute("device", rule.Device ?? ""));

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
            new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root).Save(path);
        }

        public AppConfig Clone()
        {
            return new AppConfig
            {
                PollMilliseconds = PollMilliseconds,
                FallbackDevice = FallbackDevice,
                LogEnabled = LogEnabled,
                Rules = Rules.Select(r => r.Clone()).ToList()
            };
        }

        public static readonly string DefaultXml =
@"<?xml version=""1.0"" encoding=""utf-8""?>
<PrimaryAudioSwitcher pollMilliseconds=""1000"" fallbackDevice="""" log=""true"">
  <!-- device is matched by substring against active Windows render device friendly names. -->
  <Rule name=""Game foreground"" foregroundProcess=""Game"" device=""Speakers"" />
  <Rule name=""Discord running"" runningProcess=""Discord"" device=""Headset"" />
</PrimaryAudioSwitcher>
";
    }

    internal sealed class AudioRule
    {
        public string Name { get; set; }
        public string ForegroundProcess { get; set; }
        public string RunningProcess { get; set; }
        public string Device { get; set; }

        public bool IsMatch(string foregroundProcess, HashSet<string> runningProcesses)
        {
            if (!string.IsNullOrWhiteSpace(ForegroundProcess))
            {
                return MatchesProcess(ForegroundProcess, foregroundProcess);
            }

            if (!string.IsNullOrWhiteSpace(RunningProcess))
            {
                return runningProcesses.Any(process => MatchesProcess(RunningProcess, process));
            }

            return false;
        }

        public static AudioRule FromXml(XElement element)
        {
            var device = (string)element.Attribute("device");
            if (string.IsNullOrWhiteSpace(device))
            {
                throw new InvalidOperationException("Rule is missing required device attribute.");
            }

            return new AudioRule
            {
                Name = (string)element.Attribute("name") ?? "unnamed",
                ForegroundProcess = NormalizeProcess((string)element.Attribute("foregroundProcess")),
                RunningProcess = NormalizeProcess((string)element.Attribute("runningProcess")),
                Device = device
            };
        }

        public AudioRule Clone()
        {
            return new AudioRule
            {
                Name = Name,
                ForegroundProcess = ForegroundProcess,
                RunningProcess = RunningProcess,
                Device = Device
            };
        }

        public override string ToString()
        {
            var mode = !string.IsNullOrWhiteSpace(ForegroundProcess) ? "foreground" : "running";
            var process = !string.IsNullOrWhiteSpace(ForegroundProcess) ? ForegroundProcess : RunningProcess;
            return (Name ?? "unnamed") + " [" + mode + ": " + (process ?? "") + "] -> " + (Device ?? "");
        }

        private static bool MatchesProcess(string expected, string actual)
        {
            return !string.IsNullOrWhiteSpace(actual) &&
                   actual.Equals(NormalizeProcess(expected), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeProcess(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(value)
                : value;
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly AudioDeviceManager _audio;
        private readonly ListBox _rules = new ListBox();
        private readonly TextBox _name = new TextBox();
        private readonly ComboBox _mode = new ComboBox();
        private readonly ComboBox _process = new ComboBox();
        private readonly ComboBox _device = new ComboBox();
        private readonly ComboBox _fallback = new ComboBox();
        private readonly NumericUpDown _poll = new NumericUpDown();
        private readonly CheckBox _log = new CheckBox();

        public SettingsForm(AppConfig config, AudioDeviceManager audio)
        {
            _audio = audio;
            Config = config.Clone();

            Text = "Primary Audio Switcher Settings";
            StartPosition = FormStartPosition.CenterScreen;
            Width = 760;
            Height = 480;
            MinimumSize = new Size(680, 420);

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
                ColumnCount = 2,
                RowCount = 2,
                Padding = new Padding(10)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            Controls.Add(root);

            _rules.Dock = DockStyle.Fill;
            _rules.SelectedIndexChanged += (sender, args) => LoadSelectedRule();
            root.Controls.Add(_rules, 0, 0);

            var editor = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 8,
                Padding = new Padding(10, 0, 0, 0)
            };
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            for (var i = 0; i < 8; i++)
            {
                editor.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 6 ? 70 : 34));
            }
            root.Controls.Add(editor, 1, 0);

            AddLabel(editor, "Rule name", 0);
            editor.Controls.Add(_name, 1, 0);
            editor.SetColumnSpan(_name, 2);
            _name.Dock = DockStyle.Fill;

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

            AddLabel(editor, "Audio device", 3);
            _device.DropDownStyle = ComboBoxStyle.DropDown;
            editor.Controls.Add(_device, 1, 3);
            _device.Dock = DockStyle.Fill;
            editor.Controls.Add(MakeButton("Refresh", RefreshLists), 2, 3);

            AddLabel(editor, "Fallback", 4);
            _fallback.DropDownStyle = ComboBoxStyle.DropDown;
            editor.Controls.Add(_fallback, 1, 4);
            editor.SetColumnSpan(_fallback, 2);
            _fallback.Dock = DockStyle.Fill;

            AddLabel(editor, "Poll ms", 5);
            _poll.Minimum = 250;
            _poll.Maximum = 60000;
            _poll.Increment = 250;
            editor.Controls.Add(_poll, 1, 5);
            _log.Text = "Enable log";
            _log.Dock = DockStyle.Fill;
            editor.Controls.Add(_log, 2, 5);

            var ruleButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            ruleButtons.Controls.Add(MakeButton("Add", AddRule));
            ruleButtons.Controls.Add(MakeButton("Update", UpdateRule));
            ruleButtons.Controls.Add(MakeButton("Delete", DeleteRule));
            editor.Controls.Add(ruleButtons, 1, 6);
            editor.SetColumnSpan(ruleButtons, 2);

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
            root.SetColumnSpan(bottom, 2);
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
            _fallback.Text = Config.FallbackDevice ?? "";
            _log.Checked = Config.LogEnabled;
            ReloadRuleList();
            if (_rules.Items.Count > 0)
            {
                _rules.SelectedIndex = 0;
            }
        }

        private void LoadDeviceList()
        {
            var previousDevice = _device.Text;
            var previousFallback = _fallback.Text;
            _device.Items.Clear();
            _fallback.Items.Clear();
            _fallback.Items.Add("");

            try
            {
                foreach (var device in _audio.ListRenderDevices())
                {
                    _device.Items.Add(device.Name);
                    _fallback.Items.Add(device.Name);
                }
            }
            catch
            {
                // The manual text field still works if Core Audio enumeration fails.
            }

            _device.Text = previousDevice;
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
        }

        private void BrowseProcessFile(object sender, EventArgs args)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*";
                dialog.Title = "Select application executable";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _process.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
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

        private void SaveAndClose(object sender, EventArgs args)
        {
            Config.PollMilliseconds = (int)_poll.Value;
            Config.FallbackDevice = _fallback.Text.Trim();
            Config.LogEnabled = _log.Checked;
            DialogResult = DialogResult.OK;
            Close();
        }

        private AudioRule ReadEditorRule()
        {
            var processName = NormalizeProcess(_process.Text);
            var device = _device.Text.Trim();
            if (string.IsNullOrWhiteSpace(processName))
            {
                MessageBox.Show(this, "Select or enter a process.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            if (string.IsNullOrWhiteSpace(device))
            {
                MessageBox.Show(this, "Select or enter an audio device.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            return new AudioRule
            {
                Name = string.IsNullOrWhiteSpace(_name.Text) ? processName : _name.Text.Trim(),
                ForegroundProcess = _mode.SelectedIndex == 0 ? processName : null,
                RunningProcess = _mode.SelectedIndex == 1 ? processName : null,
                Device = device
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
            _device.Text = rule.Device ?? "";
        }

        private void ReloadRuleList()
        {
            _rules.Items.Clear();
            foreach (var rule in Config.Rules)
            {
                _rules.Items.Add(rule);
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
    }

    internal static class ForegroundWindowReader
    {
        public static string GetForegroundProcessName()
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            uint processId;
            GetWindowThreadProcessId(hwnd, out processId);
            if (processId == 0)
            {
                return null;
            }

            try
            {
                using (var process = Process.GetProcessById((int)processId))
                {
                    return process.ProcessName;
                }
            }
            catch
            {
                return null;
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
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

        public AudioDevice FindRenderDevice(string deviceMatch)
        {
            return ListRenderDevices()
                .FirstOrDefault(d => d.Name.IndexOf(deviceMatch, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     d.Id.IndexOf(deviceMatch, StringComparison.OrdinalIgnoreCase) >= 0);
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
