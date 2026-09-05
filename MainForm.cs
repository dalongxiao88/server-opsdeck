using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;

namespace RDPManager
{
    public sealed class MainForm : Form
    {
        private static readonly Color WindowBackground = Color.FromArgb(241, 243, 245);
        private static readonly Color SidebarBackground = Color.FromArgb(232, 235, 238);
        private static readonly Color Surface = Color.White;
        private static readonly Color TextColor = Color.FromArgb(35, 42, 49);
        private static readonly Color MutedColor = Color.FromArgb(104, 114, 124);
        private static readonly Color BorderColor = Color.FromArgb(211, 217, 222);
        private static readonly Color Green = Color.FromArgb(26, 134, 87);
        private static readonly Color Blue = Color.FromArgb(42, 125, 185);
        private static readonly Color Orange = Color.FromArgb(210, 125, 26);
        private static readonly Color Red = Color.FromArgb(184, 62, 62);

        private readonly string dataFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "servers.xml");
        private readonly string vaultFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "servers.vault");
        private readonly List<Server> servers = new List<Server>();
        private readonly Dictionary<Server, ServerProbeResult> probes = new Dictionary<Server, ServerProbeResult>();

        private const int PasswordValidMinutes = 120;
        private bool refreshing;
        private bool updatingStatusList;
        private bool showFullIP;
        private string activeMetric = "all";
        private DateTime ipShownAt = DateTime.MinValue;
        private DateTime adminVerifiedAt = DateTime.MinValue;
        private string adminPasswordHash;
        private StorageMode storageMode;
        private byte[] vaultKey;
        private byte[] vaultSalt;

        private TextBox searchBox;
        private ListBox serverStatusList;
        private DataGridView serverGrid;
        private Label selectionInfo;
        private Label lastUpdateLabel;
        private Button serverMetric;
        private Button onlineMetric;
        private Button issuesMetric;
        private Button expiryMetric;
        private Button changeAdminButton;
        private Button resetAdminButton;
        private Button addButton;
        private Button connectButton;
        private Button editButton;
        private Button restartButton;
        private Button portButton;
        private Button refreshButton;
        private Button showIpButton;
        private Button moreButton;
        private Button databaseButton;
        private ToolStripStatusLabel statusBarLabel;
        private System.Windows.Forms.Timer refreshTimer;
        private System.Windows.Forms.Timer uiTimer;
        private bool operationRunning;

        public MainForm(StartupSession session)
        {
            InitializeComponent();
            storageMode = session == null ? StorageMode.None : session.Mode;
            adminPasswordHash = session == null ? null : session.AdminPasswordHash;
            vaultKey = session == null ? null : session.VaultKey;
            vaultSalt = session == null ? null : session.VaultSalt;
            if (session != null && session.Servers != null)
                servers.AddRange(session.Servers);
            UpdateStorageStatus();
            RefreshMetricBar();
            RefreshServerStatusList();
            RefreshGrid();
            StartTimers();
            Shown += async (sender, args) => await RefreshServerStatusAsync();
        }

        private void InitializeComponent()
        {
            Text = "小白服务器管理器";
            ClientSize = new Size(1320, 760);
            MinimumSize = new Size(980, 620);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = WindowBackground;
            Font = new Font("Microsoft YaHei UI", 9F);
            KeyPreview = true;
            KeyDown += MainForm_KeyDown;
            TryLoadIcon();

            Panel header = CreateHeader();
            Panel metrics = CreateMetricBar();
            Panel content = CreateMainContent();
            StatusStrip statusStrip = new StatusStrip
            {
                Dock = DockStyle.Bottom,
                BackColor = Surface,
                SizingGrip = false
            };
            statusBarLabel = new ToolStripStatusLabel
            {
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = MutedColor
            };
            statusStrip.Items.Add(statusBarLabel);

            Controls.Add(content);
            Controls.Add(metrics);
            Controls.Add(header);
            Controls.Add(statusStrip);
        }

        private Panel CreateHeader()
        {
            Panel header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 52,
                BackColor = Surface,
                Padding = new Padding(16, 8, 16, 8)
            };
            Label title = new Label
            {
                AutoSize = true,
                Text = "服务器管理",
                Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
                ForeColor = TextColor,
                Location = new Point(16, 14)
            };
            Label subtitle = new Label
            {
                AutoSize = true,
                Text = "RDP / SSH",
                ForeColor = MutedColor,
                Location = new Point(118, 18)
            };
            Label searchLabel = new Label
            {
                AutoSize = true,
                Text = "搜索",
                ForeColor = MutedColor,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(920, 18)
            };
            searchBox = new TextBox
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(960, 12),
                Size = new Size(330, 27),
                BorderStyle = BorderStyle.FixedSingle
            };
            searchBox.TextChanged += (sender, args) => RefreshGrid();
            header.Controls.Add(title);
            header.Controls.Add(subtitle);
            header.Controls.Add(searchLabel);
            header.Controls.Add(searchBox);
            header.Resize += (sender, args) =>
            {
                searchBox.Left = header.ClientSize.Width - searchBox.Width - 16;
                searchLabel.Left = searchBox.Left - searchLabel.Width - 10;
            };
            return header;
        }

        private Panel CreateMetricBar()
        {
            Panel panel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 46,
                BackColor = WindowBackground,
                Padding = new Padding(10, 4, 10, 4)
            };
            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 7,
                RowCount = 1,
                BackColor = WindowBackground
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 154));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 154));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            serverMetric = CreateMetricButton("服务器", Green);
            onlineMetric = CreateMetricButton("在线", Green);
            issuesMetric = CreateMetricButton("异常", Red);
            expiryMetric = CreateMetricButton("30 天内到期", Orange);
            changeAdminButton = CreateSecurityButton("修改管理密码", Blue);
            resetAdminButton = CreateSecurityButton("重置管理密码", Red);
            lastUpdateLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = MutedColor,
                Padding = new Padding(0, 0, 12, 0)
            };
            layout.Controls.Add(serverMetric, 0, 0);
            layout.Controls.Add(onlineMetric, 1, 0);
            layout.Controls.Add(issuesMetric, 2, 0);
            layout.Controls.Add(expiryMetric, 3, 0);
            layout.Controls.Add(changeAdminButton, 4, 0);
            layout.Controls.Add(resetAdminButton, 5, 0);
            layout.Controls.Add(lastUpdateLabel, 6, 0);
            serverMetric.Click += (sender, args) => SelectMetric("all");
            onlineMetric.Click += (sender, args) => SelectMetric("online");
            issuesMetric.Click += (sender, args) => SelectMetric("issues");
            expiryMetric.Click += (sender, args) => SelectMetric("expiring");
            changeAdminButton.Click += (sender, args) => ChangeAdminPassword();
            resetAdminButton.Click += (sender, args) => ResetAdminPassword();
            panel.Controls.Add(layout);
            return panel;
        }

        private static Button CreateMetricButton(string caption, Color accent)
        {
            Button button = new Button
            {
                Dock = DockStyle.Fill,
                Text = caption,
                TextAlign = ContentAlignment.MiddleCenter,
                FlatStyle = FlatStyle.Flat,
                BackColor = Surface,
                ForeColor = accent,
                Margin = new Padding(2, 0, 2, 0),
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = BorderColor;
            return button;
        }

        private static Button CreateSecurityButton(string caption, Color accent)
        {
            Button button = new Button
            {
                Dock = DockStyle.Fill,
                Text = caption,
                TextAlign = ContentAlignment.MiddleCenter,
                FlatStyle = FlatStyle.Flat,
                BackColor = Surface,
                ForeColor = accent,
                Margin = new Padding(2, 0, 2, 0),
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = accent;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(245, 246, 247);
            return button;
        }

        private Panel CreateMainContent()
        {
            Panel content = new Panel { Dock = DockStyle.Fill, BackColor = WindowBackground };
            Panel sidebar = CreateStatusSidebar();
            Panel workspace = CreateWorkspace();
            content.Controls.Add(workspace);
            content.Controls.Add(sidebar);
            return content;
        }

        private Panel CreateStatusSidebar()
        {
            Panel sidebar = new Panel
            {
                Dock = DockStyle.Left,
                Width = 238,
                BackColor = SidebarBackground,
                Padding = new Padding(10, 10, 8, 10)
            };
            Label caption = new Label
            {
                Dock = DockStyle.Top,
                Height = 30,
                Text = "实时状态",
                ForeColor = TextColor,
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                Padding = new Padding(4, 2, 0, 0)
            };
            serverStatusList = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = SidebarBackground,
                ForeColor = TextColor,
                IntegralHeight = false,
                ItemHeight = 48,
                DrawMode = DrawMode.OwnerDrawFixed,
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            serverStatusList.DrawItem += DrawStatusItem;
            serverStatusList.SelectedIndexChanged += ServerStatusList_SelectedIndexChanged;
            sidebar.Controls.Add(serverStatusList);
            sidebar.Controls.Add(caption);
            return sidebar;
        }

        private Panel CreateWorkspace()
        {
            Panel workspace = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = WindowBackground,
                Padding = new Padding(10, 10, 10, 10)
            };
            serverGrid = CreateServerGrid();
            Panel detailBar = CreateDetailBar();
            Panel actionBar = CreateActionBar();
            Panel bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 98,
                BackColor = WindowBackground
            };
            bottom.Controls.Add(detailBar);
            bottom.Controls.Add(actionBar);
            workspace.Controls.Add(serverGrid);
            workspace.Controls.Add(bottom);
            return workspace;
        }

        private DataGridView CreateServerGrid()
        {
            DataGridView grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoGenerateColumns = false,
                BackgroundColor = Surface,
                BorderStyle = BorderStyle.FixedSingle,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
                ColumnHeadersHeight = 34,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                EnableHeadersVisualStyles = false,
                GridColor = BorderColor,
                MultiSelect = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                RowTemplate = { Height = 35 },
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(246, 247, 248),
                ForeColor = MutedColor,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                SelectionBackColor = Color.FromArgb(246, 247, 248),
                SelectionForeColor = MutedColor,
                Padding = new Padding(5, 0, 0, 0)
            };
            grid.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Surface,
                ForeColor = TextColor,
                SelectionBackColor = Color.FromArgb(220, 238, 229),
                SelectionForeColor = TextColor,
                Padding = new Padding(5, 0, 5, 0)
            };
            grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(251, 252, 252) };

            AddGridColumn(grid, "status", "状态", 115);
            AddGridColumn(grid, "name", "名称", 172, DataGridViewAutoSizeColumnMode.Fill);
            AddGridColumn(grid, "type", "方式", 70);
            AddGridColumn(grid, "endpoint", "地址", 160);
            AddGridColumn(grid, "user", "账号", 120);
            AddGridColumn(grid, "group", "分组", 100);
            AddGridColumn(grid, "provider", "厂商", 90);
            AddGridColumn(grid, "expire", "到期", 108);
            AddGridColumn(grid, "checked", "检测", 84);

            grid.SelectionChanged += (sender, args) => UpdateSelectionInfo();
            grid.CellDoubleClick += (sender, args) => ConnectSelectedServer();
            grid.CellMouseDown += ServerGrid_CellMouseDown;
            grid.KeyDown += ServerGrid_KeyDown;
            grid.CellFormatting += ServerGrid_CellFormatting;
            grid.ContextMenuStrip = CreateServerContextMenu();
            return grid;
        }

        private static void AddGridColumn(DataGridView grid, string name, string header, int width, DataGridViewAutoSizeColumnMode mode = DataGridViewAutoSizeColumnMode.None)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = header,
                Width = width,
                MinimumWidth = Math.Min(width, 58),
                AutoSizeMode = mode,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                Resizable = DataGridViewTriState.False
            });
        }

        private Panel CreateDetailBar()
        {
            Panel detail = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 38,
                BackColor = Surface,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(10, 0, 10, 0)
            };
            selectionInfo = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = TextColor,
                AutoEllipsis = true,
                Text = "未选择服务器"
            };
            detail.Controls.Add(selectionInfo);
            return detail;
        }

        private Panel CreateActionBar()
        {
            Panel actionBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 60,
                BackColor = WindowBackground,
                Padding = new Padding(8, 9, 8, 9)
            };
            addButton = CreateActionButton("添加服务器", Green, true, 112);
            connectButton = CreateActionButton("连接", Green, true, 78);
            editButton = CreateActionButton("编辑", Blue, false, 78);
            restartButton = CreateActionButton("重启", Orange, false, 78);
            portButton = CreateActionButton("端口管理", Blue, false, 96);
            refreshButton = CreateActionButton("刷新状态", Blue, false, 96);
            showIpButton = CreateActionButton("显示 IP", Orange, false, 88);
            moreButton = CreateActionButton("更多操作", TextColor, false, 102);
            databaseButton = CreateActionButton("数据库管理", Color.FromArgb(116, 86, 166), false, 110);
            FlowLayoutPanel flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                BackColor = WindowBackground,
                Padding = new Padding(0, 0, 0, 0)
            };
            flow.Controls.Add(addButton);
            flow.Controls.Add(connectButton);
            flow.Controls.Add(editButton);
            flow.Controls.Add(restartButton);
            flow.Controls.Add(portButton);
            flow.Controls.Add(refreshButton);
            flow.Controls.Add(showIpButton);
            flow.Controls.Add(moreButton);
            flow.Controls.Add(databaseButton);
            addButton.Click += BtnAdd_Click;
            connectButton.Click += (sender, args) => ConnectSelectedServer();
            editButton.Click += BtnEdit_Click;
            restartButton.Click += (sender, args) => ExecutePowerAction(true);
            portButton.Click += (sender, args) => OpenPortManagement();
            refreshButton.Click += async (sender, args) => await RefreshServerStatusAsync();
            showIpButton.Click += BtnShowIP_Click;
            moreButton.Click += (sender, args) => ShowMoreMenu();
            databaseButton.Click += (sender, args) => OpenDatabaseManagement();
            actionBar.Controls.Add(flow);
            return actionBar;
        }

        private static Button CreateActionButton(string text, Color color, bool primary, int width)
        {
            Button button = new Button
            {
                Text = text,
                Width = width,
                Height = 40,
                Margin = new Padding(0, 0, 8, 0),
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

        private bool SaveServerData()
        {
            try
            {
                if (storageMode == StorageMode.PlainXml)
                {
                    PlainServerStorage.Save(dataFile, servers, adminPasswordHash);
                }
                else if (storageMode == StorageMode.EncryptedVault)
                {
                    if (vaultKey == null)
                        throw new InvalidOperationException("保险库密钥不可用");
                    VaultStore.Save(vaultFile, servers, vaultKey, vaultSalt);
                }
                else
                {
                    throw new InvalidOperationException("尚未选择服务器资料存储模式");
                }
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存服务器资料失败：" + ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private string GetServerPassword(Server server)
        {
            return server == null ? "" : (server.Password ?? "");
        }

        private void UpdateStorageStatus()
        {
            if (statusBarLabel == null)
                return;
            statusBarLabel.Text = storageMode == StorageMode.EncryptedVault
                ? "加密保险库模式"
                : storageMode == StorageMode.PlainXml
                    ? "明文兼容模式"
                    : "未选择存储模式";
        }

        private void RefreshMetricBar()
        {
            int online = servers.Count(server => GetProbe(server).IsServiceAvailable);
            int issues = servers.Count(server => IsChecked(server) && !GetProbe(server).IsServiceAvailable);
            int expiring = servers.Count(IsExpiringSoon);
            serverMetric.Text = "服务器  " + servers.Count;
            onlineMetric.Text = "在线  " + online;
            issuesMetric.Text = "异常  " + issues;
            expiryMetric.Text = "30 天内到期  " + expiring;
            UpdateMetricButtonStyle();
        }

        private void SelectMetric(string metric)
        {
            activeMetric = metric;
            RefreshMetricBar();
            RefreshGrid();
        }

        private void UpdateMetricButtonStyle()
        {
            SetMetricStyle(serverMetric, "all", Green);
            SetMetricStyle(onlineMetric, "online", Green);
            SetMetricStyle(issuesMetric, "issues", Red);
            SetMetricStyle(expiryMetric, "expiring", Orange);
        }

        private void SetMetricStyle(Button button, string metric, Color accent)
        {
            bool selected = activeMetric == metric;
            button.BackColor = selected ? Color.FromArgb(226, 239, 231) : Surface;
            button.ForeColor = accent;
            button.FlatAppearance.BorderColor = selected ? accent : BorderColor;
            button.Font = new Font("Microsoft YaHei UI", 9F, selected ? FontStyle.Bold : FontStyle.Regular);
        }

        private void RefreshServerStatusList()
        {
            if (serverStatusList == null)
                return;

            Server selected = GetSelectedServer();
            updatingStatusList = true;
            serverStatusList.BeginUpdate();
            serverStatusList.Items.Clear();
            foreach (Server server in servers)
                serverStatusList.Items.Add(new ServerStatusItem(server, GetProbe(server)));
            if (selected != null)
                SelectStatusItem(selected);
            else if (serverStatusList.Items.Count > 0)
                serverStatusList.SelectedIndex = 0;
            serverStatusList.EndUpdate();
            updatingStatusList = false;
            serverStatusList.Invalidate();
        }

        private void SelectStatusItem(Server server)
        {
            for (int i = 0; i < serverStatusList.Items.Count; i++)
            {
                ServerStatusItem item = serverStatusList.Items[i] as ServerStatusItem;
                if (item != null && ReferenceEquals(item.Server, server))
                {
                    bool previousState = updatingStatusList;
                    updatingStatusList = true;
                    serverStatusList.SelectedIndex = i;
                    updatingStatusList = previousState;
                    return;
                }
            }
        }

        private void ServerStatusList_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (updatingStatusList || serverStatusList.SelectedItem == null)
                return;
            ServerStatusItem item = serverStatusList.SelectedItem as ServerStatusItem;
            if (item != null)
            {
                if (activeMetric != "all")
                {
                    activeMetric = "all";
                    RefreshMetricBar();
                    RefreshGrid();
                }
                SelectServer(item.Server);
            }
        }

        private void RefreshGrid()
        {
            if (serverGrid == null)
                return;

            Server selected = GetSelectedServer();
            List<Server> visible = ApplyFilters().ToList();
            bool keepSelection = selected != null && visible.Contains(selected);
            serverGrid.SuspendLayout();
            serverGrid.Rows.Clear();
            for (int index = 0; index < visible.Count; index++)
            {
                Server server = visible[index];
                ServerProbeResult probe = GetProbe(server);
                int rowIndex = serverGrid.Rows.Add(
                    probe.DisplayText,
                    server.Name,
                    server.Type == ServerType.Windows ? "RDP" : "SSH",
                    GetEndpoint(server, !showFullIP),
                    EmptyAsDash(server.Username),
                    EmptyAsDash(server.Group),
                    EmptyAsDash(server.Provider),
                    server.ExpireDate == DateTime.MinValue ? "未设置" : server.GetExpireInfo(),
                    probe.CheckedAt == DateTime.MinValue ? "-" : probe.CheckedAt.ToString("HH:mm:ss"));
                DataGridViewRow row = serverGrid.Rows[rowIndex];
                row.Tag = server;
                FormatGridRow(row, server, probe);
                if ((keepSelection && ReferenceEquals(server, selected)) || (!keepSelection && index == 0))
                {
                    row.Selected = true;
                    serverGrid.CurrentCell = row.Cells["name"];
                }
            }
            serverGrid.ResumeLayout();
            UpdateSelectionInfo();
            UpdateStatusBar();
        }

        private void FormatGridRow(DataGridViewRow row, Server server, ServerProbeResult probe)
        {
            row.Cells["status"].Style.BackColor = GetProbeBackColor(probe);
            row.Cells["status"].Style.ForeColor = GetProbeColor(probe);
            row.Cells["status"].Style.SelectionBackColor = GetProbeBackColor(probe);
            row.Cells["status"].Style.SelectionForeColor = GetProbeColor(probe);
            row.Cells["expire"].Style.ForeColor = server.GetExpireColor();
        }

        private void ServerGrid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= serverGrid.Rows.Count)
                return;
            Server server = serverGrid.Rows[e.RowIndex].Tag as Server;
            if (server == null)
                return;
            ServerProbeResult probe = GetProbe(server);
            if (e.ColumnIndex == serverGrid.Columns["status"].Index)
            {
                e.CellStyle.BackColor = GetProbeBackColor(probe);
                e.CellStyle.ForeColor = GetProbeColor(probe);
                e.CellStyle.SelectionBackColor = GetProbeBackColor(probe);
                e.CellStyle.SelectionForeColor = GetProbeColor(probe);
            }
            else if (e.ColumnIndex == serverGrid.Columns["expire"].Index)
            {
                e.CellStyle.ForeColor = server.GetExpireColor();
            }
        }

        private IEnumerable<Server> ApplyFilters()
        {
            IEnumerable<Server> result = servers;
            string query = searchBox == null ? "" : searchBox.Text.Trim();
            if (!string.IsNullOrEmpty(query))
            {
                result = result.Where(server => Contains(server.Name, query) || Contains(server.IP, query) ||
                    Contains(server.Username, query) || Contains(server.Group, query) || Contains(server.Provider, query) ||
                    Contains(server.Remark, query));
            }

            if (activeMetric == "online")
                return result.Where(server => GetProbe(server).IsServiceAvailable);
            if (activeMetric == "issues")
                return result.Where(server => IsChecked(server) && !GetProbe(server).IsServiceAvailable);
            if (activeMetric == "expiring")
                return result.Where(IsExpiringSoon);
            return result;
        }

        private void UpdateSelectionInfo()
        {
            Server server = GetSelectedServer();
            bool operationIdle = !operationRunning;
            connectButton.Enabled = operationIdle && server != null;
            editButton.Enabled = operationIdle && server != null;
            restartButton.Enabled = operationIdle && server != null;
            portButton.Enabled = operationIdle && server != null;
            moreButton.Enabled = operationIdle && server != null;
            databaseButton.Enabled = operationIdle && server != null;

            if (server == null)
            {
                selectionInfo.Text = "未选择服务器";
                selectionInfo.ForeColor = MutedColor;
                return;
            }

            ServerProbeResult probe = GetProbe(server);
            selectionInfo.Text = string.Format("{0}   {1}   {2}   账号：{3}   分组：{4}   备注：{5}",
                server.Name,
                probe.DisplayText,
                GetEndpoint(server, !showFullIP),
                EmptyAsDash(server.Username),
                EmptyAsDash(server.Group),
                EmptyAsDash(server.Remark));
            selectionInfo.ForeColor = GetProbeColor(probe);
            SelectStatusItem(server);
        }

        private void UpdateStatusBar()
        {
            string mode = storageMode == StorageMode.EncryptedVault ? "加密保险库" : "明文兼容";
            statusBarLabel.Text = string.Format("{0} · 显示 {1} 台 · 双击连接 · 右键打开操作菜单 · Ctrl+F 搜索 · F5 刷新", mode, serverGrid.Rows.Count);
        }

        private async Task RefreshServerStatusAsync()
        {
            if (operationRunning || refreshing)
                return;

            refreshing = true;
            refreshButton.Enabled = false;
            lastUpdateLabel.Text = "正在检测...";
            try
            {
                Task<KeyValuePair<Server, ServerProbeResult>>[] tasks = servers.Select(async server =>
                    new KeyValuePair<Server, ServerProbeResult>(server, await ServerProbe.CheckAsync(server))).ToArray();
                KeyValuePair<Server, ServerProbeResult>[] results = await Task.WhenAll(tasks);
                foreach (KeyValuePair<Server, ServerProbeResult> result in results)
                    probes[result.Key] = result.Value;
                lastUpdateLabel.Text = "刚刚检测完成";
            }
            finally
            {
                refreshing = false;
                refreshButton.Enabled = true;
                RefreshMetricBar();
                RefreshServerStatusList();
                RefreshGrid();
            }
        }

        private void BtnAdd_Click(object sender, EventArgs e)
        {
            using (ServerForm form = new ServerForm())
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                    return;
                if (!SaveCredentialForServer(form.Server))
                    return;
                servers.Add(form.Server);
                probes[form.Server] = ServerProbeResult.Pending();
                SaveServerData();
                RefreshMetricBar();
                RefreshServerStatusList();
                RefreshGrid();
                SelectServer(form.Server);
            }
        }

        private void BtnEdit_Click(object sender, EventArgs e)
        {
            Server original = GetSelectedServer();
            if (original == null || !EnsureAdminVerified("请输入管理密码以查看服务器详细信息"))
                return;

            using (ServerForm form = new ServerForm(original))
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                    return;
                if (!SaveCredentialForServer(form.Server))
                    return;

                int index = servers.IndexOf(original);
                ServerProbeResult probe = GetProbe(original);
                servers[index] = form.Server;
                probes.Remove(original);
                probes[form.Server] = probe;
                SaveServerData();
                RefreshMetricBar();
                RefreshServerStatusList();
                RefreshGrid();
                SelectServer(form.Server);
            }
        }

        private bool SaveCredentialForServer(Server server)
        {
            return server != null;
        }

        private void ConnectSelectedServer()
        {
            Server server = GetSelectedServer();
            if (server == null || !EnsureAdminVerified("请输入管理密码以连接服务器"))
                return;

            string password = GetServerPassword(server);
            if (!EnsureServerPassword(server, ref password))
                return;
            if (server.Type == ServerType.Windows)
                ConnectRdp(server, password);
            else
                ConnectSsh(server, password);
        }

        private bool EnsureServerPassword(Server server, ref string password)
        {
            if (server != null && server.Type == ServerType.Linux && server.SshCredentialMode == SshCredentialMode.PrivateKey)
            {
                if (string.IsNullOrWhiteSpace(server.SshPrivateKeyPath) || !File.Exists(server.SshPrivateKeyPath))
                {
                    MessageBox.Show("当前 Linux 服务器使用 SSH 私钥，但私钥文件不存在。请编辑服务器重新选择私钥文件。", "私钥不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                return EnsurePrivateKeyReady(server);
            }
            if (!string.IsNullOrEmpty(password))
                return true;

            using (PasswordForm form = new PasswordForm("请输入服务器密码：" + server.Name))
            {
                if (form.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(form.Password))
                    return false;
                password = form.Password;
            }

            server.Password = password;
            if (!SaveServerData())
            {
                server.Password = null;
                return false;
            }
            statusBarLabel.Text = storageMode == StorageMode.EncryptedVault
                ? "服务器密码已保存到加密保险库"
                : "服务器密码已保存到 servers.xml";
            return true;
        }

        private bool EnsurePrivateKeyReady(Server server)
        {
            try
            {
                using (Renci.SshNet.PrivateKeyFile key = string.IsNullOrEmpty(server.SshPrivateKeyPassphrase)
                    ? new Renci.SshNet.PrivateKeyFile(server.SshPrivateKeyPath)
                    : new Renci.SshNet.PrivateKeyFile(server.SshPrivateKeyPath, server.SshPrivateKeyPassphrase))
                {
                }
                return true;
            }
            catch (Renci.SshNet.Common.SshPassPhraseNullOrEmptyException)
            {
            }
            catch (Exception ex)
            {
                if (string.IsNullOrEmpty(server.SshPrivateKeyPassphrase))
                {
                    MessageBox.Show("SSH 私钥无法读取：" + SanitizeError(ex.Message), "私钥不可用", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
            }

            using (PasswordForm form = new PasswordForm("请输入 SSH 私钥口令：" + server.Name))
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                    return false;
                try
                {
                    using (Renci.SshNet.PrivateKeyFile key = new Renci.SshNet.PrivateKeyFile(server.SshPrivateKeyPath, form.Password))
                    {
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("SSH 私钥口令错误或私钥格式不受支持：" + SanitizeError(ex.Message), "私钥验证失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
                server.SshPrivateKeyPassphrase = form.Password;
            }

            if (storageMode == StorageMode.EncryptedVault && !SaveServerData())
            {
                server.SshPrivateKeyPassphrase = null;
                return false;
            }
            statusBarLabel.Text = storageMode == StorageMode.EncryptedVault
                ? "SSH 私钥口令已保存到加密保险库"
                : "SSH 私钥口令仅在当前会话使用";
            return true;
        }

        private void ConnectRdp(Server server, string password)
        {
            try
            {
                string rdpFile = Path.Combine(Path.GetTempPath(), "rdp_manager_" + Guid.NewGuid().ToString("N") + ".rdp");
                string content = string.Join(Environment.NewLine, new[]
                {
                    "full address:s:" + server.IP + ":" + server.Port,
                    "username:s:" + server.Username,
                    "prompt for credentials:i:0",
                    "screen mode id:i:2",
                    "session bpp:i:32",
                    "compression:i:1",
                    "networkautodetect:i:1",
                    "bandwidthautodetect:i:1",
                    "redirectclipboard:i:1",
                    "redirectprinters:i:0",
                    "autoreconnection enabled:i:1"
                });
                File.WriteAllText(rdpFile, content, System.Text.Encoding.Unicode);

                ProcessStartInfo credential = new ProcessStartInfo("cmdkey.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                credential.ArgumentList.Add("/generic:TERMSRV/" + server.IP);
                credential.ArgumentList.Add("/user:" + server.Username);
                credential.ArgumentList.Add("/pass:" + password);
                using (Process process = Process.Start(credential))
                    process.WaitForExit();

                Process.Start(new ProcessStartInfo("mstsc.exe")
                {
                    UseShellExecute = true,
                    Arguments = "\"" + rdpFile + "\""
                });
                Task.Delay(5000).ContinueWith(task =>
                {
                    try { File.Delete(rdpFile); } catch { }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("RDP 连接失败：" + ex.Message, "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ConnectSsh(Server server, string password)
        {
            try
            {
                string putty = FindPutty();
                if (server.SshCredentialMode == SshCredentialMode.PrivateKey &&
                    !string.Equals(Path.GetExtension(server.SshPrivateKeyPath), ".ppk", StringComparison.OrdinalIgnoreCase))
                    putty = null;
                if (!string.IsNullOrEmpty(putty))
                {
                    ProcessStartInfo start = new ProcessStartInfo(putty) { UseShellExecute = false };
                    start.ArgumentList.Add("-ssh");
                    start.ArgumentList.Add(server.Username + "@" + server.IP);
                    start.ArgumentList.Add("-P");
                    start.ArgumentList.Add(server.Port);
                    if (server.SshCredentialMode == SshCredentialMode.PrivateKey && !string.IsNullOrWhiteSpace(server.SshPrivateKeyPath))
                    {
                        start.ArgumentList.Add("-i");
                        start.ArgumentList.Add(server.SshPrivateKeyPath);
                    }
                    else if (!string.IsNullOrEmpty(password))
                    {
                        start.ArgumentList.Add("-pw");
                        start.ArgumentList.Add(password);
                    }
                    Process.Start(start);
                    return;
                }

                ProcessStartInfo nativeSsh = new ProcessStartInfo("cmd.exe") { UseShellExecute = true };
                nativeSsh.ArgumentList.Add("/K");
                nativeSsh.ArgumentList.Add("ssh");
                nativeSsh.ArgumentList.Add("-p");
                nativeSsh.ArgumentList.Add(server.Port);
                if (server.SshCredentialMode == SshCredentialMode.PrivateKey && !string.IsNullOrWhiteSpace(server.SshPrivateKeyPath))
                {
                    nativeSsh.ArgumentList.Add("-i");
                    nativeSsh.ArgumentList.Add(server.SshPrivateKeyPath);
                }
                nativeSsh.ArgumentList.Add(server.Username + "@" + server.IP);
                Process.Start(nativeSsh);
                if (server.SshCredentialMode != SshCredentialMode.PrivateKey && !string.IsNullOrEmpty(password))
                    Clipboard.SetText(password);
                statusBarLabel.Text = server.SshCredentialMode == SshCredentialMode.PrivateKey
                    ? "SSH 已启动，正在使用私钥认证"
                    : "SSH 已启动，服务器密码已复制到剪贴板";
            }
            catch (Exception ex)
            {
                MessageBox.Show("SSH 连接失败：" + ex.Message, "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string FindPutty()
        {
            string[] paths =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PuTTY", "putty.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PuTTY", "putty.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "putty.exe")
            };
            return paths.FirstOrDefault(File.Exists);
        }

        private void BtnShowIP_Click(object sender, EventArgs e)
        {
            if (showFullIP)
            {
                HideIP();
                return;
            }
            if (!EnsureAdminVerified("请输入管理密码以显示完整 IP"))
                return;
            showFullIP = true;
            ipShownAt = DateTime.Now;
            showIpButton.Text = "隐藏 IP";
            RefreshGrid();
            UpdateSelectionInfo();
        }

        private void HideIP()
        {
            showFullIP = false;
            showIpButton.Text = "显示 IP";
            RefreshGrid();
            UpdateSelectionInfo();
        }

        private bool EnsureAdminVerified(string prompt)
        {
            if ((DateTime.Now - adminVerifiedAt).TotalMinutes < PasswordValidMinutes)
                return true;

            using (PasswordForm form = new PasswordForm(prompt))
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                    return false;
                if (!PasswordSecurity.Verify(form.Password, adminPasswordHash))
                {
                    MessageBox.Show("管理密码错误", "验证失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
            }
            adminVerifiedAt = DateTime.Now;
            return true;
        }

        private void ChangeAdminPassword()
        {
            if (!EnsureAdminVerified("请输入当前管理密码"))
                return;
            using (PasswordForm first = new PasswordForm("请输入新管理密码（至少 6 位）"))
            using (PasswordForm second = new PasswordForm("请再次输入新管理密码"))
            {
                if (first.ShowDialog(this) != DialogResult.OK)
                    return;
                if (first.Password.Length < 6)
                {
                    MessageBox.Show("新密码至少需要 6 位", "密码太短", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (second.ShowDialog(this) != DialogResult.OK)
                    return;
                if (first.Password != second.Password)
                {
                    MessageBox.Show("两次输入的密码不一致", "修改失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                try
                {
                    string newHash = PasswordSecurity.Hash(first.Password);
                    if (storageMode == StorageMode.PlainXml)
                    {
                        PlainServerStorage.Save(dataFile, servers, newHash);
                    }
                    else if (storageMode == StorageMode.EncryptedVault)
                    {
                        byte[] newSalt;
                        byte[] newKey = VaultStore.Save(vaultFile, servers, first.Password, out newSalt);
                        if (vaultKey != null)
                            CryptographicOperations.ZeroMemory(vaultKey);
                        vaultKey = newKey;
                        vaultSalt = newSalt;
                    }
                    else
                    {
                        throw new InvalidOperationException("尚未选择服务器资料存储模式");
                    }
                    adminPasswordHash = newHash;
                    adminVerifiedAt = DateTime.MinValue;
                    MessageBox.Show("管理密码已修改", "修改成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("保存新管理密码失败：" + ex.Message, "修改失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ResetAdminPassword()
        {
            DialogResult confirm = MessageBox.Show(
                "重置后将执行以下操作：\n\n" +
                "• 删除 servers.xml\n" +
                "• 删除 servers.vault\n" +
                "• 删除所有服务器凭据和 RDP 登录缓存\n" +
                "• 清除当前管理密码\n\n" +
                "服务器列表、端口、备注和其他信息均不会保留。\n" +
                "下次启动将按照首次使用流程重新设置。\n\n确定彻底重置软件吗？",
                "确认彻底重置软件",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes)
                return;

            try
            {
                StartupSecurity.ResetAllCredentials(AppDomain.CurrentDomain.BaseDirectory);
                MessageBox.Show("软件已彻底重置。所有服务器资料和密码均已删除。\n\n请重新启动管理器完成首次设置。", "重置完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("重置管理密码失败：" + ex.Message, "重置失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DeleteSelectedServer()
        {
            Server server = GetSelectedServer();
            if (server == null)
                return;
            if (MessageBox.Show("确定删除服务器“" + server.Name + "”吗？", "删除服务器", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            try
            {
                CredentialStore.Delete(CredentialStore.GetServerTarget(server.CredentialId));
                servers.Remove(server);
                probes.Remove(server);
                SaveServerData();
                RefreshMetricBar();
                RefreshServerStatusList();
                RefreshGrid();
            }
            catch (Exception ex)
            {
                MessageBox.Show("删除服务器失败：" + ex.Message, "删除失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void CopySelectedEndpoint()
        {
            Server server = GetSelectedServer();
            if (server == null || !EnsureAdminVerified("请输入管理密码以复制服务器地址"))
                return;
            Clipboard.SetText(server.IP + ":" + server.Port);
            statusBarLabel.Text = "已复制连接地址";
        }

        private void CopySelectedPassword()
        {
            Server server = GetSelectedServer();
            if (server == null || !EnsureAdminVerified("请输入管理密码以复制服务器密码"))
                return;
            if (server.Type == ServerType.Linux && server.SshCredentialMode == SshCredentialMode.PrivateKey)
            {
                MessageBox.Show("该 Linux 服务器使用 SSH 私钥认证，没有可复制的服务器登录密码。", "使用私钥认证", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Clipboard.SetText(GetServerPassword(server));
            statusBarLabel.Text = "服务器密码已复制到剪贴板";
        }

        private void OpenProviderWebsite()
        {
            Server server = GetSelectedServer();
            if (server == null || string.IsNullOrWhiteSpace(server.ProviderUrl))
            {
                MessageBox.Show("该服务器没有设置厂商网址", "无法打开", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try { Process.Start(new ProcessStartInfo(server.ProviderUrl) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show("打开厂商网站失败：" + ex.Message, "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void OpenPortManagement()
        {
            Server server = GetSelectedServer();
            if (server == null || operationRunning)
                return;
            if (!EnsureAdminVerified("请输入管理密码以管理服务端口"))
                return;

            string password = GetServerPassword(server);
            if (!EnsureServerPassword(server, ref password))
                return;

            operationRunning = true;
            try
            {
                PortInspectionResult inspection;
                using (OperationProgressForm discovery = new OperationProgressForm(
                    "发现远程服务",
                    string.Format("{0}   ·   {1}", server.Name, server.GetMaskedIP()),
                     new[] { "连接远程管理通道", "识别 SSH / RDP", "识别可管理服务" }))
                {
                    PortInspectionResult captured = null;
                    discovery.Operation = async (window, token) =>
                    {
                        window.SetStep(0, OperationStepState.Running);
                        window.SetProgress("正在连接服务器", server.Type == ServerType.Linux ? "通过 SSH 读取 Linux 服务" : "SSH 优先，失败时回退 WinRM", 10, Blue, true);
                        captured = server.Type == ServerType.Linux
                            ? await new LinuxPortManagementService().InspectAsync(server, password, token)
                            : await new PortManagementService().InspectAsync(server, password, token);
                        window.SetStep(0, OperationStepState.Completed, captured.Transport);
                        window.SetStep(1, OperationStepState.Completed, captured.Services.Count(item => item.ServiceType == "RDP" || item.ServiceType == "SSH") + " 项");
                        window.SetStep(2, OperationStepState.Completed, captured.Services.Count(item => item.ServiceType != "RDP" && item.ServiceType != "SSH") + " 项");
                        window.MarkSuccess("已发现 " + captured.Services.Count + " 个可管理服务");
                    };
                    discovery.ShowDialog(this);
                    inspection = captured;
                }

                if (inspection == null)
                    return;

                if (server.Type == ServerType.Linux)
                {
                    inspection.Services = inspection.Services
                        .Where(item => string.Equals(item.ServiceType, "SSH", StringComparison.OrdinalIgnoreCase) || item.IsSupported)
                        .ToList();
                    if (inspection.Services.Count == 0)
                    {
                        MessageBox.Show("未识别到可安全管理的 Linux SSH 或数据库服务。", "没有可管理端口", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                }

                using (PortManagementForm settings = new PortManagementForm(
                    server,
                    inspection,
                    (target, token) => server.Type == ServerType.Linux
                        ? new LinuxPortManagementService().GetAvailablePortsAsync(server, password, target, token)
                        : new PortManagementService().GetAvailablePortsAsync(server, password, target, token)))
                {
                    if (settings.ShowDialog(this) != DialogResult.OK)
                        return;

                    PortChangeRequest request = settings.Request;
                    using (OperationProgressForm progress = new OperationProgressForm(
                        "确认修改端口",
                        string.Format("{0}   ·   {1}   ·   {2} → {3}", server.Name, request.Target.DisplayName, request.Target.Port, request.NewPort),
                        new[] { "检查新端口", "备份原配置", "配置防火墙", "修改服务配置", "重启服务", "验证新端口", "失败时自动回滚" }))
                    {
                        progress.Operation = async (window, token) =>
                        {
                            int lastProgress = 0;
                             Action<int, string, string> report = (value, title, detail) =>
                             {
                                 lastProgress = value;
                                 int step = value < 20 ? 0 : value < 36 ? 1 : value < 55 ? 3 : value < 78 ? 4 : 5;
                                 window.SetStep(step, OperationStepState.Running, detail);
                                 window.SetProgress(title, detail, value, value >= 78 ? Orange : Blue, true);
                             };
                             if (server.Type == ServerType.Linux)
                                 await new LinuxPortManagementService().ExecuteAsync(
                                     server,
                                     password,
                                     request,
                                     report,
                                     () => PromptLinuxSudoPassword(server, window),
                                     token);
                             else
                                 await new PortManagementService().ExecuteAsync(server, password, request, report, token);

                            for (int index = 0; index < 6; index++)
                                window.SetStep(index, OperationStepState.Completed);
                            window.SetStep(6, OperationStepState.Skipped, "未触发");
                            SaveServicePortRecord(server, request.Target);
                            if (request.Target.ServiceType == "RDP")
                                server.Port = request.NewPort.ToString();
                            if (request.Target.ServiceType == "SSH")
                            {
                                server.ManagementPort = request.NewPort.ToString();
                                if (server.Type == ServerType.Linux)
                                    server.Port = request.NewPort.ToString();
                            }
                            SaveServerData();
                            window.MarkSuccess(request.Target.DisplayName + " 已切换到端口 " + request.NewPort);
                        };
                        progress.ShowDialog(this);
                    }
                }
            }
            finally
            {
                if (server.Type == ServerType.Linux)
                    server.SudoPassword = null;
                operationRunning = false;
                RefreshGrid();
                RefreshServerStatusList();
                UpdateSelectionInfo();
            }
        }

        private void OpenDatabaseManagement()
        {
            Server server = GetSelectedServer();
            if (server == null || operationRunning)
                return;
            if (storageMode != StorageMode.EncryptedVault)
            {
                MessageBox.Show(
                    "数据库管理凭据可能拥有创建用户和授权权限，只允许保存到 AES-256-GCM 加密保险库。\n\n当前为明文兼容模式，暂不开放数据库管理逻辑。",
                    "需要加密保险库",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (!EnsureAdminVerified("请输入管理密码以检测数据库服务"))
                return;

            string password = GetServerPassword(server);
            if (!EnsureServerPassword(server, ref password))
                return;

            operationRunning = true;
            try
            {
                PortInspectionResult inspection;
                using (OperationProgressForm discovery = new OperationProgressForm(
                    "检测数据库服务",
                    string.Format("{0}   ·   {1}", server.Name, server.GetMaskedIP()),
                    new[] { "连接远程管理通道", "识别数据库服务", "读取运行状态" }))
                {
                    PortInspectionResult captured = null;
                    discovery.Operation = async (window, token) =>
                    {
                        window.SetStep(0, OperationStepState.Running);
                        window.SetProgress("正在连接服务器", server.Type == ServerType.Linux ? "通过 SSH 读取 Linux 数据库服务" : "SSH 优先，失败时回退 WinRM", 12, Blue, true);
                        captured = server.Type == ServerType.Linux
                            ? await new LinuxPortManagementService().InspectAsync(server, password, token)
                            : await new PortManagementService().InspectAsync(server, password, token);
                        window.SetStep(0, OperationStepState.Completed, captured.Transport);

                        int databaseCount = captured.Services.Count(item => IsDatabaseServiceType(item.ServiceType));
                        window.SetStep(1, OperationStepState.Completed, databaseCount + " 项");
                        window.SetProgress("正在读取数据库状态", server.Type == ServerType.Linux ? "检查 Linux systemd 服务状态" : "检查 Windows 服务运行状态", 78, Blue, true);
                        window.SetStep(2, OperationStepState.Completed, "状态已更新");
                        window.MarkSuccess("数据库服务检测完成");
                    };
                    discovery.ShowDialog(this);
                    inspection = captured;
                }

                if (inspection == null)
                    return;
                using (DatabaseManagementForm form = new DatabaseManagementForm(
                    server,
                    inspection,
                    token => server.Type == ServerType.Linux
                        ? new LinuxPortManagementService().InspectAsync(server, password, token)
                        : new PortManagementService().InspectAsync(server, password, token),
                    password,
                    () => SaveServerData()))
                    form.ShowDialog(this);
            }
            finally
            {
                if (server.Type == ServerType.Linux)
                    server.SudoPassword = null;
                operationRunning = false;
                UpdateSelectionInfo();
            }
        }

        private void OpenLinuxSystemInfo()
        {
            Server server = GetSelectedServer();
            if (server == null || operationRunning)
                return;
            if (server.Type != ServerType.Linux)
            {
                MessageBox.Show("系统信息窗口当前用于 Linux 服务器。Windows 状态会在后续版本统一展示。", "暂不支持", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!EnsureAdminVerified("请输入管理密码以读取 Linux 系统信息"))
                return;

            string password = GetServerPassword(server);
            if (!EnsureServerPassword(server, ref password))
                return;

            operationRunning = true;
            try
            {
                RemoteSystemInfo captured = null;
                using (OperationProgressForm progress = new OperationProgressForm(
                    "读取 Linux 系统信息",
                    string.Format("{0}   ·   {1}", server.Name, server.GetMaskedIP()),
                    new[] { "连接 SSH", "读取系统信息", "确认权限与管理能力" }))
                {
                    progress.Operation = async (window, token) =>
                    {
                        window.SetStep(0, OperationStepState.Running);
                        window.SetProgress("正在连接 Linux", "建立 SSH 管理连接", 12, Blue, true);
                        using (IRemoteExecutor executor = await RemoteExecutorFactory.CreateAsync(server, password, token, RemoteTransport.SSH))
                        {
                            window.SetStep(0, OperationStepState.Completed, "SSH 已连接");
                            window.SetStep(1, OperationStepState.Running);
                            window.SetProgress("正在读取系统信息", "发行版、内核、资源与服务环境", 48, Blue, true);
                            captured = await executor.GetSystemInfoAsync(token);
                            window.SetStep(1, OperationStepState.Completed, captured.OperatingSystem + " " + captured.OsVersion);
                            window.SetStep(2, OperationStepState.Completed, captured.IsRoot ? "root" : captured.CanSudo ? "免密 sudo" : captured.HasSudo ? "sudo 需密码" : "无 sudo");
                        }
                        window.MarkSuccess("Linux 系统信息读取完成");
                    };
                    progress.ShowDialog(this);
                }

                if (captured != null)
                {
                    using (LinuxSystemInfoForm form = new LinuxSystemInfoForm(server, captured))
                        form.ShowDialog(this);
                }
            }
            finally
            {
                if (server.Type == ServerType.Linux)
                    server.SudoPassword = null;
                operationRunning = false;
                UpdateSelectionInfo();
            }
        }

        private static bool IsDatabaseServiceType(string serviceType)
        {
            return string.Equals(serviceType, "MySQL", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(serviceType, "MariaDB", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(serviceType, "MongoDB", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(serviceType, "Redis", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(serviceType, "Oracle", StringComparison.OrdinalIgnoreCase);
        }

        private static void SaveServicePortRecord(Server server, DetectedServicePort service)
        {
            ServicePortRecord existing = server.ServicePorts.FirstOrDefault(item =>
                string.Equals(item.ServiceType, service.ServiceType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ServiceName, service.ServiceName, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new ServicePortRecord();
                server.ServicePorts.Add(existing);
            }
            existing.ServiceType = service.ServiceType;
            existing.ServiceName = service.ServiceName;
            existing.Port = service.Port;
            existing.Protocol = service.Protocol;
            existing.ConfigPath = service.ConfigPath;
            existing.TargetKey = service.TargetKey;
            existing.UpdatedAt = DateTime.Now;
        }

        private void ExecutePowerAction(bool restart)
        {
            if (!restart)
                return;

            Server server = GetSelectedServer();
            if (server == null || !EnsureAdminVerified("请输入管理密码以执行远程操作"))
                return;
            if (refreshing)
                return;

            string password = GetServerPassword(server);
            if (!EnsureServerPassword(server, ref password))
                return;

            operationRunning = true;
            try
            {
                using (OperationProgressForm form = new OperationProgressForm(
                        "确认重启",
                        string.Format("{0}   ·   {1}   ·   {2}", server.Name, server.GetMaskedIP(), server.Type == ServerType.Linux ? "Linux / SSH" : "SSH 优先 / WinRM 备用"),
                    new[]
                    {
                        "连接并验证权限",
                        "发送重启命令",
                        "等待服务器重新启动",
                        "确认服务器恢复"
                    }))
                {
                    form.Operation = (window, cancellationToken) => RunRestartOperationAsync(
                        server,
                        password,
                        window,
                        cancellationToken);
                    form.ShowDialog(this);
                }
            }
            finally
            {
                if (server.Type == ServerType.Linux)
                    server.SudoPassword = null;
                operationRunning = false;
            }
            RefreshServerStatusList();
            RefreshGrid();
            UpdateSelectionInfo();
        }

        private async Task RunRestartOperationAsync(
            Server server,
            string password,
            OperationProgressForm window,
            CancellationToken cancellationToken)
        {
            int currentStep = 0;
            int managementPort = 22;
            RemoteTransport transport = RemoteTransport.SSH;
            try
            {
                window.SetStep(currentStep, OperationStepState.Running);
                window.SetProgress("正在连接服务器", "优先尝试 SSH，失败后尝试 WinRM...", 8, Blue, true);
                IRemoteExecutor executor = await RemoteExecutorFactory.CreateAsync(server, password, cancellationToken);
                RemoteSystemInfo before;
                using (executor)
                {
                    transport = executor.Transport;
                    managementPort = RemoteExecutorFactory.GetManagementPort(server, transport);
                    before = await executor.GetSystemInfoAsync(cancellationToken);
                    window.SetStep(currentStep, OperationStepState.Completed,
                        GetTransportDisplayName(transport, managementPort) + " · " + before.UserName);

                    currentStep = 1;
                    window.SetStep(currentStep, OperationStepState.Running);
                    RemoteCommandResult restartResult;
                    if (server.Type == ServerType.Linux)
                    {
                        if (!before.IsRoot && !before.CanSudo)
                        {
                            if (!before.HasSudo)
                                throw new InvalidOperationException("当前 Linux 账号不是 root，且系统没有可用的 sudo");
                            server.SudoPassword = PromptLinuxSudoPassword(server, window);
                            if (string.IsNullOrEmpty(server.SudoPassword))
                                throw new InvalidOperationException("未提供 sudo 密码，已停止远程重启");
                            before.SudoPassword = server.SudoPassword;
                        }
                        window.SetProgress("正在发送 Linux 重启命令", GetTransportDisplayName(transport, managementPort) + " 已连接，正在验证管理员权限...", 28, Blue, true);
                        string rebootCommand = before.HasSystemd ? "systemctl reboot" : "shutdown -r now";
                        string detached = "nohup sh -c 'sleep 2; " + rebootCommand + "' >/dev/null 2>&1 </dev/null & printf 'RESTART_COMMAND_ACCEPTED\\n'";
                        if (before.IsRoot)
                            restartResult = await executor.ExecuteCommandAsync(detached, TimeSpan.FromSeconds(25), cancellationToken);
                        else if (before.CanSudo)
                            restartResult = await executor.ExecuteCommandAsync("sudo -n sh -c " + QuoteLinuxShell(detached), TimeSpan.FromSeconds(25), cancellationToken);
                        else
                            restartResult = await ((ILinuxPrivilegedExecutor)executor).ExecuteSudoCommandAsync(detached, before.SudoPassword, TimeSpan.FromSeconds(25), cancellationToken);
                    }
                    else
                    {
                        window.SetProgress("正在发送重启命令", GetTransportDisplayName(transport, managementPort) + " 已连接，正在核验 shutdown.exe...", 28, Blue, true);
                        restartResult = await executor.ExecutePowerShellAsync(
                            "$output = (& shutdown.exe /r /t 5 /f 2>&1 | Out-String).Trim(); " +
                            "$code = $LASTEXITCODE; " +
                            "if ($code -ne 0) { throw ('shutdown.exe 返回错误代码 ' + $code + ($(if ($output) { ': ' + $output } else { '' }))) }; " +
                            "'RESTART_COMMAND_ACCEPTED'",
                            TimeSpan.FromSeconds(25),
                            cancellationToken);
                    }
                    if (restartResult.ExitCode != 0 || string.IsNullOrWhiteSpace(restartResult.Output) ||
                        restartResult.Output.IndexOf("RESTART_COMMAND_ACCEPTED", StringComparison.OrdinalIgnoreCase) < 0)
                        throw new InvalidOperationException((server.Type == ServerType.Linux ? "Linux 重启命令" : "shutdown.exe") + " 未确认接受：" + SanitizeError(restartResult.Error ?? restartResult.Output));
                    window.SetStep(currentStep, OperationStepState.Completed, server.Type == ServerType.Linux ? "重启命令已接受" : "shutdown.exe 已接受");
                }

                currentStep = 2;
                window.SetStep(currentStep, OperationStepState.Running);
                bool wentOffline = await WaitForPortDropAsync(
                    server.IP,
                    managementPort,
                    GetTransportDisplayName(transport, managementPort),
                    window,
                    cancellationToken);
                window.SetStep(currentStep, wentOffline ? OperationStepState.Completed : OperationStepState.Skipped,
                    wentOffline ? "已检测到连接中断" : "未捕获到离线瞬间，继续确认启动时间");

                currentStep = 3;
                window.SetStep(currentStep, OperationStepState.Running);
                RemoteSystemInfo after = await WaitForRebootCompletionAsync(
                    server,
                    password,
                    managementPort,
                    transport,
                    before,
                    window,
                    cancellationToken);
                window.SetProgress("正在确认系统已重启", "系统启动时间已变化", 95, Blue);
                window.SetStep(currentStep, OperationStepState.Completed,
                    GetTransportDisplayName(transport, managementPort) + " 已恢复 · 启动 " + after.LastBootUpTime.ToLocalTime().ToString("HH:mm:ss"));
                window.MarkSuccess("服务器已恢复，已确认完成重启");
            }
            catch (Exception ex)
            {
                window.SetStep(currentStep, OperationStepState.Failed, SanitizeError(ex.Message));
                throw;
            }
        }

        private async Task<bool> WaitForPortDropAsync(
            string host,
            int port,
            string channel,
            OperationProgressForm window,
            CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(25))
            {
                if (!await IsTcpPortOpenAsync(host, port, cancellationToken))
                    return true;

                int progress = 35 + (int)Math.Min(20, stopwatch.Elapsed.TotalSeconds / 25D * 20D);
                window.SetProgress("等待服务器重新启动", channel + " 仍在响应，继续检测...", progress, Orange);
                await Task.Delay(1000, cancellationToken);
            }
            return false;
        }

        private async Task<RemoteSystemInfo> WaitForRebootCompletionAsync(
            Server server,
            string password,
            int port,
            RemoteTransport transport,
            RemoteSystemInfo before,
            OperationProgressForm window,
            CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(90))
            {
                int progress = 56 + (int)Math.Min(38, stopwatch.Elapsed.TotalSeconds / 90D * 38D);
                window.SetProgress("等待服务器恢复", "检测 " + GetTransportDisplayName(transport, port) + " 和系统启动时间...", progress, Blue);

                if (await IsTcpPortOpenAsync(server.IP, port, cancellationToken))
                {
                    IRemoteExecutor executor = null;
                    try
                    {
                        executor = await RemoteExecutorFactory.CreateAsync(server, password, cancellationToken, transport);
                        RemoteSystemInfo after = await executor.GetSystemInfoAsync(cancellationToken);
                        if (after.LastBootUpTime > before.LastBootUpTime)
                            return after;
                    }
                    catch
                    {
                        // WinRM may accept TCP before the service is ready. Keep polling.
                    }
                    finally
                    {
                        executor?.Dispose();
                    }
                }

                await Task.Delay(2500, cancellationToken);
            }

            throw new TimeoutException("等待管理通道恢复或确认系统启动时间变化超时");
        }

        private static string GetTransportDisplayName(RemoteTransport transport, int port)
        {
            return (transport == RemoteTransport.SSH ? "SSH" : "WinRM") + " " + port;
        }

        private static async Task<bool> IsTcpPortOpenAsync(string host, int port, CancellationToken cancellationToken)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(2));
                        await client.ConnectAsync(host, port, timeout.Token);
                        return client.Connected;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw;
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static string SanitizeError(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "未知错误";
            return RemoteErrorFormatter.Format(message, "");
        }

        private string PromptLinuxSudoPassword(Server server, IWin32Window owner)
        {
            using (PasswordForm form = new PasswordForm("请输入 Linux sudo 密码：" + (server == null ? "服务器" : server.Name)))
                return form.ShowDialog(owner ?? this) == DialogResult.OK ? form.Password : null;
        }

        private static string QuoteLinuxShell(string value)
        {
            return "'" + (value ?? "").Replace("'", "'\\''") + "'";
        }

        private ContextMenuStrip CreateServerContextMenu(bool forButton = false)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem connect = new ToolStripMenuItem("连接");
            ToolStripMenuItem linuxInfo = new ToolStripMenuItem("Linux 系统信息");
            ToolStripMenuItem edit = new ToolStripMenuItem("编辑");
            ToolStripMenuItem copyAddress = new ToolStripMenuItem("复制连接地址");
            ToolStripMenuItem copyPassword = new ToolStripMenuItem("复制密码");
            ToolStripMenuItem provider = new ToolStripMenuItem("打开厂商网站");
            ToolStripMenuItem restart = new ToolStripMenuItem("重启服务器");
            ToolStripMenuItem portManagement = new ToolStripMenuItem("服务端口管理");
            ToolStripMenuItem changePassword = new ToolStripMenuItem("修改管理密码");
            ToolStripMenuItem delete = new ToolStripMenuItem("删除服务器") { ForeColor = Red };

            connect.Click += (sender, args) => ConnectSelectedServer();
            linuxInfo.Click += (sender, args) => OpenLinuxSystemInfo();
            edit.Click += BtnEdit_Click;
            copyAddress.Click += (sender, args) => CopySelectedEndpoint();
            copyPassword.Click += (sender, args) => CopySelectedPassword();
            provider.Click += (sender, args) => OpenProviderWebsite();
            restart.Click += (sender, args) => ExecutePowerAction(true);
            portManagement.Click += (sender, args) => OpenPortManagement();
            changePassword.Click += (sender, args) => ChangeAdminPassword();
            delete.Click += (sender, args) => DeleteSelectedServer();

            menu.Items.Add(connect);
            menu.Items.Add(linuxInfo);
            menu.Items.Add(edit);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(copyAddress);
            menu.Items.Add(copyPassword);
            menu.Items.Add(provider);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(restart);
            menu.Items.Add(portManagement);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(changePassword);
            menu.Items.Add(delete);
            menu.Opening += (sender, args) =>
            {
                bool enabled = GetSelectedServer() != null;
                connect.Enabled = enabled;
                linuxInfo.Visible = enabled && GetSelectedServer().Type == ServerType.Linux;
                linuxInfo.Enabled = linuxInfo.Visible;
                edit.Enabled = enabled;
                copyAddress.Enabled = enabled;
                copyPassword.Enabled = enabled;
                provider.Enabled = enabled;
                restart.Enabled = enabled;
                portManagement.Enabled = enabled;
                delete.Enabled = enabled;
            };
            return menu;
        }

        private void ShowMoreMenu()
        {
            ContextMenuStrip menu = CreateServerContextMenu(true);
            menu.Show(moreButton, new Point(0, -menu.PreferredSize.Height));
        }

        private void StartTimers()
        {
            uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            uiTimer.Tick += (sender, args) =>
            {
                if (!showFullIP)
                    return;
                if ((DateTime.Now - ipShownAt).TotalSeconds >= 5)
                    HideIP();
                else
                    statusBarLabel.Text = "完整 IP 显示中，" + Math.Max(0, 5 - (int)(DateTime.Now - ipShownAt).TotalSeconds) + " 秒后自动隐藏";
            };
            uiTimer.Start();

            refreshTimer = new System.Windows.Forms.Timer { Interval = 30000 };
            refreshTimer.Tick += async (sender, args) => await RefreshServerStatusAsync();
            refreshTimer.Start();
        }

        private Server GetSelectedServer()
        {
            return serverGrid != null && serverGrid.SelectedRows.Count > 0
                ? serverGrid.SelectedRows[0].Tag as Server
                : null;
        }

        private void SelectServer(Server server)
        {
            if (serverGrid == null)
                return;
            foreach (DataGridViewRow row in serverGrid.Rows)
            {
                if (ReferenceEquals(row.Tag, server))
                {
                    row.Selected = true;
                    serverGrid.CurrentCell = row.Cells["name"];
                    serverGrid.Focus();
                    return;
                }
            }
        }

        private void ServerGrid_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.RowIndex >= 0 && e.Button == MouseButtons.Right)
                serverGrid.Rows[e.RowIndex].Selected = true;
        }

        private void ServerGrid_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete)
            {
                DeleteSelectedServer();
                e.SuppressKeyPress = true;
            }
        }

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.F)
            {
                searchBox.Focus();
                searchBox.SelectAll();
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.F5)
            {
                _ = RefreshServerStatusAsync();
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter && serverGrid.Focused)
            {
                ConnectSelectedServer();
                e.SuppressKeyPress = true;
            }
        }

        private void DrawStatusItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0)
                return;
            ServerStatusItem item = (ServerStatusItem)serverStatusList.Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (SolidBrush brush = new SolidBrush(selected ? Color.FromArgb(215, 232, 222) : SidebarBackground))
                e.Graphics.FillRectangle(brush, e.Bounds);

            Color statusColor = GetProbeColor(item.Probe);
            using (SolidBrush brush = new SolidBrush(statusColor))
                e.Graphics.FillEllipse(brush, e.Bounds.Left + 10, e.Bounds.Top + 15, 10, 10);
            using (SolidBrush brush = new SolidBrush(TextColor))
                e.Graphics.DrawString(item.Server.Name, e.Font, brush, e.Bounds.Left + 28, e.Bounds.Top + 6);
            using (SolidBrush brush = new SolidBrush(MutedColor))
                e.Graphics.DrawString(item.Server.Type == ServerType.Windows ? "RDP" : "SSH", e.Font, brush, e.Bounds.Left + 28, e.Bounds.Top + 27);
            using (SolidBrush brush = new SolidBrush(statusColor))
                e.Graphics.DrawString(item.Probe.DisplayText, e.Font, brush, e.Bounds.Left + 72, e.Bounds.Top + 27);
            using (Pen pen = new Pen(Color.FromArgb(211, 216, 220)))
                e.Graphics.DrawLine(pen, e.Bounds.Left + 10, e.Bounds.Bottom - 1, e.Bounds.Right - 10, e.Bounds.Bottom - 1);
        }

        private static bool Contains(string value, string query)
        {
            return !string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private static bool IsExpiringSoon(Server server)
        {
            return server.ExpireDate != DateTime.MinValue && (server.ExpireDate.Date - DateTime.Now.Date).TotalDays <= 30;
        }

        private static bool IsChecked(Server server, Dictionary<Server, ServerProbeResult> probes)
        {
            ServerProbeResult result;
            return probes.TryGetValue(server, out result) && result.CheckedAt != DateTime.MinValue;
        }

        private bool IsChecked(Server server)
        {
            return IsChecked(server, probes);
        }

        private ServerProbeResult GetProbe(Server server)
        {
            ServerProbeResult result;
            return probes.TryGetValue(server, out result) ? result : ServerProbeResult.Pending();
        }

        private static Color GetProbeColor(ServerProbeResult result)
        {
            if (result.CheckedAt == DateTime.MinValue)
                return MutedColor;
            if (!result.IsServiceAvailable)
                return Red;
            if (!result.LatencyMilliseconds.HasValue || result.LatencyMilliseconds.Value < 50)
                return Color.FromArgb(35, 153, 93);
            if (result.LatencyMilliseconds.Value < 100)
                return Blue;
            if (result.LatencyMilliseconds.Value < 200)
                return Orange;
            return Red;
        }

        private static Color GetProbeBackColor(ServerProbeResult result)
        {
            if (result.CheckedAt == DateTime.MinValue)
                return Color.FromArgb(244, 246, 247);
            if (!result.IsServiceAvailable)
                return Color.FromArgb(250, 231, 231);
            if (!result.LatencyMilliseconds.HasValue || result.LatencyMilliseconds.Value < 50)
                return Color.FromArgb(225, 242, 232);
            if (result.LatencyMilliseconds.Value < 100)
                return Color.FromArgb(226, 239, 249);
            if (result.LatencyMilliseconds.Value < 200)
                return Color.FromArgb(252, 241, 219);
            return Color.FromArgb(250, 231, 231);
        }

        private static string EmptyAsDash(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        private static string GetEndpoint(Server server, bool masked)
        {
            return masked
                ? "***.***.***.***:****"
                : string.Format("{0}:{1}", server.IP, server.Port);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (vaultKey != null)
            {
                CryptographicOperations.ZeroMemory(vaultKey);
                vaultKey = null;
            }
            if (vaultSalt != null)
            {
                CryptographicOperations.ZeroMemory(vaultSalt);
                vaultSalt = null;
            }
            base.OnFormClosed(e);
        }

        private void TryLoadIcon()
        {
            try
            {
                using (Stream stream = typeof(MainForm).Assembly.GetManifestResourceStream("RDPManager.favicon.ico"))
                {
                    if (stream != null)
                        Icon = new Icon(stream);
                }
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            uiTimer?.Stop();
            refreshTimer?.Stop();
            uiTimer?.Dispose();
            refreshTimer?.Dispose();
            base.OnFormClosing(e);
        }

        private sealed class ServerStatusItem
        {
            public Server Server { get; }
            public ServerProbeResult Probe { get; }

            public ServerStatusItem(Server server, ServerProbeResult probe)
            {
                Server = server;
                Probe = probe;
            }

            public override string ToString()
            {
                return Server.Name;
            }
        }

        private sealed class FlatToolStripRenderer : ToolStripProfessionalRenderer
        {
            public FlatToolStripRenderer()
                : base(new FlatColorTable())
            {
            }
        }

        private sealed class FlatColorTable : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return Color.FromArgb(232, 241, 236); } }
            public override Color MenuItemBorder { get { return Color.FromArgb(196, 218, 204); } }
            public override Color ToolStripBorder { get { return BorderColor; } }
            public override Color SeparatorDark { get { return BorderColor; } }
            public override Color SeparatorLight { get { return Surface; } }
        }
    }
}
