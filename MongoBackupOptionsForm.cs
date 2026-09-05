using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RDPManager
{
    public sealed class MongoBackupOptions
    {
        public string DatabaseName { get; set; }
        public bool IncludeUsersAndRoles { get; set; } = true;
    }

    public sealed class MongoBackupOptionsForm : Form
    {
        private readonly ComboBox databaseBox;
        private readonly CheckBox usersRolesBox;
        public MongoBackupOptions Options { get; private set; }

        public MongoBackupOptionsForm(IList<string> databases)
        {
            Text = "MongoDB 备份选项";
            ClientSize = new Size(520, 280);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F);
            Controls.Add(new Label { AutoSize = true, Text = "选择 MongoDB 备份范围", Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold), ForeColor = Color.FromArgb(35,42,49), Location = new Point(24,20) });
            Controls.Add(new Label { AutoEllipsis = true, Size = new Size(450, 28), Text = "MongoDB 使用 BSON archive；系统数据库默认不列出。", ForeColor = Color.FromArgb(104,114,124), Location = new Point(26,55) });
            Controls.Add(new Label { AutoSize = true, Text = "数据库", ForeColor = Color.FromArgb(104,114,124), Location = new Point(26,98) });
            databaseBox = new ComboBox { Location = new Point(26,120), Width = 430, DropDownStyle = ComboBoxStyle.DropDownList };
            databaseBox.Items.Add("全部非系统数据库");
            foreach (string database in databases ?? new List<string>()) databaseBox.Items.Add(database);
            databaseBox.SelectedIndex = 0;
            Controls.Add(databaseBox);
            usersRolesBox = new CheckBox { AutoSize = true, Text = "包含用户和角色信息（单库备份时）", Checked = true, Location = new Point(26,164), ForeColor = Color.FromArgb(35,42,49) };
            Controls.Add(usersRolesBox);
            Button cancel = CreateButton("取消", Color.FromArgb(104,114,124), 330, 220, 78); cancel.Click += (s,e) => DialogResult = DialogResult.Cancel; Controls.Add(cancel);
            Button confirm = CreateButton("选择保存位置", Color.FromArgb(26,134,87), 416, 220, 90); confirm.Click += (s,e) => { Options = new MongoBackupOptions { DatabaseName = databaseBox.SelectedIndex <= 0 ? "" : Convert.ToString(databaseBox.SelectedItem), IncludeUsersAndRoles = usersRolesBox.Checked }; DialogResult = DialogResult.OK; }; Controls.Add(confirm);
            AcceptButton = confirm; CancelButton = cancel;
        }
        private static Button CreateButton(string text, Color color, int x, int y, int width) { Button b = new Button { Text=text, Width=width, Height=34, Location=new Point(x,y), FlatStyle=FlatStyle.Flat, BackColor=Color.White, ForeColor=color, UseVisualStyleBackColor=false, Cursor=Cursors.Hand }; b.FlatAppearance.BorderColor=color; return b; }
    }
}
