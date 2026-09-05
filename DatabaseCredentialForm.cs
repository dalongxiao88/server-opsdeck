using System;
using System.Drawing;
using System.Windows.Forms;

namespace RDPManager
{
    public sealed class DatabaseCredentialForm : Form
    {
        private readonly string databaseType;
        private TextBox usernameBox;
        private TextBox passwordBox;
        private TextBox databaseBox;

        public string Username { get; private set; }
        public string Password { get; private set; }
        public string DatabaseName { get; private set; }

        public DatabaseCredentialForm(string databaseType, DatabaseCredentialRecord existing = null)
        {
            this.databaseType = databaseType ?? "数据库";
            InitializeComponent(existing);
        }

        private void InitializeComponent(DatabaseCredentialRecord existing)
        {
            Text = "验证数据库凭据 · " + databaseType;
            ClientSize = new Size(470, 330);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F);

            Label title = new Label
            {
                AutoSize = true,
                Text = databaseType + " 数据库凭据",
                Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
                ForeColor = Color.FromArgb(35, 42, 49),
                Location = new Point(24, 20)
            };
            Label note = new Label
            {
                AutoEllipsis = true,
                Size = new Size(415, 42),
                Text = "首次验证成功后，账号和密码将使用当前保险库加密保存。\n数据库连接通过 SSH 隧道，不要求开放公网端口。",
                ForeColor = Color.FromArgb(104, 114, 124),
                Location = new Point(26, 55)
            };
            string defaultUser = databaseType == "Redis" ? "default" : databaseType == "MongoDB" ? "admin" : "root";
            usernameBox = CreateTextBox(existing == null ? defaultUser : existing.Username, 26, 125);
            passwordBox = CreateTextBox("", 26, 180);
            passwordBox.UseSystemPasswordChar = true;
            databaseBox = CreateTextBox(existing == null ? "" : existing.DatabaseName, 26, 235);
            AddLabel("管理用户名", 26, 105);
            AddLabel("管理密码", 26, 160);
            AddLabel("默认数据库（可选）", 26, 215);
            Button cancel = CreateButton("取消", Color.FromArgb(104, 114, 124), 82);
            cancel.Location = new Point(220, 278);
            cancel.Click += (sender, args) => DialogResult = DialogResult.Cancel;
            Button confirm = CreateButton("测试连接并保存", Color.FromArgb(26, 134, 87), 130);
            confirm.Location = new Point(314, 278);
            confirm.Click += Confirm_Click;

            Controls.Add(title);
            Controls.Add(note);
            Controls.Add(usernameBox);
            Controls.Add(passwordBox);
            Controls.Add(databaseBox);
            Controls.Add(cancel);
            Controls.Add(confirm);
            AcceptButton = confirm;
            CancelButton = cancel;
        }

        private void AddLabel(string text, int x, int y)
        {
            Controls.Add(new Label
            {
                AutoSize = true,
                Text = text,
                ForeColor = Color.FromArgb(104, 114, 124),
                Location = new Point(x, y)
            });
        }

        private static TextBox CreateTextBox(string text, int x, int y)
        {
            return new TextBox
            {
                Location = new Point(x, y),
                Size = new Size(418, 27),
                Text = text ?? "",
                BorderStyle = BorderStyle.FixedSingle
            };
        }

        private static Button CreateButton(string text, Color color, int width)
        {
            Button button = new Button
            {
                Text = text,
                Width = width,
                Height = 34,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = color,
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = color;
            return button;
        }

        private void Confirm_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(usernameBox.Text))
            {
                MessageBox.Show("请输入数据库管理用户名。", "信息不完整", MessageBoxButtons.OK, MessageBoxIcon.Information);
                usernameBox.Focus();
                return;
            }
            if (string.IsNullOrEmpty(passwordBox.Text))
            {
                MessageBox.Show("请输入数据库管理密码。", "信息不完整", MessageBoxButtons.OK, MessageBoxIcon.Information);
                passwordBox.Focus();
                return;
            }
            Username = usernameBox.Text.Trim();
            Password = passwordBox.Text;
            DatabaseName = databaseBox.Text.Trim();
            DialogResult = DialogResult.OK;
        }
    }
}
