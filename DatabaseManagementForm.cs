using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RDPManager
{
    public enum DatabaseStatusKind
    {
        Normal,
        NeedsConfiguration,
        Error,
        NotDetected,
        Development
    }

    public sealed class DatabaseServiceItem
    {
        public string Type { get; set; }
        public string DisplayName { get; set; }
        public string ServiceName { get; set; }
        public string Version { get; set; }
        public int Port { get; set; }
        public string Status { get; set; }
        public string CredentialState { get; set; }
        public bool IsSupported { get; set; }
        public bool IsDetected { get; set; }
        public string RemoteServiceStatus { get; set; }
        public DatabaseStatusKind StatusKind { get; set; }

        public Color GetStatusColor()
        {
            switch (StatusKind)
            {
                case DatabaseStatusKind.Normal:
                    return Color.FromArgb(26, 134, 87);
                case DatabaseStatusKind.NeedsConfiguration:
                    return Color.FromArgb(210, 125, 26);
                case DatabaseStatusKind.Error:
                    return Color.FromArgb(184, 62, 62);
                default:
                    return Color.FromArgb(128, 136, 144);
            }
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    public sealed class DatabaseManagementForm : Form
    {
        private static readonly Color WindowBackground = Color.FromArgb(241, 243, 245);
        private static readonly Color Surface = Color.White;
        private static readonly Color TextColor = Color.FromArgb(35, 42, 49);
        private static readonly Color MutedColor = Color.FromArgb(104, 114, 124);
        private static readonly Color BorderColor = Color.FromArgb(211, 217, 222);
        private static readonly Color Green = Color.FromArgb(26, 134, 87);
        private static readonly Color Blue = Color.FromArgb(42, 125, 185);
        private static readonly Color Purple = Color.FromArgb(116, 86, 166);
        private static readonly Color Orange = Color.FromArgb(210, 125, 26);

        private readonly Server server;
        private readonly List<DatabaseServiceItem> services;
        private readonly Func<CancellationToken, Task<PortInspectionResult>> inspectionLoader;
        private readonly string serverPassword;
        private readonly Func<bool> persistChanges;
        private PortInspectionResult inspection;
        private ListBox serviceList;
        private Panel detailHost;
        private Label selectionLabel;
        private Label databaseCountLabel;
        private Label readyCountLabel;
        private Label pendingCountLabel;
        private Button refreshButton;
        private CancellationTokenSource refreshCancellation;
        private bool oracleNoticeShown;

        public DatabaseManagementForm(Server server)
            : this(server, null, null, "", null)
        {
        }

        public DatabaseManagementForm(
            Server server,
            PortInspectionResult inspection,
            Func<CancellationToken, Task<PortInspectionResult>> inspectionLoader = null,
            string serverPassword = "",
            Func<bool> persistChanges = null)
        {
            this.server = server;
            this.inspection = inspection;
            this.inspectionLoader = inspectionLoader;
            this.serverPassword = serverPassword ?? "";
            this.persistChanges = persistChanges;
            services = CreateServices(server, inspection);
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Text = "数据库管理 · " + (server == null ? "服务器" : server.Name);
            ClientSize = new Size(1100, 700);
            MinimumSize = new Size(950, 600);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = WindowBackground;
            Font = new Font("Microsoft YaHei UI", 9F);
            TryLoadIcon();

            Panel header = CreateHeader();
            Panel metrics = CreateMetrics();
            Panel content = CreateContent();
            StatusStrip footer = new StatusStrip
            {
                Dock = DockStyle.Bottom,
                BackColor = Surface,
                SizingGrip = false
            };
            footer.Items.Add(new ToolStripStatusLabel
            {
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = MutedColor,
                Text = BuildFooterText()
            });

            Controls.Add(content);
            Controls.Add(metrics);
            Controls.Add(header);
            Controls.Add(footer);

            if (serviceList.Items.Count > 0)
                serviceList.SelectedIndex = 0;
        }

        private Panel CreateHeader()
        {
            Panel panel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 68,
                BackColor = Surface,
                Padding = new Padding(20, 11, 20, 8)
            };
            Label title = new Label
            {
                AutoSize = true,
                Text = "数据库管理",
                Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
                ForeColor = TextColor,
                Location = new Point(20, 12)
            };
            Label subtitle = new Label
            {
                AutoEllipsis = true,
                Size = new Size(560, 22),
                Text = string.Format("服务器：{0}   ·   管理通道：SSH / WinRM   ·   凭据按数据库独立保存", server == null ? "-" : server.Name),
                ForeColor = MutedColor,
                Location = new Point(22, 40)
            };
            Button close = CreateButton("关闭", MutedColor, false, 78);
            close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            close.Location = new Point(980, 18);
            close.Click += (sender, args) => Close();
            panel.Controls.Add(title);
            panel.Controls.Add(subtitle);
            panel.Controls.Add(close);
            panel.Resize += (sender, args) => close.Left = panel.ClientSize.Width - close.Width - 20;
            return panel;
        }

        private Panel CreateMetrics()
        {
            Panel panel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 78,
                BackColor = WindowBackground,
                Padding = new Padding(16, 10, 16, 10)
            };
            databaseCountLabel = new Label { Text = services.Count(item => item.IsDetected).ToString() };
            readyCountLabel = new Label { Text = services.Count(item => item.StatusKind == DatabaseStatusKind.Normal).ToString() };
            pendingCountLabel = new Label { Text = services.Count(item => item.StatusKind == DatabaseStatusKind.NeedsConfiguration).ToString() };
            FlowLayoutPanel flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                BackColor = WindowBackground,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            flow.Controls.Add(CreateMetricCard("数据库服务", databaseCountLabel, Purple));
            flow.Controls.Add(CreateMetricCard("已验证", readyCountLabel, Green));
            flow.Controls.Add(CreateMetricCard("待配置", pendingCountLabel, Orange));

            Button deploy = CreateButton("部署数据库", Blue, true, 150);
            deploy.Dock = DockStyle.Fill;
            deploy.Margin = new Padding(0);
            deploy.Enabled = server != null;
            deploy.Click += async (sender, args) => await OpenDatabaseDeploymentAsync();

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = WindowBackground,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.Controls.Add(flow, 0, 0);
            layout.Controls.Add(deploy, 1, 0);
            panel.Controls.Add(layout);
            return panel;
        }

        private Panel CreateMetricCard(string caption, Label valueLabel, Color accent)
        {
            Panel card = new Panel
            {
                Width = 194,
                Height = 58,
                BackColor = Surface,
                Margin = new Padding(0, 0, 10, 0),
                Padding = new Padding(14, 7, 10, 6),
                BorderStyle = BorderStyle.FixedSingle
            };
            Label captionLabel = new Label
            {
                AutoSize = true,
                Text = caption,
                ForeColor = MutedColor,
                Location = new Point(12, 8)
            };
            valueLabel.AutoSize = true;
            valueLabel.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);
            valueLabel.ForeColor = accent;
            valueLabel.Location = new Point(12, 26);
            card.Controls.Add(captionLabel);
            card.Controls.Add(valueLabel);
            return card;
        }

        private Panel CreateContent()
        {
            Panel content = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = WindowBackground,
                Padding = new Padding(16, 0, 16, 12)
            };
            Panel right = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Surface,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(0)
            };
            detailHost = right;
            Panel left = CreateServiceSidebar();
            content.Controls.Add(right);
            content.Controls.Add(left);
            return content;
        }

        private Panel CreateServiceSidebar()
        {
            Panel panel = new Panel
            {
                Dock = DockStyle.Left,
                Width = 258,
                BackColor = Color.FromArgb(232, 235, 238),
                Padding = new Padding(12, 12, 10, 12)
            };
            Label caption = new Label
            {
                AutoSize = true,
                Text = "数据库服务",
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                ForeColor = TextColor,
                Location = new Point(13, 12)
            };
            selectionLabel = new Label
            {
                AutoEllipsis = true,
                Size = new Size(220, 34),
                Text = "选择服务查看管理面板",
                ForeColor = MutedColor,
                Location = new Point(13, 35)
            };
            serviceList = new ListBox
            {
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 70,
                IntegralHeight = false,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Surface,
                ForeColor = TextColor,
                Location = new Point(12, 76),
                Size = new Size(224, 470),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                SelectionMode = SelectionMode.One
            };
            foreach (DatabaseServiceItem item in services)
                serviceList.Items.Add(item);
            serviceList.DrawItem += DrawServiceItem;
            serviceList.SelectedIndexChanged += ServiceList_SelectedIndexChanged;
            refreshButton = CreateButton("刷新检测", Blue, false, 100);
            refreshButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            refreshButton.Location = new Point(12, 562);
            refreshButton.Enabled = inspectionLoader != null;
            refreshButton.Click += async (sender, args) => await RefreshInspectionAsync();
            panel.Controls.Add(caption);
            panel.Controls.Add(selectionLabel);
            panel.Controls.Add(serviceList);
            panel.Controls.Add(refreshButton);
            panel.Resize += (sender, args) =>
            {
                serviceList.Height = panel.ClientSize.Height - 160;
                refreshButton.Top = panel.ClientSize.Height - 70;
            };
            return panel;
        }

        private async Task OpenDatabaseDeploymentAsync()
        {
            if (server == null)
            {
                MessageBox.Show("当前没有可用的目标服务器。", "无法部署", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using (DatabaseDeploymentForm form = new DatabaseDeploymentForm(server, serverPassword, persistChanges))
            {
                if (form.ShowDialog(this) == DialogResult.OK && form.DeploymentCompleted)
                {
                    string deployedType = form.Draft == null ? "" : form.Draft.DatabaseType;
                    int deployedPort = form.Draft == null ? 0 : form.Draft.Port;
                    await RefreshInspectionAsync();
                    for (int index = 0; index < serviceList.Items.Count; index++)
                    {
                        DatabaseServiceItem item = serviceList.Items[index] as DatabaseServiceItem;
                        if (item != null && item.Port == deployedPort && string.Equals(item.Type, deployedType, StringComparison.OrdinalIgnoreCase))
                        {
                            serviceList.SelectedIndex = index;
                            break;
                        }
                    }
                }
            }
        }

        private void DrawServiceItem(object sender, DrawItemEventArgs args)
        {
            if (args.Index < 0 || args.Index >= serviceList.Items.Count)
                return;
            DatabaseServiceItem item = (DatabaseServiceItem)serviceList.Items[args.Index];
            bool selected = (args.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color background = selected ? Color.FromArgb(226, 239, 231) : Surface;
            Color accent = item.Type == "Oracle" ? Orange : item.Type == "Redis" ? Purple : Blue;
            Color statusColor = item.GetStatusColor();
            using (SolidBrush backgroundBrush = new SolidBrush(background))
                args.Graphics.FillRectangle(backgroundBrush, args.Bounds);
            using (SolidBrush dotBrush = new SolidBrush(statusColor))
                args.Graphics.FillEllipse(dotBrush, args.Bounds.Left + 12, args.Bounds.Top + 17, 10, 10);
            using (SolidBrush titleBrush = new SolidBrush(TextColor))
                args.Graphics.DrawString(item.DisplayName, Font, titleBrush, args.Bounds.Left + 32, args.Bounds.Top + 10);
            using (SolidBrush detailBrush = new SolidBrush(statusColor))
                args.Graphics.DrawString(string.Format("{0}   ·   TCP {1}", item.Status, item.Port), Font, detailBrush, args.Bounds.Left + 32, args.Bounds.Top + 33);
            using (SolidBrush credentialBrush = new SolidBrush(item.IsDetected ? accent : MutedColor))
                args.Graphics.DrawString(item.CredentialState, Font, credentialBrush, args.Bounds.Left + 32, args.Bounds.Top + 51);
            if (selected)
                using (Pen pen = new Pen(statusColor, 2F))
                    args.Graphics.DrawLine(pen, args.Bounds.Left + 1, args.Bounds.Top + 1, args.Bounds.Left + 1, args.Bounds.Bottom - 1);
        }

        private void ServiceList_SelectedIndexChanged(object sender, EventArgs e)
        {
            DatabaseServiceItem item = serviceList.SelectedItem as DatabaseServiceItem;
            if (item == null)
                return;
            selectionLabel.Text = item.DisplayName + " · " + item.Port;
            if (item.Type == "Oracle" && !oracleNoticeShown)
            {
                oracleNoticeShown = true;
                MessageBox.Show("Oracle 数据库管理功能正在开发中。\n\n当前版本暂不执行 Oracle 用户、权限、备份或迁移操作。", "Oracle 暂未开放", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            detailHost.Controls.Clear();
            Control detail;
            if (item.Type == "Oracle")
                detail = new OracleDatabasePanel(server, item);
            else if (item.Type == "Redis")
                detail = new RedisDatabasePanel(server, item, serverPassword, persistChanges, RefreshCredentialStates);
            else if (item.Type == "MongoDB")
                detail = new MongoDbDatabasePanel(server, item, serverPassword, persistChanges, RefreshCredentialStates);
            else
                detail = new MySqlDatabasePanel(server, item, item.Type, serverPassword, persistChanges, RefreshCredentialStates);
            detail.Dock = DockStyle.Fill;
            detailHost.Controls.Add(detail);
        }

        private void RefreshCredentialStates()
        {
            foreach (DatabaseServiceItem item in services)
            {
                if (!item.IsDetected || string.Equals(item.Type, "Oracle", StringComparison.OrdinalIgnoreCase))
                    continue;
                bool verified = server != null && server.DatabaseCredentials != null && server.DatabaseCredentials.Any(credential =>
                    string.Equals(credential.DatabaseType, item.Type, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(credential.ServiceName, item.ServiceName, StringComparison.OrdinalIgnoreCase) &&
                    credential.Port == item.Port && credential.IsVerified);
                if (item.StatusKind != DatabaseStatusKind.Error)
                    item.StatusKind = verified ? DatabaseStatusKind.Normal : DatabaseStatusKind.NeedsConfiguration;
                item.CredentialState = verified ? "凭据已验证" : "未录入凭据";
            }
            databaseCountLabel.Text = services.Count(item => item.IsDetected).ToString();
            readyCountLabel.Text = services.Count(item => item.StatusKind == DatabaseStatusKind.Normal).ToString();
            pendingCountLabel.Text = services.Count(item => item.StatusKind == DatabaseStatusKind.NeedsConfiguration).ToString();
            serviceList.Invalidate();
        }

        private string BuildFooterText()
        {
            if (inspection == null)
                return "尚未执行远程数据库检测";
            int detected = services.Count(item => item.IsDetected);
            int errors = services.Count(item => item.StatusKind == DatabaseStatusKind.Error);
            return errors == 0
                ? string.Format("服务状态来自远程检测 · 已发现 {0} 项 · SSH 隧道管理已开放", detected)
                : string.Format("服务状态来自远程检测 · 已发现 {0} 项 · 异常 {1} 项", detected, errors);
        }

        private async Task RefreshInspectionAsync()
        {
            if (inspectionLoader == null || refreshCancellation != null)
                return;

            refreshCancellation = new CancellationTokenSource();
            refreshButton.Enabled = false;
            refreshButton.Text = "检测中...";
            try
            {
                PortInspectionResult updated = await inspectionLoader(refreshCancellation.Token);
                if (updated == null)
                    return;
                inspection = updated;
                services.Clear();
                services.AddRange(CreateServices(server, inspection));
                serviceList.Items.Clear();
                foreach (DatabaseServiceItem item in services)
                    serviceList.Items.Add(item);
                databaseCountLabel.Text = services.Count(item => item.IsDetected).ToString();
                readyCountLabel.Text = services.Count(item => item.StatusKind == DatabaseStatusKind.Normal).ToString();
                pendingCountLabel.Text = services.Count(item => item.StatusKind == DatabaseStatusKind.NeedsConfiguration).ToString();
                if (serviceList.Items.Count > 0)
                    serviceList.SelectedIndex = 0;
                serviceList.Invalidate();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                MessageBox.Show("刷新数据库状态失败：" + ex.Message, "检测失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                refreshCancellation.Dispose();
                refreshCancellation = null;
                refreshButton.Text = "刷新检测";
                refreshButton.Enabled = inspectionLoader != null;
            }
        }

        private static List<DatabaseServiceItem> CreateServices(Server server, PortInspectionResult inspection)
        {
            List<DatabaseServiceItem> list = new List<DatabaseServiceItem>();
            AddDetectedOrPlaceholder(list, server, inspection, "MySQL", "MySQL80", "8.x", 3306);
            AddDetectedOrPlaceholder(list, server, inspection, "MariaDB", "MariaDB", "10.x / 11.x", 3306);
            AddDetectedOrPlaceholder(list, server, inspection, "MongoDB", "MongoDB", "7.x", 27017);
            AddDetectedOrPlaceholder(list, server, inspection, "Redis", "Redis", "7.x", 6379);
            AddDetectedOrPlaceholder(list, server, inspection, "Oracle", "Oracle Listener", "21c XE", 1521);
            return list;
        }

        private static void AddDetectedOrPlaceholder(
            List<DatabaseServiceItem> list,
            Server server,
            PortInspectionResult inspection,
            string type,
            string defaultName,
            string version,
            int defaultPort)
        {
            List<DetectedServicePort> found = inspection == null
                ? new List<DetectedServicePort>()
                : inspection.Services.Where(item => string.Equals(item.ServiceType, type, StringComparison.OrdinalIgnoreCase)).ToList();
            if (found.Count == 0)
            {
                list.Add(CreateUndetectedItem(server, type, defaultName, version, defaultPort));
                return;
            }
            foreach (DetectedServicePort item in found)
                list.Add(CreateDetectedItem(server, type, version, item));
        }

        private static DatabaseServiceItem CreateUndetectedItem(Server server, string type, string name, string version, int defaultPort)
        {
            int port = defaultPort;
            if (server != null && server.ServicePorts != null)
            {
                ServicePortRecord record = server.ServicePorts.FirstOrDefault(item =>
                    string.Equals(item.ServiceType, type, StringComparison.OrdinalIgnoreCase) ||
                    (type == "MySQL" && string.Equals(item.ServiceType, "MariaDB", StringComparison.OrdinalIgnoreCase)));
                if (record != null && record.Port > 0)
                    port = record.Port;
            }
            return new DatabaseServiceItem
            {
                Type = type,
                DisplayName = type,
                ServiceName = name,
                Version = version,
                Port = port,
                Status = "未检测到",
                CredentialState = type == "Oracle" ? "暂不支持" : "未发现服务",
                IsSupported = type != "Oracle",
                IsDetected = false,
                RemoteServiceStatus = "",
                StatusKind = DatabaseStatusKind.NotDetected
            };
        }

        private static DatabaseServiceItem CreateDetectedItem(Server server, string type, string version, DetectedServicePort detected)
        {
            bool isOracle = string.Equals(type, "Oracle", StringComparison.OrdinalIgnoreCase);
            bool isRunning = string.Equals(detected.ServiceStatus, "Running", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(detected.ServiceStatus, "active", StringComparison.OrdinalIgnoreCase);
            bool isStopped = string.Equals(detected.ServiceStatus, "Stopped", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(detected.ServiceStatus, "Paused", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(detected.ServiceStatus, "inactive", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(detected.ServiceStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(detected.ServiceStatus, "dead", StringComparison.OrdinalIgnoreCase);
            string serviceName = string.IsNullOrWhiteSpace(detected.DisplayName)
                ? detected.ServiceName
                : detected.DisplayName;
            bool verified = !isOracle && server != null && server.DatabaseCredentials != null && server.DatabaseCredentials.Any(credential =>
                string.Equals(credential.DatabaseType, type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(credential.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase) &&
                credential.Port == detected.Port && credential.IsVerified);
            DatabaseStatusKind kind = isStopped
                ? DatabaseStatusKind.Error
                : isOracle
                    ? DatabaseStatusKind.Development
                    : verified ? DatabaseStatusKind.Normal : DatabaseStatusKind.NeedsConfiguration;
            return new DatabaseServiceItem
            {
                Type = type,
                DisplayName = type,
                ServiceName = serviceName,
                Version = version,
                Port = detected.Port,
                Status = isRunning ? "运行中" : isStopped ? "服务异常" : "状态未知",
                CredentialState = isOracle ? "功能开发中" : verified ? "凭据已验证" : "未录入凭据",
                IsSupported = !isOracle,
                IsDetected = true,
                RemoteServiceStatus = detected.ServiceStatus,
                StatusKind = kind
            };
        }

        private void ShowPreviewMessage(string action)
        {
            MessageBox.Show(action + "界面已完成，真实数据库逻辑将在界面确认后接入。", "界面预览", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static Button CreateButton(string text, Color color, bool primary, int width)
        {
            Button button = new Button
            {
                Text = text,
                Width = width,
                Height = 34,
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? color : Surface,
                ForeColor = primary ? Color.White : color,
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = primary ? color : Color.FromArgb(190, 198, 205);
            button.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(color) : Color.FromArgb(239, 244, 241);
            return button;
        }

        private void TryLoadIcon()
        {
            try
            {
                using (System.IO.Stream stream = typeof(DatabaseManagementForm).Assembly.GetManifestResourceStream("RDPManager.favicon.ico"))
                {
                    if (stream != null)
                        Icon = new Icon(stream);
                }
            }
            catch
            {
            }
        }
    }

    public abstract class DatabaseDetailPanel : UserControl
    {
        protected static readonly Color Surface = Color.White;
        protected static readonly Color TextColor = Color.FromArgb(35, 42, 49);
        protected static readonly Color MutedColor = Color.FromArgb(104, 114, 124);
        protected static readonly Color BorderColor = Color.FromArgb(211, 217, 222);
        protected static readonly Color Green = Color.FromArgb(26, 134, 87);
        protected static readonly Color Blue = Color.FromArgb(42, 125, 185);
        protected static readonly Color Purple = Color.FromArgb(116, 86, 166);
        protected static readonly Color Orange = Color.FromArgb(210, 125, 26);

        protected readonly Server Server;
        protected readonly DatabaseServiceItem Item;
        protected TabControl Tabs;
        protected Label ShellStatusLabel;

        protected DatabaseDetailPanel(Server server, DatabaseServiceItem item)
        {
            Server = server;
            Item = item;
            Dock = DockStyle.Fill;
            BackColor = Surface;
            Font = new Font("Microsoft YaHei UI", 9F);
        }

        protected void BuildShell(string title, string subtitle, Color accent)
        {
            Panel header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 84,
                BackColor = Surface,
                Padding = new Padding(22, 14, 18, 8)
            };
            Label titleLabel = new Label
            {
                AutoSize = true,
                Text = title,
                Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
                ForeColor = TextColor,
                Location = new Point(22, 13)
            };
            Label subtitleLabel = new Label
            {
                AutoEllipsis = true,
                Size = new Size(570, 24),
                Text = subtitle,
                ForeColor = MutedColor,
                Location = new Point(24, 44)
            };
            ShellStatusLabel = new Label
            {
                AutoEllipsis = true,
                Size = new Size(350, 24),
                Text = Item.Status + "  ·  TCP " + Item.Port + "  ·  " + Item.CredentialState,
                ForeColor = Item.GetStatusColor(),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(0, 14)
            };
            header.Controls.Add(titleLabel);
            header.Controls.Add(subtitleLabel);
            header.Controls.Add(ShellStatusLabel);
            header.Resize += (sender, args) =>
            {
                ShellStatusLabel.Left = Math.Max(titleLabel.Right + 18, header.ClientSize.Width - ShellStatusLabel.Width - 18);
                ShellStatusLabel.Width = Math.Max(220, header.ClientSize.Width - ShellStatusLabel.Left - 18);
                subtitleLabel.Width = Math.Max(220, ShellStatusLabel.Left - subtitleLabel.Left - 18);
            };
            Tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Padding = new Point(16, 6),
                Appearance = TabAppearance.Normal,
                HotTrack = true
            };
            Controls.Add(Tabs);
            Controls.Add(header);
        }

        protected TabPage CreateTab(string title)
        {
            TabPage page = new TabPage(title)
            {
                BackColor = Color.FromArgb(250, 251, 252),
                Padding = new Padding(18),
                AutoScroll = true
            };
            return page;
        }

        protected Panel CreateSurfacePanel(int height = 110)
        {
            return new Panel
            {
                Dock = DockStyle.Top,
                Height = height,
                BackColor = Surface,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 0, 0, 12),
                Padding = new Padding(16)
            };
        }

        protected Label CreateHeading(string text, int x, int y)
        {
            return new Label
            {
                AutoSize = true,
                Text = text,
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                ForeColor = TextColor,
                Location = new Point(x, y)
            };
        }

        protected Label CreateValue(string text, int x, int y, Color color = default(Color))
        {
            return new Label
            {
                AutoSize = true,
                Text = text,
                ForeColor = color == default(Color) ? TextColor : color,
                Location = new Point(x, y)
            };
        }

        protected Button CreatePanelButton(string text, Color color, int width = 104)
        {
            Button button = new Button
            {
                Text = text,
                Width = width,
                Height = 34,
                FlatStyle = FlatStyle.Flat,
                BackColor = Surface,
                ForeColor = color,
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 8, 0)
            };
            button.FlatAppearance.BorderColor = color;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(239, 244, 241);
            button.Click += (sender, args) => PreviewAction(text);
            return button;
        }

        protected Button CreateFunctionalPanelButton(string text, Color color, int width = 104)
        {
            Button button = new Button
            {
                Text = text,
                Width = width,
                Height = 34,
                FlatStyle = FlatStyle.Flat,
                BackColor = Surface,
                ForeColor = color,
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 8, 0)
            };
            button.FlatAppearance.BorderColor = color;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(239, 244, 241);
            return button;
        }

        protected Panel CreateConnectionOverviewPanel(
            string title,
            string accountText,
            string scopeCaption,
            string scopeText,
            string credentialText,
            Color credentialColor,
            EventHandler testHandler,
            EventHandler versionHandler,
            out Label accountLabel,
            out Label scopeLabel,
            out Label statusLabel,
            out Label credentialLabel,
            out Label versionLabel)
        {
            Panel panel = CreateSurfacePanel(220);
            panel.Controls.Add(CreateHeading(title, 16, 14));

            panel.Controls.Add(CreateValue("连接地址", 18, 50, MutedColor));
            panel.Controls.Add(CreateValue("127.0.0.1:" + Item.Port, 18, 74));
            panel.Controls.Add(CreateValue("管理账号", 275, 50, MutedColor));
            accountLabel = CreateValue(accountText, 275, 74);
            panel.Controls.Add(accountLabel);
            panel.Controls.Add(CreateValue("服务状态", 530, 50, MutedColor));
            statusLabel = CreateValue(Item.Status, 530, 74, Item.GetStatusColor());
            panel.Controls.Add(statusLabel);

            panel.Controls.Add(CreateValue(scopeCaption, 18, 106, MutedColor));
            scopeLabel = CreateValue(scopeText, 18, 130, Orange);
            panel.Controls.Add(scopeLabel);
            panel.Controls.Add(CreateValue("凭据状态", 275, 106, MutedColor));
            credentialLabel = CreateValue(credentialText, 275, 130, credentialColor);
            panel.Controls.Add(credentialLabel);
            panel.Controls.Add(CreateValue("版本信息", 530, 106, MutedColor));
            versionLabel = CreateValue("点击查询", 530, 130, Blue);
            panel.Controls.Add(versionLabel);

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Location = new Point(18, 164),
                Size = new Size(500, 40),
                WrapContents = false
            };
            Button test = CreateFunctionalPanelButton("连接测试", Green);
            test.Click += testHandler;
            buttons.Controls.Add(test);
            Button version = CreateFunctionalPanelButton("查看版本", Blue);
            version.Click += versionHandler;
            buttons.Controls.Add(version);
            panel.Controls.Add(buttons);
            return panel;
        }

        protected void PreviewAction(string action)
        {
            MessageBox.Show(action + "界面已完成，真实数据库逻辑将在界面确认后接入。", "界面预览", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        protected void RefreshShellStatus()
        {
            if (ShellStatusLabel == null)
                return;
            ShellStatusLabel.Text = Item.Status + "  ·  TCP " + Item.Port + "  ·  " + Item.CredentialState;
            ShellStatusLabel.ForeColor = Item.GetStatusColor();
        }

        protected DataGridView CreateGrid(params string[] columns)
        {
            DataGridView grid = new DataGridView
            {
                Dock = DockStyle.Top,
                Height = 220,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoGenerateColumns = false,
                BackgroundColor = Surface,
                BorderStyle = BorderStyle.FixedSingle,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
                ColumnHeadersHeight = 32,
                EnableHeadersVisualStyles = false,
                GridColor = BorderColor,
                MultiSelect = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                RowTemplate = { Height = 34 },
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(246, 247, 248),
                ForeColor = MutedColor,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                SelectionBackColor = Color.FromArgb(246, 247, 248),
                SelectionForeColor = MutedColor
            };
            grid.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Surface,
                ForeColor = TextColor,
                SelectionBackColor = Color.FromArgb(220, 238, 229),
                SelectionForeColor = TextColor
            };
            foreach (string column in columns)
                grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = column,
                    AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                    SortMode = DataGridViewColumnSortMode.NotSortable
                });
            return grid;
        }

        protected FlowLayoutPanel CreateButtonRow(params Button[] buttons)
        {
            FlowLayoutPanel row = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 42,
                WrapContents = false,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 4, 0, 4)
            };
            foreach (Button button in buttons)
                row.Controls.Add(button);
            return row;
        }

        protected static void AddNote(Control parent, string text, int top, Color color = default(Color))
        {
            parent.Controls.Add(new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Top,
                Height = 30,
                Text = text,
                ForeColor = color == default(Color) ? MutedColor : color,
                Padding = new Padding(0, 5, 0, 0)
            });
        }
    }

    public sealed class MySqlDatabasePanel : DatabaseDetailPanel
    {
        private readonly string databaseTitle;
        private readonly string serverPassword;
        private readonly Func<bool> persistChanges;
        private readonly Action credentialChanged;
        private readonly MySqlDatabaseService databaseService = new MySqlDatabaseService();
        private readonly MySqlBackupService backupService = new MySqlBackupService();
        private DatabaseCredentialRecord credential;
        private Label overviewStatusLabel;
        private Label overviewCredentialLabel;
        private Label overviewAccountLabel;
        private Label overviewScopeLabel;
        private Label overviewVersionLabel;
        private Label usersNoteLabel;
        private DataGridView usersGrid;

        public MySqlDatabasePanel(Server server, DatabaseServiceItem item, string databaseTitle)
            : this(server, item, databaseTitle, "", null, null)
        {
        }

        public MySqlDatabasePanel(
            Server server,
            DatabaseServiceItem item,
            string databaseTitle,
            string serverPassword,
            Func<bool> persistChanges,
            Action credentialChanged)
            : base(server, item)
        {
            this.databaseTitle = databaseTitle;
            this.serverPassword = serverPassword ?? "";
            this.persistChanges = persistChanges;
            this.credentialChanged = credentialChanged;
            credential = FindCredential();
            BuildShell(databaseTitle + " 管理", "用户、权限、备份和迁移使用独立面板", Blue);
            Tabs.TabPages.Add(CreateOverviewTab());
            Tabs.TabPages.Add(CreateUsersTab());
            Tabs.TabPages.Add(CreateMigrationTab());
            Tabs.TabPages.Add(CreateConnectionTab());
            Tabs.Selected += async (sender, args) =>
            {
                if (args.TabPage != null && args.TabPage.Text == "用户与权限")
                    await RefreshUsersAsync();
            };
        }

        private TabPage CreateOverviewTab()
        {
            TabPage page = CreateTab("数据库概览");
            Panel overview = CreateConnectionOverviewPanel(
                databaseTitle + " 连接概览",
                GetManagementAccountText(),
                "默认数据库",
                GetAuthenticationScopeText(),
                GetCredentialDisplayText(),
                GetCredentialColor(),
                async (sender, args) => await TestCredentialAsync(),
                async (sender, args) => await ShowVersionAsync((Button)sender),
                out overviewAccountLabel,
                out overviewScopeLabel,
                out overviewStatusLabel,
                out overviewCredentialLabel,
                out overviewVersionLabel);
            page.Controls.Add(overview);
            return page;
        }

        private TabPage CreateUsersTab()
        {
            TabPage page = CreateTab("用户与权限");
            Panel section = CreateSurfacePanel(430);
            section.Controls.Add(CreateHeading(databaseTitle + " 用户", 16, 14));
            usersNoteLabel = new Label
            {
                AutoEllipsis = true,
                Size = new Size(760, 24),
                Text = "默认不显示密码，创建成功并验证登录后才保存凭据。",
                ForeColor = MutedColor,
                Location = new Point(18, 43)
            };
            section.Controls.Add(usersNoteLabel);
            usersGrid = CreateGrid("用户名", "来源主机", "权限范围", "状态", "凭据");
            usersGrid.Location = new Point(16, 76);
            usersGrid.Size = new Size(760, 220);
            usersGrid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            section.Controls.Add(usersGrid);
            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Location = new Point(16, 310),
                Size = new Size(700, 42),
                WrapContents = false
            };
            Button create = CreateFunctionalPanelButton("新建用户", Blue);
            create.Click += async (sender, args) => await CreateUserAsync();
            buttons.Controls.Add(create);
            Button editPermissions = CreateFunctionalPanelButton("编辑权限", Purple);
            editPermissions.Click += async (sender, args) => await EditSelectedPermissionsAsync();
            buttons.Controls.Add(editPermissions);
            Button resetPassword = CreateFunctionalPanelButton("重置密码", Orange);
            resetPassword.Click += async (sender, args) => await ResetSelectedPasswordAsync();
            buttons.Controls.Add(resetPassword);
            Button delete = CreateFunctionalPanelButton("删除用户", Color.FromArgb(184, 62, 62));
            delete.Click += async (sender, args) => await DeleteSelectedUserAsync();
            buttons.Controls.Add(delete);
            section.Controls.Add(buttons);
            page.Controls.Add(section);
            return page;
        }

        private TabPage CreateMigrationTab()
        {
            TabPage page = CreateTab("备份与迁移");
            Panel source = CreateSurfacePanel(180);
            source.Controls.Add(CreateHeading("远程数据库 → 本地数据库", 16, 14));
            source.Controls.Add(CreateValue("源数据库", 18, 50, MutedColor));
            source.Controls.Add(CreateValue("服务器上的 " + databaseTitle, 18, 76));
            source.Controls.Add(CreateValue("本地目标", 280, 50, MutedColor));
            source.Controls.Add(CreateValue("等待选择本机数据库", 280, 76, Orange));
            source.Controls.Add(CreateValue("传输方式", 560, 50, MutedColor));
            source.Controls.Add(CreateValue("SSH / SFTP", 560, 76, Blue));
            source.Controls.Add(CreateValue("数据库选择", 18, 112, MutedColor));
            source.Controls.Add(CreateValue("等待检测数据库列表", 18, 138, Orange));
            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Location = new Point(560, 118),
                Size = new Size(410, 42),
                WrapContents = false
            };
            Button choose = CreateFunctionalPanelButton("选择数据库", Blue);
            choose.Click += async (sender, args) => await BackupToLocalAsync();
            buttons.Controls.Add(choose);
            Button start = CreateFunctionalPanelButton("开始迁移", Green);
            start.Click += async (sender, args) => await MigrateToLocalOrBackupAsync();
            buttons.Controls.Add(start);
            Button restore = CreateFunctionalPanelButton("恢复备份", Orange);
            restore.Click += async (sender, args) => await RestoreBackupAsync();
            buttons.Controls.Add(restore);
            source.Controls.Add(buttons);
            page.Controls.Add(source);
            Panel progress = CreateSurfacePanel(120);
            progress.Controls.Add(CreateHeading("任务进度", 16, 14));
            ProgressBar bar = new ProgressBar { Location = new Point(18, 54), Size = new Size(760, 22), Style = ProgressBarStyle.Continuous, Value = 0 };
            progress.Controls.Add(bar);
            progress.Controls.Add(CreateValue("等待开始", 18, 82, MutedColor));
            page.Controls.Add(progress);
            return page;
        }

        private TabPage CreateConnectionTab()
        {
            TabPage page = CreateTab("连接设置");
            Panel section = CreateSurfacePanel(240);
            section.Controls.Add(CreateHeading("数据库凭据", 16, 14));
            section.Controls.Add(CreateValue("首次使用时验证并保存到保险库；后续操作通过 SSH 隧道连接服务器本地数据库。", 18, 44, MutedColor));
            section.Controls.Add(CreateValue("管理用户名", 18, 86, MutedColor));
            TextBox user = new TextBox { Location = new Point(18, 110), Width = 260, Text = credential == null ? "root" : credential.Username, ReadOnly = true };
            section.Controls.Add(user);
            section.Controls.Add(CreateValue("数据库地址", 320, 86, MutedColor));
            TextBox address = new TextBox { Location = new Point(320, 110), Width = 260, Text = "127.0.0.1:" + Item.Port, ReadOnly = true };
            section.Controls.Add(address);
            Button verify = CreateFunctionalPanelButton(credential == null ? "验证并保存凭据" : "重新验证凭据", Blue, 140);
            verify.Click += async (sender, args) => await TestCredentialAsync();
            verify.Location = new Point(18, 164);
            section.Controls.Add(verify);
            Button clear = CreateFunctionalPanelButton("清除已保存凭据", Color.FromArgb(184, 62, 62), 140);
            clear.Click += (sender, args) => ClearCredential();
            clear.Location = new Point(162, 164);
            section.Controls.Add(clear);
            page.Controls.Add(section);
            return page;
        }

        private async Task BackupToLocalAsync()
        {
            if (credential == null || !credential.IsVerified)
            {
                MessageBox.Show("请先在“连接设置”中验证数据库管理账号。", "需要先验证凭据", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            IList<string> databases;
            Cursor previousCursor = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                databases = (await databaseService.ListDatabasesAsync(Server, serverPassword, credential, CancellationToken.None))
                    .Where(IsApplicationDatabase)
                    .ToList();
            }
            catch (Exception ex)
            {
                MessageBox.Show("读取数据库列表失败：" + ex.Message, "备份准备失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            finally
            {
                Cursor = previousCursor;
            }

            using (MySqlBackupOptionsForm optionsForm = new MySqlBackupOptionsForm(databaseTitle, databases))
            {
                if (optionsForm.ShowDialog(this) != DialogResult.OK)
                    return;
                using (SaveFileDialog dialog = new SaveFileDialog
                {
                    Title = "选择备份保存位置",
                    Filter = "压缩备份 (*.sql.gz)|*.sql.gz|SQL 文件 (*.sql)|*.sql",
                    DefaultExt = "sql.gz",
                    AddExtension = true,
                    FileName = databaseTitle + "_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".sql.gz",
                    OverwritePrompt = true
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK)
                        return;
                    string outputPath = dialog.FileName;
                    MySqlBackupOptions selected = optionsForm.Options;
                    using (OperationProgressForm progress = new OperationProgressForm(
                        "备份数据库",
                        databaseTitle + " · 远程导出到本地",
                        new[] { "检查数据库凭据", "远程导出数据", "SSH 下载备份", "压缩并保存", "清理临时资源" }))
                    {
                        progress.Operation = async (window, token) =>
                        {
                            window.SetStep(0, OperationStepState.Completed, "凭据已验证");
                            window.SetStep(1, OperationStepState.Running);
                            window.SetProgress("正在远程导出", string.Join("、", selected.DatabaseNames), 12, Purple, true);
                            MySqlBackupRequest request = new MySqlBackupRequest
                            {
                                DatabaseNames = selected.DatabaseNames,
                                OutputPath = outputPath,
                                IncludeRoutines = selected.IncludeRoutines,
                                IncludeEvents = selected.IncludeEvents,
                                IncludeTriggers = selected.IncludeTriggers
                            };
                            MySqlBackupResult result = await backupService.ExportAsync(
                                Server,
                                serverPassword,
                                credential,
                                request,
                                bytes => window.SetProgress("正在下载备份", FormatBytes(bytes), 48, Purple, true),
                                token);
                            window.SetStep(1, OperationStepState.Completed, "导出完成");
                            window.SetStep(2, OperationStepState.Completed, FormatBytes(result.BytesWritten));
                            window.SetStep(3, OperationStepState.Completed, Path.GetFileName(result.OutputPath));
                            window.SetStep(4, OperationStepState.Completed, "已清理");
                            window.MarkSuccess("备份已保存到本地：" + result.OutputPath);
                        };
                        progress.ShowDialog(this);
                    }
                }
            }
        }

        private async Task MigrateToLocalOrBackupAsync()
        {
            if (credential == null || !credential.IsVerified)
            {
                MessageBox.Show("请先在“连接设置”中验证数据库管理账号。", "需要先验证凭据", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            LocalDatabaseTarget detected = LocalDatabaseTools.Detect(databaseTitle);
            bool localAvailable = LocalDatabaseTools.HasUsableLocalTarget(detected);
            using (DatabaseMigrationDecisionForm decisionForm = new DatabaseMigrationDecisionForm(databaseTitle, localAvailable))
            {
                if (decisionForm.ShowDialog(this) != DialogResult.OK)
                    return;
                switch (decisionForm.Decision)
                {
                    case DatabaseMigrationDecision.ExportBackup:
                        await BackupToLocalAsync();
                        return;
                    case DatabaseMigrationDecision.ConfigureLocalTarget:
                        await MigrateToLocalAsync();
                        return;
                    case DatabaseMigrationDecision.InstallLocalDatabase:
                        using (LocalDatabaseInstallForm installer = new LocalDatabaseInstallForm(databaseTitle))
                        {
                            installer.ShowDialog(this);
                            if (installer.InstallerStarted)
                                MessageBox.Show("安装程序已启动。安装完成后请重新打开数据库管理，再次检测本机目标。", "等待安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        return;
                    case DatabaseMigrationDecision.OtherTarget:
                        MessageBox.Show("其他目标服务器迁移将在后续版本接入。当前可以先选择“仅导出备份”。", "暂未接入", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                }
            }
        }

        private async Task MigrateToLocalAsync()
        {
            using (LocalMySqlTargetForm targetForm = new LocalMySqlTargetForm(databaseTitle))
            {
                if (targetForm.ShowDialog(this) != DialogResult.OK || targetForm.Target == null)
                    return;
                IList<string> databases;
                try
                {
                    databases = (await databaseService.ListDatabasesAsync(Server, serverPassword, credential, CancellationToken.None))
                        .Where(IsApplicationDatabase)
                        .ToList();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("读取远程数据库列表失败：" + ex.Message, "迁移准备失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                using (MySqlBackupOptionsForm optionsForm = new MySqlBackupOptionsForm(databaseTitle, databases, true))
                {
                    if (optionsForm.ShowDialog(this) != DialogResult.OK)
                        return;
                    string temporarySql = Path.Combine(Path.GetTempPath(), "xiaobai-migrate-" + Guid.NewGuid().ToString("N") + ".sql");
                    try
                    {
                        MySqlBackupOptions selected = optionsForm.Options;
                        using (OperationProgressForm progress = new OperationProgressForm(
                            "迁移数据库",
                            databaseTitle + " · 远程数据库 → 本机数据库",
                            new[] { "远程导出", "传输到本机", "导入本机数据库", "校验完成", "清理临时文件" }))
                        {
                            progress.Operation = async (window, token) =>
                            {
                                window.SetStep(0, OperationStepState.Running);
                                window.SetProgress("正在远程导出", string.Join("、", selected.DatabaseNames), 12, Purple, true);
                                MySqlBackupResult backupResult = await backupService.ExportAsync(
                                    Server,
                                    serverPassword,
                                    credential,
                                new MySqlBackupRequest
                                {
                                    DatabaseNames = selected.DatabaseNames,
                                    OutputPath = temporarySql,
                                    IncludeRoutines = selected.IncludeRoutines,
                                    IncludeEvents = selected.IncludeEvents,
                                    IncludeTriggers = selected.IncludeTriggers,
                                    OverwriteExistingTables = selected.OverwriteExistingTables
                                },
                                    bytes => window.SetProgress("正在接收备份", FormatBytes(bytes), 42, Purple, true),
                                    token);
                                window.SetStep(0, OperationStepState.Completed, "导出完成");
                                window.SetStep(1, OperationStepState.Completed, FormatBytes(backupResult.BytesWritten));
                                window.SetStep(2, OperationStepState.Running);
                                MySqlBackupService.ValidateBackupFile(temporarySql);
                                foreach (string databaseName in selected.DatabaseNames)
                                    if (!MySqlBackupService.ContainsDatabaseDump(temporarySql, databaseName))
                                        throw new InvalidOperationException("备份内容中未找到数据库：" + databaseName);
                                await LocalDatabaseTools.ImportSqlAsync(
                                    targetForm.Target,
                                    temporarySql,
                                    (copied, total) =>
                                    {
                                        int percent = total <= 0 ? 70 : 50 + (int)Math.Min(38, copied * 38L / total);
                                        window.SetProgress("正在导入本机数据库", FormatBytes(copied) + " / " + FormatBytes(total), percent, Green, false);
                                    },
                                    token);
                                window.SetStep(2, OperationStepState.Completed, "导入完成");
                                window.SetStep(3, OperationStepState.Completed, "本机客户端已返回成功");
                                window.SetStep(4, OperationStepState.Completed, "已清理");
                                window.MarkSuccess("数据库迁移完成");
                            };
                            progress.ShowDialog(this);
                        }
                    }
                    finally
                    {
                        try { if (File.Exists(temporarySql)) File.Delete(temporarySql); } catch { }
                    }
                }
            }
        }

        private async Task RestoreBackupAsync()
        {
            using (OpenFileDialog fileDialog = new OpenFileDialog
            {
                Title = "选择 MySQL / MariaDB 备份文件",
                Filter = "数据库备份 (*.sql;*.sql.gz)|*.sql;*.sql.gz|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            })
            {
                if (fileDialog.ShowDialog(this) != DialogResult.OK)
                    return;
                try
                {
                    MySqlBackupService.ValidateBackupFile(fileDialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("备份文件校验失败：" + ex.Message, "无法恢复", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                LocalDatabaseTarget detected = LocalDatabaseTools.Detect(databaseTitle);
                if (!LocalDatabaseTools.HasUsableLocalTarget(detected))
                {
                    using (DatabaseMigrationDecisionForm decision = new DatabaseMigrationDecisionForm(databaseTitle, false))
                    {
                        if (decision.ShowDialog(this) == DialogResult.OK && decision.Decision == DatabaseMigrationDecision.InstallLocalDatabase)
                        {
                            using (LocalDatabaseInstallForm installer = new LocalDatabaseInstallForm(databaseTitle))
                                installer.ShowDialog(this);
                        }
                    }
                    return;
                }

                using (LocalMySqlTargetForm targetForm = new LocalMySqlTargetForm(databaseTitle))
                {
                    if (targetForm.ShowDialog(this) != DialogResult.OK || targetForm.Target == null)
                        return;
                    if (MessageBox.Show(
                        "导入备份可能创建数据库、创建表，并可能根据备份内容删除或覆盖本机同名表。\n\n请确认已备份本机现有数据。确定继续恢复吗？",
                        "恢复备份确认",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning) != DialogResult.Yes)
                        return;

                    using (OperationProgressForm progress = new OperationProgressForm(
                        "恢复数据库备份",
                        Path.GetFileName(fileDialog.FileName) + " → 本机 " + databaseTitle,
                        new[] { "校验备份文件", "连接本机数据库", "导入数据库", "确认客户端结果", "清理临时文件" }))
                    {
                        progress.Operation = async (window, token) =>
                        {
                            window.SetStep(0, OperationStepState.Completed, FormatBytes(new FileInfo(fileDialog.FileName).Length));
                            window.SetStep(1, OperationStepState.Completed, targetForm.Target.Host + ":" + targetForm.Target.Port);
                            window.SetStep(2, OperationStepState.Running);
                            await LocalDatabaseTools.ImportSqlAsync(
                                targetForm.Target,
                                fileDialog.FileName,
                                (copied, total) =>
                                {
                                    int percent = total <= 0 ? 60 : 20 + (int)Math.Min(70, copied * 70L / total);
                                    window.SetProgress("正在恢复数据库", FormatBytes(copied) + " / " + FormatBytes(total), percent, Green, false);
                                },
                                token);
                            window.SetStep(2, OperationStepState.Completed, "导入完成");
                            window.SetStep(3, OperationStepState.Completed, "本机客户端返回成功");
                            window.SetStep(4, OperationStepState.Completed, "已清理");
                            window.MarkSuccess("备份恢复完成");
                        };
                        progress.ShowDialog(this);
                    }
                }
            }
        }

        private static bool IsApplicationDatabase(string name)
        {
            return !string.Equals(name, "information_schema", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "performance_schema", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "mysql", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "sys", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
                return bytes + " B";
            if (bytes < 1024 * 1024)
                return (bytes / 1024D).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024L * 1024L)
                return (bytes / (1024D * 1024D)).ToString("0.0") + " MB";
            return (bytes / (1024D * 1024D * 1024D)).ToString("0.0") + " GB";
        }

        private DatabaseCredentialRecord FindCredential()
        {
            if (Server == null || Server.DatabaseCredentials == null)
                return null;
            return Server.DatabaseCredentials.FirstOrDefault(item =>
                string.Equals(item.DatabaseType, Item.Type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ServiceName, Item.ServiceName, StringComparison.OrdinalIgnoreCase) &&
                item.Port == Item.Port);
        }

        private string GetCredentialDisplayText()
        {
            return credential != null && credential.IsVerified ? "凭据已验证" : "未录入凭据";
        }

        private string GetManagementAccountText()
        {
            return credential == null || string.IsNullOrWhiteSpace(credential.Username) ? "未配置" : credential.Username;
        }

        private string GetAuthenticationScopeText()
        {
            return credential == null || string.IsNullOrWhiteSpace(credential.DatabaseName) ? "未指定" : credential.DatabaseName;
        }

        private string GetVersionDisplayText()
        {
            return string.IsNullOrWhiteSpace(Item.Version) || Item.Version.IndexOf('x') >= 0 ? "点击查询" : Item.Version;
        }

        private Color GetCredentialColor()
        {
            return credential != null && credential.IsVerified ? Green : Orange;
        }

        private void RefreshOverviewLabels()
        {
            if (overviewStatusLabel != null)
            {
                overviewStatusLabel.Text = Item.Status;
                overviewStatusLabel.ForeColor = Item.GetStatusColor();
            }
            if (overviewCredentialLabel != null)
            {
                overviewCredentialLabel.Text = GetCredentialDisplayText();
                overviewCredentialLabel.ForeColor = GetCredentialColor();
            }
            if (overviewAccountLabel != null)
                overviewAccountLabel.Text = GetManagementAccountText();
            if (overviewScopeLabel != null)
                overviewScopeLabel.Text = GetAuthenticationScopeText();
            if (overviewVersionLabel != null)
                overviewVersionLabel.Text = GetVersionDisplayText();
            RefreshShellStatus();
            if (usersNoteLabel != null && (credential == null || !credential.IsVerified))
            {
                usersNoteLabel.Text = "请先在“连接设置”中验证数据库管理账号。";
                usersNoteLabel.ForeColor = Orange;
            }
        }

        private async Task TestCredentialAsync()
        {
            if (Server == null || string.IsNullOrWhiteSpace(serverPassword))
            {
                MessageBox.Show("当前没有可用的服务器管理凭据，无法建立 SSH 隧道。", "无法连接", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using (DatabaseCredentialForm form = new DatabaseCredentialForm(databaseTitle, credential))
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                    return;

                DatabaseCredentialRecord candidate = new DatabaseCredentialRecord
                {
                    Id = credential == null ? Guid.NewGuid().ToString("N") : credential.Id,
                    DatabaseType = Item.Type,
                    ServiceName = Item.ServiceName,
                    Host = "127.0.0.1",
                    Port = Item.Port,
                    Username = form.Username,
                    Password = form.Password,
                    DatabaseName = form.DatabaseName,
                    AuthenticationDatabase = "",
                    Users = credential == null || credential.Users == null
                        ? new List<DatabaseUserRecord>()
                        : credential.Users,
                    IsVerified = false
                };
                Cursor previousCursor = Cursor;
                Cursor = Cursors.WaitCursor;
                try
                {
                    MySqlConnectionTestResult result = await databaseService.TestConnectionAsync(
                        Server, serverPassword, candidate, CancellationToken.None);
                    candidate.IsVerified = true;
                    candidate.LastVerifiedAt = DateTime.Now;
                    int oldIndex = credential == null ? -1 : Server.DatabaseCredentials.IndexOf(credential);
                    DatabaseCredentialRecord oldCredential = credential;
                    if (oldIndex >= 0)
                        Server.DatabaseCredentials[oldIndex] = candidate;
                    else
                        Server.DatabaseCredentials.Add(candidate);
                    credential = candidate;
                    if (persistChanges != null && !persistChanges())
                    {
                        if (oldIndex >= 0)
                            Server.DatabaseCredentials[oldIndex] = oldCredential;
                        else
                            Server.DatabaseCredentials.Remove(candidate);
                        credential = oldCredential;
                        throw new InvalidOperationException("保险库存储失败，凭据没有保存");
                    }

                    if (Item.StatusKind != DatabaseStatusKind.Error)
                        Item.StatusKind = DatabaseStatusKind.Normal;
                    Item.Version = result.ServerVersion;
                    Item.CredentialState = "凭据已验证";
                    credentialChanged?.Invoke();
                    RefreshOverviewLabels();
                    await RefreshUsersAsync();
                    MessageBox.Show("数据库连接验证成功，凭据已保存到保险库。\n\n服务版本：" + result.ServerVersion, "验证成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("数据库连接验证失败：" + ex.Message, "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    Cursor = previousCursor;
                }
            }
        }

        private async Task ShowVersionAsync(Button button)
        {
            if (credential == null || !credential.IsVerified)
            {
                MessageBox.Show("请先验证并保存数据库管理凭据。", "需要先验证凭据", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (Server == null || string.IsNullOrWhiteSpace(serverPassword))
            {
                MessageBox.Show("当前没有可用的服务器管理凭据，无法建立 SSH 隧道。", "无法查询版本", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Cursor previousCursor = Cursor;
            string previousText = button.Text;
            button.Enabled = false;
            button.Text = "查询中...";
            Cursor = Cursors.WaitCursor;
            try
            {
                MySqlConnectionTestResult result = await databaseService.TestConnectionAsync(
                    Server, serverPassword, credential, CancellationToken.None);
                Item.Version = result.ServerVersion;
                RefreshOverviewLabels();
                MessageBox.Show(
                    databaseTitle + " 版本：" + result.ServerVersion +
                    "\n认证账号：" + result.UserName +
                    "\n远程端口：" + Item.Port +
                    "\n连接方式：SSH 安全隧道",
                    databaseTitle + " 版本信息",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("查询 " + databaseTitle + " 版本失败：" + ex.Message, "查询失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = previousCursor;
                button.Text = previousText;
                button.Enabled = true;
            }
        }

        private async Task RefreshUsersAsync()
        {
            if (usersGrid == null)
                return;
            usersGrid.Rows.Clear();
            if (credential == null || !credential.IsVerified)
            {
                RefreshOverviewLabels();
                return;
            }

            Cursor previousCursor = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                IList<MySqlUserInfo> users = await databaseService.ListUsersAsync(
                    Server, serverPassword, credential, CancellationToken.None);
                foreach (MySqlUserInfo user in users)
                {
                    bool saved = credential.Users != null && credential.Users.Any(item =>
                        string.Equals(item.Username, user.Username, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.HostPattern, user.HostPattern, StringComparison.OrdinalIgnoreCase));
                    string summary = string.IsNullOrWhiteSpace(user.PermissionSummary) ? "无显式授权" : user.PermissionSummary;
                    usersGrid.Rows.Add(user.Username, user.HostPattern, summary, "已发现", saved ? "已保存" : "未保存");
                }
                usersNoteLabel.Text = "已从远程数据库读取 " + users.Count + " 个用户；密码不会显示。";
                usersNoteLabel.ForeColor = MutedColor;
            }
            catch (Exception ex)
            {
                usersNoteLabel.Text = "读取用户失败：" + ex.Message;
                usersNoteLabel.ForeColor = Color.FromArgb(184, 62, 62);
            }
            finally
            {
                Cursor = previousCursor;
            }
        }

        private async Task CreateUserAsync()
        {
            if (credential == null || !credential.IsVerified)
            {
                MessageBox.Show("请先验证数据库管理账号，再创建新用户。", "需要先验证凭据", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (MySqlUserForm form = new MySqlUserForm())
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                    return;
                MySqlUserRequest request = form.Request;
                if (request.Permissions.AllPrivileges || request.Permissions.GrantOption)
                {
                    if (MessageBox.Show("当前用户申请了高风险权限，可能获得全库管理或继续授权能力。\n\n确定继续吗？", "高风险权限确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                        return;
                }

                bool created = false;
                DatabaseUserRecord savedUser = null;
                Cursor previousCursor = Cursor;
                Cursor = Cursors.WaitCursor;
                try
                {
                    await databaseService.CreateUserAsync(Server, serverPassword, credential, request, CancellationToken.None);
                    created = true;
                    DatabaseCredentialRecord newUserCredential = new DatabaseCredentialRecord
                    {
                        DatabaseType = Item.Type,
                        ServiceName = Item.ServiceName,
                        Host = credential.Host,
                        Port = credential.Port,
                        Username = request.Username,
                        Password = request.Password,
                        DatabaseName = request.Permissions.DatabaseName,
                        IsVerified = true,
                        LastVerifiedAt = DateTime.Now
                    };
                    await databaseService.TestConnectionAsync(Server, serverPassword, newUserCredential, CancellationToken.None);
                    savedUser = new DatabaseUserRecord
                    {
                        Username = request.Username,
                        Password = request.Password,
                        HostPattern = request.HostPattern,
                        DatabaseName = request.Permissions.DatabaseName,
                        PermissionSummary = BuildPermissionSummary(request.Permissions),
                        CreatedAt = DateTime.Now,
                        LastVerifiedAt = DateTime.Now,
                        IsVerified = true
                    };
                    if (credential.Users == null)
                        credential.Users = new List<DatabaseUserRecord>();
                    credential.Users.RemoveAll(item =>
                        string.Equals(item.Username, savedUser.Username, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.HostPattern, savedUser.HostPattern, StringComparison.OrdinalIgnoreCase));
                    credential.Users.Add(savedUser);
                    if (persistChanges != null && !persistChanges())
                        throw new InvalidOperationException("保险库存储失败，新用户没有记录");
                    created = false;
                    await RefreshUsersAsync();
                    MessageBox.Show("新用户创建成功，并已通过登录验证。用户名和密码已保存到保险库。", "创建成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    if (savedUser != null && credential.Users != null)
                        credential.Users.Remove(savedUser);
                    if (created)
                    {
                        try { await databaseService.DropUserAsync(Server, serverPassword, credential, request.Username, request.HostPattern, CancellationToken.None); }
                        catch { }
                    }
                    MessageBox.Show("创建用户失败：" + ex.Message, "创建失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    Cursor = previousCursor;
                }
            }
        }

        private async Task DeleteSelectedUserAsync()
        {
            if (credential == null || !credential.IsVerified || usersGrid == null || usersGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show("请先验证数据库凭据并选择要删除的用户。", "无法删除", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DataGridViewRow row = usersGrid.SelectedRows[0];
            string username = Convert.ToString(row.Cells[0].Value);
            string hostPattern = Convert.ToString(row.Cells[1].Value);
            if (IsProtectedMySqlUser(username))
            {
                MessageBox.Show("系统内置账号不能删除。", "操作被阻止", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.Equals(username, credential.Username, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("不能删除当前用于管理数据库的账号。", "操作被阻止", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show("确定删除用户 “" + username + "@" + hostPattern + "” 吗？此操作会立即影响远程数据库。", "确认删除用户", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            try
            {
                await databaseService.DropUserAsync(Server, serverPassword, credential, username, hostPattern, CancellationToken.None);
                if (credential.Users != null)
                    credential.Users.RemoveAll(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase) && string.Equals(item.HostPattern, hostPattern, StringComparison.OrdinalIgnoreCase));
                if (persistChanges != null && !persistChanges())
                    throw new InvalidOperationException("远程用户已删除，但保险库更新失败");
                await RefreshUsersAsync();
                MessageBox.Show("用户已删除。", "操作完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("删除用户失败：" + ex.Message, "删除失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task EditSelectedPermissionsAsync()
        {
            if (credential == null || !credential.IsVerified || usersGrid == null || usersGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show("请先验证数据库凭据并选择要编辑的用户。", "无法编辑", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DataGridViewRow row = usersGrid.SelectedRows[0];
            string username = Convert.ToString(row.Cells[0].Value);
            string hostPattern = Convert.ToString(row.Cells[1].Value);
            if (IsProtectedMySqlUser(username))
            {
                MessageBox.Show("系统内置账号不能修改权限。", "操作被阻止", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.Equals(username, credential.Username, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("当前管理账号受到保护，不能在这里修改权限，避免管理器失去数据库管理能力。", "操作被阻止", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Cursor previousCursor = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                IList<MySqlGrantScope> scopes = await databaseService.ListGrantScopesAsync(
                    Server, serverPassword, credential, username, hostPattern, CancellationToken.None);
                using (MySqlPermissionForm form = new MySqlPermissionForm(username, hostPattern, scopes))
                {
                    Cursor = previousCursor;
                    if (form.ShowDialog(this) != DialogResult.OK)
                        return;
                    Cursor = Cursors.WaitCursor;
                    MySqlGrantScope requested = form.Result;
                    if (requested.AllPrivileges || requested.GrantOption)
                    {
                        if (MessageBox.Show("当前权限包含全库授权或继续授权能力，属于高风险操作。\n\n确定继续吗？", "高风险权限确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                            return;
                    }
                    await databaseService.UpdatePermissionsAsync(
                        Server, serverPassword, credential, username, hostPattern, requested, CancellationToken.None);
                    IList<MySqlGrantScope> verifiedScopes = await databaseService.ListGrantScopesAsync(
                        Server, serverPassword, credential, username, hostPattern, CancellationToken.None);
                    MySqlGrantScope verified = verifiedScopes.FirstOrDefault(item =>
                        string.Equals(item.DatabaseName, requested.DatabaseName, StringComparison.OrdinalIgnoreCase));
                    if (verified == null && (requested.AllPrivileges || requested.Select || requested.Insert || requested.Update || requested.Delete || requested.Create || requested.Alter || requested.Execute || requested.GrantOption))
                        throw new InvalidOperationException("权限修改后未读取到目标授权，已停止保存本地记录");

                    DatabaseUserRecord savedUser = FindSavedUser(username, hostPattern);
                    if (savedUser != null)
                    {
                        savedUser.PermissionSummary = BuildPermissionSummary(requested);
                        savedUser.LastVerifiedAt = DateTime.Now;
                    }
                    if (persistChanges != null && !persistChanges())
                        throw new InvalidOperationException("远程权限已修改，但保险库更新失败");
                    await RefreshUsersAsync();
                    MessageBox.Show("用户权限已修改，并已重新读取授权结果。", "权限修改成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("编辑权限失败：" + ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = previousCursor;
            }
        }

        private async Task ResetSelectedPasswordAsync()
        {
            if (credential == null || !credential.IsVerified || usersGrid == null || usersGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show("请先验证数据库凭据并选择要重置密码的用户。", "无法重置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DataGridViewRow row = usersGrid.SelectedRows[0];
            string username = Convert.ToString(row.Cells[0].Value);
            string hostPattern = Convert.ToString(row.Cells[1].Value);
            if (IsProtectedMySqlUser(username))
            {
                MessageBox.Show("系统内置账号不能重置密码。", "操作被阻止", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.Equals(username, credential.Username, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("当前管理账号受到保护，不能在这里重置密码。请在数据库自身管理工具中修改后，再回到连接设置重新验证。", "操作被阻止", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using (MySqlResetPasswordForm form = new MySqlResetPasswordForm(username, hostPattern))
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                    return;
                if (MessageBox.Show("重置后远程数据库密码会立即生效，旧密码将失效。\n\n确定继续吗？", "确认重置密码", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;

                DatabaseUserRecord savedUser = FindSavedUser(username, hostPattern);
                string oldPassword = savedUser == null ? null : savedUser.Password;
                bool remotePasswordChanged = false;
                Cursor previousCursor = Cursor;
                Cursor = Cursors.WaitCursor;
                try
                {
                    await databaseService.ResetUserPasswordAsync(
                        Server, serverPassword, credential, username, hostPattern, form.NewPassword, CancellationToken.None);
                    remotePasswordChanged = true;
                    DatabaseCredentialRecord candidate = new DatabaseCredentialRecord
                    {
                        DatabaseType = Item.Type,
                        ServiceName = Item.ServiceName,
                        Host = credential.Host,
                        Port = credential.Port,
                        Username = username,
                        Password = form.NewPassword,
                        DatabaseName = savedUser == null ? "" : savedUser.DatabaseName,
                        IsVerified = true,
                        LastVerifiedAt = DateTime.Now
                    };
                    await databaseService.TestConnectionAsync(Server, serverPassword, candidate, CancellationToken.None);
                    if (savedUser == null)
                    {
                        if (credential.Users == null)
                            credential.Users = new List<DatabaseUserRecord>();
                        savedUser = new DatabaseUserRecord
                        {
                            Username = username,
                            HostPattern = hostPattern,
                            Password = form.NewPassword,
                            DatabaseName = candidate.DatabaseName,
                            PermissionSummary = Convert.ToString(row.Cells[2].Value),
                            CreatedAt = DateTime.Now,
                            IsVerified = true
                        };
                        credential.Users.Add(savedUser);
                    }
                    else
                    {
                        savedUser.Password = form.NewPassword;
                        savedUser.LastVerifiedAt = DateTime.Now;
                        savedUser.IsVerified = true;
                    }
                    if (persistChanges != null && !persistChanges())
                        throw new InvalidOperationException("远程密码已修改，但保险库更新失败");
                    await RefreshUsersAsync();
                    MessageBox.Show("用户密码已重置，新密码已通过登录验证并保存到保险库。", "密码重置成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    bool restored = false;
                    if (savedUser != null && oldPassword != null)
                    {
                        try
                        {
                            await databaseService.ResetUserPasswordAsync(
                                Server, serverPassword, credential, username, hostPattern, oldPassword, CancellationToken.None);
                            savedUser.Password = oldPassword;
                            restored = true;
                        }
                        catch { }
                    }
                    string prefix = remotePasswordChanged && !restored
                        ? "远程密码可能已经修改，但新密码验证或保险库存储失败，且无法自动恢复旧密码。请立即通过其他管理方式确认该账号状态。\n\n"
                        : "重置密码失败：";
                    MessageBox.Show(prefix + ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    Cursor = previousCursor;
                }
            }
        }

        private DatabaseUserRecord FindSavedUser(string username, string hostPattern)
        {
            return credential == null || credential.Users == null ? null : credential.Users.FirstOrDefault(item =>
                string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.HostPattern, hostPattern, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsProtectedMySqlUser(string username)
        {
            return !string.IsNullOrWhiteSpace(username) &&
                username.StartsWith("mysql.", StringComparison.OrdinalIgnoreCase);
        }

        private void ClearCredential()
        {
            if (credential == null)
            {
                MessageBox.Show("当前没有已保存的数据库凭据。", "无需清除", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show("确定清除当前数据库凭据吗？远程数据库用户不会被删除，但后续管理需要重新输入管理账号密码。", "确认清除凭据", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            DatabaseCredentialRecord oldCredential = credential;
            Server.DatabaseCredentials.Remove(oldCredential);
            credential = null;
            if (persistChanges != null && !persistChanges())
            {
                Server.DatabaseCredentials.Add(oldCredential);
                credential = oldCredential;
                return;
            }
            if (Item.StatusKind == DatabaseStatusKind.Normal)
                Item.StatusKind = DatabaseStatusKind.NeedsConfiguration;
            Item.CredentialState = "未录入凭据";
            credentialChanged?.Invoke();
            RefreshOverviewLabels();
            usersGrid?.Rows.Clear();
        }

        private static string BuildPermissionSummary(MySqlPermissionSelection permission)
        {
            if (permission == null)
                return "无显式授权";
            if (permission.AllPrivileges)
                return "全部权限";
            List<string> values = new List<string>();
            if (permission.Select) values.Add("读取");
            if (permission.Insert) values.Add("新增");
            if (permission.Update) values.Add("修改");
            if (permission.Delete) values.Add("删除");
            if (permission.Create) values.Add("建表");
            if (permission.Alter) values.Add("改表");
            if (permission.Execute) values.Add("执行");
            if (permission.GrantOption) values.Add("继续授权");
            return values.Count == 0 ? "无显式授权" : string.Join("、", values);
        }

        private static string BuildPermissionSummary(MySqlGrantScope permission)
        {
            if (permission == null)
                return "无显式授权";
            if (permission.AllPrivileges)
                return "全部权限";
            List<string> values = new List<string>();
            if (permission.Select) values.Add("读取");
            if (permission.Insert) values.Add("新增");
            if (permission.Update) values.Add("修改");
            if (permission.Delete) values.Add("删除");
            if (permission.Create) values.Add("建表");
            if (permission.Alter) values.Add("改表");
            if (permission.Execute) values.Add("执行");
            if (permission.GrantOption) values.Add("继续授权");
            return values.Count == 0 ? "无显式授权" : string.Join("、", values);
        }
    }

    public sealed class MongoDbDatabasePanel : DatabaseDetailPanel
    {
        private readonly string serverPassword;
        private readonly Func<bool> persistChanges;
        private readonly Action credentialChanged;
        private readonly MongoDatabaseService databaseService = new MongoDatabaseService();
        private readonly MongoBackupService backupService = new MongoBackupService();
        private DatabaseCredentialRecord credential;
        private DataGridView usersGrid;
        private Label usersNoteLabel;
        private Label overviewStatusLabel;
        private Label overviewCredentialLabel;
        private Label overviewAccountLabel;
        private Label overviewScopeLabel;
        private Label overviewVersionLabel;

        public MongoDbDatabasePanel(Server server, DatabaseServiceItem item)
            : this(server, item, "", null, null)
        {
        }

        public MongoDbDatabasePanel(
            Server server,
            DatabaseServiceItem item,
            string serverPassword,
            Func<bool> persistChanges,
            Action credentialChanged)
            : base(server, item)
        {
            this.serverPassword = serverPassword ?? "";
            this.persistChanges = persistChanges;
            this.credentialChanged = credentialChanged;
            credential = FindCredential();
            BuildShell("MongoDB 管理", "MongoDB 使用认证数据库和角色权限模型", Green);
            Tabs.TabPages.Add(CreateOverviewTab());
            Tabs.TabPages.Add(CreateUsersTab());
            Tabs.TabPages.Add(CreateMigrationTab());
            Tabs.TabPages.Add(CreateConnectionTab());
            Tabs.Selected += async (sender, args) =>
            {
                if (args.TabPage != null && args.TabPage.Text == "用户与角色")
                    await RefreshUsersAsync();
            };
        }

        private TabPage CreateOverviewTab()
        {
            TabPage page = CreateTab("数据库概览");
            Panel overview = CreateConnectionOverviewPanel(
                "MongoDB 连接概览",
                GetManagementAccountText(),
                "认证数据库",
                GetAuthenticationScopeText(),
                GetCredentialDisplayText(),
                GetCredentialColor(),
                async (sender, args) => await TestCredentialAsync(),
                async (sender, args) => await ShowVersionAsync((Button)sender),
                out overviewAccountLabel,
                out overviewScopeLabel,
                out overviewStatusLabel,
                out overviewCredentialLabel,
                out overviewVersionLabel);
            page.Controls.Add(overview);
            return page;
        }

        private TabPage CreateUsersTab()
        {
            TabPage page = CreateTab("用户与角色");
            Panel panel = CreateSurfacePanel(430);
            panel.Controls.Add(CreateHeading("MongoDB 用户", 16, 14));
            usersNoteLabel = new Label { AutoEllipsis = true, Size = new Size(760, 24), Text = "创建用户时选择认证数据库和角色，不直接套用 MySQL 权限。", ForeColor = MutedColor, Location = new Point(18, 43) };
            panel.Controls.Add(usersNoteLabel);
            usersGrid = CreateGrid("用户名", "认证数据库", "角色", "状态", "凭据");
            usersGrid.Location = new Point(16, 76);
            usersGrid.Size = new Size(760, 220);
            usersGrid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            panel.Controls.Add(usersGrid);
            FlowLayoutPanel buttons = new FlowLayoutPanel { Location = new Point(16, 310), Size = new Size(600, 42), WrapContents = false };
            Button create = CreateFunctionalPanelButton("新建用户", Blue);
            create.Click += async (sender, args) => await CreateUserAsync();
            buttons.Controls.Add(create);
            Button edit = CreateFunctionalPanelButton("编辑角色", Purple);
            edit.Click += async (sender, args) => await EditUserAsync();
            buttons.Controls.Add(edit);
            Button reset = CreateFunctionalPanelButton("重置密码", Orange);
            reset.Click += async (sender, args) => await ResetPasswordAsync();
            buttons.Controls.Add(reset);
            Button delete = CreateFunctionalPanelButton("删除用户", Color.FromArgb(184, 62, 62));
            delete.Click += async (sender, args) => await DeleteUserAsync();
            buttons.Controls.Add(delete);
            panel.Controls.Add(buttons);
            page.Controls.Add(panel);
            return page;
        }

        private TabPage CreateMigrationTab()
        {
            TabPage page = CreateTab("备份与迁移");
            Panel panel = CreateSurfacePanel(210);
            panel.Controls.Add(CreateHeading("mongodump / mongorestore", 16, 14));
            panel.Controls.Add(CreateValue("远程导出后通过 SSH/SFTP 下载，再导入本机 MongoDB。", 18, 44, MutedColor));
            panel.Controls.Add(CreateValue("源数据库", 18, 82, MutedColor));
            panel.Controls.Add(CreateValue("等待连接", 18, 106, Orange));
            panel.Controls.Add(CreateValue("本地目标", 270, 82, MutedColor));
            panel.Controls.Add(CreateValue("等待选择", 270, 106, Orange));
            Button backup = CreateFunctionalPanelButton("备份数据库", Blue, 112);
            backup.Click += async (sender, args) => await BackupAsync();
            backup.Location = new Point(520, 88);
            panel.Controls.Add(backup);
            Button restore = CreateFunctionalPanelButton("恢复备份", Green, 112);
            restore.Click += async (sender, args) => await RestoreAsync();
            restore.Location = new Point(642, 88);
            panel.Controls.Add(restore);
            page.Controls.Add(panel);
            return page;
        }

        private TabPage CreateConnectionTab()
        {
            TabPage page = CreateTab("连接设置");
            Panel panel = CreateSurfacePanel(190);
            panel.Controls.Add(CreateHeading("认证设置", 16, 14));
            panel.Controls.Add(CreateValue("认证数据库", 18, 54, MutedColor));
            panel.Controls.Add(CreateValue("admin", 18, 80));
            panel.Controls.Add(CreateValue("隧道地址", 220, 54, MutedColor));
            panel.Controls.Add(CreateValue("127.0.0.1:" + Item.Port, 220, 80));
            Button verify = CreateFunctionalPanelButton("验证并保存凭据", Blue, 132);
            verify.Click += async (sender, args) => await TestCredentialAsync();
            verify.Location = new Point(18, 120);
            panel.Controls.Add(verify);
            page.Controls.Add(panel);
            return page;
        }

        private async Task BackupAsync()
        {
            if (credential == null || !credential.IsVerified)
            {
                MessageBox.Show("请先在“连接设置”中验证 MongoDB 管理账号。", "需要先验证凭据", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            IList<string> databases;
            try
            {
                databases = await databaseService.ListDatabasesAsync(Server, serverPassword, credential, CancellationToken.None);
            }
            catch (Exception ex)
            {
                MessageBox.Show("读取 MongoDB 数据库列表失败：" + ex.Message, "备份准备失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            using (MongoBackupOptionsForm optionsForm = new MongoBackupOptionsForm(databases))
            {
                if (optionsForm.ShowDialog(this) != DialogResult.OK)
                    return;
                using (SaveFileDialog dialog = new SaveFileDialog
                {
                    Title = "选择 MongoDB 备份保存位置",
                    Filter = "MongoDB 压缩归档 (*.archive.gz)|*.archive.gz",
                    DefaultExt = "archive.gz",
                    AddExtension = true,
                    FileName = "MongoDB_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".archive.gz",
                    OverwritePrompt = true
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK)
                        return;
                    MongoBackupOptions selected = optionsForm.Options;
                    using (OperationProgressForm progress = new OperationProgressForm(
                        "备份 MongoDB", "远程导出到本地 BSON archive",
                        new[] { "检查凭据", "远程导出", "SFTP 下载", "完整性校验", "清理临时资源" }))
                    {
                        progress.Operation = async (window, token) =>
                        {
                            window.SetStep(0, OperationStepState.Completed, "凭据已验证");
                            window.SetStep(1, OperationStepState.Running);
                            MongoBackupResult result = await backupService.ExportAsync(
                                Server, serverPassword, credential,
                                new MongoBackupRequest { DatabaseName = selected.DatabaseName, OutputPath = dialog.FileName, IncludeUsersAndRoles = selected.IncludeUsersAndRoles },
                                bytes => window.SetProgress("正在下载 MongoDB 归档", FormatBytes(bytes), 48, Blue, true), token);
                            window.SetStep(1, OperationStepState.Completed, "导出完成");
                            window.SetStep(2, OperationStepState.Completed, FormatBytes(result.BytesWritten));
                            window.SetStep(3, OperationStepState.Completed, "SHA-256 校验通过");
                            window.SetStep(4, OperationStepState.Completed, "已清理");
                            window.MarkSuccess("MongoDB 备份已保存到本地：" + result.OutputPath);
                        };
                        progress.ShowDialog(this);
                    }
                }
            }
        }

        private async Task RestoreAsync()
        {
            using (OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "选择 MongoDB archive 备份",
                Filter = "MongoDB 压缩归档 (*.archive.gz)|*.archive.gz|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                try { MongoBackupService.ValidateBackupFile(dialog.FileName); }
                catch (Exception ex) { MessageBox.Show("MongoDB 备份校验失败：" + ex.Message, "无法恢复", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                if (string.IsNullOrWhiteSpace(MongoBackupService.FindRestoreTool()))
                {
                    MessageBox.Show("当前电脑未找到 mongorestore.exe。请安装 MongoDB Database Tools 后再恢复。", "缺少本机工具", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                using (MongoLocalTargetForm targetForm = new MongoLocalTargetForm())
                {
                    if (targetForm.ShowDialog(this) != DialogResult.OK || targetForm.Target == null)
                        return;
                    if (MessageBox.Show("恢复 MongoDB 归档可能覆盖本机同名集合。请先确认本机数据已有备份。\n\n确定继续吗？", "恢复确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                        return;
                    using (OperationProgressForm progress = new OperationProgressForm(
                        "恢复 MongoDB", Path.GetFileName(dialog.FileName) + " → 本机 MongoDB",
                        new[] { "校验归档", "连接本机数据库", "恢复数据", "确认完成", "清理临时资源" }))
                    {
                        progress.Operation = async (window, token) =>
                        {
                            window.SetStep(0, OperationStepState.Completed, "归档有效");
                            window.SetStep(1, OperationStepState.Completed, targetForm.Target.Host + ":" + targetForm.Target.Port);
                            window.SetStep(2, OperationStepState.Running);
                            await backupService.RestoreAsync(targetForm.Target, dialog.FileName, false,
                                (copied, total) => window.SetProgress("正在恢复 MongoDB", FormatBytes(copied) + " / " + FormatBytes(total), 20 + (int)Math.Min(70, total <= 0 ? 0 : copied * 70L / total), Green, false), token);
                            window.SetStep(2, OperationStepState.Completed, "恢复完成");
                            window.SetStep(3, OperationStepState.Completed, "mongorestore 返回成功");
                            window.SetStep(4, OperationStepState.Completed, "已清理");
                            window.MarkSuccess("MongoDB 备份恢复完成");
                        };
                        progress.ShowDialog(this);
                    }
                }
            }
        }

        private DatabaseCredentialRecord FindCredential()
        {
            if (Server == null || Server.DatabaseCredentials == null)
                return null;
            return Server.DatabaseCredentials.FirstOrDefault(item =>
                string.Equals(item.DatabaseType, "MongoDB", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ServiceName, Item.ServiceName, StringComparison.OrdinalIgnoreCase) &&
                item.Port == Item.Port);
        }

        private string GetCredentialDisplayText()
        {
            return credential != null && credential.IsVerified ? "凭据已验证" : "未录入凭据";
        }

        private string GetManagementAccountText()
        {
            return credential == null || string.IsNullOrWhiteSpace(credential.Username) ? "未配置" : credential.Username;
        }

        private string GetAuthenticationScopeText()
        {
            return credential == null || string.IsNullOrWhiteSpace(credential.AuthenticationDatabase)
                ? "admin"
                : credential.AuthenticationDatabase;
        }

        private string GetVersionDisplayText()
        {
            return string.IsNullOrWhiteSpace(Item.Version) || Item.Version.IndexOf('x') >= 0 ? "点击查询" : Item.Version;
        }

        private Color GetCredentialColor()
        {
            return credential != null && credential.IsVerified ? Green : Orange;
        }

        private void RefreshLabels()
        {
            if (overviewStatusLabel != null)
            {
                overviewStatusLabel.Text = Item.Status;
                overviewStatusLabel.ForeColor = Item.GetStatusColor();
            }
            if (overviewCredentialLabel != null)
            {
                overviewCredentialLabel.Text = GetCredentialDisplayText();
                overviewCredentialLabel.ForeColor = GetCredentialColor();
            }
            if (overviewAccountLabel != null)
                overviewAccountLabel.Text = GetManagementAccountText();
            if (overviewScopeLabel != null)
                overviewScopeLabel.Text = GetAuthenticationScopeText();
            if (overviewVersionLabel != null)
                overviewVersionLabel.Text = GetVersionDisplayText();
            RefreshShellStatus();
        }

        private async Task TestCredentialAsync()
        {
            if (Server == null || string.IsNullOrWhiteSpace(serverPassword))
            {
                MessageBox.Show("当前没有可用的服务器管理凭据，无法建立 SSH 隧道。", "无法连接", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using (DatabaseCredentialForm form = new DatabaseCredentialForm("MongoDB", credential))
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                    return;
                DatabaseCredentialRecord candidate = new DatabaseCredentialRecord
                {
                    Id = credential == null ? Guid.NewGuid().ToString("N") : credential.Id,
                    DatabaseType = "MongoDB",
                    ServiceName = Item.ServiceName,
                    Host = "127.0.0.1",
                    Port = Item.Port,
                    Username = form.Username,
                    Password = form.Password,
                    AuthenticationDatabase = string.IsNullOrWhiteSpace(form.DatabaseName) ? "admin" : form.DatabaseName,
                    DatabaseName = string.IsNullOrWhiteSpace(form.DatabaseName) ? "admin" : form.DatabaseName,
                    Users = credential == null || credential.Users == null ? new List<DatabaseUserRecord>() : credential.Users,
                    IsVerified = false
                };
                Cursor previous = Cursor;
                Cursor = Cursors.WaitCursor;
                try
                {
                    MongoConnectionTestResult result = await databaseService.TestConnectionAsync(Server, serverPassword, candidate, CancellationToken.None);
                    candidate.IsVerified = true;
                    candidate.LastVerifiedAt = DateTime.Now;
                    int index = credential == null ? -1 : Server.DatabaseCredentials.IndexOf(credential);
                    DatabaseCredentialRecord old = credential;
                    if (index >= 0) Server.DatabaseCredentials[index] = candidate; else Server.DatabaseCredentials.Add(candidate);
                    credential = candidate;
                    if (persistChanges != null && !persistChanges())
                    {
                        if (index >= 0) Server.DatabaseCredentials[index] = old; else Server.DatabaseCredentials.Remove(candidate);
                        credential = old;
                        throw new InvalidOperationException("保险库存储失败，凭据没有保存");
                    }
                    if (Item.StatusKind != DatabaseStatusKind.Error) Item.StatusKind = DatabaseStatusKind.Normal;
                    Item.Version = result.ServerVersion;
                    Item.CredentialState = "凭据已验证";
                    credentialChanged?.Invoke();
                    RefreshLabels();
                    await RefreshUsersAsync();
                    MessageBox.Show("MongoDB 连接验证成功，凭据已保存到保险库。\n\nMongoDB 版本：" + result.ServerVersion, "验证成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("MongoDB 连接验证失败：" + ex.Message, "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { Cursor = previous; }
            }
        }

        private async Task ShowVersionAsync(Button button)
        {
            if (credential == null || !credential.IsVerified)
            {
                MessageBox.Show("请先验证并保存 MongoDB 管理凭据。", "需要先验证凭据", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (Server == null || string.IsNullOrWhiteSpace(serverPassword))
            {
                MessageBox.Show("当前没有可用的服务器管理凭据，无法建立 SSH 隧道。", "无法查询版本", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Cursor previousCursor = Cursor;
            string previousText = button.Text;
            button.Enabled = false;
            button.Text = "查询中...";
            Cursor = Cursors.WaitCursor;
            try
            {
                MongoConnectionTestResult result = await databaseService.TestConnectionAsync(
                    Server, serverPassword, credential, CancellationToken.None);
                Item.Version = result.ServerVersion;
                RefreshLabels();
                MessageBox.Show(
                    "MongoDB 版本：" + result.ServerVersion +
                    "\n认证账号：" + result.UserName +
                    "\n认证数据库：" + result.AuthenticationDatabase +
                    "\n远程端口：" + Item.Port +
                    "\n连接方式：SSH 安全隧道",
                    "MongoDB 版本信息",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("查询 MongoDB 版本失败：" + ex.Message, "查询失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = previousCursor;
                button.Text = previousText;
                button.Enabled = true;
            }
        }

        private async Task RefreshUsersAsync()
        {
            if (usersGrid == null)
                return;
            usersGrid.Rows.Clear();
            if (credential == null || !credential.IsVerified)
            {
                if (usersNoteLabel != null) { usersNoteLabel.Text = "请先在“连接设置”中验证 MongoDB 管理账号。"; usersNoteLabel.ForeColor = Orange; }
                return;
            }
            Cursor previous = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                IList<MongoUserInfo> users = await databaseService.ListUsersAsync(Server, serverPassword, credential, CancellationToken.None);
                foreach (MongoUserInfo user in users)
                {
                    bool saved = credential.Users != null && credential.Users.Any(item => string.Equals(item.Username, user.UserName, StringComparison.OrdinalIgnoreCase));
                    usersGrid.Rows.Add(user.UserName, user.AuthenticationDatabase, string.IsNullOrWhiteSpace(user.Roles) ? "无角色" : user.Roles, "已发现", saved ? "已保存" : "未保存");
                }
                usersNoteLabel.Text = "已从 MongoDB 读取 " + users.Count + " 个用户；密码不会显示。";
                usersNoteLabel.ForeColor = MutedColor;
            }
            catch (Exception ex)
            {
                usersNoteLabel.Text = "读取 MongoDB 用户失败：" + ex.Message;
                usersNoteLabel.ForeColor = Color.FromArgb(184, 62, 62);
            }
            finally { Cursor = previous; }
        }

        private async Task CreateUserAsync()
        {
            if (credential == null || !credential.IsVerified)
            {
                MessageBox.Show("请先验证 MongoDB 管理账号。", "需要先验证凭据", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (MongoUserForm form = new MongoUserForm())
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                    return;
                MongoUserRequest request = form.Request;
                if (request.Roles.Any(role => role.StartsWith("clusterAdmin@", StringComparison.OrdinalIgnoreCase) || role.StartsWith("userAdmin@", StringComparison.OrdinalIgnoreCase)))
                {
                    if (MessageBox.Show("当前用户申请了高风险 MongoDB 管理角色，确定继续吗？", "高风险权限确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                        return;
                }
                Cursor previous = Cursor;
                Cursor = Cursors.WaitCursor;
                try
                {
                    await databaseService.CreateUserAsync(Server, serverPassword, credential, request, CancellationToken.None);
                    if (credential.Users == null) credential.Users = new List<DatabaseUserRecord>();
                    credential.Users.RemoveAll(item => string.Equals(item.Username, request.UserName, StringComparison.OrdinalIgnoreCase));
                    credential.Users.Add(new DatabaseUserRecord
                    {
                        Username = request.UserName,
                        Password = request.Password,
                        HostPattern = "MongoDB",
                        DatabaseName = request.DatabaseName,
                        PermissionSummary = string.Join("、", request.Roles),
                        CreatedAt = DateTime.Now,
                        LastVerifiedAt = DateTime.Now,
                        IsVerified = true
                    });
                    if (persistChanges != null && !persistChanges()) throw new InvalidOperationException("MongoDB 用户已创建，但保险库更新失败");
                    await RefreshUsersAsync();
                    MessageBox.Show("MongoDB 用户创建成功，并已保存到保险库。", "创建成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    if (credential.Users != null) credential.Users.RemoveAll(item => string.Equals(item.Username, request.UserName, StringComparison.OrdinalIgnoreCase));
                    MessageBox.Show("创建 MongoDB 用户失败：" + ex.Message, "创建失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { Cursor = previous; }
            }
        }

        private async Task EditUserAsync()
        {
            if (credential == null || !credential.IsVerified || usersGrid == null || usersGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show("请先验证凭据并选择 MongoDB 用户。", "无法编辑", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DataGridViewRow row = usersGrid.SelectedRows[0];
            string username = Convert.ToString(row.Cells[0].Value);
            string authDb = Convert.ToString(row.Cells[1].Value);
            if (string.Equals(username, credential.Username, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("当前管理账号受到保护，不能在这里编辑角色。", "操作被阻止", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using (MongoUserForm form = new MongoUserForm(username, new MongoUserRequest { UserName = username, AuthenticationDatabase = authDb, DatabaseName = authDb, Roles = ParseMongoRoles(Convert.ToString(row.Cells[2].Value)) }))
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                    return;
                try
                {
                    await databaseService.UpdateRolesAsync(Server, serverPassword, credential, form.Request, CancellationToken.None);
                    DatabaseUserRecord saved = FindSavedUser(username);
                    if (saved != null) { saved.PermissionSummary = string.Join("、", form.Request.Roles); saved.LastVerifiedAt = DateTime.Now; }
                    if (persistChanges != null && !persistChanges()) throw new InvalidOperationException("远程角色已修改，但保险库更新失败");
                    await RefreshUsersAsync();
                    MessageBox.Show("MongoDB 用户角色已修改。", "修改成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { MessageBox.Show("编辑 MongoDB 角色失败：" + ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        private async Task ResetPasswordAsync()
        {
            if (credential == null || !credential.IsVerified || usersGrid == null || usersGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show("请先验证凭据并选择 MongoDB 用户。", "无法重置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DataGridViewRow row = usersGrid.SelectedRows[0];
            string username = Convert.ToString(row.Cells[0].Value);
            string authDb = Convert.ToString(row.Cells[1].Value);
            if (string.Equals(username, credential.Username, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("当前管理账号受到保护，不能在这里重置密码。", "操作被阻止", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using (MySqlResetPasswordForm form = new MySqlResetPasswordForm(username, authDb))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;
                if (MessageBox.Show("重置后旧密码会立即失效，确定继续吗？", "确认重置密码", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                DatabaseUserRecord saved = FindSavedUser(username);
                try
                {
                    await databaseService.ResetPasswordAsync(Server, serverPassword, credential, username, authDb, form.NewPassword, CancellationToken.None);
                    if (saved == null)
                    {
                        if (credential.Users == null) credential.Users = new List<DatabaseUserRecord>();
                        saved = new DatabaseUserRecord { Username = username, Password = form.NewPassword, HostPattern = "MongoDB", DatabaseName = authDb, PermissionSummary = Convert.ToString(row.Cells[2].Value), CreatedAt = DateTime.Now, LastVerifiedAt = DateTime.Now, IsVerified = true };
                        credential.Users.Add(saved);
                    }
                    else { saved.Password = form.NewPassword; saved.LastVerifiedAt = DateTime.Now; saved.IsVerified = true; }
                    if (persistChanges != null && !persistChanges()) throw new InvalidOperationException("远程密码已修改，但保险库更新失败");
                    await RefreshUsersAsync();
                    MessageBox.Show("MongoDB 用户密码已重置并保存到保险库。", "重置成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { MessageBox.Show("重置 MongoDB 密码失败：" + ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        private async Task DeleteUserAsync()
        {
            if (credential == null || !credential.IsVerified || usersGrid == null || usersGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show("请先验证凭据并选择 MongoDB 用户。", "无法删除", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string username = Convert.ToString(usersGrid.SelectedRows[0].Cells[0].Value);
            string authDb = Convert.ToString(usersGrid.SelectedRows[0].Cells[1].Value);
            if (string.Equals(username, credential.Username, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("当前管理账号不能删除。", "操作被阻止", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show("确定删除 MongoDB 用户“" + username + "”吗？", "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                await databaseService.DeleteUserAsync(Server, serverPassword, credential, username, authDb, CancellationToken.None);
                if (credential.Users != null) credential.Users.RemoveAll(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase));
                if (persistChanges != null && !persistChanges()) throw new InvalidOperationException("远程用户已删除，但保险库更新失败");
                await RefreshUsersAsync();
                MessageBox.Show("MongoDB 用户已删除。", "操作完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show("删除 MongoDB 用户失败：" + ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private DatabaseUserRecord FindSavedUser(string username)
        {
            return credential == null || credential.Users == null ? null : credential.Users.FirstOrDefault(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase));
        }

        private static List<string> ParseMongoRoles(string value)
        {
            return (value ?? "").Split(new[] { '、', ',', '，' }, StringSplitOptions.RemoveEmptyEntries).Select(item => item.Trim()).Where(item => item.Length > 0).ToList();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024D).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024L * 1024L) return (bytes / (1024D * 1024D)).ToString("0.0") + " MB";
            return (bytes / (1024D * 1024D * 1024D)).ToString("0.0") + " GB";
        }
    }

    public sealed class RedisDatabasePanel : DatabaseDetailPanel
    {
        private readonly string serverPassword;
        private readonly Func<bool> persistChanges;
        private readonly Action credentialChanged;
        private readonly RedisDatabaseService databaseService = new RedisDatabaseService();
        private readonly RedisBackupService backupService = new RedisBackupService();
        private DatabaseCredentialRecord credential;
        private DataGridView aclGrid;
        private Label aclNoteLabel;
        private Label overviewStatusLabel;
        private Label overviewCredentialLabel;
        private Label overviewAccountLabel;
        private Label overviewScopeLabel;
        private Label overviewVersionLabel;

        public RedisDatabasePanel(Server server, DatabaseServiceItem item)
            : this(server, item, "", null, null)
        {
        }

        public RedisDatabasePanel(
            Server server,
            DatabaseServiceItem item,
            string serverPassword,
            Func<bool> persistChanges,
            Action credentialChanged)
            : base(server, item)
        {
            this.serverPassword = serverPassword ?? "";
            this.persistChanges = persistChanges;
            this.credentialChanged = credentialChanged;
            credential = FindCredential();
            BuildShell("Redis 管理", "Redis 使用 ACL 用户、命令权限和 Key 访问范围", Purple);
            Tabs.TabPages.Add(CreateOverviewTab());
            Tabs.TabPages.Add(CreateAclTab());
            Tabs.TabPages.Add(CreateMigrationTab());
            Tabs.TabPages.Add(CreateConnectionTab());
            Tabs.Selected += async (sender, args) =>
            {
                if (args.TabPage != null && args.TabPage.Text == "ACL 用户与权限")
                    await RefreshUsersAsync();
            };
        }

        private TabPage CreateOverviewTab()
        {
            TabPage page = CreateTab("数据库概览");
            Panel overview = CreateConnectionOverviewPanel(
                "Redis 连接概览",
                GetManagementAccountText(),
                "数据库编号",
                GetAuthenticationScopeText(),
                GetCredentialDisplayText(),
                GetCredentialColor(),
                async (sender, args) => await TestCredentialAsync(),
                async (sender, args) => await ShowVersionAsync((Button)sender),
                out overviewAccountLabel,
                out overviewScopeLabel,
                out overviewStatusLabel,
                out overviewCredentialLabel,
                out overviewVersionLabel);
            page.Controls.Add(overview);
            return page;
        }

        private TabPage CreateAclTab()
        {
            TabPage page = CreateTab("ACL 用户与权限");
            Panel panel = CreateSurfacePanel(440);
            panel.Controls.Add(CreateHeading("Redis ACL 用户", 16, 14));
            aclNoteLabel = new Label { AutoEllipsis = true, Size = new Size(760, 24), Text = "Redis 不使用传统数据库 GRANT，而是配置命令和 Key 权限。", ForeColor = MutedColor, Location = new Point(18, 43) };
            panel.Controls.Add(aclNoteLabel);
            aclGrid = CreateGrid("用户名", "状态", "Key 范围", "命令权限", "凭据");
            aclGrid.Location = new Point(16, 76);
            aclGrid.Size = new Size(760, 220);
            aclGrid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            panel.Controls.Add(aclGrid);
            FlowLayoutPanel buttons = new FlowLayoutPanel { Location = new Point(16, 310), Size = new Size(700, 42), WrapContents = false };
            Button create = CreateFunctionalPanelButton("新建 ACL 用户", Blue, 120);
            create.Click += async (sender, args) => await CreateUserAsync();
            buttons.Controls.Add(create);
            Button edit = CreateFunctionalPanelButton("编辑命令权限", Purple, 120);
            edit.Click += async (sender, args) => await EditUserAsync();
            buttons.Controls.Add(edit);
            Button keyScope = CreateFunctionalPanelButton("设置 Key 范围", Orange, 120);
            keyScope.Click += async (sender, args) => await EditUserAsync();
            buttons.Controls.Add(keyScope);
            Button reset = CreateFunctionalPanelButton("重置密码", Orange, 104);
            reset.Click += async (sender, args) => await ResetUserPasswordAsync();
            buttons.Controls.Add(reset);
            Button delete = CreateFunctionalPanelButton("删除用户", Color.FromArgb(184, 62, 62));
            delete.Click += async (sender, args) => await DeleteUserAsync();
            buttons.Controls.Add(delete);
            panel.Controls.Add(buttons);
            page.Controls.Add(panel);
            return page;
        }

        private DatabaseCredentialRecord FindCredential()
        {
            if (Server == null || Server.DatabaseCredentials == null)
                return null;
            return Server.DatabaseCredentials.FirstOrDefault(item =>
                string.Equals(item.DatabaseType, "Redis", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ServiceName, Item.ServiceName, StringComparison.OrdinalIgnoreCase) &&
                item.Port == Item.Port);
        }

        private string GetCredentialDisplayText()
        {
            return credential != null && credential.IsVerified ? "凭据已验证" : "未录入凭据";
        }

        private string GetManagementAccountText()
        {
            return credential == null || string.IsNullOrWhiteSpace(credential.Username) ? "未配置" : credential.Username;
        }

        private string GetAuthenticationScopeText()
        {
            return "DB " + (credential == null || string.IsNullOrWhiteSpace(credential.DatabaseName) ? "0" : credential.DatabaseName);
        }

        private string GetVersionDisplayText()
        {
            return string.IsNullOrWhiteSpace(Item.Version) || Item.Version.IndexOf('x') >= 0 ? "点击查询" : Item.Version;
        }

        private Color GetCredentialColor()
        {
            return credential != null && credential.IsVerified ? Green : Orange;
        }

        private void RefreshLabels()
        {
            if (overviewStatusLabel != null)
            {
                overviewStatusLabel.Text = Item.Status;
                overviewStatusLabel.ForeColor = Item.GetStatusColor();
            }
            if (overviewCredentialLabel != null)
            {
                overviewCredentialLabel.Text = GetCredentialDisplayText();
                overviewCredentialLabel.ForeColor = GetCredentialColor();
            }
            if (overviewAccountLabel != null)
                overviewAccountLabel.Text = GetManagementAccountText();
            if (overviewScopeLabel != null)
                overviewScopeLabel.Text = GetAuthenticationScopeText();
            if (overviewVersionLabel != null)
                overviewVersionLabel.Text = GetVersionDisplayText();
            RefreshShellStatus();
        }

        private async Task TestCredentialAsync()
        {
            if (Server == null || string.IsNullOrWhiteSpace(serverPassword))
            {
                MessageBox.Show("当前没有可用的服务器管理凭据，无法建立 SSH 隧道。", "无法连接", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using (DatabaseCredentialForm form = new DatabaseCredentialForm("Redis", credential))
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                    return;
                DatabaseCredentialRecord candidate = new DatabaseCredentialRecord
                {
                    Id = credential == null ? Guid.NewGuid().ToString("N") : credential.Id,
                    DatabaseType = "Redis",
                    ServiceName = Item.ServiceName,
                    Host = "127.0.0.1",
                    Port = Item.Port,
                    Username = form.Username,
                    Password = form.Password,
                    DatabaseName = string.IsNullOrWhiteSpace(form.DatabaseName) ? "0" : form.DatabaseName,
                    AuthenticationDatabase = "",
                    Users = credential == null || credential.Users == null ? new List<DatabaseUserRecord>() : credential.Users,
                    IsVerified = false
                };
                Cursor previous = Cursor;
                Cursor = Cursors.WaitCursor;
                try
                {
                    RedisConnectionTestResult result = await databaseService.TestConnectionAsync(Server, serverPassword, candidate, CancellationToken.None);
                    candidate.IsVerified = true;
                    candidate.LastVerifiedAt = DateTime.Now;
                    int index = credential == null ? -1 : Server.DatabaseCredentials.IndexOf(credential);
                    DatabaseCredentialRecord old = credential;
                    if (index >= 0) Server.DatabaseCredentials[index] = candidate; else Server.DatabaseCredentials.Add(candidate);
                    credential = candidate;
                    if (persistChanges != null && !persistChanges())
                    {
                        if (index >= 0) Server.DatabaseCredentials[index] = old; else Server.DatabaseCredentials.Remove(candidate);
                        credential = old;
                        throw new InvalidOperationException("保险库存储失败，凭据没有保存");
                    }
                    if (Item.StatusKind != DatabaseStatusKind.Error) Item.StatusKind = DatabaseStatusKind.Normal;
                    Item.Version = result.Version;
                    Item.CredentialState = "凭据已验证";
                    Item.Status = result.AclSupported ? "运行中 · ACL" : "运行中 · 旧版无 ACL";
                    credentialChanged?.Invoke();
                    RefreshLabels();
                    await RefreshUsersAsync();
                    MessageBox.Show("Redis 连接验证成功，凭据已保存到保险库。\n\nRedis 版本：" + result.Version, "验证成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Redis 连接验证失败：" + ex.Message, "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { Cursor = previous; }
            }
        }

        private async Task ShowVersionAsync(Button button)
        {
            if (credential == null || !credential.IsVerified)
            {
                MessageBox.Show("请先验证并保存 Redis 管理凭据。", "需要先验证凭据", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (Server == null || string.IsNullOrWhiteSpace(serverPassword))
            {
                MessageBox.Show("当前没有可用的服务器管理凭据，无法建立 SSH 隧道。", "无法查询版本", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Cursor previousCursor = Cursor;
            string previousText = button.Text;
            button.Enabled = false;
            button.Text = "查询中...";
            Cursor = Cursors.WaitCursor;
            try
            {
                RedisConnectionTestResult result = await databaseService.TestConnectionAsync(
                    Server, serverPassword, credential, CancellationToken.None);
                Item.Version = result.Version;
                Item.Status = result.AclSupported ? "运行中 · ACL" : "运行中 · 旧版无 ACL";
                RefreshLabels();
                MessageBox.Show(
                    "Redis 版本：" + result.Version +
                    "\n认证账号：" + result.UserName +
                    "\n数据库编号：" + result.DatabaseIndex +
                    "\nACL 支持：" + (result.AclSupported ? "支持" : "不支持") +
                    "\n远程端口：" + Item.Port +
                    "\n连接方式：SSH 安全隧道",
                    "Redis 版本信息",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("查询 Redis 版本失败：" + ex.Message, "查询失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = previousCursor;
                button.Text = previousText;
                button.Enabled = true;
            }
        }

        private async Task RefreshUsersAsync()
        {
            if (aclGrid == null)
                return;
            aclGrid.Rows.Clear();
            if (credential == null || !credential.IsVerified)
            {
                if (aclNoteLabel != null) { aclNoteLabel.Text = "请先在“连接设置”中验证 Redis 管理账号。"; aclNoteLabel.ForeColor = Orange; }
                return;
            }
            Cursor previous = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                IList<RedisAclUserInfo> users = await databaseService.ListUsersAsync(Server, serverPassword, credential, CancellationToken.None);
                foreach (RedisAclUserInfo user in users)
                {
                    bool saved = credential.Users != null && credential.Users.Any(item => string.Equals(item.Username, user.Username, StringComparison.OrdinalIgnoreCase));
                    string commandText = string.IsNullOrWhiteSpace(user.CommandRules) ? "无命令权限" : user.CommandRules;
                    string keyText = string.IsNullOrWhiteSpace(user.KeyPatterns) ? "无 Key" : user.KeyPatterns;
                    aclGrid.Rows.Add(user.Username, user.Enabled ? "启用" : "禁用", keyText, commandText, saved ? "已保存" : "未保存");
                }
                aclNoteLabel.Text = "已从 Redis ACL 读取 " + users.Count + " 个用户；密码不会显示。";
                aclNoteLabel.ForeColor = MutedColor;
            }
            catch (Exception ex)
            {
                aclNoteLabel.Text = "读取 Redis ACL 失败：" + ex.Message;
                aclNoteLabel.ForeColor = Color.FromArgb(184, 62, 62);
            }
            finally { Cursor = previous; }
        }

        private async Task CreateUserAsync()
        {
            if (credential == null || !credential.IsVerified)
            {
                MessageBox.Show("请先验证 Redis 管理账号。", "需要先验证凭据", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (RedisAclForm form = new RedisAclForm())
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                    return;
                RedisAclSelection request = form.Selection;
                if (request.AllCommands || request.Admin)
                {
                    if (MessageBox.Show("当前用户申请了高风险 Redis 命令权限，可能修改配置或管理其他用户。\n\n确定继续吗？", "高风险权限确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                        return;
                }
                Cursor previous = Cursor;
                Cursor = Cursors.WaitCursor;
                try
                {
                    await databaseService.CreateUserAsync(Server, serverPassword, credential, request, CancellationToken.None);
                    if (credential.Users == null) credential.Users = new List<DatabaseUserRecord>();
                    credential.Users.RemoveAll(item => string.Equals(item.Username, request.Username, StringComparison.OrdinalIgnoreCase));
                    credential.Users.Add(new DatabaseUserRecord
                    {
                        Username = request.Username,
                        Password = request.Password,
                        HostPattern = "Redis ACL",
                        DatabaseName = credential.DatabaseName,
                        PermissionSummary = BuildRedisPermissionSummary(request),
                        CreatedAt = DateTime.Now,
                        LastVerifiedAt = DateTime.Now,
                        IsVerified = true
                    });
                    if (persistChanges != null && !persistChanges())
                        throw new InvalidOperationException("Redis 用户已创建，但保险库更新失败");
                    await RefreshUsersAsync();
                    MessageBox.Show("Redis ACL 用户创建成功，已通过登录验证并保存到保险库。", "创建成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    if (credential.Users != null) credential.Users.RemoveAll(item => string.Equals(item.Username, request.Username, StringComparison.OrdinalIgnoreCase));
                    MessageBox.Show("创建 Redis ACL 用户失败：" + ex.Message, "创建失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { Cursor = previous; }
            }
        }

        private async Task EditUserAsync()
        {
            if (credential == null || !credential.IsVerified || aclGrid == null || aclGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show("请先验证凭据并选择 Redis ACL 用户。", "无法编辑", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string username = Convert.ToString(aclGrid.SelectedRows[0].Cells[0].Value);
            if (string.Equals(username, "default", StringComparison.OrdinalIgnoreCase) || string.Equals(username, credential.Username, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Redis default 用户和当前管理账号受到保护，不能在这里修改权限。", "操作被阻止", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                IList<RedisAclUserInfo> users = await databaseService.ListUsersAsync(Server, serverPassword, credential, CancellationToken.None);
                RedisAclUserInfo current = users.FirstOrDefault(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase));
                if (current == null) throw new InvalidOperationException("Redis ACL 用户不存在");
                RedisAclSelection existing = ParseRedisSelection(current);
                using (RedisAclForm form = new RedisAclForm(username, existing))
                {
                    if (form.ShowDialog(this) != DialogResult.OK) return;
                    RedisAclSelection request = form.Selection;
                    if (request.AllCommands || request.Admin)
                    {
                        if (MessageBox.Show("当前权限包含高风险 Redis 命令，确定继续吗？", "高风险权限确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    }
                    await databaseService.UpdatePermissionsAsync(Server, serverPassword, credential, request, CancellationToken.None);
                    DatabaseUserRecord saved = FindSavedUser(username);
                    if (saved != null) { saved.PermissionSummary = BuildRedisPermissionSummary(request); saved.LastVerifiedAt = DateTime.Now; }
                    if (persistChanges != null && !persistChanges()) throw new InvalidOperationException("远程权限已修改，但保险库更新失败");
                    await RefreshUsersAsync();
                    MessageBox.Show("Redis ACL 权限已修改。", "修改成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex) { MessageBox.Show("编辑 Redis ACL 权限失败：" + ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private async Task ResetUserPasswordAsync()
        {
            if (credential == null || !credential.IsVerified || aclGrid == null || aclGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show("请先验证凭据并选择 Redis ACL 用户。", "无法重置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string username = Convert.ToString(aclGrid.SelectedRows[0].Cells[0].Value);
            if (string.Equals(username, "default", StringComparison.OrdinalIgnoreCase) || string.Equals(username, credential.Username, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Redis default 用户和当前管理账号受到保护，不能在这里重置密码。", "操作被阻止", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using (RedisResetPasswordForm form = new RedisResetPasswordForm(username))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;
                if (MessageBox.Show("重置后旧密码会立即失效，确定继续吗？", "确认重置密码", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                DatabaseUserRecord saved = FindSavedUser(username);
                string oldPassword = saved == null ? null : saved.Password;
                try
                {
                    await databaseService.ResetPasswordAsync(Server, serverPassword, credential, username, form.NewPassword, oldPassword, CancellationToken.None);
                    if (saved == null)
                    {
                        if (credential.Users == null) credential.Users = new List<DatabaseUserRecord>();
                        saved = new DatabaseUserRecord { Username = username, Password = form.NewPassword, HostPattern = "Redis ACL", DatabaseName = credential.DatabaseName, PermissionSummary = Convert.ToString(aclGrid.SelectedRows[0].Cells[3].Value), CreatedAt = DateTime.Now, IsVerified = true };
                        credential.Users.Add(saved);
                    }
                    else { saved.Password = form.NewPassword; saved.LastVerifiedAt = DateTime.Now; saved.IsVerified = true; }
                    if (persistChanges != null && !persistChanges()) throw new InvalidOperationException("远程密码已修改，但保险库更新失败");
                    await RefreshUsersAsync();
                    MessageBox.Show("Redis ACL 密码已重置，并已通过登录验证保存到保险库。", "重置成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { MessageBox.Show("重置 Redis ACL 密码失败：" + ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        private async Task DeleteUserAsync()
        {
            if (credential == null || !credential.IsVerified || aclGrid == null || aclGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show("请先验证凭据并选择 Redis ACL 用户。", "无法删除", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string username = Convert.ToString(aclGrid.SelectedRows[0].Cells[0].Value);
            if (string.Equals(username, "default", StringComparison.OrdinalIgnoreCase) || string.Equals(username, credential.Username, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Redis default 用户和当前管理账号不能删除。", "操作被阻止", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show("确定删除 Redis ACL 用户“" + username + "”吗？此操作会立即影响远程服务。", "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                await databaseService.DeleteUserAsync(Server, serverPassword, credential, username, CancellationToken.None);
                if (credential.Users != null) credential.Users.RemoveAll(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase));
                if (persistChanges != null && !persistChanges()) throw new InvalidOperationException("远程用户已删除，但保险库更新失败");
                await RefreshUsersAsync();
                MessageBox.Show("Redis ACL 用户已删除。", "操作完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show("删除 Redis ACL 用户失败：" + ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private DatabaseUserRecord FindSavedUser(string username)
        {
            return credential == null || credential.Users == null ? null : credential.Users.FirstOrDefault(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase));
        }

        private static RedisAclSelection ParseRedisSelection(RedisAclUserInfo info)
        {
            string rules = info.CommandRules ?? "";
            return new RedisAclSelection
            {
                Username = info.Username,
                KeyPattern = info.KeyPatterns.Replace("~", "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "*",
                Read = ContainsAclRule(rules, "+@read"),
                Write = ContainsAclRule(rules, "+@write"),
                Connection = ContainsAclRule(rules, "+@connection"),
                Transaction = ContainsAclRule(rules, "+@transaction"),
                PubSub = ContainsAclRule(rules, "+@pubsub"),
                Scripting = ContainsAclRule(rules, "+@scripting"),
                Admin = ContainsAclRule(rules, "+@admin"),
                AllCommands = ContainsAclRule(rules, "+@all")
            };
        }

        private static bool ContainsAclRule(string rules, string value)
        {
            return (rules ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildRedisPermissionSummary(RedisAclSelection selection)
        {
            if (selection == null) return "无命令权限";
            if (selection.AllCommands) return "全部命令";
            List<string> values = new List<string>();
            if (selection.Read) values.Add("读取");
            if (selection.Write) values.Add("写入");
            if (selection.Connection) values.Add("连接");
            if (selection.Transaction) values.Add("事务");
            if (selection.PubSub) values.Add("发布订阅");
            if (selection.Scripting) values.Add("脚本");
            if (selection.Admin) values.Add("管理");
            return values.Count == 0 ? "无命令权限" : string.Join("、", values);
        }

        private async Task BackupRdbAsync()
        {
            if (credential == null || !credential.IsVerified)
            {
                MessageBox.Show("请先在“连接设置”中验证 Redis 管理账号。", "需要先验证凭据", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (SaveFileDialog dialog = new SaveFileDialog
            {
                Title = "选择 Redis RDB 备份保存位置",
                Filter = "Redis RDB 文件 (*.rdb)|*.rdb",
                DefaultExt = "rdb",
                AddExtension = true,
                FileName = "Redis_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".rdb",
                OverwritePrompt = true
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                using (OperationProgressForm progress = new OperationProgressForm(
                    "备份 Redis RDB",
                    "远程触发快照并下载到本机",
                    new[] { "检查凭据", "生成 RDB 快照", "SFTP 下载", "完整性校验", "清理临时资源" }))
                {
                    progress.Operation = async (window, token) =>
                    {
                        window.SetStep(0, OperationStepState.Completed, "凭据已验证");
                        window.SetStep(1, OperationStepState.Running);
                        RedisBackupResult result = await backupService.ExportRdbAsync(
                            Server,
                            serverPassword,
                            credential,
                            new RedisBackupRequest { Mode = RedisBackupMode.Rdb, OutputPath = dialog.FileName },
                            bytes => window.SetProgress("正在下载 RDB", FormatBytes(bytes), 50, Purple, true),
                            token);
                        window.SetStep(1, OperationStepState.Completed, "快照完成");
                        window.SetStep(2, OperationStepState.Completed, FormatBytes(result.BytesWritten));
                        window.SetStep(3, OperationStepState.Completed, "SHA-256 校验通过");
                        window.SetStep(4, OperationStepState.Completed, "已清理");
                        window.MarkSuccess("Redis RDB 已保存到本地：" + result.OutputPath);
                    };
                    progress.ShowDialog(this);
                }
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024D).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024L * 1024L) return (bytes / (1024D * 1024D)).ToString("0.0") + " MB";
            return (bytes / (1024D * 1024D * 1024D)).ToString("0.0") + " GB";
        }

        private TabPage CreateMigrationTab()
        {
            TabPage page = CreateTab("备份与迁移");
            Panel panel = CreateSurfacePanel(220);
            panel.Controls.Add(CreateHeading("RDB / AOF 迁移", 16, 14));
            panel.Controls.Add(CreateValue("Redis 迁移采用 RDB 或 AOF，不使用 SQL 导出流程。", 18, 44, MutedColor));
            panel.Controls.Add(CreateValue("迁移方式", 18, 84, MutedColor));
            ComboBox mode = new ComboBox { Location = new Point(18, 108), Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
            mode.Items.AddRange(new object[] { "RDB 快照", "AOF 持久化文件" });
            mode.SelectedIndex = 0;
            panel.Controls.Add(mode);
            Button backup = CreateFunctionalPanelButton("导出 RDB", Blue, 112);
            backup.Click += async (sender, args) => await BackupRdbAsync();
            backup.Location = new Point(270, 106);
            panel.Controls.Add(backup);
            Button restore = CreateFunctionalPanelButton("恢复 RDB", Green, 112);
            restore.Click += (sender, args) => MessageBox.Show("RDB 恢复需要停止本机 Redis 服务并确认数据目录，功能将在本机目标配置完成后开放。", "恢复说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
            restore.Location = new Point(398, 106);
            panel.Controls.Add(restore);
            page.Controls.Add(panel);
            return page;
        }

        private TabPage CreateConnectionTab()
        {
            TabPage page = CreateTab("连接设置");
            Panel panel = CreateSurfacePanel(190);
            panel.Controls.Add(CreateHeading("Redis 凭据", 16, 14));
            panel.Controls.Add(CreateValue("用户名", 18, 54, MutedColor));
            panel.Controls.Add(CreateValue("default", 18, 80));
            panel.Controls.Add(CreateValue("连接地址", 220, 54, MutedColor));
            panel.Controls.Add(CreateValue("127.0.0.1:" + Item.Port, 220, 80));
            Button verify = CreateFunctionalPanelButton("验证并保存凭据", Blue, 132);
            verify.Click += async (sender, args) => await TestCredentialAsync();
            verify.Location = new Point(18, 120);
            panel.Controls.Add(verify);
            page.Controls.Add(panel);
            return page;
        }
    }

    public sealed class OracleDatabasePanel : DatabaseDetailPanel
    {
        public OracleDatabasePanel(Server server, DatabaseServiceItem item)
            : base(server, item)
        {
            BuildShell("Oracle 管理", "Oracle 用户、权限、备份和迁移接口预留", Orange);
            TabPage page = CreateTab("功能状态");
            Panel panel = CreateSurfacePanel(260);
            panel.Controls.Add(CreateHeading("Oracle 数据库管理功能正在开发中", 24, 32));
            panel.Controls.Add(CreateValue("当前版本暂不执行以下操作：", 26, 78, MutedColor));
            panel.Controls.Add(CreateValue("用户创建、权限分配、备份、迁移和数据库连接测试。", 26, 108, TextColor));
            panel.Controls.Add(CreateValue("Oracle Listener 端口管理已经支持，但数据库管理模块将在后续版本单独实现。", 26, 150, Blue));
            Button close = CreatePanelButton("关闭", Orange, 100);
            close.Location = new Point(26, 190);
            close.Click += (sender, args) => FindForm()?.Close();
            panel.Controls.Add(close);
            page.Controls.Add(panel);
            Tabs.TabPages.Add(page);
        }
    }
}
