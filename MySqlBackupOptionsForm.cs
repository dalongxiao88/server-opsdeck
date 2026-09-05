using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RDPManager
{
    public sealed class MySqlBackupOptions
    {
        public IList<string> DatabaseNames { get; set; } = new List<string>();
        public bool IncludeRoutines { get; set; } = true;
        public bool IncludeEvents { get; set; } = true;
        public bool IncludeTriggers { get; set; } = true;
        public bool OverwriteExistingTables { get; set; }
    }

    public sealed class MySqlBackupOptionsForm : Form
    {
        private readonly CheckedListBox databaseList;
        private readonly CheckBox routinesBox;
        private readonly CheckBox eventsBox;
        private readonly CheckBox triggersBox;
        private readonly CheckBox overwriteBox;

        public MySqlBackupOptions Options { get; private set; }

        public MySqlBackupOptionsForm(string databaseType, IList<string> databaseNames, bool migrationMode = false)
        {
            Text = "备份选项 · " + databaseType;
            ClientSize = new Size(560, 500);
            MinimumSize = new Size(500, 440);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F);

            Controls.Add(new Label
            {
                AutoSize = true,
                Text = "选择要备份的数据库",
                Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
                ForeColor = Color.FromArgb(35, 42, 49),
                Location = new Point(24, 18)
            });
            Controls.Add(new Label
            {
                AutoEllipsis = true,
                Size = new Size(500, 26),
                Text = "默认排除 mysql、sys、information_schema 和 performance_schema 系统库。",
                ForeColor = Color.FromArgb(104, 114, 124),
                Location = new Point(26, 52)
            });

            databaseList = new CheckedListBox
            {
                Location = new Point(26, 88),
                Size = new Size(500, 230),
                CheckOnClick = true,
                BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false
            };
            foreach (string name in databaseNames ?? new List<string>())
                databaseList.Items.Add(name, true);
            Controls.Add(databaseList);

            Button selectAll = CreateButton("全选", Color.FromArgb(42, 125, 185), 26, 330, 76);
            selectAll.Click += (sender, args) => SetAll(true);
            Controls.Add(selectAll);
            Button selectNone = CreateButton("清空", Color.FromArgb(104, 114, 124), 110, 330, 76);
            selectNone.Click += (sender, args) => SetAll(false);
            Controls.Add(selectNone);

            routinesBox = CreateCheckBox("包含存储过程", 26, 372, true);
            eventsBox = CreateCheckBox("包含事件", 176, 372, true);
            triggersBox = CreateCheckBox("包含触发器", 286, 372, true);
            overwriteBox = CreateCheckBox("迁移时覆盖本机同名表", 26, 402, false);
            overwriteBox.Visible = migrationMode;
            Controls.Add(routinesBox);
            Controls.Add(eventsBox);
            Controls.Add(triggersBox);
            Controls.Add(overwriteBox);

            Button cancel = CreateButton("取消", Color.FromArgb(104, 114, 124), 342, 450, 82);
            cancel.Click += (sender, args) => DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);
            Button confirm = CreateButton("选择保存位置", Color.FromArgb(26, 134, 87), 432, 450, 110);
            confirm.Click += Confirm_Click;
            Controls.Add(confirm);
            AcceptButton = confirm;
            CancelButton = cancel;
        }

        private void Confirm_Click(object sender, EventArgs e)
        {
            List<string> selected = databaseList.CheckedItems.Cast<object>().Select(item => Convert.ToString(item)).Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("至少选择一个数据库。", "未选择数据库", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Options = new MySqlBackupOptions
            {
                DatabaseNames = selected,
                IncludeRoutines = routinesBox.Checked,
                IncludeEvents = eventsBox.Checked,
                IncludeTriggers = triggersBox.Checked,
                OverwriteExistingTables = overwriteBox.Checked
            };
            DialogResult = DialogResult.OK;
        }

        private void SetAll(bool value)
        {
            for (int index = 0; index < databaseList.Items.Count; index++)
                databaseList.SetItemChecked(index, value);
        }

        private static CheckBox CreateCheckBox(string text, int x, int y, bool checkedValue)
        {
            return new CheckBox { AutoSize = true, Text = text, Checked = checkedValue, Location = new Point(x, y), ForeColor = Color.FromArgb(35, 42, 49) };
        }

        private static Button CreateButton(string text, Color color, int x, int y, int width)
        {
            Button button = new Button { Text = text, Width = width, Height = 34, Location = new Point(x, y), FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = color, UseVisualStyleBackColor = false, Cursor = Cursors.Hand };
            button.FlatAppearance.BorderColor = color;
            return button;
        }
    }
}
