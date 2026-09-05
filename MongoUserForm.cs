using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace RDPManager
{
    public sealed class MongoUserForm : Form
    {
        private readonly bool createMode;
        private readonly string existingUser;
        private TextBox usernameBox;
        private TextBox passwordBox;
        private TextBox confirmPasswordBox;
        private TextBox authDbBox;
        private TextBox databaseBox;
        private CheckBox readBox;
        private CheckBox readWriteBox;
        private CheckBox dbAdminBox;
        private CheckBox userAdminBox;
        private CheckBox clusterAdminBox;

        public MongoUserRequest Request { get; private set; }

        public MongoUserForm(string username = null, MongoUserRequest existing = null)
        {
            createMode = string.IsNullOrWhiteSpace(username);
            existingUser = username;
            Text = createMode ? "新建 MongoDB 用户" : "编辑 MongoDB 角色 · " + username;
            ClientSize = new Size(650, createMode ? 560 : 470);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F);
            InitializeComponent(existing);
        }

        private void InitializeComponent(MongoUserRequest existing)
        {
            Controls.Add(new Label { AutoSize = true, Text = createMode ? "新建 MongoDB 用户" : "编辑 MongoDB 角色", Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold), ForeColor = Color.FromArgb(35, 42, 49), Location = new Point(24, 18) });
            Controls.Add(new Label { AutoEllipsis = true, Size = new Size(580, 30), Text = createMode ? "创建完成后会用新账号重新连接验证，成功后保存到保险库。" : "MongoDB 使用角色授权，角色来自认证数据库和目标数据库。", ForeColor = Color.FromArgb(104, 114, 124), Location = new Point(26, 52) });
            AddLabel("用户名", 26, 94);
            usernameBox = Box(createMode ? "" : existingUser, 26, 116, 250);
            usernameBox.ReadOnly = !createMode;
            Controls.Add(usernameBox);
            AddLabel("认证数据库", 310, 94);
            authDbBox = Box(existing == null ? "admin" : existing.AuthenticationDatabase, 310, 116, 250);
            authDbBox.ReadOnly = !createMode;
            Controls.Add(authDbBox);
            AddLabel("密码", 26, 154);
            passwordBox = Box("", 26, 176, 250);
            passwordBox.UseSystemPasswordChar = true;
            passwordBox.Visible = createMode;
            Controls.Add(passwordBox);
            if (createMode)
            {
                Button generate = Button("生成密码", Color.FromArgb(42, 125, 185), 310, 174, 100);
                generate.Click += (sender, args) => passwordBox.Text = GeneratePassword();
                Controls.Add(generate);
                AddLabel("确认密码", 26, 213);
                confirmPasswordBox = Box("", 26, 235, 250);
                confirmPasswordBox.UseSystemPasswordChar = true;
                Controls.Add(confirmPasswordBox);
            }
            AddLabel("目标数据库", 310, 154);
            databaseBox = Box(existing == null ? "" : existing.DatabaseName, 310, 176, 250);
            databaseBox.PlaceholderText = "例如：app_db";
            Controls.Add(databaseBox);

            int top = createMode ? 285 : 245;
            Controls.Add(new Label { AutoSize = true, Text = "角色选择", Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), ForeColor = Color.FromArgb(35, 42, 49), Location = new Point(26, top) });
            Controls.Add(new Label { AutoEllipsis = true, Size = new Size(565, 28), Text = "默认不授予集群管理权限；高权限角色会在提交前再次确认。", ForeColor = Color.FromArgb(210, 125, 26), Location = new Point(28, top + 30) });
            readBox = Check("read", 28, top + 70);
            readWriteBox = Check("readWrite", 150, top + 70);
            dbAdminBox = Check("dbAdmin", 300, top + 70);
            userAdminBox = Check("userAdmin", 430, top + 70);
            clusterAdminBox = Check("clusterAdmin（高风险）", 28, top + 105);
            Controls.Add(readBox); Controls.Add(readWriteBox); Controls.Add(dbAdminBox); Controls.Add(userAdminBox); Controls.Add(clusterAdminBox);
            if (existing != null && existing.Roles != null)
            {
                readBox.Checked = existing.Roles.Any(role => role.StartsWith("read@", StringComparison.OrdinalIgnoreCase));
                readWriteBox.Checked = existing.Roles.Any(role => role.StartsWith("readWrite@", StringComparison.OrdinalIgnoreCase));
                dbAdminBox.Checked = existing.Roles.Any(role => role.StartsWith("dbAdmin@", StringComparison.OrdinalIgnoreCase));
                userAdminBox.Checked = existing.Roles.Any(role => role.StartsWith("userAdmin@", StringComparison.OrdinalIgnoreCase));
                clusterAdminBox.Checked = existing.Roles.Any(role => role.StartsWith("clusterAdmin@", StringComparison.OrdinalIgnoreCase));
            }
            int bottom = createMode ? 505 : 415;
            Button cancel = Button("取消", Color.FromArgb(104, 114, 124), 430, bottom, 82);
            cancel.Click += (sender, args) => DialogResult = DialogResult.Cancel;
            Button confirm = Button(createMode ? "创建并验证" : "应用角色", createMode ? Color.FromArgb(26, 134, 87) : Color.FromArgb(116, 86, 166), 520, bottom, 110);
            confirm.Click += Confirm_Click;
            Controls.Add(cancel); Controls.Add(confirm);
            AcceptButton = confirm; CancelButton = cancel;
        }

        private void Confirm_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(usernameBox.Text) ||
                (createMode && (string.IsNullOrEmpty(passwordBox.Text) || passwordBox.Text != confirmPasswordBox.Text)))
            {
                MessageBox.Show("用户名和密码不能为空，且两次密码必须一致。", "信息不完整", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            List<string> roles = new List<string>();
            string database = databaseBox.Text.Trim();
            if (readBox.Checked) roles.Add("read@" + database);
            if (readWriteBox.Checked) roles.Add("readWrite@" + database);
            if (dbAdminBox.Checked) roles.Add("dbAdmin@" + database);
            if (userAdminBox.Checked) roles.Add("userAdmin@" + database);
            if (clusterAdminBox.Checked) roles.Add("clusterAdmin@admin");
            Request = new MongoUserRequest
            {
                UserName = usernameBox.Text.Trim(),
                Password = passwordBox.Text,
                AuthenticationDatabase = string.IsNullOrWhiteSpace(authDbBox.Text) ? "admin" : authDbBox.Text.Trim(),
                DatabaseName = database,
                Roles = roles
            };
            DialogResult = DialogResult.OK;
        }

        private void AddLabel(string text, int x, int y) => Controls.Add(new Label { AutoSize = true, Text = text, ForeColor = Color.FromArgb(104, 114, 124), Location = new Point(x, y) });
        private static TextBox Box(string text, int x, int y, int width) => new TextBox { Text = text ?? "", Location = new Point(x, y), Width = width, Height = 27, BorderStyle = BorderStyle.FixedSingle };
        private static CheckBox Check(string text, int x, int y) => new CheckBox { AutoSize = true, Text = text, Location = new Point(x, y), ForeColor = Color.FromArgb(35, 42, 49) };
        private static Button Button(string text, Color color, int x, int y, int width) { Button b = new Button { Text = text, Width = width, Height = 34, Location = new Point(x, y), FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = color, UseVisualStyleBackColor = false, Cursor = Cursors.Hand }; b.FlatAppearance.BorderColor = color; return b; }
        private static string GeneratePassword() { const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%"; byte[] random = RandomNumberGenerator.GetBytes(18); char[] value = new char[18]; for (int i = 0; i < value.Length; i++) value[i] = chars[random[i] % chars.Length]; return new string(value); }
    }
}
