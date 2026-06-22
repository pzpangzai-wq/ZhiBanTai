using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Win7CuteLanMonitor
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e) { CrashLog.Write(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) { CrashLog.Write(e.ExceptionObject as Exception); };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool silent = Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));
            Application.Run(new MonitorForm(silent));
        }
    }

    internal sealed class MonitorForm : Form
    {
        private readonly List<PcInfo> _pcs;
        private readonly GlassMapPanel _mapPanel = new GlassMapPanel();
        private readonly Label _summary = new Label();
        private readonly Label _clock = new Label();
        private readonly Button _discoverButton = new Button();
        private readonly Button _settingsButton = new Button();
        private readonly Dictionary<string, PcCard> _cards = new Dictionary<string, PcCard>();
        private readonly NotifyIcon _tray = new NotifyIcon();
        private AppSettings _settings = AppSettings.Load();
        private readonly System.Windows.Forms.Timer _animationTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _probeTimer = new System.Windows.Forms.Timer();
        private readonly bool _startSilent;
        private int _tick;
        private bool _checking;
        private bool _discovering;
        private bool _allowClose;

        public MonitorForm(bool startSilent)
        {
            _startSilent = startSilent;
            Text = "值班台";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1000, 740);
            Size = new Size(1160, 820);
            DoubleBuffered = true;
            Font = PickFont(10.5f, FontStyle.Regular);
            BackColor = Color.FromArgb(8, 10, 16);
            Icon = AppIcon.Load();

            _pcs = PcConfig.Load();
            BuildLayout();

            _animationTimer.Interval = 70;
            _animationTimer.Tick += delegate
            {
                _tick++;
                _clock.Text = DateTime.Now.ToString("HH:mm:ss");
                foreach (PcCard card in _cards.Values)
                {
                    card.AnimationTick = _tick;
                    card.Invalidate();
                }
                _mapPanel.AnimationTick = _tick;
                _mapPanel.Invalidate();
            };
            _animationTimer.Start();

            _probeTimer.Interval = 5000;
            _probeTimer.Tick += async delegate { await ProbeAllAsync(); };
            _probeTimer.Start();

            Shown += async delegate
            {
                if (PcConfig.ShouldAutoDiscover(_pcs))
                {
                    _summary.Text = "首次启动正在扫描局域网，电脑多时需要十几秒，请稍等...";
                    await Task.Delay(250);
                    await DiscoverLanAsync(false);
                }
                await ProbeAllAsync();
                if (_startSilent)
                {
                    System.Windows.Forms.Timer hideTimer = new System.Windows.Forms.Timer();
                    hideTimer.Interval = 350;
                    hideTimer.Tick += delegate
                    {
                        hideTimer.Stop();
                        hideTimer.Dispose();
                        HideToTray();
                    };
                    hideTimer.Start();
                }
            };
        }

        private static Font PickFont(float size, FontStyle style)
        {
            string[] names = { "Microsoft YaHei UI", "微软雅黑", "Segoe UI", "Arial" };
            foreach (string name in names)
            {
                try { return new Font(name, size, style); }
                catch { }
            }
            return new Font(FontFamily.GenericSansSerif, size, style);
        }

        private void BuildLayout()
        {
            Panel top = new Panel
            {
                Dock = DockStyle.Top,
                Height = 108,
                Padding = new Padding(30, 18, 30, 12),
                BackColor = Color.Transparent
            };

            Label title = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 44,
                Text = "值班台",
                Font = PickFont(22f, FontStyle.Bold),
                ForeColor = Color.FromArgb(238, 242, 255)
            };

            _summary.AutoSize = false;
            _summary.Dock = DockStyle.Fill;
            _summary.Font = PickFont(11.5f, FontStyle.Regular);
            _summary.ForeColor = Color.FromArgb(188, 196, 214);

            _clock.AutoSize = false;
            _clock.Dock = DockStyle.Right;
            _clock.Width = 120;
            _clock.TextAlign = ContentAlignment.MiddleRight;
            _clock.Font = PickFont(12f, FontStyle.Bold);
            _clock.ForeColor = Color.FromArgb(228, 218, 212);

            _settingsButton.Dock = DockStyle.Right;
            _settingsButton.Width = 92;
            _settingsButton.Text = "设置";
            _settingsButton.FlatStyle = FlatStyle.Flat;
            _settingsButton.FlatAppearance.BorderColor = Color.FromArgb(90, 255, 255, 255);
            _settingsButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(38, 255, 255, 255);
            _settingsButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(54, 255, 255, 255);
            _settingsButton.BackColor = Color.FromArgb(24, 255, 255, 255);
            _settingsButton.ForeColor = Color.FromArgb(238, 242, 255);
            _settingsButton.Font = PickFont(10f, FontStyle.Bold);
            _settingsButton.Click += delegate { ShowSettings(); };

            _discoverButton.Dock = DockStyle.Right;
            _discoverButton.Width = 92;
            _discoverButton.Text = "扫描";
            _discoverButton.FlatStyle = FlatStyle.Flat;
            _discoverButton.FlatAppearance.BorderColor = Color.FromArgb(90, 255, 255, 255);
            _discoverButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(38, 255, 255, 255);
            _discoverButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(54, 255, 255, 255);
            _discoverButton.BackColor = Color.FromArgb(24, 255, 255, 255);
            _discoverButton.ForeColor = Color.FromArgb(238, 242, 255);
            _discoverButton.Font = PickFont(10f, FontStyle.Bold);
            _discoverButton.Click += async delegate { await DiscoverLanAsync(true); };

            Panel summaryRow = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            summaryRow.Controls.Add(_summary);
            summaryRow.Controls.Add(_settingsButton);
            summaryRow.Controls.Add(_discoverButton);
            summaryRow.Controls.Add(_clock);
            top.Controls.Add(summaryRow);
            top.Controls.Add(title);

            _mapPanel.Dock = DockStyle.Fill;
            _mapPanel.BackColor = Color.Transparent;
            _mapPanel.AutoScroll = true;
            _mapPanel.Resize += delegate { LayoutCards(); };

            SyncCards();

            Controls.Add(_mapPanel);
            Controls.Add(top);
            BuildTray();
            UpdateSummary();
            LayoutCards();
        }

        private void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("显示主界面", null, delegate { ShowFromTray(); });
            menu.Items.Add("设置", null, delegate { ShowSettings(); });
            menu.Items.Add("退出", null, delegate
            {
                _allowClose = true;
                Close();
            });

            _tray.Text = "值班台";
            _tray.Icon = Icon ?? SystemIcons.Application;
            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ShowFromTray(); };
        }

        private void ShowSettings()
        {
            using (SettingsDialog dialog = new SettingsDialog(_settings, AppSettings.IsAutoStartEnabled()))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _settings = dialog.Settings;
                    _settings.Save();
                    AppSettings.SetAutoStart(dialog.AutoStartEnabled, _settings.SilentStartup);
                }
            }
        }

        private async Task DiscoverLanAsync(bool showResult)
        {
            if (_discovering) return;
            _discovering = true;
            _discoverButton.Enabled = false;
            _summary.Text = showResult ? "正在重新扫描局域网，请稍等..." : "首次启动正在扫描局域网，电脑多时需要十几秒，请稍等...";
            try
            {
                List<LanDevice> devices = await LanDiscovery.FindAsync(900);
                List<PcInfo> discovered = PcConfig.ApplyDiscoveredDevices(_pcs, devices);
                PcConfig.Save(_pcs);
                PcConfig.SaveReadme(_pcs);
                SyncCards();
                foreach (PcCard card in _cards.Values) card.Invalidate();
                LayoutCards();

                UpdateSummary();
                if (showResult)
                {
                    int scanned = devices == null ? 0 : devices.Count;
                    int remembered = _pcs.Count(p => !p.IsLocal && !p.Online);
                    string text = scanned == 0
                        ? "这次没有扫描到在线电脑。已记住的电脑会继续保留为离线卡片，除非你手动删除。"
                        : "扫描到 " + scanned + " 台在线电脑，新添加 " + discovered.Count + " 台。离线卡片会继续保留，除非你手动删除。";
                    if (remembered > 0) text += Environment.NewLine + Environment.NewLine + "当前保留离线卡片：" + remembered + " 台。";
                    MessageBox.Show(text, "局域网扫描完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
                _summary.Text = "局域网扫描超时或失败。可以先使用已记住的电脑，稍后再点“扫描”重试。";
            }
            finally
            {
                _discoverButton.Enabled = true;
                _discovering = false;
            }
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
            _tray.BalloonTipTitle = "已静默启动";
            _tray.BalloonTipText = "值班台正在托盘里监控。";
            _tray.ShowBalloonTip(1800);
        }

        private void ShowFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void SyncCards()
        {
            foreach (PcInfo pc in _pcs)
            {
                PcCard card;
                if (_cards.TryGetValue(pc.Slot, out card))
                {
                    card.Pc = pc;
                    continue;
                }

                card = new PcCard(pc);
                card.Activated += HandleCardClick;
                card.Renamed += delegate(PcInfo changed)
                {
                    PcConfig.Save(_pcs);
                    PcConfig.SaveReadme(_pcs);
                    UpdateSummary();
                };
                card.DeleteRequested += HandleDeleteCard;
                _cards[pc.Slot] = card;
                _mapPanel.Controls.Add(card);
            }

            foreach (string slot in _cards.Keys.ToArray())
            {
                if (_pcs.Any(p => p.Slot == slot)) continue;
                PcCard card = _cards[slot];
                _mapPanel.Controls.Remove(card);
                card.Dispose();
                _cards.Remove(slot);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized)
            {
                HideToTray();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowClose && e.CloseReason == CloseReason.UserClosing && _settings.CloseToTray)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            _tray.Visible = false;
            _tray.Dispose();
            base.OnFormClosing(e);
        }

        private void LayoutCards()
        {
            if (_mapPanel.ClientSize.Width <= 0 || _cards.Count == 0) return;

            int w = _mapPanel.ClientSize.Width;
            int padding = 26;
            int gap = 22;
            int columns = Math.Max(2, Math.Min(4, (w - padding * 2 + gap) / 260));
            int cardW = Math.Max(220, Math.Min(300, (w - padding * 2 - gap * (columns - 1)) / columns));
            int cardH = 214;
            int index = 0;

            foreach (PcInfo pc in _pcs.OrderBy(p => p.IsLocal ? 0 : 1).ThenBy(p => p.Slot))
            {
                PcCard card;
                if (!_cards.TryGetValue(pc.Slot, out card)) continue;
                int col = index % columns;
                int row = index / columns;
                card.Bounds = new Rectangle(padding + col * (cardW + gap), padding + row * (cardH + gap), cardW, cardH);
                index++;
            }

            int rows = (index + columns - 1) / columns;
            _mapPanel.AutoScrollMinSize = new Size(0, padding * 2 + rows * cardH + Math.Max(0, rows - 1) * gap);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (LinearGradientBrush brush = new LinearGradientBrush(ClientRectangle,
                Color.FromArgb(6, 8, 14), Color.FromArgb(34, 30, 42), 70f))
            {
                e.Graphics.FillRectangle(brush, ClientRectangle);
            }
        }

        private async Task ProbeAllAsync()
        {
            if (_checking) return;
            _checking = true;
            try
            {
                var remote = _pcs.Where(p => !p.IsLocal && !string.IsNullOrWhiteSpace(p.Host)).ToArray();
                var tasks = remote.Select(async pc =>
                {
                    bool online = await NetworkProbe.IsOnlineAsync(pc.Host, 900);
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke((Action)(() =>
                        {
                            pc.Online = online;
                            if (!online) pc.ShareStatus = ShareAccessStatus.Unknown;
                            pc.LastChecked = DateTime.Now;
                            PcCard card;
                            if (_cards.TryGetValue(pc.Slot, out card)) card.Invalidate();
                            UpdateSummary();
                        }));
                    }
                }).ToArray();
                await Task.WhenAll(tasks);
            }
            finally
            {
                _checking = false;
            }
        }

        private void UpdateSummary()
        {
            int total = _pcs.Count(p => !p.IsLocal);
            int online = _pcs.Count(p => !p.IsLocal && p.Online);
            int offline = _pcs.Count(p => !p.IsLocal && !p.Online);
            int direct = _pcs.Count(p => !p.IsLocal && p.DeviceKind != DeviceKind.Printer && p.ShareStatus == ShareAccessStatus.Direct);
            int limited = _pcs.Count(p => !p.IsLocal && p.DeviceKind != DeviceKind.Printer && p.ShareStatus == ShareAccessStatus.NeedsAttention);
            _summary.Text = string.Format("已记住 {0} 台设备，{1} 台在线，{2} 台离线  |  绿色已确认可进入，黄色已提醒用户检查密码或权限", total, online, offline);
            if (direct + limited > 0) _summary.Text += string.Format("  |  绿色 {0}，黄色 {1}", direct, limited);
        }

        private async void HandleCardClick(PcInfo pc)
        {
            if (pc.IsLocal)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:MyComputerFolder") { UseShellExecute = true });
                return;
            }

            if (string.IsNullOrWhiteSpace(pc.Host))
            {
                MessageBox.Show("这个位置还没有配置电脑。请点击右上角“扫描”自动扫描局域网，或编辑“电脑配置.txt”手动填写主机名/IP。", "等待扫描", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!pc.Online)
            {
                MessageBox.Show(OfflineMessage(), "它暂时不在", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (pc.DeviceKind == DeviceKind.Printer)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("http://" + pc.Host) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show("打印机“" + pc.DisplayName + "”在线，但暂时打不开设备页面。" + Environment.NewLine + Environment.NewLine + "系统返回：" + ex.Message,
                        "暂时打不开打印机", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            string path = @"\\" + pc.Host;
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                UpdateShareStatus(pc, ShareAccessStatus.Unknown);
                ShareAccessStatus openedStatus = await DetectOpenedShareStatusAsync(pc.Host);
                UpdateShareStatus(pc, openedStatus);
            }
            catch (Exception ex)
            {
                UpdateShareStatus(pc, ShareAccessStatus.NeedsAttention);
                MessageBox.Show(ShareTroublesMessage(pc) + Environment.NewLine + Environment.NewLine + "系统返回：" + ex.Message,
                    "暂时打不开共享", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async Task<ShareAccessStatus> DetectOpenedShareStatusAsync(string host)
        {
            for (int i = 0; i < 12; i++)
            {
                await Task.Delay(250);
                if (CredentialPromptDetector.HasNetworkCredentialPrompt(host)) return ShareAccessStatus.NeedsAttention;
            }
            return ShareAccessStatus.Direct;
        }

        private void UpdateShareStatus(PcInfo pc, ShareAccessStatus status)
        {
            if (pc == null || !_pcs.Contains(pc)) return;
            pc.ShareStatus = status;
            PcConfig.Save(_pcs);
            PcConfig.SaveReadme(_pcs);
            PcCard card;
            if (_cards.TryGetValue(pc.Slot, out card)) card.Invalidate();
            UpdateSummary();
        }

        private void HandleDeleteCard(PcInfo pc)
        {
            if (pc == null || pc.IsLocal) return;
            string name = string.IsNullOrWhiteSpace(pc.DisplayName) ? pc.Host : pc.DisplayName;
            DialogResult result = MessageBox.Show(
                "确定删除“" + name + "”这张卡片吗？" + Environment.NewLine + Environment.NewLine + "删除后它不会继续保留离线状态；以后如果重新扫描到在线电脑，会再添加回来。",
                "确定删除吗",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.OK) return;

            _pcs.Remove(pc);
            PcConfig.Save(_pcs);
            PcConfig.SaveReadme(_pcs);
            SyncCards();
            LayoutCards();
            UpdateSummary();
        }

        private static string OfflineMessage()
        {
            int hour = DateTime.Now.Hour;
            if (hour >= 11 && hour <= 13) return "它去吃饭啦，稍微等一下哦。";
            if (hour >= 18 || hour < 8) return "它已经下班啦，明天再来吧。";
            return "它可能离开工位一小会儿，稍微等一下哦。";
        }

        private static string ShareTroublesMessage(PcInfo pc)
        {
            return "电脑“" + pc.DisplayName + "”在线，但 Windows 共享暂时访问不了。" + Environment.NewLine + Environment.NewLine
                + "可以让对方检查这些设置：" + Environment.NewLine
                + "1. 已开启“网络发现”和“文件和打印机共享”。" + Environment.NewLine
                + "2. 防火墙允许文件共享，端口 445/139 没被拦截。" + Environment.NewLine
                + "3. 共享文件夹已经给当前用户权限。" + Environment.NewLine
                + "4. 如果对方电脑设置了登录密码，访问时需要输入对方电脑的用户名和密码。" + Environment.NewLine + Environment.NewLine
                + "也可以在资源管理器地址栏手动输入：\\\\" + pc.Host;
        }
    }

    internal sealed class PcInfo
    {
        public string Slot;
        public string DisplayName;
        public string Host;
        public string ShortcutPath;
        public string OsName;
        public DeviceKind DeviceKind;
        public ShareAccessStatus ShareStatus;
        public bool IsLocal;
        public bool Online;
        public DateTime LastChecked;
        public int Theme;
    }

    internal enum ShareAccessStatus
    {
        Unknown,
        Direct,
        NeedsAttention
    }

    internal enum DeviceKind
    {
        Computer,
        Printer
    }

    internal static class PcConfig
    {
        private const int MaxRemote = 20;
        private static readonly string[] LegacyRemoteSlots = { "左上", "上", "右上", "左", "右" };
        private static readonly string[] RemoteSlots = Enumerable.Range(1, MaxRemote).Select(i => "电脑" + i.ToString("00")).ToArray();
        private static readonly string[] Slots = new[] { "本机" }.Concat(RemoteSlots).ToArray();

        public static string ConfigPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "电脑配置.txt"); }
        }

        public static List<PcInfo> Load()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            List<PcInfo> loaded = File.Exists(ConfigPath) ? LoadConfig(ConfigPath, desktop) : new List<PcInfo>();
            List<PcInfo> loadedRemote = loaded
                .Where(p => !string.Equals(p.Slot, "本机", StringComparison.OrdinalIgnoreCase))
                .Where(p => !string.IsNullOrWhiteSpace(p.Host))
                .Where(p => !string.Equals(p.DisplayName, "待发现", StringComparison.OrdinalIgnoreCase))
                .Take(MaxRemote)
                .ToList();
            Dictionary<string, PcInfo> bySlot = loaded.GroupBy(p => p.Slot).ToDictionary(g => g.Key, g => g.First());
            List<PcInfo> result = new List<PcInfo>();

            for (int i = 0; i < loadedRemote.Count; i++)
            {
                string slot = RemoteSlots[i];
                PcInfo existing;
                if (!TryCloneForSlot(loadedRemote[i], slot, out existing)) continue;
                existing.Slot = slot;
                existing.Theme = result.Count;
                existing.IsLocal = false;
                existing.Online = false;
                result.Add(existing);
            }

            PcInfo local;
            if (!bySlot.TryGetValue("本机", out local))
            {
                local = new PcInfo
                {
                    Slot = "本机",
                    DisplayName = "本机",
                    Host = Environment.MachineName,
                    ShortcutPath = "",
                    OsName = OsDetector.GetLocalOsName(),
                    DeviceKind = DeviceKind.Computer,
                    ShareStatus = ShareAccessStatus.Direct,
                    IsLocal = true,
                    Online = true,
                    LastChecked = DateTime.Now,
                    Theme = 8
                };
            }
            local.IsLocal = true;
            local.DisplayName = string.IsNullOrWhiteSpace(local.DisplayName) ? "本机" : local.DisplayName;
            local.Host = Environment.MachineName;
            local.OsName = OsDetector.GetLocalOsName();
            local.DeviceKind = DeviceKind.Computer;
            local.ShareStatus = ShareAccessStatus.Direct;
            local.Online = true;
            local.LastChecked = DateTime.Now;

            result.Insert(0, local);
            Save(result);
            SaveReadme(result);
            return result;
        }

        private static bool TryCloneForSlot(PcInfo source, string slot, out PcInfo pc)
        {
            pc = null;
            if (source == null) return false;
            pc = new PcInfo
            {
                Slot = slot,
                DisplayName = source.DisplayName,
                Host = source.Host,
                ShortcutPath = source.ShortcutPath,
                OsName = source.OsName,
                DeviceKind = source.DeviceKind,
                ShareStatus = source.ShareStatus,
                LastChecked = source.LastChecked
            };
            return true;
        }

        public static bool ShouldAutoDiscover(List<PcInfo> pcs)
        {
            string machine = Environment.MachineName;
            PcInfo local = pcs.FirstOrDefault(p => p.IsLocal);
            if (local == null || !string.Equals(local.Host, machine, StringComparison.OrdinalIgnoreCase)) return true;
            return !pcs.Any(p => !p.IsLocal && !string.IsNullOrWhiteSpace(p.Host));
        }

        public static List<PcInfo> ApplyDiscoveredDevices(List<PcInfo> pcs, List<LanDevice> devices)
        {
            List<PcInfo> changed = new List<PcInfo>();
            if (devices == null) return changed;

            var candidates = devices
                .Where(d => d != null && !string.IsNullOrWhiteSpace(d.Host))
                .Where(d => !string.Equals(d.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                .GroupBy(d => NormalizeHost(d.Host), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(d => d.Host)
                .Take(RemoteSlots.Length)
                .ToList();

            DateTime now = DateTime.Now;
            foreach (PcInfo existing in pcs.Where(p => !p.IsLocal))
            {
                existing.Online = false;
                existing.ShareStatus = ShareAccessStatus.Unknown;
                existing.LastChecked = now;
            }

            foreach (LanDevice device in candidates)
            {
                string host = NormalizeHost(device.Host);
                PcInfo pc = pcs.FirstOrDefault(p => !p.IsLocal && string.Equals(NormalizeHost(p.Host), host, StringComparison.OrdinalIgnoreCase));
                if (pc != null)
                {
                    pc.Host = host;
                    if (string.IsNullOrWhiteSpace(pc.DisplayName) || string.Equals(pc.DisplayName, pc.Host, StringComparison.OrdinalIgnoreCase))
                    {
                        pc.DisplayName = string.IsNullOrWhiteSpace(device.DisplayName) ? host : device.DisplayName;
                    }
                    if (!string.IsNullOrWhiteSpace(device.OsName)) pc.OsName = device.OsName;
                    pc.DeviceKind = device.DeviceKind;
                    pc.ShareStatus = ShareAccessStatus.Unknown;
                    pc.Online = device.Online;
                    pc.LastChecked = now;
                    continue;
                }

                string slot = FirstFreeRemoteSlot(pcs);
                if (string.IsNullOrWhiteSpace(slot)) break;
                int theme = Array.IndexOf(RemoteSlots, slot);
                pc = new PcInfo
                {
                    Slot = slot,
                    DisplayName = string.IsNullOrWhiteSpace(device.DisplayName) ? host : device.DisplayName,
                    Host = host,
                    ShortcutPath = "",
                    OsName = device.OsName,
                    DeviceKind = device.DeviceKind,
                    ShareStatus = ShareAccessStatus.Unknown,
                    IsLocal = false,
                    Online = device.Online,
                    LastChecked = now,
                    Theme = theme < 0 ? pcs.Count : theme
                };
                pcs.Add(pc);
                changed.Add(pc);
            }

            PcInfo local = pcs.FirstOrDefault(p => p.IsLocal);
            if (local != null)
            {
                local.DisplayName = "本机";
                local.Host = Environment.MachineName;
                local.OsName = OsDetector.GetLocalOsName();
                local.DeviceKind = DeviceKind.Computer;
                local.ShareStatus = ShareAccessStatus.Direct;
                local.Online = true;
                local.LastChecked = now;
            }

            return changed;
        }

        private static string FirstFreeRemoteSlot(List<PcInfo> pcs)
        {
            foreach (string slot in RemoteSlots)
            {
                if (!pcs.Any(p => string.Equals(p.Slot, slot, StringComparison.OrdinalIgnoreCase))) return slot;
            }
            return null;
        }

        public static void Save(List<PcInfo> pcs)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# 每行格式：位置|显示名称|主机名或IP|桌面快捷方式路径|系统版本|设备类型");
                sb.AppendLine("# 位置用于排序：本机、电脑01、电脑02 ... 电脑20。显示名称可在界面卡片右上角编辑。");
                foreach (PcInfo pc in pcs.OrderBy(p => p.IsLocal ? 0 : 1).ThenBy(p => p.Slot))
                {
                    if (pc == null) continue;
                    sb.AppendLine(pc.Slot + "|" + pc.DisplayName + "|" + pc.Host + "|" + pc.ShortcutPath + "|" + (pc.OsName ?? "") + "|" + FormatDeviceKind(pc.DeviceKind));
                }
                File.WriteAllText(ConfigPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private static List<PcInfo> LoadConfig(string path, string desktop)
        {
            var list = new List<PcInfo>();
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;
                string[] parts = trimmed.Split('|');
                if (parts.Length >= 4)
                {
                    string slot = parts[0].Trim();
                    if (!IsKnownSlot(slot)) continue;
                    list.Add(new PcInfo
                    {
                        Slot = slot,
                        DisplayName = parts[1].Trim().Length > 0 ? parts[1].Trim() : parts[2].Trim(),
                        Host = NormalizeHost(parts[2].Trim()),
                        ShortcutPath = Environment.ExpandEnvironmentVariables(parts[3].Trim()),
                        OsName = parts.Length >= 5 ? parts[4].Trim() : "",
                        DeviceKind = ParseDeviceKind(parts.Length >= 6 ? parts[5].Trim() : "", parts[1].Trim(), parts[2].Trim()),
                        LastChecked = DateTime.MinValue
                    });
                }
                else if (parts.Length >= 2)
                {
                    string oldName = parts[0].Trim();
                    string host = parts[1].Trim();
                    if (!IsKnownSlot(oldName)) continue;
                    string shortcut = parts.Length >= 3 ? Environment.ExpandEnvironmentVariables(parts[2].Trim()) : Path.Combine(desktop, oldName + ".lnk");
                    list.Add(new PcInfo
                    {
                        Slot = oldName,
                        DisplayName = NormalizeHost(host),
                        Host = NormalizeHost(host),
                        ShortcutPath = shortcut,
                        OsName = "",
                        DeviceKind = ParseDeviceKind("", oldName, host),
                        LastChecked = DateTime.MinValue
                    });
                }
            }
            return list;
        }

        private static bool IsKnownSlot(string slot)
        {
            return Slots.Contains(slot) || LegacyRemoteSlots.Contains(slot);
        }

        private static string TryExtractHost(string shortcutPath)
        {
            try
            {
                string detailsHost = ShellLinkDetails.TryGetNetworkPath(shortcutPath);
                if (!string.IsNullOrWhiteSpace(detailsHost)) return NormalizeHost(detailsHost);
            }
            catch { }

            try
            {
                byte[] bytes = File.ReadAllBytes(shortcutPath);
                string ascii = Encoding.Default.GetString(bytes);
                string unicode = Encoding.Unicode.GetString(bytes);
                foreach (string text in new[] { ascii, unicode })
                {
                    Match m = Regex.Match(text, @"\\\\([A-Za-z0-9][A-Za-z0-9\-_\.]{1,63})");
                    if (m.Success) return NormalizeHost(m.Groups[1].Value);

                    m = Regex.Match(text, @"\b((?:BF|PC)-[A-Za-z0-9\-_]{4,40})\b", RegexOptions.IgnoreCase);
                    if (m.Success) return NormalizeHost(m.Groups[1].Value);
                }
            }
            catch { }

            return null;
        }

        private static string NormalizeHost(string raw)
        {
            string value = (raw ?? "").Trim();
            if (value.StartsWith(@"\\")) value = value.TrimStart('\\');
            int slash = value.IndexOf('\\');
            if (slash >= 0) value = value.Substring(0, slash);
            return value;
        }

        private static string FormatDeviceKind(DeviceKind kind)
        {
            return kind == DeviceKind.Printer ? "打印机" : "电脑";
        }

        private static DeviceKind ParseDeviceKind(string value, string displayName, string host)
        {
            if (string.Equals(value, "打印机", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Printer", StringComparison.OrdinalIgnoreCase))
            {
                return DeviceKind.Printer;
            }

            string text = ((displayName ?? "") + " " + (host ?? "") + " " + (value ?? "")).ToLowerInvariant();
            string[] words = { "printer", "print", "打印", "打印机", "hp", "canon", "epson", "brother", "laserjet", "deskjet", "ricoh", "xerox", "kyocera", "konica", "sharp" };
            return words.Any(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) ? DeviceKind.Printer : DeviceKind.Computer;
        }

        public static void SaveReadme(List<PcInfo> pcs)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "电脑清单.txt");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("值班台 - 当前电脑清单");
                sb.AppendLine();
                foreach (PcInfo pc in pcs)
                {
                    string kind = pc.DeviceKind == DeviceKind.Printer ? "打印机" : "电脑";
                    string os = pc.DeviceKind == DeviceKind.Printer ? "打印机设备" : (string.IsNullOrWhiteSpace(pc.OsName) ? "系统未知" : pc.OsName);
                    sb.AppendLine(pc.Slot + " | " + pc.DisplayName + " | " + pc.Host + " | " + kind + " | " + os);
                }
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }

    internal static class ShellLinkDetails
    {
        public static string TryGetNetworkPath(string shortcutPath)
        {
            string folderPath = Path.GetDirectoryName(shortcutPath);
            string fileName = Path.GetFileName(shortcutPath);
            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return null;
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic folder = shell.Namespace(folderPath);
            dynamic item = folder.ParseName(fileName);
            if (item == null) return null;

            string best = null;
            for (int i = 0; i < 320; i++)
            {
                string value = folder.GetDetailsOf(item, i) as string;
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (value.StartsWith(@"\\"))
                {
                    best = value;
                    break;
                }
                if (i == 61 && Regex.IsMatch(value, @"^(BF|PC)-", RegexOptions.IgnoreCase))
                {
                    best = value;
                }
            }
            return best;
        }
    }

    internal static class NetworkProbe
    {
        public static async Task<bool> IsOnlineAsync(string host, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            Task<bool>[] tasks =
            {
                IsComputerLikeAsync(host, timeoutMs),
                HasPrinterPortAsync(host, timeoutMs)
            };

            bool[] results = await Task.WhenAll(tasks);
            return results.Any(x => x);
        }

        public static async Task<bool> IsComputerLikeAsync(string host, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            Task<bool>[] tasks =
            {
                PingAsync(host, timeoutMs),
                HasComputerServiceAsync(host, timeoutMs)
            };

            bool[] results = await Task.WhenAll(tasks);
            return results.Any(x => x);
        }

        public static async Task<bool> HasComputerServiceAsync(string host, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            Task<bool>[] tasks =
            {
                TcpAsync(host, 445, timeoutMs),
                TcpAsync(host, 139, timeoutMs)
            };

            bool[] results = await Task.WhenAll(tasks);
            return results.Any(x => x);
        }

        public static async Task<bool> HasPrinterPortAsync(string host, int timeoutMs)
        {
            Task<bool>[] tasks =
            {
                TcpAsync(host, 9100, timeoutMs),
                TcpAsync(host, 515, timeoutMs),
                TcpAsync(host, 631, timeoutMs)
            };
            bool[] results = await Task.WhenAll(tasks);
            return results.Any(x => x);
        }

        public static bool CanReachShare(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            return Tcp(host, 445, 650) || Tcp(host, 139, 650);
        }

        public static Task<ShareAccessStatus> GetShareStatusAsync(string host, int timeoutMs)
        {
            return Task.Factory.StartNew(() => GetShareStatus(host, timeoutMs));
        }

        public static ShareAccessStatus GetShareStatus(string host, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(host)) return ShareAccessStatus.Unknown;
            if (!CanReachShare(host)) return ShareAccessStatus.NeedsAttention;

            ShareAccessStatus result = ShareAccessStatus.Unknown;
            ManualResetEvent done = new ManualResetEvent(false);
            Thread worker = new Thread(new ThreadStart(delegate
            {
                try
                {
                    Directory.GetDirectories(@"\\" + host);
                    result = ShareAccessStatus.Direct;
                }
                catch (UnauthorizedAccessException)
                {
                    result = ShareAccessStatus.NeedsAttention;
                }
                catch (IOException)
                {
                    result = ShareAccessStatus.NeedsAttention;
                }
                catch
                {
                    result = ShareAccessStatus.Unknown;
                }
                finally
                {
                    try { done.Set(); }
                    catch { }
                }
            }));
            worker.IsBackground = true;
            worker.Start();
            if (!done.WaitOne(timeoutMs)) result = ShareAccessStatus.Unknown;

            return result;
        }

        private static async Task<bool> PingAsync(string host, int timeoutMs)
        {
            try
            {
                using (Ping ping = new Ping())
                {
                    PingReply reply = await ping.SendPingAsync(host, timeoutMs);
                    return reply.Status == IPStatus.Success;
                }
            }
            catch { return false; }
        }

        private static async Task<bool> TcpAsync(string host, int port, int timeoutMs)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    Task connect = client.ConnectAsync(host, port);
                    Task delay = Task.Delay(timeoutMs);
                    Task winner = await Task.WhenAny(connect, delay);
                    return winner == connect && client.Connected;
                }
            }
            catch { return false; }
        }

        private static bool Tcp(string host, int port, int timeoutMs)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect(host, port, null, null);
                    bool ok = ar.AsyncWaitHandle.WaitOne(timeoutMs);
                    if (!ok) return false;
                    client.EndConnect(ar);
                    return client.Connected;
                }
            }
            catch { return false; }
        }
    }

    internal static class CredentialPromptDetector
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        public static bool HasNetworkCredentialPrompt(string host)
        {
            bool found = false;
            string normalizedHost = (host ?? "").Trim();
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hWnd)) return true;

                StringBuilder title = new StringBuilder(256);
                GetWindowText(hWnd, title, title.Capacity);
                string text = title.ToString();
                if (IsCredentialTitle(text, normalizedHost))
                {
                    found = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static bool IsCredentialTitle(string title, string host)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;
            string t = title.Trim();
            if (t.IndexOf("输入网络凭据", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (t.IndexOf("Windows 安全", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (t.IndexOf("Windows Security", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (t.IndexOf("Enter network credentials", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (t.IndexOf("Network Credentials", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return !string.IsNullOrWhiteSpace(host) && t.IndexOf(host, StringComparison.OrdinalIgnoreCase) >= 0
                && (t.IndexOf("密码", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("credential", StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    internal sealed class LanDevice
    {
        public string Host;
        public string Ip;
        public string DisplayName;
        public string OsName;
        public DeviceKind DeviceKind;
        public bool Online;
    }

    internal static class LanDiscovery
    {
        private const int MaxScanDurationMs = 25000;
        private const int MaxParallelProbes = 32;
        private const int ReverseDnsTimeoutMs = 1200;

        public static async Task<List<LanDevice>> FindAsync(int timeoutMs)
        {
            List<IPAddress> addresses = GetCandidateAddresses();
            if (addresses.Count == 0) return new List<LanDevice>();

            SemaphoreSlim gate = new SemaphoreSlim(MaxParallelProbes);
            List<Task> touchTasks = addresses
                .Select(ip => TouchAddressAsync(ip, timeoutMs, gate))
                .ToList();

            await Task.WhenAny(Task.WhenAll(touchTasks), Task.Delay(Math.Min(MaxScanDurationMs, 9000)));

            HashSet<string> candidateIps = new HashSet<string>(addresses.Select(ip => ip.ToString()), StringComparer.OrdinalIgnoreCase);
            List<IPAddress> reliableAddresses = GetReliableArpAddresses()
                .Where(ip => candidateIps.Contains(ip))
                .Select(ip => IPAddress.Parse(ip))
                .ToList();

            List<Task<LanDevice>> deviceTasks = reliableAddresses
                .Select(ip => BuildDeviceAsync(ip, timeoutMs))
                .ToList();

            Task<LanDevice[]> all = Task.WhenAll(deviceTasks);
            Task finished = await Task.WhenAny(all, Task.Delay(Math.Max(1000, MaxScanDurationMs - 9000)));
            IEnumerable<LanDevice> found = finished == all
                ? all.Result
                : deviceTasks
                    .Where(t => t.IsCompleted && !t.IsFaulted && !t.IsCanceled)
                    .Select(t => t.Result);

            return found
                .Where(d => d != null)
                .GroupBy(d => d.Host, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(d => d.Host)
                .ToList();
        }

        private static async Task TouchAddressAsync(IPAddress ip, int timeoutMs, SemaphoreSlim gate)
        {
            await gate.WaitAsync();
            try
            {
                string ipText = ip.ToString();
                await NetworkProbe.IsOnlineAsync(ipText, Math.Min(timeoutMs, 450));
            }
            catch { }
            finally
            {
                gate.Release();
            }
        }

        private static async Task<LanDevice> BuildDeviceAsync(IPAddress ip, int timeoutMs)
        {
            try
            {
                string ipText = ip.ToString();
                string host = await TryResolveHostAsync(ip, ReverseDnsTimeoutMs);
                string target = string.IsNullOrWhiteSpace(host) ? ipText : host;
                Task<bool> computerTask = NetworkProbe.HasComputerServiceAsync(ipText, Math.Min(timeoutMs, 650));
                Task<bool> printerPortTask = NetworkProbe.HasPrinterPortAsync(ipText, 550);
                await Task.WhenAll(computerTask, printerPortTask);

                bool computer = computerTask.Result;
                bool printerPort = printerPortTask.Result;
                bool printer = printerPort && (LooksLikePrinter(target) || !computer);
                string osName = printer ? "" : await OsDetector.GetRemoteOsNameAsync(target, 1100);

                return new LanDevice
                {
                    Host = target,
                    Ip = ipText,
                    DisplayName = target,
                    OsName = osName,
                    DeviceKind = printer ? DeviceKind.Printer : DeviceKind.Computer,
                    Online = true
                };
            }
            catch
            {
                return null;
            }
        }

        private static bool LooksLikePrinter(string text)
        {
            string value = (text ?? "").ToLowerInvariant();
            string[] words = { "printer", "print", "打印", "打印机", "hp", "canon", "epson", "brother", "laserjet", "deskjet", "ricoh", "xerox", "kyocera", "konica", "sharp" };
            return words.Any(w => value.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static List<IPAddress> GetCandidateAddresses()
        {
            List<IPAddress> preferred = new List<IPAddress>();
            List<IPAddress> fallback = new List<IPAddress>();
            HashSet<string> localAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); }
                catch { continue; }

                bool preferredNic = HasUsableIpv4Gateway(props);
                foreach (UnicastIPAddressInformation uni in props.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] ip = uni.Address.GetAddressBytes();
                    byte[] mask = uni.IPv4Mask == null ? null : uni.IPv4Mask.GetAddressBytes();
                    if (mask == null) continue;
                    if (!IsUsableLanIPv4(ip)) continue;

                    localAddresses.Add(uni.Address.ToString());
                    AddSubnet(preferredNic ? preferred : fallback, ip, mask);
                }
            }

            List<IPAddress> result = preferred.Count > 0 ? preferred : fallback;
            return result
                .Where(ip => !localAddresses.Contains(ip.ToString()))
                .GroupBy(ip => ip.ToString())
                .Select(g => g.First())
                .Take(254)
                .ToList();
        }

        private static bool HasUsableIpv4Gateway(IPInterfaceProperties props)
        {
            try
            {
                return props.GatewayAddresses.Any(g =>
                    g != null &&
                    g.Address != null &&
                    g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    IsUsableLanIPv4(g.Address.GetAddressBytes()));
            }
            catch { return false; }
        }

        private static bool IsUsableLanIPv4(byte[] ip)
        {
            if (ip == null || ip.Length != 4) return false;
            if (ip[0] == 10) return true;
            if (ip[0] == 172 && ip[1] >= 16 && ip[1] <= 31) return true;
            if (ip[0] == 192 && ip[1] == 168) return true;
            return false;
        }

        private static void AddSubnet(List<IPAddress> result, byte[] ip, byte[] mask)
        {
            uint ipNum = ToUInt(ip);
            uint maskNum = ToUInt(mask);
            uint network = ipNum & maskNum;
            uint broadcast = network | ~maskNum;
            uint count = broadcast > network ? broadcast - network - 1 : 0;
            if (count == 0 || count > 254)
            {
                network = ipNum & 0xFFFFFF00;
                broadcast = network | 0x000000FF;
            }

            for (uint n = network + 1; n < broadcast; n++)
            {
                result.Add(FromUInt(n));
            }
        }

        private static uint ToUInt(byte[] bytes)
        {
            return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        }

        private static IPAddress FromUInt(uint value)
        {
            return new IPAddress(new[]
            {
                (byte)((value >> 24) & 255),
                (byte)((value >> 16) & 255),
                (byte)((value >> 8) & 255),
                (byte)(value & 255)
            });
        }

        private static string GetLocalIPv4()
        {
            try
            {
                foreach (IPAddress ip in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork) return ip.ToString();
                }
            }
            catch { }
            return "";
        }

        private static string TryResolveHost(IPAddress ip)
        {
            try
            {
                IPHostEntry entry = Dns.GetHostEntry(ip);
                if (!string.IsNullOrWhiteSpace(entry.HostName))
                {
                    return entry.HostName.Split('.')[0];
                }
            }
            catch { }
            return null;
        }

        private static async Task<string> TryResolveHostAsync(IPAddress ip, int timeoutMs)
        {
            List<Task<string>> resolvers = new List<Task<string>>
            {
                Task.Factory.StartNew(() => TryResolveHost(ip)),
                Task.Factory.StartNew(() => TryResolveNetBiosName(ip, timeoutMs))
            };
            Task timeout = Task.Delay(timeoutMs);

            while (resolvers.Count > 0)
            {
                Task winner = await Task.WhenAny(resolvers.Cast<Task>().Concat(new[] { timeout }));
                if (winner == timeout) return null;

                Task<string> resolved = (Task<string>)winner;
                resolvers.Remove(resolved);
                try
                {
                    if (!string.IsNullOrWhiteSpace(resolved.Result)) return resolved.Result;
                }
                catch { }
            }

            return null;
        }

        private static bool HasReliableArpEntry(string ipText)
        {
            string mac;
            return TryGetArpMac(ipText, out mac);
        }

        private static HashSet<string> GetReliableArpAddresses()
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.SystemDirectory, "arp.exe"),
                        Arguments = "-a",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    process.Start();
                    if (!process.WaitForExit(1200))
                    {
                        try { process.Kill(); }
                        catch { }
                        return result;
                    }

                    string output = process.StandardOutput.ReadToEnd();
                    foreach (string line in Regex.Split(output ?? "", "\r?\n"))
                    {
                        string ipText;
                        string mac;
                        if (TryParseReliableArpLine(line, out ipText, out mac)) result.Add(ipText);
                    }
                }
            }
            catch { }
            return result;
        }

        private static bool TryGetArpMac(string ipText, out string mac)
        {
            mac = null;
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.SystemDirectory, "arp.exe"),
                        Arguments = "-a " + ipText,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    process.Start();
                    if (!process.WaitForExit(650))
                    {
                        try { process.Kill(); }
                        catch { }
                        return false;
                    }

                    string output = process.StandardOutput.ReadToEnd();
                    foreach (string line in Regex.Split(output ?? "", "\r?\n"))
                    {
                        string foundIp;
                        string foundMac;
                        if (TryParseReliableArpLine(line, out foundIp, out foundMac) && string.Equals(foundIp, ipText, StringComparison.OrdinalIgnoreCase))
                        {
                            mac = foundMac;
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool TryParseReliableArpLine(string line, out string ipText, out string mac)
        {
            ipText = null;
            mac = null;
            Match match = Regex.Match(line ?? "", @"^\s*((?:\d{1,3}\.){3}\d{1,3})\s+([0-9a-fA-F]{2}(?:-[0-9a-fA-F]{2}){5})(?:\s+\S+)?\s*$");
            if (!match.Success) return false;

            string value = match.Groups[2].Value.ToLowerInvariant();
            if (value == "00-00-00-00-00-00") return false;
            if (value == "ff-ff-ff-ff-ff-ff") return false;

            ipText = match.Groups[1].Value;
            mac = value;
            return true;
        }

        private static string TryResolveNetBiosName(IPAddress ip, int timeoutMs)
        {
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.SystemDirectory, "nbtstat.exe"),
                        Arguments = "-A " + ip,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    process.Start();
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); }
                        catch { }
                        return null;
                    }

                    string output = process.StandardOutput.ReadToEnd();
                    foreach (string line in Regex.Split(output ?? "", "\r?\n"))
                    {
                        Match match = Regex.Match(line, @"^\s*([A-Za-z0-9][A-Za-z0-9_-]{0,14})\s+<00>\s+");
                        if (!match.Success) continue;
                        if (line.IndexOf("GROUP", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("组", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                        string name = match.Groups[1].Value.Trim();
                        if (string.Equals(name, "WORKGROUP", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(name, "MSHOME", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(name, "__MSBROWSE__", StringComparison.OrdinalIgnoreCase)) continue;
                        return name;
                    }
                }
            }
            catch { }
            return null;
        }
    }

    internal static class OsDetector
    {
        public static Task<string> GetRemoteOsNameAsync(string host, int timeoutMs)
        {
            return Task.Factory.StartNew(() =>
            {
                string result = null;
                ManualResetEvent done = new ManualResetEvent(false);
                Thread worker = new Thread(new ThreadStart(delegate
                {
                    try { result = GetRemoteOsName(host); }
                    catch { }
                    finally
                    {
                        try { done.Set(); }
                        catch { }
                    }
                }));
                worker.IsBackground = true;
                worker.Start();
                done.WaitOne(timeoutMs);
                return result ?? "";
            });
        }

        private static string GetRemoteOsName(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return "";
            if (string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return GetLocalOsName();

            try
            {
                using (RegistryKey key = RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, host)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false))
                {
                    if (key == null) return "";
                    string productName = Convert.ToString(key.GetValue("ProductName"));
                    string buildText = Convert.ToString(key.GetValue("CurrentBuildNumber"));
                    int build;
                    int.TryParse(buildText, out build);
                    return Simplify(productName, build);
                }
            }
            catch { return ""; }
        }

        public static string GetLocalOsName()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false))
                {
                    if (key != null)
                    {
                        string productName = Convert.ToString(key.GetValue("ProductName"));
                        string buildText = Convert.ToString(key.GetValue("CurrentBuildNumber"));
                        int build;
                        int.TryParse(buildText, out build);
                        string simplified = Simplify(productName, build);
                        if (!string.IsNullOrWhiteSpace(simplified)) return simplified;
                    }
                }
            }
            catch { }

            Version v = Environment.OSVersion.Version;
            if (v.Major == 6 && v.Minor == 1) return "Win7";
            if (v.Major == 10 && v.Build >= 22000) return "Win11";
            if (v.Major == 10) return "Win10";
            return "Windows " + v;
        }

        private static string Simplify(string productName, int build)
        {
            string name = productName ?? "";
            if (build >= 22000) return "Win11";
            if (name.IndexOf("Windows 11", StringComparison.OrdinalIgnoreCase) >= 0) return "Win11";
            if (name.IndexOf("Windows 10", StringComparison.OrdinalIgnoreCase) >= 0) return "Win10";
            if (name.IndexOf("Windows 7", StringComparison.OrdinalIgnoreCase) >= 0) return "Win7";
            if (name.IndexOf("Windows 8", StringComparison.OrdinalIgnoreCase) >= 0) return "Win8";
            return name;
        }
    }

    internal sealed class GlassMapPanel : Panel
    {
        public int AnimationTick;

        public GlassMapPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = ClientRectangle;

            using (LinearGradientBrush bg = new LinearGradientBrush(r, Color.FromArgb(7, 9, 16), Color.FromArgb(24, 22, 31), 82f))
            {
                g.FillRectangle(bg, r);
            }

            DrawLiquidBand(g, new Rectangle(-80, 38, Width + 180, Height / 2 + 80),
                Color.FromArgb(32, 60, 84, 105), Color.FromArgb(210, 240, 172, 122), -12);
            DrawLiquidBand(g, new Rectangle(-140, Height / 3, Width + 260, Height / 2 + 160),
                Color.FromArgb(145, 22, 25, 34), Color.FromArgb(190, 178, 82, 68), 16);
            DrawLiquidBand(g, new Rectangle(Width / 2 - 80, -60, Width / 2 + 180, Height + 140),
                Color.FromArgb(45, 135, 128, 160), Color.FromArgb(130, 238, 204, 178), 34);

            using (SolidBrush glow = new SolidBrush(Color.FromArgb(30, 255, 221, 190)))
            {
                int pulse = 18 + (int)(Math.Sin(AnimationTick / 18.0) * 10);
                g.FillEllipse(glow, Width / 2 - 150 - pulse, Height / 2 - 110 - pulse, 300 + pulse * 2, 220 + pulse * 2);
            }

            base.OnPaint(e);
        }

        private static void DrawLiquidBand(Graphics g, Rectangle bounds, Color c1, Color c2, int tilt)
        {
            using (GraphicsPath path = new GraphicsPath())
            {
                int x = bounds.X;
                int y = bounds.Y;
                int w = bounds.Width;
                int h = bounds.Height;
                path.StartFigure();
                path.AddBezier(x - 30, y + h / 2 + tilt, x + w / 4, y - 80, x + w / 2, y + h + 70, x + w + 30, y + h / 3);
                path.AddLine(x + w + 30, y + h / 3, x + w + 30, y + h + 90);
                path.AddBezier(x + w + 30, y + h + 90, x + w / 2, y + h - 60, x + w / 4, y + h + 120, x - 30, y + h / 2 + tilt + 120);
                path.CloseFigure();
                using (LinearGradientBrush brush = new LinearGradientBrush(bounds, c1, c2, 28f))
                {
                    g.FillPath(brush, path);
                }
                using (Pen shine = new Pen(Color.FromArgb(90, 255, 226, 196), 1.6f))
                {
                    g.DrawPath(shine, path);
                }
            }
        }
    }

    internal sealed class PcCard : Control
    {
        public PcInfo Pc;
        public int AnimationTick;
        public event Action<PcInfo> Activated;
        public event Action<PcInfo> Renamed;
        public event Action<PcInfo> DeleteRequested;
        private Rectangle _editRect;
        private Rectangle _deleteRect;

        public PcCard(PcInfo pc)
        {
            Pc = pc;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left && _editRect.Contains(e.Location))
            {
                RenameDialog dialog = new RenameDialog(Pc.DisplayName);
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    Pc.DisplayName = dialog.Value.Trim();
                    if (Pc.DisplayName.Length == 0) Pc.DisplayName = Pc.IsLocal ? "本机" : Pc.Host;
                    Action<PcInfo> renamed = Renamed;
                    if (renamed != null) renamed(Pc);
                    Invalidate();
                }
                return;
            }

            if (e.Button == MouseButtons.Left && _deleteRect.Contains(e.Location))
            {
                Action<PcInfo> deleteRequested = DeleteRequested;
                if (deleteRequested != null) deleteRequested(Pc);
                return;
            }

            if (e.Button == MouseButtons.Left)
            {
                Action<PcInfo> activated = Activated;
                if (activated != null) activated(Pc);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(1, 1, Width - 3, Height - 3);
            bool online = Pc.IsLocal || Pc.Online;

            using (GraphicsPath shadowPath = Rounded(rect.X + 5, rect.Y + 8, rect.Width - 2, rect.Height - 2, 22))
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
            {
                g.FillPath(shadow, shadowPath);
            }

            using (GraphicsPath path = Rounded(rect, 22))
            using (LinearGradientBrush fill = new LinearGradientBrush(rect,
                Color.FromArgb(106, 255, 255, 255),
                Color.FromArgb(34, 255, 255, 255), 115f))
            using (Pen edge = new Pen(Color.FromArgb(online ? 155 : 86, online ? 181 : 168, online ? 255 : 186, online ? 222 : 205), 1.4f))
            {
                g.FillPath(fill, path);
                g.DrawPath(edge, path);
            }

            using (GraphicsPath gloss = Rounded(rect.X + 10, rect.Y + 8, rect.Width - 20, rect.Height / 2 - 12, 16))
            using (LinearGradientBrush shine = new LinearGradientBrush(new Rectangle(rect.X, rect.Y, rect.Width, rect.Height / 2),
                Color.FromArgb(80, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
            {
                g.FillPath(shine, gloss);
            }

            DrawHeader(g, online);
            DrawPixelScene(g, online);
            DrawFooter(g, online);
        }

        private void DrawHeader(Graphics g, bool online)
        {
            _deleteRect = Pc.IsLocal ? Rectangle.Empty : new Rectangle(Width - 42, 16, 26, 24);
            _editRect = Pc.IsLocal ? new Rectangle(Width - 42, 16, 26, 24) : new Rectangle(Width - 74, 16, 26, 24);
            int reserved = Pc.IsLocal ? 82 : 114;
            using (Font titleFont = new Font(Font.FontFamily, 13.5f, FontStyle.Bold))
            using (Font metaFont = new Font(Font.FontFamily, 8.8f, FontStyle.Regular))
            using (SolidBrush titleBrush = new SolidBrush(Color.FromArgb(244, 247, 255)))
            using (SolidBrush metaBrush = new SolidBrush(Color.FromArgb(188, 198, 218)))
            {
                string title = TrimToFit(g, Pc.DisplayName, titleFont, Width - reserved);
                g.DrawString(title, titleFont, titleBrush, 18, 15);
                string os = Pc.DeviceKind == DeviceKind.Printer ? "打印机" : (string.IsNullOrWhiteSpace(Pc.OsName) ? "系统未知" : Pc.OsName);
                string meta = (Pc.IsLocal ? Environment.MachineName : Pc.Host) + "  |  " + os;
                g.DrawString(TrimToFit(g, meta, metaFont, Width - 42), metaFont, metaBrush, 19, 42);
            }

            if (!Pc.IsLocal)
            {
                using (GraphicsPath delete = Rounded(_deleteRect, 10))
                using (SolidBrush deleteFill = new SolidBrush(Color.FromArgb(64, 255, 92, 92)))
                using (Pen deletePen = new Pen(Color.FromArgb(135, 255, 202, 202), 1f))
                {
                    g.FillPath(deleteFill, delete);
                    g.DrawPath(deletePen, delete);
                }

                using (Pen p = new Pen(Color.FromArgb(240, 255, 238, 238), 2f))
                {
                    g.DrawLine(p, _deleteRect.X + 8, _deleteRect.Y + 8, _deleteRect.X + 18, _deleteRect.Y + 18);
                    g.DrawLine(p, _deleteRect.X + 18, _deleteRect.Y + 8, _deleteRect.X + 8, _deleteRect.Y + 18);
                }
            }

            using (GraphicsPath edit = Rounded(_editRect, 10))
            using (SolidBrush editFill = new SolidBrush(Color.FromArgb(54, 255, 255, 255)))
            using (Pen editPen = new Pen(Color.FromArgb(96, 255, 255, 255), 1f))
            {
                g.FillPath(editFill, edit);
                g.DrawPath(editPen, edit);
            }

            using (Pen p = new Pen(Color.FromArgb(230, 245, 238, 226), 2f))
            {
                g.DrawLine(p, _editRect.X + 8, _editRect.Y + 16, _editRect.X + 17, _editRect.Y + 7);
                g.DrawLine(p, _editRect.X + 15, _editRect.Y + 5, _editRect.X + 19, _editRect.Y + 9);
            }

            Rectangle status = new Rectangle(18, Height - 34, 78, 22);
            using (GraphicsPath pill = Rounded(status, 11))
            using (SolidBrush b = new SolidBrush(online ? Color.FromArgb(170, 61, 213, 141) : Color.FromArgb(124, 130, 122, 142)))
            {
                g.FillPath(b, pill);
            }
            using (Font f = new Font(Font.FontFamily, 8.8f, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(Color.White))
            {
                string text = Pc.IsLocal ? "本机在线" : (Pc.DeviceKind == DeviceKind.Printer ? "打印机" : (online ? "在线" : "离线"));
                SizeF s = g.MeasureString(text, f);
                g.DrawString(text, f, b, status.X + (status.Width - s.Width) / 2, status.Y + 3);
            }

            DrawShareBadge(g, online);
        }

        private void DrawShareBadge(Graphics g, bool online)
        {
            if (Pc.IsLocal || Pc.DeviceKind == DeviceKind.Printer || !online || Pc.ShareStatus == ShareAccessStatus.Unknown) return;

            bool direct = Pc.ShareStatus == ShareAccessStatus.Direct;
            Rectangle box = new Rectangle(Width - 112, Height - 36, 92, 24);
            Color fill = direct ? Color.FromArgb(198, 44, 190, 116) : Color.FromArgb(216, 230, 178, 64);
            Color edge = direct ? Color.FromArgb(235, 158, 255, 205) : Color.FromArgb(245, 255, 229, 146);
            Color textColor = direct ? Color.White : Color.FromArgb(56, 42, 20);

            using (GraphicsPath path = Rounded(box, 5))
            using (SolidBrush brush = new SolidBrush(fill))
            using (Pen pen = new Pen(edge, 1.2f))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            using (Font f = new Font(Font.FontFamily, 8.6f, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(textColor))
            {
                string text = direct ? "直达" : "已提醒";
                SizeF size = g.MeasureString(text, f);
                g.DrawString(text, f, b, box.X + (box.Width - size.Width) / 2, box.Y + 4);
            }
        }

        private void DrawPixelScene(Graphics g, bool online)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;

            Rectangle scene = new Rectangle(16, 70, Width - 32, Height - 112);
            DrawPortraitBackground(g, scene, online);
            GraphicsState clipState = g.Save();
            g.SetClip(scene);

            if (Pc.DeviceKind == DeviceKind.Printer)
            {
                DrawPrinterScene(g, scene, online);
                g.Restore(clipState);
                g.SmoothingMode = old;
                return;
            }

            int s = scene.Height >= 108 && scene.Width >= 190 ? 2 : 1;
            int personX = scene.X + scene.Width / 2 - 22 * s;
            int personY = scene.Bottom - 78 * s + (online ? (int)(Math.Sin((AnimationTick + Pc.Theme * 8) / 8.0) * 2) : 0);
            if (personY < scene.Y + 3) personY = scene.Y + 3;

            if (online)
            {
                DrawOnlineWorkScene(g, scene, personX, personY, s);
            }
            else
            {
                if (Pc.Theme % 2 == 0)
                {
                    DrawLargeSleeper(g, personX - 5 * s, scene.Bottom - 44 * s, s, AnimationTick);
                }
                else
                {
                    DrawLargeLeaving(g, personX + (AnimationTick % 12), personY + 2 * s, s);
                }
                DrawPixelDesk(g, scene.Right - 92 * s / 2, scene.Bottom - 52 * s, s, false);
            }

            g.Restore(clipState);
            g.SmoothingMode = old;
        }

        private void DrawPrinterScene(Graphics g, Rectangle scene, bool online)
        {
            int unit = Math.Max(2, Math.Min(scene.Width / 110, scene.Height / 64));
            int w = 68 * unit;
            int h = 42 * unit;
            int x = scene.X + (scene.Width - w) / 2;
            int y = scene.Y + (scene.Height - h) / 2 + 8 * unit;

            DrawWhiteOutline(g, x - 3 * unit, y - 11 * unit, w + 6 * unit, h + 18 * unit, unit);
            FillPx(g, Color.FromArgb(238, 241, 232), x + 10 * unit, y - 18 * unit, 48 * unit, 20 * unit);
            FillPx(g, Color.FromArgb(78, 92, 118), x + 6 * unit, y, 56 * unit, 30 * unit);
            FillPx(g, Color.FromArgb(48, 56, 74), x, y + 12 * unit, 68 * unit, 28 * unit);
            FillPx(g, online ? Color.FromArgb(76, 220, 150) : Color.FromArgb(132, 122, 142), x + 52 * unit, y + 18 * unit, 5 * unit, 5 * unit);
            FillPx(g, Color.FromArgb(210, 216, 220), x + 13 * unit, y + 29 * unit, 42 * unit, 20 * unit);
            FillPx(g, Color.FromArgb(86, 142, 190), x + 19 * unit, y + 35 * unit, 25 * unit, 3 * unit);
            FillPx(g, Color.FromArgb(206, 88, 88), x + 19 * unit, y + 42 * unit, 35 * unit, 3 * unit);
            FillPx(g, Color.FromArgb(34, 42, 58), x + 12 * unit, y + 7 * unit, 34 * unit, 5 * unit);

            using (Font f = new Font(Font.FontFamily, 9.2f, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(242, 246, 255)))
            {
                string label = online ? "打印机" : "打印机离线";
                SizeF size = g.MeasureString(label, f);
                g.DrawString(label, f, b, scene.X + (scene.Width - size.Width) / 2, scene.Y + 8);
            }
        }

        private void DrawOnlineWorkScene(Graphics g, Rectangle scene, int personX, int personY, int s)
        {
            int t = AnimationTick + Pc.Theme * 13;
            int sceneKind = Math.Abs(Pc.Theme) % 5;
            DrawLargeWorker(g, personX, personY, s, t);

            if (sceneKind == 0)
            {
                DrawPixelDesk(g, scene.Right - 92 * s / 2, scene.Bottom - 52 * s, s, true);
                DrawPixelParticles(g, scene.Right - 50, scene.Y + 16, s);
            }
            else if (sceneKind == 1)
            {
                DrawWhiteboardScene(g, scene, s, t);
            }
            else if (sceneKind == 2)
            {
                DrawServerScene(g, scene, s, t);
            }
            else if (sceneKind == 3)
            {
                DrawPaperworkScene(g, scene, s, t);
            }
            else
            {
                DrawCallDeskScene(g, scene, s, t);
            }
        }

        private void DrawWhiteboardScene(Graphics g, Rectangle scene, int s, int t)
        {
            int unit = Math.Max(2, s);
            int x = scene.Right - 72 * unit;
            int y = scene.Y + 14 * unit;
            FillPx(g, Color.FromArgb(230, 238, 232), x, y, 56 * unit, 34 * unit);
            FillPx(g, Color.FromArgb(70, 86, 96), x, y + 34 * unit, 56 * unit, 3 * unit);
            FillPx(g, Color.FromArgb(54, 170, 190), x + 8 * unit, y + 8 * unit, 18 * unit, 3 * unit);
            FillPx(g, Color.FromArgb(222, 94, 96), x + 8 * unit, y + 16 * unit, 34 * unit, 3 * unit);
            FillPx(g, Color.FromArgb(244, 190, 86), x + 8 * unit, y + 24 * unit, 25 * unit, 3 * unit);
            FillPx(g, Color.FromArgb(245, 235, 158), scene.X + 18 * unit, scene.Bottom - 35 * unit, 42 * unit, 10 * unit);
            FillPx(g, Color.FromArgb(90, 62, 72), scene.X + 18 * unit, scene.Bottom - 25 * unit, 42 * unit, 4 * unit);
        }

        private void DrawServerScene(Graphics g, Rectangle scene, int s, int t)
        {
            int unit = Math.Max(2, s);
            int x = scene.Right - 58 * unit;
            int y = scene.Bottom - 78 * unit;
            FillPx(g, Color.FromArgb(42, 50, 69), x, y, 40 * unit, 66 * unit);
            FillPx(g, Color.FromArgb(71, 82, 106), x + 4 * unit, y + 6 * unit, 32 * unit, 12 * unit);
            FillPx(g, Color.FromArgb(71, 82, 106), x + 4 * unit, y + 25 * unit, 32 * unit, 12 * unit);
            FillPx(g, Color.FromArgb(71, 82, 106), x + 4 * unit, y + 44 * unit, 32 * unit, 12 * unit);
            for (int i = 0; i < 3; i++)
            {
                Color light = ((t / 5 + i) % 2 == 0) ? Color.FromArgb(75, 240, 146) : Color.FromArgb(244, 205, 86);
                FillPx(g, light, x + 9 * unit, y + (10 + i * 19) * unit, 4 * unit, 4 * unit);
            }
            FillPx(g, Color.FromArgb(76, 210, 230), scene.X + 20 * unit, scene.Y + 18 * unit, 28 * unit, 5 * unit);
            FillPx(g, Color.FromArgb(76, 210, 230), scene.X + 28 * unit, scene.Y + 28 * unit, 42 * unit, 5 * unit);
        }

        private void DrawPaperworkScene(Graphics g, Rectangle scene, int s, int t)
        {
            int unit = Math.Max(2, s);
            int x = scene.Right - 98 * unit;
            int y = scene.Bottom - 52 * unit;
            FillPx(g, Color.FromArgb(166, 112, 87), x, y + 26 * unit, 86 * unit, 8 * unit);
            for (int i = 0; i < 4; i++)
            {
                int dx = (i * 14 + (t / 8) % 5) * unit;
                FillPx(g, Color.FromArgb(235, 235, 218), x + 8 * unit + dx, y + (8 + i % 2 * 5) * unit, 22 * unit, 14 * unit);
                FillPx(g, Color.FromArgb(93, 126, 170), x + 11 * unit + dx, y + (12 + i % 2 * 5) * unit, 13 * unit, 2 * unit);
            }
            FillPx(g, Color.FromArgb(74, 62, 82), x + 10 * unit, y + 34 * unit, 7 * unit, 24 * unit);
            FillPx(g, Color.FromArgb(74, 62, 82), x + 72 * unit, y + 34 * unit, 7 * unit, 24 * unit);
        }

        private void DrawCallDeskScene(Graphics g, Rectangle scene, int s, int t)
        {
            int unit = Math.Max(2, s);
            int x = scene.Right - 94 * unit;
            int y = scene.Bottom - 53 * unit;
            DrawPixelDesk(g, x, y, unit, true);
            FillPx(g, Color.FromArgb(45, 50, 68), scene.X + 22 * unit, scene.Y + 20 * unit, 24 * unit, 16 * unit);
            FillPx(g, Color.FromArgb(102, 224, 190), scene.X + 27 * unit, scene.Y + 24 * unit, 14 * unit, 5 * unit);
            FillPx(g, Color.FromArgb(240, 216, 145), x + 14 * unit, y + 13 * unit, 12 * unit, 13 * unit);
            FillPx(g, Color.FromArgb(132, 85, 62), x + 13 * unit, y + 22 * unit, 14 * unit, 4 * unit);
            int ring = (t / 4) % 8;
            FillPx(g, Color.FromArgb(246, 230, 126), scene.X + (38 + ring) * unit, scene.Y + 14 * unit, 4 * unit, 4 * unit);
        }

        private void DrawPortraitBackground(Graphics g, Rectangle scene, bool online)
        {
            Color top = online ? Color.FromArgb(37, 48, 72) : Color.FromArgb(24, 27, 40);
            Color bottom = online ? Color.FromArgb(86, 58, 62) : Color.FromArgb(45, 38, 58);
            using (LinearGradientBrush bg = new LinearGradientBrush(scene, top, bottom, 90f))
            {
                g.FillRectangle(bg, scene);
            }

            int unit = Math.Max(2, Width / 120);
            FillPx(g, Color.FromArgb(72, 47, 45), scene.X, scene.Bottom - 18 * unit, scene.Width, 18 * unit);
            FillPx(g, Color.FromArgb(114, 75, 58), scene.X, scene.Bottom - 22 * unit, scene.Width, 4 * unit);
            FillPx(g, Color.FromArgb(46, 58, 78), scene.X + 12 * unit, scene.Y + 8 * unit, 34 * unit, 22 * unit);
            FillPx(g, Color.FromArgb(86, 158, 184), scene.X + 15 * unit, scene.Y + 11 * unit, 11 * unit, 8 * unit);
            FillPx(g, Color.FromArgb(86, 158, 184), scene.X + 29 * unit, scene.Y + 11 * unit, 11 * unit, 8 * unit);
            FillPx(g, Color.FromArgb(31, 36, 53), scene.X + 26 * unit, scene.Y + 9 * unit, 2 * unit, 21 * unit);
            FillPx(g, Color.FromArgb(31, 36, 53), scene.X + 13 * unit, scene.Y + 20 * unit, 31 * unit, 2 * unit);

            if (online)
            {
                FillPx(g, Color.FromArgb(121, 83, 64), scene.Right - 40 * unit, scene.Y + 11 * unit, 28 * unit, 42 * unit);
                FillPx(g, Color.FromArgb(176, 116, 72), scene.Right - 43 * unit, scene.Y + 9 * unit, 34 * unit, 5 * unit);
                for (int i = 0; i < 3; i++) FillPx(g, Color.FromArgb(73, 185, 204), scene.Right - 35 * unit, scene.Y + (18 + i * 9) * unit, 20 * unit, 3 * unit);
            }
            else
            {
                using (SolidBrush dim = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
                {
                    g.FillRectangle(dim, scene);
                }
                FillPx(g, Color.FromArgb(88, 65, 106), scene.Right - 30 * unit, scene.Y + 12 * unit, 12 * unit, 30 * unit);
                FillPx(g, Color.FromArgb(244, 197, 95), scene.Right - 26 * unit, scene.Y + 28 * unit, 3 * unit, 3 * unit);
            }
        }

        private void DrawPixelDesk(Graphics g, int x, int y, int s, bool online)
        {
            FillPx(g, Color.FromArgb(92, 64, 78), x, y + 33 * s, 82 * s, 8 * s);
            FillPx(g, Color.FromArgb(166, 112, 87), x, y + 26 * s, 82 * s, 8 * s);
            FillPx(g, Color.FromArgb(78, 55, 70), x + 8 * s, y + 41 * s, 6 * s, 22 * s);
            FillPx(g, Color.FromArgb(78, 55, 70), x + 68 * s, y + 41 * s, 6 * s, 22 * s);
            FillPx(g, Color.FromArgb(46, 52, 76), x + 37 * s, y, 35 * s, 25 * s);
            FillPx(g, online ? Color.FromArgb(75, 224, 230) : Color.FromArgb(64, 63, 82), x + 41 * s, y + 4 * s, 27 * s, 16 * s);
            FillPx(g, Color.FromArgb(38, 42, 60), x + 52 * s, y + 25 * s, 5 * s, 8 * s);
            FillPx(g, Color.FromArgb(38, 42, 60), x + 45 * s, y + 32 * s, 20 * s, 3 * s);
        }

        private void DrawWorkingSprite(Graphics g, int x, int y, int s, int t)
        {
            int arm = ((t / 3) % 2 == 0) ? -2 * s : 2 * s;
            FillPx(g, Color.FromArgb(84, 52, 49), x + 7 * s, y, 17 * s, 7 * s);
            FillPx(g, Color.FromArgb(230, 171, 122), x + 6 * s, y + 6 * s, 20 * s, 17 * s);
            FillPx(g, Color.FromArgb(84, 52, 49), x + 5 * s, y + 6 * s, 5 * s, 11 * s);
            FillPx(g, Color.FromArgb(37, 30, 34), x + 11 * s, y + 13 * s, 2 * s, 2 * s);
            FillPx(g, Color.FromArgb(37, 30, 34), x + 20 * s, y + 13 * s, 2 * s, 2 * s);
            FillPx(g, Color.FromArgb(93, 190, 174), x + 4 * s, y + 24 * s, 24 * s, 24 * s);
            FillPx(g, Color.FromArgb(230, 171, 122), x - 4 * s, y + 29 * s + arm, 10 * s, 5 * s);
            FillPx(g, Color.FromArgb(230, 171, 122), x + 25 * s, y + 29 * s - arm, 13 * s, 5 * s);
            FillPx(g, Color.FromArgb(49, 42, 56), x + 2 * s, y + 48 * s, 9 * s, 13 * s);
            FillPx(g, Color.FromArgb(49, 42, 56), x + 20 * s, y + 48 * s, 9 * s, 13 * s);
        }

        private void DrawLargeWorker(Graphics g, int x, int y, int s, int t)
        {
            Color hair = HairColor();
            Color shirt = ShirtColor();
            int arm = ((t / 4) % 2 == 0) ? -2 * s : 2 * s;

            DrawWhiteOutline(g, x + 5 * s, y, 41 * s, 67 * s, s);
            FillPx(g, hair, x + 9 * s, y, 28 * s, 7 * s);
            FillPx(g, hair, x + 5 * s, y + 7 * s, 39 * s, 12 * s);
            FillPx(g, hair, x + 3 * s, y + 17 * s, 10 * s, 18 * s);
            FillPx(g, hair, x + 34 * s, y + 15 * s, 11 * s, 22 * s);
            FillPx(g, Color.FromArgb(242, 178, 132), x + 11 * s, y + 15 * s, 26 * s, 28 * s);
            FillPx(g, Color.FromArgb(255, 204, 156), x + 14 * s, y + 18 * s, 20 * s, 19 * s);
            FillPx(g, Color.FromArgb(28, 31, 43), x + 16 * s, y + 27 * s, 3 * s, 3 * s);
            FillPx(g, Color.FromArgb(28, 31, 43), x + 29 * s, y + 27 * s, 3 * s, 3 * s);
            FillPx(g, Color.FromArgb(176, 67, 88), x + 21 * s, y + 35 * s, 8 * s, 2 * s);

            FillPx(g, shirt, x + 8 * s, y + 44 * s, 31 * s, 30 * s);
            FillPx(g, Darken(shirt), x + 8 * s, y + 44 * s, 31 * s, 5 * s);
            FillPx(g, Color.FromArgb(242, 178, 132), x - 2 * s, y + 51 * s + arm, 14 * s, 6 * s);
            FillPx(g, Color.FromArgb(242, 178, 132), x + 36 * s, y + 52 * s - arm, 18 * s, 6 * s);
            FillPx(g, Color.FromArgb(37, 41, 58), x + 5 * s, y + 72 * s, 36 * s, 6 * s);
        }

        private void DrawLargeSleeper(Graphics g, int x, int y, int s, int t)
        {
            Color hair = HairColor();
            Color shirt = ShirtColor();

            DrawWhiteOutline(g, x + 2 * s, y - 8 * s, 57 * s, 40 * s, s);
            FillPx(g, shirt, x + 2 * s, y + 19 * s, 55 * s, 18 * s);
            FillPx(g, Color.FromArgb(242, 178, 132), x + 9 * s, y + 1 * s, 34 * s, 21 * s);
            FillPx(g, hair, x + 8 * s, y - 4 * s, 36 * s, 10 * s);
            FillPx(g, hair, x + 5 * s, y + 5 * s, 9 * s, 13 * s);
            FillPx(g, Color.FromArgb(32, 34, 48), x + 20 * s, y + 12 * s, 16 * s, 2 * s);
            int bubble = 5 + (t % 24) / 5;
            FillPx(g, Color.FromArgb(185, 232, 248), x + 43 * s, y + 5 * s, bubble * s, bubble * s);
            FillPx(g, Color.FromArgb(243, 244, 255), x + 48 * s, y - 12 * s, 5 * s, 5 * s);
            FillPx(g, Color.FromArgb(243, 244, 255), x + 55 * s, y - 20 * s, 3 * s, 3 * s);
        }

        private void DrawLargeLeaving(Graphics g, int x, int y, int s)
        {
            Color hair = HairColor();
            Color shirt = ShirtColor();

            DrawWhiteOutline(g, x + 2 * s, y, 48 * s, 70 * s, s);
            FillPx(g, hair, x + 9 * s, y, 27 * s, 8 * s);
            FillPx(g, hair, x + 6 * s, y + 7 * s, 34 * s, 13 * s);
            FillPx(g, Color.FromArgb(242, 178, 132), x + 11 * s, y + 16 * s, 26 * s, 26 * s);
            FillPx(g, Color.FromArgb(28, 31, 43), x + 18 * s, y + 27 * s, 3 * s, 3 * s);
            FillPx(g, Color.FromArgb(28, 31, 43), x + 30 * s, y + 27 * s, 3 * s, 3 * s);
            FillPx(g, Color.FromArgb(176, 67, 88), x + 22 * s, y + 35 * s, 8 * s, 2 * s);
            FillPx(g, shirt, x + 9 * s, y + 43 * s, 29 * s, 27 * s);
            FillPx(g, Color.FromArgb(105, 72, 54), x + 39 * s, y + 51 * s, 13 * s, 16 * s);
            FillPx(g, Color.FromArgb(37, 41, 58), x + 12 * s, y + 69 * s, 8 * s, 12 * s);
            FillPx(g, Color.FromArgb(37, 41, 58), x + 30 * s, y + 69 * s, 8 * s, 12 * s);
        }

        private void DrawSleepingSprite(Graphics g, int x, int y, int s, int t)
        {
            FillPx(g, Color.FromArgb(112, 91, 142), x + 1 * s, y + 17 * s, 39 * s, 15 * s);
            FillPx(g, Color.FromArgb(230, 171, 122), x + 8 * s, y, 25 * s, 19 * s);
            FillPx(g, Color.FromArgb(84, 52, 49), x + 7 * s, y, 27 * s, 7 * s);
            FillPx(g, Color.FromArgb(42, 35, 48), x + 15 * s, y + 11 * s, 11 * s, 2 * s);
            int bubble = 4 + (t % 18) / 5;
            FillPx(g, Color.FromArgb(176, 229, 244), x + 34 * s, y + 9 * s, bubble * s, bubble * s);
        }

        private void DrawLeavingSprite(Graphics g, int x, int y, int s)
        {
            FillPx(g, Color.FromArgb(79, 55, 46), x + 8 * s, y, 18 * s, 8 * s);
            FillPx(g, Color.FromArgb(230, 171, 122), x + 7 * s, y + 7 * s, 21 * s, 17 * s);
            FillPx(g, Color.FromArgb(96, 140, 212), x + 5 * s, y + 25 * s, 24 * s, 25 * s);
            FillPx(g, Color.FromArgb(70, 45, 52), x + 31 * s, y + 35 * s, 12 * s, 14 * s);
            FillPx(g, Color.FromArgb(43, 38, 50), x + 7 * s, y + 50 * s, 7 * s, 15 * s);
            FillPx(g, Color.FromArgb(43, 38, 50), x + 23 * s, y + 50 * s, 7 * s, 15 * s);
        }

        private void DrawPixelParticles(Graphics g, int x, int y, int s)
        {
            int phase = AnimationTick % 36;
            FillPx(g, Color.FromArgb(246, 231, 164), x + phase / 2 * s, y, 4 * s, 4 * s);
            FillPx(g, Color.FromArgb(205, 230, 255), x + 15 * s, y + (phase % 18) * s / 2, 7 * s, 5 * s);
            FillPx(g, Color.FromArgb(255, 190, 132), x - 9 * s, y + 20 * s - (phase % 12) * s / 2, 5 * s, 5 * s);
        }

        private Color HairColor()
        {
            Color[] colors =
            {
                Color.FromArgb(222, 55, 132),
                Color.FromArgb(42, 34, 76),
                Color.FromArgb(118, 53, 185),
                Color.FromArgb(126, 56, 32),
                Color.FromArgb(238, 178, 48),
                Color.FromArgb(105, 46, 38)
            };
            return colors[Math.Abs(Pc.Theme) % colors.Length];
        }

        private Color ShirtColor()
        {
            Color[] colors =
            {
                Color.FromArgb(202, 52, 63),
                Color.FromArgb(38, 45, 63),
                Color.FromArgb(40, 118, 190),
                Color.FromArgb(230, 235, 240),
                Color.FromArgb(38, 132, 203),
                Color.FromArgb(154, 41, 69)
            };
            return colors[Math.Abs(Pc.Theme) % colors.Length];
        }

        private static Color Darken(Color color)
        {
            return Color.FromArgb(Math.Max(0, color.R - 42), Math.Max(0, color.G - 38), Math.Max(0, color.B - 34));
        }

        private static void DrawWhiteOutline(Graphics g, int x, int y, int w, int h, int s)
        {
            FillPx(g, Color.FromArgb(245, 246, 255), x - 2 * s, y + 4 * s, w + 4 * s, h - 8 * s);
            FillPx(g, Color.FromArgb(245, 246, 255), x + 3 * s, y - 2 * s, w - 6 * s, h + 4 * s);
            FillPx(g, Color.FromArgb(38, 21, 34), x - 4 * s, y + 7 * s, 2 * s, h - 14 * s);
            FillPx(g, Color.FromArgb(38, 21, 34), x + w + 2 * s, y + 7 * s, 2 * s, h - 14 * s);
            FillPx(g, Color.FromArgb(38, 21, 34), x + 7 * s, y - 4 * s, w - 14 * s, 2 * s);
            FillPx(g, Color.FromArgb(38, 21, 34), x + 7 * s, y + h + 2 * s, w - 14 * s, 2 * s);
        }

        private void DrawFooter(Graphics g, bool online)
        {
            if (!Pc.IsLocal && Pc.DeviceKind != DeviceKind.Printer && online && Pc.ShareStatus != ShareAccessStatus.Unknown) return;

            string text;
            if (Pc.IsLocal)
            {
                text = "打开本机";
            }
            else if (Pc.DeviceKind == DeviceKind.Printer)
            {
                text = online ? "打开设备" : (Pc.LastChecked == DateTime.MinValue ? "正在检测" : "上次检测 " + Pc.LastChecked.ToString("HH:mm:ss"));
            }
            else
            {
                text = online ? "打开共享" : (Pc.LastChecked == DateTime.MinValue ? "正在检测" : "上次检测 " + Pc.LastChecked.ToString("HH:mm:ss"));
            }

            using (Font f = new Font(Font.FontFamily, 8.8f, FontStyle.Regular))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(204, 211, 229)))
            {
                SizeF s = g.MeasureString(text, f);
                g.DrawString(text, f, b, Width - 18 - s.Width, Height - 32);
            }
        }

        private static string TrimToFit(Graphics g, string text, Font font, int width)
        {
            if (g.MeasureString(text, font).Width <= width) return text;
            string t = text;
            while (t.Length > 1 && g.MeasureString(t + "...", font).Width > width)
                t = t.Substring(0, t.Length - 1);
            return t + "...";
        }

        private static void FillPx(Graphics g, Color color, int x, int y, int w, int h)
        {
            using (SolidBrush b = new SolidBrush(color))
            {
                g.FillRectangle(b, x, y, w, h);
            }
        }

        private static GraphicsPath Rounded(Rectangle rect, int radius)
        {
            return Rounded(rect.X, rect.Y, rect.Width, rect.Height, radius);
        }

        private static GraphicsPath Rounded(int x, int y, int width, int height, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + width - d, y, d, d, 270, 90);
            path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
            path.AddArc(x, y + height - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class RenameDialog : Form
    {
        private readonly TextBox _text = new TextBox();
        public string Value { get { return _text.Text; } }

        public RenameDialog(string current)
        {
            Text = "编辑名称";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(360, 130);
            BackColor = Color.FromArgb(22, 24, 32);
            Font = new Font("Microsoft YaHei UI", 10f);

            Label label = new Label
            {
                Text = "卡片显示名称",
                ForeColor = Color.FromArgb(230, 235, 248),
                AutoSize = false,
                Bounds = new Rectangle(18, 16, 320, 24)
            };
            _text.Bounds = new Rectangle(18, 44, 320, 28);
            _text.Text = current;
            _text.SelectAll();

            Button ok = new Button { Text = "保存", DialogResult = DialogResult.OK, Bounds = new Rectangle(178, 88, 76, 28) };
            Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(262, 88, 76, 28) };
            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(label);
            Controls.Add(_text);
            Controls.Add(ok);
            Controls.Add(cancel);
        }
    }

    internal sealed class AppSettings
    {
        public bool SilentStartup;
        public bool CloseToTray = true;
        private const string RunName = "ZhiBanTai";
        private const string LegacyRunName = "Win7CuteLanMonitor";

        private static string SettingsPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "程序设置.txt"); }
        }

        public static AppSettings Load()
        {
            AppSettings settings = new AppSettings();
            try
            {
                if (!File.Exists(SettingsPath)) return settings;
                foreach (string line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;
                    string[] parts = trimmed.Split(new[] { '=' }, 2);
                    if (parts.Length != 2) continue;
                    if (string.Equals(parts[0].Trim(), "SilentStartup", StringComparison.OrdinalIgnoreCase))
                    {
                        settings.SilentStartup = IsTrue(parts[1]);
                    }
                    else if (string.Equals(parts[0].Trim(), "CloseToTray", StringComparison.OrdinalIgnoreCase))
                    {
                        settings.CloseToTray = IsTrue(parts[1]);
                    }
                }
            }
            catch { }
            return settings;
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# 程序设置");
                sb.AppendLine("SilentStartup=" + (SilentStartup ? "true" : "false"));
                sb.AppendLine("CloseToTray=" + (CloseToTray ? "true" : "false"));
                File.WriteAllText(SettingsPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        public static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    object value = key == null ? null : key.GetValue(RunName);
                    object legacy = key == null ? null : key.GetValue(LegacyRunName);
                    return (value != null && value.ToString().IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (legacy != null && legacy.ToString().IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0);
                }
            }
            catch { return false; }
        }

        public static void SetAutoStart(bool enabled, bool silent)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    key.DeleteValue(LegacyRunName, false);
                    if (!enabled)
                    {
                        key.DeleteValue(RunName, false);
                        return;
                    }

                    string command = "\"" + Application.ExecutablePath + "\"";
                    if (silent) command += " --silent";
                    key.SetValue(RunName, command, RegistryValueKind.String);
                }
            }
            catch
            {
                MessageBox.Show("写入开机启动设置失败，请确认当前用户权限。", "设置未保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static bool IsTrue(string value)
        {
            string v = (value ?? "").Trim();
            return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class CrashLog
    {
        public static void Write(Exception ex)
        {
            if (ex == null) return;
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "错误日志.txt");
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }

    internal static class AppIcon
    {
        public static Icon Load()
        {
            try
            {
                Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                return icon ?? SystemIcons.Application;
            }
            catch
            {
                return SystemIcons.Application;
            }
        }
    }

    internal sealed class SettingsDialog : Form
    {
        private readonly CheckBox _autoStart = new CheckBox();
        private readonly CheckBox _silent = new CheckBox();
        private readonly CheckBox _closeToTray = new CheckBox();
        public AppSettings Settings { get; private set; }
        public bool AutoStartEnabled { get { return _autoStart.Checked; } }

        public SettingsDialog(AppSettings settings, bool autoStartEnabled)
        {
            Settings = settings;
            Text = "设置";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(430, 286);
            BackColor = Color.FromArgb(18, 20, 29);
            Font = new Font("Microsoft YaHei UI", 10f);

            Label title = new Label
            {
                Text = "启动选项",
                ForeColor = Color.FromArgb(240, 244, 255),
                Font = new Font(Font.FontFamily, 16f, FontStyle.Bold),
                Bounds = new Rectangle(22, 18, 260, 32)
            };

            Label desc = new Label
            {
                Text = "静默启动会在开机自启动时隐藏到托盘，手动打开仍显示主界面。",
                ForeColor = Color.FromArgb(178, 188, 208),
                Bounds = new Rectangle(24, 54, 380, 32)
            };

            _autoStart.Text = "开机自启动";
            _autoStart.Checked = autoStartEnabled;
            _autoStart.ForeColor = Color.FromArgb(232, 236, 248);
            _autoStart.Bounds = new Rectangle(26, 96, 180, 26);
            _autoStart.CheckedChanged += delegate
            {
                if (!_autoStart.Checked) _silent.Checked = false;
                _silent.Enabled = _autoStart.Checked;
            };

            _silent.Text = "静默启动到托盘";
            _silent.Checked = settings.SilentStartup && autoStartEnabled;
            _silent.Enabled = autoStartEnabled;
            _silent.ForeColor = Color.FromArgb(232, 236, 248);
            _silent.Bounds = new Rectangle(26, 128, 190, 26);

            Label closeTitle = new Label
            {
                Text = "关闭按钮",
                ForeColor = Color.FromArgb(240, 244, 255),
                Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
                Bounds = new Rectangle(24, 164, 160, 24)
            };

            _closeToTray.Text = "点击右上角关闭时最小化到托盘";
            _closeToTray.Checked = settings.CloseToTray;
            _closeToTray.ForeColor = Color.FromArgb(232, 236, 248);
            _closeToTray.Bounds = new Rectangle(26, 192, 260, 26);

            Label closeHint = new Label
            {
                Text = "取消勾选后，点击关闭会直接退出程序。",
                ForeColor = Color.FromArgb(178, 188, 208),
                Bounds = new Rectangle(44, 218, 320, 22)
            };

            Button ok = new Button { Text = "保存", DialogResult = DialogResult.OK, Bounds = new Rectangle(244, 248, 76, 28) };
            Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(328, 248, 76, 28) };
            ok.Click += delegate
            {
                Settings.SilentStartup = _autoStart.Checked && _silent.Checked;
                Settings.CloseToTray = _closeToTray.Checked;
            };

            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(title);
            Controls.Add(desc);
            Controls.Add(_autoStart);
            Controls.Add(_silent);
            Controls.Add(closeTitle);
            Controls.Add(_closeToTray);
            Controls.Add(closeHint);
            Controls.Add(ok);
            Controls.Add(cancel);
        }
    }
}
