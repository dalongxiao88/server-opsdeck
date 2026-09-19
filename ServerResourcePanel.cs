using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ServerForge
{
    public sealed class ServerResourcePanel : UserControl
    {
        private const int GaugeHeight = 118;
        private const int GaugeColumnGap = 8;
        private const int DiskGaugeTopGap = 11;

        private static readonly Color SidebarBackground = Color.FromArgb(232, 235, 238);
        private static readonly Color Surface = Color.White;
        private static readonly Color TextColor = Color.FromArgb(35, 42, 49);
        private static readonly Color MutedColor = Color.FromArgb(104, 114, 124);
        private static readonly Color BorderColor = Color.FromArgb(202, 209, 215);
        private static readonly Color Green = Color.FromArgb(26, 134, 87);
        private static readonly Color Blue = Color.FromArgb(42, 125, 185);
        private static readonly Color Orange = Color.FromArgb(210, 125, 26);
        private static readonly Color Red = Color.FromArgb(184, 62, 62);

        private readonly Panel header;
        private readonly Label serverNameLabel;
        private readonly Label stateLabel;
        private readonly Label messageLabel;
        private readonly LoadingIndicatorControl loadingIndicator;
        private readonly FlowLayoutPanel monitorFlow;
        private readonly Label systemLabel;
        private readonly UsageGaugeControl cpuMeter;
        private readonly UsageGaugeControl memoryMeter;
        private readonly Panel gaugeCanvas;
        private readonly List<UsageGaugeControl> diskMeters = new List<UsageGaugeControl>();
        private Label emptyDiskLabel;
        private readonly NetworkHistoryControl networkChart;
        private readonly Label networkTotalLabel;
        private readonly Label processHeader;
        private readonly ListView processList;
        private readonly Button refreshButton;
        private readonly List<Control> fullWidthControls = new List<Control>();

        public event EventHandler RefreshRequested;

        public ServerResourcePanel()
        {
            BackColor = SidebarBackground;
            Font = new Font("Microsoft YaHei UI", 9F);

            header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 20,
                BackColor = SidebarBackground,
                Padding = new Padding(12, 2, 10, 1)
            };
            serverNameLabel = new Label
            {
                AutoEllipsis = true,
                ForeColor = TextColor,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                Location = new Point(12, 2),
                Size = new Size(160, 21),
                Text = "未选择服务器"
            };
            stateLabel = new Label
            {
                AutoEllipsis = true,
                ForeColor = MutedColor,
                Location = new Point(180, 2),
                Size = new Size(160, 21),
                Text = "等待选择"
            };
            header.Controls.Add(serverNameLabel);
            header.Controls.Add(stateLabel);

            Panel footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 54,
                BackColor = SidebarBackground,
                Padding = new Padding(10, 7, 10, 7)
            };
            refreshButton = new Button
            {
                Dock = DockStyle.Fill,
                Text = "刷新当前服务器",
                FlatStyle = FlatStyle.Flat,
                BackColor = Surface,
                ForeColor = Blue,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            refreshButton.FlatAppearance.BorderColor = Blue;
            refreshButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(239, 244, 247);
            refreshButton.Click += (sender, args) => RefreshRequested?.Invoke(this, EventArgs.Empty);
            footer.Controls.Add(refreshButton);

            messageLabel = new Label
            {
                Dock = DockStyle.Fill,
                BackColor = SidebarBackground,
                ForeColor = MutedColor,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(22),
                Text = "未选择服务器"
            };
            loadingIndicator = new LoadingIndicatorControl
            {
                Dock = DockStyle.Fill,
                BackColor = SidebarBackground,
                Visible = false
            };

            monitorFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = SidebarBackground,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(10, 5, 8, 10),
                Visible = false
            };
            monitorFlow.SizeChanged += (sender, args) => LayoutFullWidthControls();

            systemLabel = new Label
            {
                Location = new Point(142, 1),
                Size = new Size(104, 18),
                ForeColor = Color.FromArgb(43, 94, 128),
                BackColor = Color.FromArgb(218, 228, 235),
                Font = new Font("Microsoft YaHei UI", 8.25F, FontStyle.Bold),
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(3, 0, 3, 0),
                Text = "",
                Visible = false
            };
            systemLabel.TextChanged += (sender, args) =>
                systemLabel.Visible = !string.IsNullOrWhiteSpace(systemLabel.Text);
            header.Controls.Add(systemLabel);
            cpuMeter = new UsageGaugeControl
            {
                Size = new Size(100, GaugeHeight)
            };
            memoryMeter = new UsageGaugeControl
            {
                Size = new Size(100, GaugeHeight)
            };
            gaugeCanvas = new Panel
            {
                Height = GaugeHeight,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = SidebarBackground
            };
            gaugeCanvas.Controls.Add(cpuMeter);
            gaugeCanvas.Controls.Add(memoryMeter);
            Panel networkPanel = new Panel
            {
                Height = 138,
                BackColor = SidebarBackground,
                Margin = new Padding(0)
            };
            networkChart = new NetworkHistoryControl
            {
                Location = new Point(0, 0),
                Height = 112,
                BackColor = Surface
            };
            networkTotalLabel = new Label
            {
                Location = new Point(2, 116),
                Height = 20,
                ForeColor = MutedColor,
                AutoEllipsis = true
            };
            networkPanel.Controls.Add(networkChart);
            networkPanel.Controls.Add(networkTotalLabel);

            processHeader = CreateSectionHeader("额外进程");
            processList = new ListView
            {
                Height = 184,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                MultiSelect = false,
                BackColor = Surface,
                ForeColor = TextColor,
                BorderStyle = BorderStyle.FixedSingle,
                ShowItemToolTips = true
            };
            processList.Columns.Add("进程", 112, HorizontalAlignment.Left);
            processList.Columns.Add("PID", 48, HorizontalAlignment.Right);
            processList.Columns.Add("内存", 76, HorizontalAlignment.Right);

            AddFullWidthControl(gaugeCanvas);
            AddFullWidthControl(networkPanel);
            AddFullWidthControl(processHeader);
            AddFullWidthControl(processList);

            Controls.Add(messageLabel);
            Controls.Add(loadingIndicator);
            Controls.Add(monitorFlow);
            Controls.Add(footer);
            Controls.Add(header);
            Resize += (sender, args) =>
            {
                LayoutHeader();
                LayoutFullWidthControls();
            };
            ShowIdle();
        }

        public void ShowIdle()
        {
            serverNameLabel.Text = "未选择服务器";
            stateLabel.Text = "等待选择";
            stateLabel.ForeColor = MutedColor;
            systemLabel.Text = "";
            loadingIndicator.Visible = false;
            loadingIndicator.Stop();
            refreshButton.Enabled = false;
            monitorFlow.Visible = false;
            messageLabel.Text = "未选择服务器";
            messageLabel.ForeColor = MutedColor;
            messageLabel.Visible = true;
            messageLabel.BringToFront();
        }

        public void ShowLoading(Server server, int percentage, string message)
        {
            serverNameLabel.Text = server == null ? "服务器" : server.Name;
            stateLabel.Text = string.IsNullOrWhiteSpace(message) ? "正在读取资源" : message;
            stateLabel.ForeColor = Blue;
            systemLabel.Text = "";
            loadingIndicator.SetMessage("正在读取资源...");
            loadingIndicator.Visible = true;
            loadingIndicator.Start();
            refreshButton.Enabled = false;
            monitorFlow.Visible = false;
            messageLabel.Text = "正在读取资源...";
            messageLabel.ForeColor = MutedColor;
            messageLabel.Visible = true;
            messageLabel.BringToFront();
            loadingIndicator.BringToFront();
        }

        public void UpdateProgress(int percentage, string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                stateLabel.Text = message;
                loadingIndicator.SetMessage(message);
            }
        }

        public void ShowUnavailable(Server server, string message)
        {
            serverNameLabel.Text = server == null ? "服务器" : server.Name;
            stateLabel.Text = "暂未读取";
            stateLabel.ForeColor = Orange;
            systemLabel.Text = "系统版本未知";
            loadingIndicator.Visible = false;
            loadingIndicator.Stop();
            refreshButton.Enabled = server != null;
            monitorFlow.Visible = false;
            messageLabel.Text = message ?? "资源信息不可用";
            messageLabel.ForeColor = MutedColor;
            messageLabel.Visible = true;
            messageLabel.BringToFront();
        }

        public void ShowError(Server server, string message)
        {
            serverNameLabel.Text = server == null ? "服务器" : server.Name;
            stateLabel.Text = "读取失败";
            stateLabel.ForeColor = Red;
            systemLabel.Text = "系统版本未知";
            loadingIndicator.Visible = false;
            loadingIndicator.Stop();
            refreshButton.Enabled = server != null;
            monitorFlow.Visible = false;
            messageLabel.Text = string.IsNullOrWhiteSpace(message) ? "无法读取服务器资源" : message;
            messageLabel.ForeColor = Red;
            messageLabel.Visible = true;
            messageLabel.BringToFront();
        }

        public void ShowSnapshot(ServerResourceSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            serverNameLabel.Text = string.IsNullOrWhiteSpace(snapshot.ServerName) ? snapshot.HostName : snapshot.ServerName;
            stateLabel.Text = "资源读取完成";
            stateLabel.ForeColor = Green;
            loadingIndicator.Visible = false;
            loadingIndicator.Stop();
            refreshButton.Enabled = true;
            messageLabel.Visible = false;
            monitorFlow.Visible = true;
            monitorFlow.BringToFront();

            systemLabel.Text = FormatOperatingSystem(snapshot.OperatingSystem);
            cpuMeter.SetValue("CPU", snapshot.CpuUsagePercent, snapshot.LogicalProcessorCount + "核");
            memoryMeter.SetValue("内存", snapshot.MemoryUsagePercent,
                string.Format("{0} / {1}", FormatBytes(snapshot.MemoryUsedBytes), FormatBytes(snapshot.MemoryTotalBytes)));

            gaugeCanvas.SuspendLayout();
            foreach (UsageGaugeControl meter in diskMeters)
            {
                gaugeCanvas.Controls.Remove(meter);
                meter.Dispose();
            }
            diskMeters.Clear();
            if (emptyDiskLabel != null)
            {
                gaugeCanvas.Controls.Remove(emptyDiskLabel);
                emptyDiskLabel.Dispose();
                emptyDiskLabel = null;
            }

            if (snapshot.Disks.Count > 0)
            {
                for (int index = 0; index < snapshot.Disks.Count; index++)
                {
                    DiskResourceSnapshot disk = snapshot.Disks[index];
                    UsageGaugeControl meter = new UsageGaugeControl();
                    string caption = FormatDiskCaption(disk);
                    meter.SetValue(caption, disk.UsagePercent,
                        string.Format("{0} / {1}", FormatBytes(disk.UsedBytes), FormatBytes(disk.TotalBytes)));
                    diskMeters.Add(meter);
                    gaugeCanvas.Controls.Add(meter);
                }
            }
            else
            {
                emptyDiskLabel = new Label
                {
                    ForeColor = MutedColor,
                    Text = "未读取到固定磁盘",
                    TextAlign = ContentAlignment.MiddleLeft
                };
                gaugeCanvas.Controls.Add(emptyDiskLabel);
            }
            LayoutGaugeCanvas();
            gaugeCanvas.ResumeLayout(false);

            string networkRate = string.Format("下载 {0}/s    上传 {1}/s",
                FormatBytes(snapshot.CurrentDownloadBytesPerSecond),
                FormatBytes(snapshot.CurrentUploadBytesPerSecond));
            networkChart.SetData(snapshot.NetworkSamples, "网络速率与波动", networkRate);
            networkTotalLabel.Text = snapshot.NetworkTotalReceivedBytes > 0 || snapshot.NetworkTotalSentBytes > 0
                ? string.Format("累计接收 {0} · 发送 {1}", FormatBytes(snapshot.NetworkTotalReceivedBytes), FormatBytes(snapshot.NetworkTotalSentBytes))
                : string.Format("最近 {0} 次采样 · 绿：下载  蓝：上传", Math.Max(1, snapshot.NetworkSamples.Count));

            processHeader.Text = "额外进程  " + snapshot.ExtraProcesses.Count;
            processList.BeginUpdate();
            processList.Items.Clear();
            foreach (ProcessResourceSnapshot process in snapshot.ExtraProcesses)
            {
                ListViewItem item = new ListViewItem(EmptyAsDash(process.Name));
                item.SubItems.Add(process.Id.ToString());
                item.SubItems.Add(FormatBytes(process.WorkingSetBytes));
                item.ToolTipText = string.IsNullOrWhiteSpace(process.Path) ? process.Name : process.Path;
                processList.Items.Add(item);
            }
            if (processList.Items.Count == 0)
            {
                ListViewItem empty = new ListViewItem("未发现额外进程") { ForeColor = MutedColor };
                empty.SubItems.Add("-");
                empty.SubItems.Add("-");
                processList.Items.Add(empty);
            }
            processList.EndUpdate();
            LayoutFullWidthControls();
        }

        private Label CreateSectionHeader(string text)
        {
            return new Label
            {
                Height = 31,
                Text = text,
                ForeColor = TextColor,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                Padding = new Padding(2, 8, 0, 0),
                Margin = new Padding(0, 5, 0, 0)
            };
        }

        private void AddFullWidthControl(Control control)
        {
            control.Margin = new Padding(0);
            monitorFlow.Controls.Add(control);
            fullWidthControls.Add(control);
        }

        private void LayoutFullWidthControls()
        {
            if (monitorFlow == null)
                return;
            int width = Math.Max(190, monitorFlow.ClientSize.Width - monitorFlow.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 2);
            foreach (Control control in fullWidthControls)
                control.Width = width;
            LayoutGaugeCanvas();
            networkChart.Width = width;
            networkTotalLabel.Width = width - 4;

            int processWidth = Math.Max(190, width - 4);
            if (processList.Columns.Count == 3)
            {
                processList.Columns[1].Width = 48;
                processList.Columns[2].Width = 74;
                processList.Columns[0].Width = Math.Max(70, processWidth - processList.Columns[1].Width - processList.Columns[2].Width - 8);
            }
        }

        private void LayoutHeader()
        {
            const int left = 12;
            const int right = 10;
            const int gap = 6;
            int contentWidth = Math.Max(240, header.ClientSize.Width - left - right);
            int columnWidth = Math.Max(1, contentWidth - gap * 2);
            int nameWidth = columnWidth * 36 / 100;
            int systemWidth = columnWidth * 31 / 100;
            int stateWidth = Math.Max(1, columnWidth - nameWidth - systemWidth);

            serverNameLabel.Bounds = new Rectangle(left, 2, nameWidth, 18);
            systemLabel.Bounds = new Rectangle(left + nameWidth + gap, 1, systemWidth, 18);
            stateLabel.Bounds = new Rectangle(left + nameWidth + gap + systemWidth + gap, 2, stateWidth, 18);
        }

        private void LayoutGaugeCanvas()
        {
            if (gaugeCanvas == null)
                return;

            int width = Math.Max(190, gaugeCanvas.ClientSize.Width);
            int leftWidth = Math.Max(1, (width - GaugeColumnGap) / 2);
            int rightX = leftWidth + GaugeColumnGap;
            int rightWidth = Math.Max(1, width - rightX);
            cpuMeter.Bounds = new Rectangle(0, 0, leftWidth, GaugeHeight);
            memoryMeter.Bounds = new Rectangle(rightX, 0, rightWidth, GaugeHeight);

            int diskTop = GaugeHeight + DiskGaugeTopGap;
            for (int index = 0; index < diskMeters.Count; index++)
            {
                int column = index % 2;
                int row = index / 2;
                diskMeters[index].Bounds = column == 0
                    ? new Rectangle(0, diskTop + row * GaugeHeight, leftWidth, GaugeHeight)
                    : new Rectangle(rightX, diskTop + row * GaugeHeight, rightWidth, GaugeHeight);
            }

            if (diskMeters.Count > 0)
            {
                int diskRows = (diskMeters.Count + 1) / 2;
                gaugeCanvas.Height = diskTop + diskRows * GaugeHeight;
            }
            else if (emptyDiskLabel != null)
            {
                emptyDiskLabel.Bounds = new Rectangle(2, diskTop, Math.Max(1, width - 4), 30);
                gaugeCanvas.Height = diskTop + 30;
            }
            else
            {
                gaugeCanvas.Height = GaugeHeight;
            }
        }

        private static string EmptyAsDash(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        private static string FormatDiskCaption(DiskResourceSnapshot disk)
        {
            if (disk == null)
                return "磁盘 -";

            string name = EmptyAsDash(disk.Name);
            string caption = "磁盘 " + name;
            string label = (disk.Label ?? "").Trim();
            // Keep the label only when it is short enough to remain legible inside the gauge.
            if (label.Length > 0 && label.Length <= 5)
                caption += " " + label;
            return caption;
        }

        private static string FormatOperatingSystem(string operatingSystem)
        {
            string value = (operatingSystem ?? "").Trim();
            if (value.Length == 0)
                return "未知系统";

            Match match = Regex.Match(value,
                @"\bWindows(?:\s+Server)?\s+(?<version>20\d{2}(?:\s+R2)?|11|10|8(?:\.1)?|7)\b",
                RegexOptions.IgnoreCase);
            if (match.Success)
                return "Windows " + match.Groups["version"].Value.ToUpperInvariant();

            string[,] distributions =
            {
                { "Ubuntu", @"\bUbuntu\s+(?<version>\d+\.\d+)" },
                { "Debian", @"\bDebian(?:\s+GNU/Linux)?\s+(?<version>\d+(?:\.\d+)?)" },
                { "CentOS", @"\bCentOS(?:\s+Linux|\s+Stream)?\s+(?<version>\d+(?:\.\d+)?)" },
                { "Rocky Linux", @"\bRocky\s+Linux\s+(?<version>\d+(?:\.\d+)?)" },
                { "AlmaLinux", @"\bAlmaLinux\s+(?<version>\d+(?:\.\d+)?)" },
                { "Oracle Linux", @"\bOracle\s+Linux(?:\s+Server)?\s+(?<version>\d+(?:\.\d+)?)" },
                { "Amazon Linux", @"\bAmazon\s+Linux\s+(?<version>\d+(?:\.\d+)?)" },
                { "Alibaba Cloud Linux", @"\bAlibaba\s+Cloud\s+Linux\s+(?<version>\d+(?:\.\d+)?)" },
                { "Fedora", @"\bFedora(?:\s+Linux)?\s+(?<version>\d+(?:\.\d+)?)" },
                { "Linux Mint", @"\bLinux\s+Mint\s+(?<version>\d+(?:\.\d+)?)" },
                { "openSUSE", @"\bopenSUSE(?:\s+Leap)?\s+(?<version>\d+(?:\.\d+)?)" },
                { "Alpine Linux", @"\bAlpine\s+Linux\s+v?(?<version>\d+(?:\.\d+)?)" }
            };
            for (int index = 0; index < distributions.GetLength(0); index++)
            {
                match = Regex.Match(value, distributions[index, 1], RegexOptions.IgnoreCase);
                if (match.Success)
                    return distributions[index, 0] + " " + match.Groups["version"].Value;
            }

            string concise = Regex.Replace(value, @"\s*\([^)]*\)", "");
            concise = Regex.Replace(concise,
                @"\b(Microsoft|Datacenter|Standard|Enterprise|Evaluation|GNU/Linux|LTS)\b",
                "",
                RegexOptions.IgnoreCase);
            concise = Regex.Replace(concise, @"\s+", " ").Trim();
            return concise.Length == 0 ? "未知系统" : concise;
        }

        private static string FormatBytes(double bytes)
        {
            double value = Math.Max(0, bytes);
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0 ? value.ToString("0") + " " + units[unit] : value.ToString(value >= 100 ? "0" : "0.0") + " " + units[unit];
        }

        private sealed class LoadingIndicatorControl : Control
        {
            private readonly Timer animationTimer;
            private int angle;
            private string message = "正在读取资源...";

            public LoadingIndicatorControl()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.ResizeRedraw, true);
                animationTimer = new Timer { Interval = 45 };
                animationTimer.Tick += (sender, args) =>
                {
                    angle = (angle + 12) % 360;
                    Invalidate();
                };
            }

            public void SetMessage(string value)
            {
                message = string.IsNullOrWhiteSpace(value) ? "正在读取资源..." : value;
                Invalidate();
            }

            public void Start()
            {
                angle = 0;
                animationTimer.Start();
                Invalidate();
            }

            public void Stop()
            {
                animationTimer.Stop();
                Invalidate();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    animationTimer.Dispose();
                base.Dispose(disposing);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int diameter = Math.Min(56, Math.Max(38, Math.Min(Width / 4, Height / 7)));
                int centerX = Width / 2;
                int centerY = Math.Max(30, Height / 2 - 12);
                Rectangle ring = new Rectangle(centerX - diameter / 2, centerY - diameter / 2, diameter, diameter);
                using (Pen track = new Pen(Color.FromArgb(210, 216, 221), 5F))
                    e.Graphics.DrawArc(track, ring, 0F, 360F);
                using (Pen active = new Pen(Blue, 5F))
                    e.Graphics.DrawArc(active, ring, angle, 105F);

                Rectangle textArea = new Rectangle(0, centerY + diameter / 2 + 12, Width, 24);
                using (Font textFont = new Font("Microsoft YaHei UI", 9F))
                    TextRenderer.DrawText(e.Graphics, message, textFont, textArea, MutedColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        private sealed class UsageGaugeControl : Control
        {
            private string caption = "";
            private string detail = "";
            private double value;

            public UsageGaugeControl()
            {
                DoubleBuffered = true;
                BackColor = SidebarBackground;
                ForeColor = TextColor;
                Font = new Font("Microsoft YaHei UI", 9F);
                SetStyle(ControlStyles.ResizeRedraw, true);
            }

            public void SetValue(string newCaption, double newValue, string newDetail)
            {
                caption = newCaption ?? "";
                detail = newDetail ?? "";
                value = Math.Max(0, Math.Min(100, newValue));
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int diameter = Math.Max(64, Math.Min(104, Math.Min(Width - 10, Height - 8)));
                int left = (Width - diameter) / 2;
                int top = 5;
                Rectangle ring = new Rectangle(left, top, diameter, diameter);
                Color valueColor = value >= 90 ? Red : value >= 75 ? Orange : Green;
                using (Pen trackPen = new Pen(Color.FromArgb(210, 216, 221), 9F))
                    e.Graphics.DrawArc(trackPen, ring, -90F, 360F);
                if (value > 0)
                {
                    using (Pen valuePen = new Pen(valueColor, 9F))
                        e.Graphics.DrawArc(valuePen, ring, -90F, (float)(360D * value / 100D));
                }

                // Use separate rows inside the enlarged ring so the value, name, and detail never overlap.
                int centerY = top + diameter / 2;
                Rectangle valueArea = new Rectangle(left - 10, centerY - 22, diameter + 20, 22);
                using (Font valueFont = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold))
                    TextRenderer.DrawText(e.Graphics, value.ToString("0.0") + "%", valueFont, valueArea, TextColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                Rectangle captionArea = new Rectangle(left + 6, centerY + 1, Math.Max(1, diameter - 12), 17);
                using (Font captionFont = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold))
                    TextRenderer.DrawText(e.Graphics, caption, captionFont, captionArea, TextColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                Rectangle detailArea = new Rectangle(left + 4, centerY + 18, Math.Max(1, diameter - 8), 16);
                using (Font detailFont = new Font("Microsoft YaHei UI", 7.5F))
                    TextRenderer.DrawText(e.Graphics, detail, detailFont, detailArea, MutedColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }

        private sealed class NetworkHistoryControl : Control
        {
            private List<NetworkRateSample> samples = new List<NetworkRateSample>();
            private string title = "网络速率与波动";
            private string rate = "下载 0 B/s    上传 0 B/s";

            public NetworkHistoryControl()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.ResizeRedraw, true);
            }

            public void SetData(IEnumerable<NetworkRateSample> values, string newTitle, string newRate)
            {
                samples = values == null ? new List<NetworkRateSample>() : values.TakeLast(30).ToList();
                title = string.IsNullOrWhiteSpace(newTitle) ? "网络速率与波动" : newTitle;
                rate = newRate ?? "";
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle plot = new Rectangle(8, 8, Math.Max(1, Width - 16), Math.Max(1, Height - 16));
                using (Pen border = new Pen(BorderColor))
                    e.Graphics.DrawRectangle(border, plot);
                using (Pen grid = new Pen(Color.FromArgb(229, 233, 236)))
                {
                    for (int row = 1; row < 4; row++)
                    {
                        int y = plot.Top + plot.Height * row / 4;
                        e.Graphics.DrawLine(grid, plot.Left, y, plot.Right, y);
                    }
                }

                Color overlayTitle = Color.FromArgb(128, 139, 149);
                Color overlayText = Color.FromArgb(148, 158, 167);
                Rectangle titleArea = new Rectangle(plot.Left + 8, plot.Top + 5, Math.Max(1, plot.Width - 16), 18);
                Rectangle rateArea = new Rectangle(plot.Left + 8, plot.Top + 23, Math.Max(1, plot.Width - 16), 18);
                using (Font titleFont = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold))
                    TextRenderer.DrawText(e.Graphics, title, titleFont, titleArea, overlayTitle,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                using (Font rateFont = new Font("Microsoft YaHei UI", 8F))
                    TextRenderer.DrawText(e.Graphics, rate, rateFont, rateArea, overlayText,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                if (samples.Count == 0)
                    return;
                double maximum = Math.Max(1, samples.Max(item => Math.Max(item.ReceivedBytesPerSecond, item.SentBytesPerSecond)));
                DrawSeries(e.Graphics, plot, samples.Select(item => item.ReceivedBytesPerSecond).ToArray(), maximum, Green);
                DrawSeries(e.Graphics, plot, samples.Select(item => item.SentBytesPerSecond).ToArray(), maximum, Blue);
            }

            private static void DrawSeries(Graphics graphics, Rectangle plot, double[] values, double maximum, Color color)
            {
                if (values.Length == 0)
                    return;
                PointF[] points = new PointF[values.Length];
                for (int index = 0; index < values.Length; index++)
                {
                    float x = values.Length == 1
                        ? plot.Right - 2
                        : plot.Left + index * plot.Width / (float)(values.Length - 1);
                    float y = plot.Bottom - (float)(Math.Max(0, values[index]) / maximum * plot.Height);
                    points[index] = new PointF(x, Math.Max(plot.Top, Math.Min(plot.Bottom, y)));
                }
                using (Pen pen = new Pen(color, 2F))
                using (SolidBrush brush = new SolidBrush(color))
                {
                    if (points.Length > 1)
                        graphics.DrawLines(pen, points);
                    foreach (PointF point in points)
                        graphics.FillEllipse(brush, point.X - 2F, point.Y - 2F, 4F, 4F);
                }
            }
        }
    }
}
