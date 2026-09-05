using System;
using System.Drawing;
using System.Windows.Forms;

namespace RDPManager
{
    public sealed class StartupPasswordForm : Form
    {
        private readonly bool firstRun;
        private readonly string currentHash;
        private readonly Func<string, bool> unlockValidator;
        private TextBox passwordBox;
        private TextBox confirmBox;
        private Label confirmLabel;
        private Label hintLabel;
        private Button confirmButton;
        private Button resetButton;

        public string PasswordHash { get; private set; }
        public string Password { get; private set; }
        public bool ResetRequested { get; private set; }

        private StartupPasswordForm(bool firstRun, string currentHash, Func<string, bool> unlockValidator = null)
        {
            this.firstRun = firstRun;
            this.currentHash = currentHash;
            this.unlockValidator = unlockValidator;
            InitializeComponent();
        }

        public static StartupPasswordForm CreateFirstRun()
        {
            return new StartupPasswordForm(true, null);
        }

        public static StartupPasswordForm CreateUnlock(string currentHash)
        {
            return new StartupPasswordForm(false, currentHash, null);
        }

        public static StartupPasswordForm CreateUnlock(Func<string, bool> unlockValidator)
        {
            return new StartupPasswordForm(false, null, unlockValidator);
        }

        private void InitializeComponent()
        {
            Text = firstRun ? "首次启动设置" : "解锁服务器管理器";
            AutoScaleMode = AutoScaleMode.None;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(430, firstRun ? 205 : 170);
            AutoSize = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F);
            ShowInTaskbar = true;

            Label title = new Label
            {
                Text = firstRun ? "设置管理密码" : "请输入管理密码",
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold),
                ForeColor = Color.FromArgb(35, 42, 49),
                Location = new Point(24, 20)
            };
            hintLabel = new Label
            {
                Text = firstRun ? "用于保护服务器列表和远程连接操作" : "验证通过后才能进入服务器管理器",
                AutoSize = true,
                ForeColor = Color.FromArgb(105, 115, 125),
                Location = new Point(26, 52)
            };
            Label passwordLabel = new Label
            {
                Text = firstRun ? "新密码" : "管理密码",
                AutoSize = true,
                ForeColor = Color.FromArgb(75, 84, 93),
                Location = new Point(26, 88)
            };
            passwordBox = new TextBox
            {
                Location = new Point(108, 84),
                Size = new Size(285, 26),
                UseSystemPasswordChar = true,
                BorderStyle = BorderStyle.FixedSingle
            };
            passwordBox.KeyDown += PasswordBox_KeyDown;

            confirmLabel = new Label
            {
                Text = "确认密码",
                AutoSize = true,
                ForeColor = Color.FromArgb(75, 84, 93),
                Location = new Point(26, 124),
                Visible = firstRun
            };
            confirmBox = new TextBox
            {
                Location = new Point(108, 120),
                Size = new Size(285, 26),
                UseSystemPasswordChar = true,
                BorderStyle = BorderStyle.FixedSingle,
                Visible = firstRun
            };
            confirmBox.KeyDown += PasswordBox_KeyDown;

            confirmButton = CreateButton(firstRun ? "下一步" : "验证进入", Color.FromArgb(26, 134, 87), 108);
            confirmButton.Location = new Point(194, firstRun ? 158 : 122);
            confirmButton.Click += ConfirmButton_Click;
            Button cancelButton = CreateButton("取消", Color.FromArgb(104, 114, 124), 78);
            cancelButton.Location = new Point(310, firstRun ? 158 : 122);
            cancelButton.Click += (sender, args) => DialogResult = DialogResult.Cancel;

            resetButton = CreateButton("重置管理密码", Color.FromArgb(184, 62, 62), 112);
            resetButton.Location = new Point(26, firstRun ? 158 : 122);
            resetButton.Visible = !firstRun;
            resetButton.Click += ResetButton_Click;

            Controls.Add(title);
            Controls.Add(hintLabel);
            Controls.Add(passwordLabel);
            Controls.Add(passwordBox);
            Controls.Add(confirmLabel);
            Controls.Add(confirmBox);
            Controls.Add(confirmButton);
            Controls.Add(cancelButton);
            Controls.Add(resetButton);
            AcceptButton = confirmButton;
            CancelButton = cancelButton;
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

        private void ConfirmButton_Click(object sender, EventArgs e)
        {
            if (firstRun)
            {
                if (passwordBox.Text.Length < 6)
                {
                    MessageBox.Show("管理密码至少需要 6 位", "密码太短", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    passwordBox.Focus();
                    return;
                }
                if (passwordBox.Text != confirmBox.Text)
                {
                    MessageBox.Show("两次输入的密码不一致", "设置失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    confirmBox.Focus();
                    return;
                }
                PasswordHash = PasswordSecurity.Hash(passwordBox.Text);
                Password = passwordBox.Text;
                DialogResult = DialogResult.OK;
                return;
            }

            bool valid = unlockValidator != null
                ? unlockValidator(passwordBox.Text)
                : PasswordSecurity.Verify(passwordBox.Text, currentHash);
            if (!valid)
            {
                MessageBox.Show("管理密码错误", "验证失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                passwordBox.SelectAll();
                passwordBox.Focus();
                return;
            }
            PasswordHash = currentHash;
            Password = passwordBox.Text;
            DialogResult = DialogResult.OK;
        }

        private void ResetButton_Click(object sender, EventArgs e)
        {
            DialogResult result = MessageBox.Show(
                "忘记密码时使用此功能。重置后将删除 servers.xml、servers.vault 以及全部服务器凭据。\n\n服务器列表、密码和软件配置均不会保留，下次启动将按首次使用处理。\n\n确定彻底重置吗？",
                "确认重置",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (result == DialogResult.Yes)
            {
                ResetRequested = true;
                DialogResult = DialogResult.OK;
            }
        }

        private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
                ConfirmButton_Click(sender, e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            passwordBox.Focus();
        }
    }
}
