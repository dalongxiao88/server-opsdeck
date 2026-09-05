using System;
using System.Drawing;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace RDPManager
{
    public sealed class MySqlResetPasswordForm : Form
    {
        private TextBox passwordBox;
        private TextBox confirmBox;

        public string NewPassword { get; private set; }

        public MySqlResetPasswordForm(string username, string hostPattern)
        {
            Text = "重置密码 · " + username + "@" + hostPattern;
            ClientSize = new Size(500, 300);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F);
            InitializeComponent(username, hostPattern);
        }

        private void InitializeComponent(string username, string hostPattern)
        {
            Controls.Add(new Label
            {
                AutoSize = true,
                Text = "重置 MySQL 用户密码",
                Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
                ForeColor = Color.FromArgb(35, 42, 49),
                Location = new Point(24, 20)
            });
            Controls.Add(new Label
            {
                AutoEllipsis = true,
                Size = new Size(440, 30),
                Text = "目标用户：" + username + "@" + hostPattern + "\n重置后将使用新密码测试登录并更新保险库。",
                ForeColor = Color.FromArgb(104, 114, 124),
                Location = new Point(26, 57)
            });
            Controls.Add(new Label { AutoSize = true, Text = "新密码", ForeColor = Color.FromArgb(104, 114, 124), Location = new Point(26, 116) });
            passwordBox = CreateBox(26, 140);
            passwordBox.UseSystemPasswordChar = true;
            Controls.Add(passwordBox);
            Button generate = CreateButton("生成密码", Color.FromArgb(42, 125, 185), 358, 138, 100);
            generate.Click += (sender, args) => passwordBox.Text = GeneratePassword();
            Controls.Add(generate);
            Controls.Add(new Label { AutoSize = true, Text = "确认新密码", ForeColor = Color.FromArgb(104, 114, 124), Location = new Point(26, 178) });
            confirmBox = CreateBox(26, 202);
            confirmBox.UseSystemPasswordChar = true;
            Controls.Add(confirmBox);
            Button cancel = CreateButton("取消", Color.FromArgb(104, 114, 124), 296, 250, 82);
            cancel.Click += (sender, args) => DialogResult = DialogResult.Cancel;
            Button confirm = CreateButton("重置并验证", Color.FromArgb(210, 125, 26), 386, 250, 100);
            confirm.Click += Confirm_Click;
            Controls.Add(cancel);
            Controls.Add(confirm);
            AcceptButton = confirm;
            CancelButton = cancel;
        }

        private void Confirm_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(passwordBox.Text))
            {
                MessageBox.Show("请输入新密码。", "信息不完整", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!string.Equals(passwordBox.Text, confirmBox.Text, StringComparison.Ordinal))
            {
                MessageBox.Show("两次输入的密码不一致。", "密码不一致", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            NewPassword = passwordBox.Text;
            DialogResult = DialogResult.OK;
        }

        private static TextBox CreateBox(int x, int y)
        {
            return new TextBox { Location = new Point(x, y), Size = new Size(310, 27), BorderStyle = BorderStyle.FixedSingle };
        }

        private static Button CreateButton(string text, Color color, int x, int y, int width)
        {
            Button button = new Button { Text = text, Width = width, Height = 34, Location = new Point(x, y), FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = color, UseVisualStyleBackColor = false, Cursor = Cursors.Hand };
            button.FlatAppearance.BorderColor = color;
            return button;
        }

        private static string GeneratePassword()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%";
            byte[] random = RandomNumberGenerator.GetBytes(18);
            char[] value = new char[18];
            for (int index = 0; index < value.Length; index++)
                value[index] = chars[random[index] % chars.Length];
            return new string(value);
        }
    }
}
