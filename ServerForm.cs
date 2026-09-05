using System;
using System.Drawing;
using System.Windows.Forms;

namespace RDPManager
{
    public sealed class ServerForm : Form
    {
        public Server Server { get; private set; }

        private TextBox txtName;
        private TextBox txtIP;
        private TextBox txtPort;
        private TextBox txtUsername;
        private TextBox txtPassword;
        private TextBox txtGroup;
        private TextBox txtProvider;
        private TextBox txtProviderUrl;
        private TextBox txtRemark;
        private TextBox txtManagementPort;
        private ComboBox cmbServerType;
        private ComboBox cmbManagement;
        private DateTimePicker dtpPurchase;
        private DateTimePicker dtpExpire;
        private bool loading;

        public ServerForm(Server server = null)
        {
            Server = server == null ? new Server() : new Server(server);
            InitializeComponent();
            LoadData();
        }

        private void InitializeComponent()
        {
            Text = Server.Name == null ? "添加服务器" : "编辑服务器";
            ClientSize = new Size(560, 520);
            MinimumSize = new Size(560, 520);
            MaximumSize = new Size(560, 520);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.White;

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18, 16, 18, 10),
                ColumnCount = 4,
                RowCount = 10,
                BackColor = Color.White
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int i = 0; i < 8; i++)
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            cmbServerType = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbServerType.Items.AddRange(new object[] { "Windows / RDP", "Linux / SSH" });
            cmbServerType.SelectedIndexChanged += CmbServerType_SelectedIndexChanged;
            txtName = CreateTextBox();
            txtIP = CreateTextBox();
            txtPort = CreateTextBox();
            txtGroup = CreateTextBox();
            txtProvider = CreateTextBox();
            txtUsername = CreateTextBox();
            txtPassword = CreateTextBox();
            txtPassword.UseSystemPasswordChar = true;
            txtProviderUrl = CreateTextBox();
            txtRemark = CreateTextBox();
            txtRemark.Multiline = true;
            txtRemark.Height = 50;
            txtManagementPort = CreateTextBox();
            cmbManagement = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbManagement.Items.AddRange(new object[] { "自动选择（SSH 优先，WinRM 备用）", "SSH", "WinRM" });

            dtpPurchase = CreateDatePicker();
            dtpExpire = CreateDatePicker();

            AddField(layout, 0, 0, "类型", cmbServerType);
            AddField(layout, 0, 2, "名称", txtName);
            AddField(layout, 1, 0, "地址", txtIP);
            AddField(layout, 1, 2, "端口", txtPort);
            AddField(layout, 2, 0, "分组", txtGroup);
            AddField(layout, 2, 2, "云厂商", txtProvider);
            AddField(layout, 3, 0, "用户名", txtUsername);
            AddField(layout, 3, 2, "密码", txtPassword);
            AddField(layout, 4, 0, "远程管理", cmbManagement);
            AddField(layout, 4, 2, "管理端口", txtManagementPort);
            AddField(layout, 5, 0, "厂商网址", txtProviderUrl, 3);
            AddField(layout, 6, 0, "购买日期", dtpPurchase);
            AddField(layout, 6, 2, "到期日期", dtpExpire);
            AddField(layout, 7, 0, "备注", txtRemark, 3);

            Label credentialNote = new Label
            {
                Text = "密码由 Windows 凭据管理器保存，不写入服务器清单文件",
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(96, 105, 114),
                Padding = new Padding(0, 6, 0, 0)
            };
            layout.Controls.Add(credentialNote, 0, 8);
            layout.SetColumnSpan(credentialNote, 4);

            FlowLayoutPanel actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, 2, 0, 0)
            };
            Button save = CreateActionButton("保存", true, 90);
            Button cancel = CreateActionButton("取消", false, 80);
            save.Click += BtnSave_Click;
            cancel.Click += (sender, args) => DialogResult = DialogResult.Cancel;
            actions.Controls.Add(cancel);
            actions.Controls.Add(save);
            layout.Controls.Add(actions, 0, 9);
            layout.SetColumnSpan(actions, 4);

            Controls.Add(layout);
            AcceptButton = save;
            CancelButton = cancel;
        }

        private static TextBox CreateTextBox()
        {
            return new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
        }

        private static DateTimePicker CreateDatePicker()
        {
            return new DateTimePicker
            {
                Dock = DockStyle.Fill,
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "yyyy-MM-dd",
                ShowCheckBox = true,
                Checked = false
            };
        }

        private static Button CreateActionButton(string text, bool primary, int width)
        {
            Button button = new Button
            {
                Text = text,
                Width = width,
                Height = 30,
                Margin = new Padding(6, 0, 0, 0),
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Color.FromArgb(26, 137, 89) : Color.White,
                ForeColor = primary ? Color.White : Color.FromArgb(35, 42, 49),
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = primary ? Color.FromArgb(26, 137, 89) : Color.FromArgb(194, 201, 208);
            return button;
        }

        private static void AddField(TableLayoutPanel layout, int row, int labelColumn, string labelText, Control control, int span = 1)
        {
            Label label = new Label
            {
                Text = labelText,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(83, 92, 101)
            };
            layout.Controls.Add(label, labelColumn, row);
            layout.Controls.Add(control, labelColumn + 1, row);
            if (span > 1)
                layout.SetColumnSpan(control, span);
        }

        private void LoadData()
        {
            loading = true;
            Server.EnsureDefaults();
            cmbServerType.SelectedIndex = Server.Type == ServerType.Windows ? 0 : 1;
            txtName.Text = Server.Name;
            txtIP.Text = Server.IP;
            txtPort.Text = Server.Port;
            txtGroup.Text = Server.Group;
            txtProvider.Text = Server.Provider;
            txtProviderUrl.Text = Server.ProviderUrl;
            txtUsername.Text = Server.Username;
            txtManagementPort.Text = Server.ManagementPort;
            cmbManagement.SelectedIndex = (int)Server.ManagementType;
            txtPassword.Text = Server.Password;
            txtRemark.Text = Server.Remark;
            if (Server.PurchaseDate != DateTime.MinValue)
            {
                dtpPurchase.Value = Server.PurchaseDate;
                dtpPurchase.Checked = true;
            }
            if (Server.ExpireDate != DateTime.MinValue)
            {
                dtpExpire.Value = Server.ExpireDate;
                dtpExpire.Checked = true;
            }
            loading = false;
        }

        private void CmbServerType_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (loading || cmbServerType.SelectedIndex < 0)
                return;

            ServerType type = cmbServerType.SelectedIndex == 0 ? ServerType.Windows : ServerType.Linux;
            if (Server.Type == type)
                return;

            Server.Type = type;
            if (txtPort.Text == "3389" || txtPort.Text == "22" || string.IsNullOrWhiteSpace(txtPort.Text))
                txtPort.Text = Server.GetDefaultPort();
            if (txtUsername.Text == "Administrator" || txtUsername.Text == "root" || string.IsNullOrWhiteSpace(txtUsername.Text))
                txtUsername.Text = Server.GetDefaultUsername();
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            int port;
            if (string.IsNullOrWhiteSpace(txtName.Text))
            {
                ShowValidation("请输入服务器名称", txtName);
                return;
            }
            if (string.IsNullOrWhiteSpace(txtIP.Text))
            {
                ShowValidation("请输入服务器地址", txtIP);
                return;
            }
            if (!int.TryParse(txtPort.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                ShowValidation("端口必须是 1-65535 之间的数字", txtPort);
                return;
            }

            Server.Type = cmbServerType.SelectedIndex == 0 ? ServerType.Windows : ServerType.Linux;
            Server.Name = txtName.Text.Trim();
            Server.IP = txtIP.Text.Trim();
            Server.Port = port.ToString();
            Server.Group = string.IsNullOrWhiteSpace(txtGroup.Text) ? "未分组" : txtGroup.Text.Trim();
            Server.Provider = string.IsNullOrWhiteSpace(txtProvider.Text) ? "其他" : txtProvider.Text.Trim();
            Server.ProviderUrl = txtProviderUrl.Text.Trim();
            Server.Username = txtUsername.Text.Trim();
            Server.Password = txtPassword.Text;
            int managementPort;
            if (!int.TryParse(txtManagementPort.Text.Trim(), out managementPort) || managementPort < 1 || managementPort > 65535)
            {
                ShowValidation("管理端口必须是 1-65535 之间的数字", txtManagementPort);
                return;
            }
            Server.ManagementType = (RemoteManagementType)Math.Max(0, cmbManagement.SelectedIndex);
            Server.ManagementPort = managementPort.ToString();
            Server.Remark = txtRemark.Text.Trim();
            Server.PurchaseDate = dtpPurchase.Checked ? dtpPurchase.Value.Date : DateTime.MinValue;
            Server.ExpireDate = dtpExpire.Checked ? dtpExpire.Value.Date : DateTime.MinValue;
            Server.EnsureDefaults();
            DialogResult = DialogResult.OK;
        }

        private static void ShowValidation(string message, Control control)
        {
            MessageBox.Show(message, "信息不完整", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            control.Focus();
        }
    }
}
