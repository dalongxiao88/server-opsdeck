using System;
using System.Drawing;
using System.Windows.Forms;

namespace RDPManager
{
    public sealed class RedisAclForm : Form
    {
        private readonly bool createMode;
        private readonly string username;
        private TextBox usernameBox;
        private TextBox passwordBox;
        private TextBox keyPatternBox;
        private CheckBox readBox;
        private CheckBox writeBox;
        private CheckBox connectionBox;
        private CheckBox transactionBox;
        private CheckBox pubSubBox;
        private CheckBox scriptingBox;
        private CheckBox adminBox;
        private CheckBox allCommandsBox;

        public RedisAclSelection Selection { get; private set; }

        public RedisAclForm(string username = null, RedisAclSelection existing = null)
        {
            createMode = string.IsNullOrWhiteSpace(username);
            this.username = username;
            Text = createMode ? "新建 Redis ACL 用户" : "编辑 Redis ACL 权限 · " + username;
            ClientSize = new Size(610, createMode ? 510 : 430);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F);
            InitializeComponent(existing);
        }

        private void InitializeComponent(RedisAclSelection existing)
        {
            Controls.Add(new Label
            {
                AutoSize = true,
                Text = createMode ? "新建 Redis ACL 用户" : "编辑 Redis ACL 权限",
                Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
                ForeColor = Color.FromArgb(35, 42, 49),
                Location = new Point(24, 18)
            });
            Controls.Add(new Label
            {
                AutoEllipsis = true,
                Size = new Size(550, 30),
                Text = createMode ? "创建完成后会使用新账号通过 SSH 隧道验证登录。" : "只修改命令和 Key 权限，不修改该用户密码。",
                ForeColor = Color.FromArgb(104, 114, 124),
                Location = new Point(26, 52)
            });
            AddLabel("用户名", 26, 96);
            usernameBox = Box(createMode ? "" : username, 26, 118, 250);
            usernameBox.ReadOnly = !createMode;
            Controls.Add(usernameBox);
            AddLabel("密码", 310, 96);
            passwordBox = Box("", 310, 118, 250);
            passwordBox.UseSystemPasswordChar = true;
            passwordBox.Visible = createMode;
            Controls.Add(passwordBox);
            if (createMode)
            {
                Button generate = Button("生成密码", Color.FromArgb(42, 125, 185), 310, 151, 100);
                generate.Click += (sender, args) => passwordBox.Text = GeneratePassword();
                Controls.Add(generate);
            }
            AddLabel("Key 范围", 26, 174);
            keyPatternBox = Box("*", 26, 196, 534);
            Controls.Add(keyPatternBox);
            Controls.Add(new Label { AutoEllipsis = true, Size = new Size(530, 25), Text = "例如：* 或 app:*；只允许访问匹配的 Key。", ForeColor = Color.FromArgb(104, 114, 124), Location = new Point(28, 228) });

            int y = createMode ? 270 : 258;
            Controls.Add(new Label { AutoSize = true, Text = "命令类别", Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), ForeColor = Color.FromArgb(35, 42, 49), Location = new Point(26, y) });
            y += 34;
            readBox = Check("只读命令（@read）", 28, y);
            writeBox = Check("写入命令（@write）", 210, y);
            connectionBox = Check("连接命令", 402, y);
            y += 32;
            transactionBox = Check("事务命令", 28, y);
            pubSubBox = Check("发布订阅", 210, y);
            scriptingBox = Check("脚本命令", 402, y);
            y += 32;
            adminBox = Check("管理命令（高风险）", 28, y);
            allCommandsBox = Check("全部命令（高风险）", 210, y);
            Controls.Add(readBox); Controls.Add(writeBox); Controls.Add(connectionBox); Controls.Add(transactionBox); Controls.Add(pubSubBox); Controls.Add(scriptingBox); Controls.Add(adminBox); Controls.Add(allCommandsBox);
            allCommandsBox.CheckedChanged += (sender, args) =>
            {
                bool enabled = !allCommandsBox.Checked;
                readBox.Enabled = enabled; writeBox.Enabled = enabled; connectionBox.Enabled = enabled;
                transactionBox.Enabled = enabled; pubSubBox.Enabled = enabled; scriptingBox.Enabled = enabled; adminBox.Enabled = enabled;
            };
            if (existing != null)
            {
                keyPatternBox.Text = string.IsNullOrWhiteSpace(existing.KeyPattern) ? "*" : existing.KeyPattern;
                readBox.Checked = existing.Read; writeBox.Checked = existing.Write; connectionBox.Checked = existing.Connection;
                transactionBox.Checked = existing.Transaction; pubSubBox.Checked = existing.PubSub; scriptingBox.Checked = existing.Scripting;
                adminBox.Checked = existing.Admin; allCommandsBox.Checked = existing.AllCommands;
            }

            int bottom = createMode ? 470 : 390;
            Button cancel = Button("取消", Color.FromArgb(104, 114, 124), 390, bottom, 82);
            cancel.Click += (sender, args) => DialogResult = DialogResult.Cancel;
            Button apply = Button(createMode ? "创建并验证" : "应用权限", createMode ? Color.FromArgb(26, 134, 87) : Color.FromArgb(116, 86, 166), 480, bottom, 110);
            apply.Click += Apply_Click;
            Controls.Add(cancel); Controls.Add(apply);
            AcceptButton = apply; CancelButton = cancel;
        }

        private void Apply_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(usernameBox.Text) || (createMode && string.IsNullOrEmpty(passwordBox.Text)))
            {
                MessageBox.Show("用户名和密码不能为空。", "信息不完整", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Selection = new RedisAclSelection
            {
                Username = usernameBox.Text.Trim(),
                Password = passwordBox.Text,
                KeyPattern = keyPatternBox.Text.Trim(),
                Read = readBox.Checked,
                Write = writeBox.Checked,
                Connection = connectionBox.Checked,
                Transaction = transactionBox.Checked,
                PubSub = pubSubBox.Checked,
                Scripting = scriptingBox.Checked,
                Admin = adminBox.Checked,
                AllCommands = allCommandsBox.Checked
            };
            DialogResult = DialogResult.OK;
        }

        private void AddLabel(string text, int x, int y) => Controls.Add(new Label { AutoSize = true, Text = text, ForeColor = Color.FromArgb(104, 114, 124), Location = new Point(x, y) });
        private static TextBox Box(string text, int x, int y, int width) => new TextBox { Text = text, Location = new Point(x, y), Width = width, Height = 27, BorderStyle = BorderStyle.FixedSingle };
        private static CheckBox Check(string text, int x, int y) => new CheckBox { AutoSize = true, Text = text, Location = new Point(x, y), ForeColor = Color.FromArgb(35, 42, 49) };
        private static Button Button(string text, Color color, int x, int y, int width) { Button b = new Button { Text = text, Width = width, Height = 34, Location = new Point(x, y), FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = color, UseVisualStyleBackColor = false, Cursor = Cursors.Hand }; b.FlatAppearance.BorderColor = color; return b; }
        private static string GeneratePassword()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%";
            byte[] random = System.Security.Cryptography.RandomNumberGenerator.GetBytes(18);
            char[] value = new char[18];
            for (int index = 0; index < value.Length; index++) value[index] = chars[random[index] % chars.Length];
            return new string(value);
        }
    }
}
