using System.Drawing;
using System.Windows.Forms;

namespace RDPManager
{
    public sealed class StorageModeForm : Form
    {
        private readonly RadioButton plainOption;
        private readonly RadioButton vaultOption;
        private readonly Button confirmButton;

        public StorageMode SelectedMode { get; private set; }

        public StorageModeForm(bool migration)
        {
            AutoScaleMode = AutoScaleMode.None;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(600, 370);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F);
            Text = migration ? "选择服务器资料存储模式" : "选择存储模式";

            Label title = new Label
            {
                AutoSize = true,
                Text = "请选择服务器资料的存储方式",
                Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
                ForeColor = Color.FromArgb(35, 42, 49),
                Location = new Point(24, 20)
            };
            Label subtitle = new Label
            {
                AutoEllipsis = true,
                Size = new Size(550, 42),
                Text = migration
                    ? "检测到已有服务器资料。选择后程序会将现有资料转换为对应模式。"
                    : "未选择存储模式前不能进入管理器，请仔细阅读两种模式的区别。",
                ForeColor = Color.FromArgb(105, 115, 125),
                Location = new Point(26, 54)
            };

            plainOption = CreateOption(
                "明文兼容模式（不推荐）",
                "服务器信息和密码直接保存到 servers.xml，便于查看和兼容，但读取文件即可看到全部凭据。",
                new Point(26, 108));
            vaultOption = CreateOption(
                "加密保险库模式（推荐）",
                "所有服务器信息使用 AES-256-GCM 加密保存到 servers.vault，主密码同时用于解锁工具和保险库。",
                new Point(26, 196));
            plainOption.CheckedChanged += OptionChanged;
            vaultOption.CheckedChanged += OptionChanged;

            Label note = new Label
            {
                AutoEllipsis = true,
                Size = new Size(550, 44),
                Text = "注意：忘记加密模式主密码后无法恢复保险库；强制重置会删除全部服务器资料。",
                ForeColor = Color.FromArgb(184, 62, 62),
                Location = new Point(26, 282)
            };
            confirmButton = CreateButton("确定选择", Color.FromArgb(26, 134, 87), 100);
            confirmButton.Location = new Point(388, 326);
            confirmButton.Enabled = false;
            confirmButton.Click += ConfirmButton_Click;
            Button cancel = CreateButton("取消", Color.FromArgb(105, 115, 125), 80);
            cancel.Location = new Point(500, 326);
            cancel.Click += (sender, args) => DialogResult = DialogResult.Cancel;

            Controls.Add(title);
            Controls.Add(subtitle);
            Controls.Add(plainOption);
            Controls.Add(vaultOption);
            Controls.Add(note);
            Controls.Add(confirmButton);
            Controls.Add(cancel);
            CancelButton = cancel;
        }

        private RadioButton CreateOption(string title, string description, Point location)
        {
            RadioButton option = new RadioButton
            {
                AutoSize = false,
                Size = new Size(548, 72),
                Location = location,
                Text = title + "\r\n" + description,
                Font = new Font("Microsoft YaHei UI", 9F),
                ForeColor = Color.FromArgb(35, 42, 49),
                TextAlign = ContentAlignment.MiddleLeft,
                UseVisualStyleBackColor = true
            };
            return option;
        }

        private void OptionChanged(object sender, System.EventArgs e)
        {
            confirmButton.Enabled = plainOption.Checked || vaultOption.Checked;
        }

        private void ConfirmButton_Click(object sender, System.EventArgs e)
        {
            SelectedMode = vaultOption.Checked ? StorageMode.EncryptedVault : StorageMode.PlainXml;
            DialogResult = DialogResult.OK;
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
