using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RDPManager
{
    public sealed class PortManagementForm : Form
    {
        private readonly List<DetectedServicePort> services;
        private readonly Func<DetectedServicePort, CancellationToken, Task<IList<int>>> availabilityLoader;
        private ComboBox serviceBox;
        private Label currentPortLabel;
        private Label configLabel;
        private TextBox newPortBox;
        private Label warningLabel;
        private CheckBox firewallBox;
        private Button randomPortButton;
        private bool portsScanned;
        private bool scanningPorts;
        private List<int> availablePorts = new List<int>();

        public PortChangeRequest Request { get; private set; }

        public PortManagementForm(
            Server server,
            PortInspectionResult inspection,
            Func<DetectedServicePort, CancellationToken, Task<IList<int>>> availabilityLoader = null)
        {
            services = inspection == null ? new List<DetectedServicePort>() : inspection.Services;
            this.availabilityLoader = availabilityLoader;
            InitializeComponent(server, inspection);
        }

        private void InitializeComponent(Server server, PortInspectionResult inspection)
        {
            AutoScaleMode = AutoScaleMode.None;
            AutoScaleDimensions = new SizeF(96F, 96F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ClientSize = new Size(560, 350);
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F);
            Text = "服务端口管理";

            Label title = new Label
            {
                AutoSize = true,
                Text = "服务端口管理",
                Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
                ForeColor = Color.FromArgb(35, 42, 49),
                Location = new Point(22, 18)
            };
            Label target = new Label
            {
                AutoEllipsis = true,
                Text = string.Format("服务器：{0}   ·   {1}", server.Name, inspection == null ? "" : inspection.HostName),
                Size = new Size(510, 24),
                ForeColor = Color.FromArgb(105, 115, 125),
                Location = new Point(24, 52)
            };
            Label serviceCaption = CreateCaption("服务类型", 24, 94);
            serviceBox = new ComboBox
            {
                Location = new Point(122, 90),
                Size = new Size(390, 27),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            foreach (DetectedServicePort item in services)
                serviceBox.Items.Add(item);
            serviceBox.SelectedIndexChanged += (sender, args) => UpdateSelectedService();

            Label currentCaption = CreateCaption("当前端口", 24, 132);
            currentPortLabel = new Label
            {
                AutoSize = true,
                Text = "-",
                ForeColor = Color.FromArgb(35, 42, 49),
                Location = new Point(122, 134)
            };
            Label configCaption = CreateCaption("配置位置", 24, 164);
            configLabel = new Label
            {
                AutoEllipsis = true,
                Size = new Size(390, 24),
                Text = "-",
                ForeColor = Color.FromArgb(105, 115, 125),
                Location = new Point(122, 164)
            };
            Label newPortCaption = CreateCaption("新端口", 24, 202);
            newPortBox = new TextBox
            {
                Location = new Point(122, 198),
                Size = new Size(150, 26),
                BorderStyle = BorderStyle.FixedSingle,
                ReadOnly = true,
                Text = "点击检测可用端口"
            };
            newPortBox.MouseDown += async (sender, args) => await EnsurePortScanAsync();
            newPortBox.Enter += async (sender, args) => await EnsurePortScanAsync();
            randomPortButton = CreateButton("随机", Color.FromArgb(42, 125, 185), 56);
            randomPortButton.Location = new Point(280, 196);
            randomPortButton.Enabled = false;
            randomPortButton.Click += async (sender, args) =>
            {
                await EnsurePortScanAsync();
                ChooseRandomPort();
            };
            firewallBox = new CheckBox
            {
                Text = server != null && server.Type == ServerType.Linux ? "自动配置 Linux 防火墙" : "自动配置 Windows 防火墙",
                Checked = true,
                AutoSize = true,
                Location = new Point(350, 201),
                ForeColor = Color.FromArgb(70, 80, 90)
            };
            warningLabel = new Label
            {
                AutoEllipsis = true,
                Size = new Size(510, 26),
                Text = "修改前会自动备份配置；验证失败时尝试恢复原端口",
                ForeColor = Color.FromArgb(193, 117, 28),
                Location = new Point(24, 258)
            };

            Button cancel = CreateButton("取消", Color.FromArgb(105, 115, 125), 80);
            cancel.Location = new Point(326, 302);
            cancel.Click += (sender, args) => DialogResult = DialogResult.Cancel;
            Button execute = CreateButton("开始修改", Color.FromArgb(26, 134, 87), 100);
            execute.Location = new Point(420, 302);
            execute.Click += Execute_Click;

            Controls.Add(title);
            Controls.Add(target);
            Controls.Add(serviceCaption);
            Controls.Add(serviceBox);
            Controls.Add(currentCaption);
            Controls.Add(currentPortLabel);
            Controls.Add(configCaption);
            Controls.Add(configLabel);
            Controls.Add(newPortCaption);
            Controls.Add(newPortBox);
            Controls.Add(randomPortButton);
            Controls.Add(firewallBox);
            Controls.Add(warningLabel);
            Controls.Add(cancel);
            Controls.Add(execute);

            if (serviceBox.Items.Count > 0)
                serviceBox.SelectedIndex = 0;
            else
            {
                serviceBox.Enabled = false;
                execute.Enabled = false;
                warningLabel.Text = "没有发现可自动管理的 RDP、SSH 或数据库服务";
                warningLabel.ForeColor = Color.FromArgb(184, 62, 62);
            }
            AcceptButton = execute;
            CancelButton = cancel;
        }

        private void UpdateSelectedService()
        {
            DetectedServicePort item = serviceBox.SelectedItem as DetectedServicePort;
            if (item == null)
                return;
            currentPortLabel.Text = item.Port.ToString() + " / " + item.Protocol;
            configLabel.Text = string.IsNullOrWhiteSpace(item.ConfigPath) ? "自动识别" : item.ConfigPath;
            newPortBox.Text = item.Port.ToString();
            newPortBox.ReadOnly = true;
            portsScanned = false;
            scanningPorts = false;
            availablePorts.Clear();
            randomPortButton.Enabled = false;
            if (item.ServiceType == "RDP" || item.ServiceType == "SSH")
                warningLabel.Text = "点击新端口输入框会先检测远程端口；修改连接端口可能导致当前连接中断";
            else if (item.ServiceType == "HTTP" || item.ServiceType == "HTTPS")
                warningLabel.Text = item.IsSupported
                    ? item.ConfigPath.IndexOf("nginx", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "请先确认 Nginx 配置中的当前端口；程序只修改当前识别到的 listen 配置"
                        : "请先确认 Apache 配置中的当前端口；程序只修改当前识别到的 Listen/VirtualHost 配置"
                    : "未识别到明确配置文件，程序不会自动修改此 Web 服务";
            else
                warningLabel.Text = "修改前会自动备份配置；验证失败时尝试恢复原端口";
        }

        private void Execute_Click(object sender, EventArgs e)
        {
            DetectedServicePort item = serviceBox.SelectedItem as DetectedServicePort;
            int port;
            if (item == null)
                return;
            if (!item.IsSupported)
            {
                MessageBox.Show(
                    "程序没有识别到可以安全修改的配置目标。\n\n请先在服务器上确认实际 Web 配置文件或站点绑定，当前版本不会盲目修改。",
                    "需要手动确认配置",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            if (!int.TryParse(newPortBox.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                MessageBox.Show("请先点击新端口输入框，让程序快速检测可用端口", "请先检测端口", MessageBoxButtons.OK, MessageBoxIcon.Information);
                newPortBox.Focus();
                return;
            }
            if (port == item.Port)
            {
                MessageBox.Show("新端口与当前端口相同", "无需修改", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (port < 1024 && MessageBox.Show(
                    "新端口属于系统低位端口（1-1023），可能与系统服务或安全策略冲突。\n\n仍然继续吗？",
                    "低位端口提示",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            bool confirmedWeb = false;
            if (item.ServiceType == "HTTP" || item.ServiceType == "HTTPS")
            {
                DialogResult confirmation = MessageBox.Show(
                    "请务必先确认服务器实际配置中的当前端口为 " + item.Port + "。\n\n程序只会修改当前识别到的 Nginx/Apache 配置文件，不会替你猜测容器、反向代理或其他站点配置。\n\n确认继续吗？",
                    "确认 Web 配置",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (confirmation != DialogResult.Yes)
                    return;
                confirmedWeb = true;
            }
            Request = new PortChangeRequest
            {
                Target = item,
                NewPort = port,
                ConfigureFirewall = firewallBox.Checked,
                VerifyAfterChange = true,
                KeepOldFirewallRule = true,
                ConfirmWebConfiguration = confirmedWeb
            };
            DialogResult = DialogResult.OK;
        }

        private async Task EnsurePortScanAsync()
        {
            if (portsScanned || scanningPorts || availabilityLoader == null)
                return;

            DetectedServicePort item = serviceBox.SelectedItem as DetectedServicePort;
            if (item == null)
                return;

            scanningPorts = true;
            newPortBox.ReadOnly = true;
            randomPortButton.Enabled = false;
            warningLabel.Text = "正在快速检测服务器监听端口，请稍候...";
            warningLabel.ForeColor = Color.FromArgb(42, 125, 185);
            try
            {
                IList<int> result = await availabilityLoader(item, CancellationToken.None);
                availablePorts = (result ?? new List<int>()).Where(port => port != item.Port).Distinct().ToList();
                portsScanned = true;
                newPortBox.ReadOnly = false;
                randomPortButton.Enabled = availablePorts.Count > 0;
                warningLabel.Text = "已检测到 " + availablePorts.Count + " 个可用端口；输入框已解锁，旁边按钮可随机选择";
                warningLabel.ForeColor = Color.FromArgb(26, 134, 87);
                newPortBox.SelectAll();
                newPortBox.Focus();
            }
            catch (Exception ex)
            {
                warningLabel.Text = "端口检测失败：" + ex.Message;
                warningLabel.ForeColor = Color.FromArgb(184, 62, 62);
            }
            finally
            {
                scanningPorts = false;
            }
        }

        private void ChooseRandomPort()
        {
            if (availablePorts.Count == 0)
                return;
            Random random = new Random();
            List<int> preferred = availablePorts.Where(port => port >= 10000).ToList();
            IList<int> candidates = preferred.Count > 0 ? preferred : availablePorts;
            newPortBox.ReadOnly = false;
            newPortBox.Text = candidates[random.Next(candidates.Count)].ToString();
            newPortBox.SelectAll();
            newPortBox.Focus();
        }

        private static Label CreateCaption(string text, int x, int y)
        {
            return new Label
            {
                AutoSize = true,
                Text = text,
                ForeColor = Color.FromArgb(75, 84, 93),
                Location = new Point(x, y + 3)
            };
        }

        private static Button CreateButton(string text, Color color, int width)
        {
            Button button = new Button
            {
                Text = text,
                Size = new Size(width, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = color,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = color;
            return button;
        }
    }
}
