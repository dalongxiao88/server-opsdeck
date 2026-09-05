using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RDPManager
{
    public sealed class MySqlPermissionForm : Form
    {
        private readonly List<MySqlGrantScope> scopes;
        private ComboBox scopeBox;
        private CheckBox allBox;
        private CheckBox selectBox;
        private CheckBox insertBox;
        private CheckBox updateBox;
        private CheckBox deleteBox;
        private CheckBox createBox;
        private CheckBox alterBox;
        private CheckBox executeBox;
        private CheckBox grantBox;

        public MySqlGrantScope Result { get; private set; }

        public MySqlPermissionForm(string username, string hostPattern, IList<MySqlGrantScope> grantScopes)
        {
            scopes = grantScopes == null ? new List<MySqlGrantScope>() : grantScopes.ToList();
            Text = "编辑权限 · " + username + "@" + hostPattern;
            ClientSize = new Size(650, 470);
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
                Text = "编辑用户权限",
                Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
                ForeColor = Color.FromArgb(35, 42, 49),
                Location = new Point(24, 18)
            });
            Controls.Add(new Label
            {
                AutoEllipsis = true,
                Size = new Size(585, 26),
                Text = username + "@" + hostPattern + " · 选择一个授权范围进行修改",
                ForeColor = Color.FromArgb(104, 114, 124),
                Location = new Point(26, 52)
            });
            Controls.Add(new Label { AutoSize = true, Text = "授权范围", ForeColor = Color.FromArgb(104, 114, 124), Location = new Point(26, 92) });
            scopeBox = new ComboBox
            {
                Location = new Point(26, 116),
                Width = 564,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            foreach (MySqlGrantScope scope in scopes)
                scopeBox.Items.Add(scope);
            if (scopeBox.Items.Count == 0)
            {
                MySqlGrantScope empty = new MySqlGrantScope
                {
                    DatabaseName = "*",
                    ScopeText = "*.*",
                    IsEditable = true
                };
                scopes.Add(empty);
                scopeBox.Items.Add(empty);
            }
            scopeBox.SelectedIndexChanged += (sender, args) => LoadScope(scopeBox.SelectedItem as MySqlGrantScope);
            Controls.Add(scopeBox);

            Controls.Add(new Label
            {
                AutoEllipsis = true,
                Size = new Size(565, 30),
                Text = "本次只修改所选授权范围；取消全部权限将清除该范围的授权。",
                ForeColor = Color.FromArgb(210, 125, 26),
                Location = new Point(28, 155)
            });
            allBox = AddCheckBox("全部权限", 28, 200);
            selectBox = AddCheckBox("读取数据", 160, 200);
            insertBox = AddCheckBox("新增数据", 292, 200);
            updateBox = AddCheckBox("修改数据", 424, 200);
            deleteBox = AddCheckBox("删除数据", 28, 235);
            createBox = AddCheckBox("创建表", 160, 235);
            alterBox = AddCheckBox("修改表结构", 292, 235);
            executeBox = AddCheckBox("执行存储过程", 424, 235);
            grantBox = AddCheckBox("继续授权", 28, 270);
            allBox.CheckedChanged += (sender, args) =>
            {
                bool enabled = !allBox.Checked;
                selectBox.Enabled = enabled;
                insertBox.Enabled = enabled;
                updateBox.Enabled = enabled;
                deleteBox.Enabled = enabled;
                createBox.Enabled = enabled;
                alterBox.Enabled = enabled;
                executeBox.Enabled = enabled;
            };

            Button cancel = CreateButton("取消", Color.FromArgb(104, 114, 124), 390, 405, 92);
            cancel.Click += (sender, args) => DialogResult = DialogResult.Cancel;
            Button apply = CreateButton("应用权限", Color.FromArgb(116, 86, 166), 492, 405, 110);
            apply.Click += Apply_Click;
            Controls.Add(cancel);
            Controls.Add(apply);
            AcceptButton = apply;
            CancelButton = cancel;
            if (scopeBox.Items.Count > 0)
                scopeBox.SelectedIndex = 0;
        }

        private CheckBox AddCheckBox(string text, int x, int y)
        {
            CheckBox box = new CheckBox
            {
                AutoSize = true,
                Text = text,
                ForeColor = Color.FromArgb(35, 42, 49),
                Location = new Point(x, y)
            };
            Controls.Add(box);
            return box;
        }

        private void LoadScope(MySqlGrantScope scope)
        {
            if (scope == null)
                return;
            bool editable = scope.IsEditable;
            allBox.Enabled = editable;
            grantBox.Enabled = editable;
            allBox.Checked = scope.AllPrivileges;
            selectBox.Checked = scope.Select;
            insertBox.Checked = scope.Insert;
            updateBox.Checked = scope.Update;
            deleteBox.Checked = scope.Delete;
            createBox.Checked = scope.Create;
            alterBox.Checked = scope.Alter;
            executeBox.Checked = scope.Execute;
            grantBox.Checked = scope.GrantOption;
            selectBox.Enabled = editable && !allBox.Checked;
            insertBox.Enabled = editable && !allBox.Checked;
            updateBox.Enabled = editable && !allBox.Checked;
            deleteBox.Enabled = editable && !allBox.Checked;
            createBox.Enabled = editable && !allBox.Checked;
            alterBox.Enabled = editable && !allBox.Checked;
            executeBox.Enabled = editable && !allBox.Checked;
        }

        private void Apply_Click(object sender, EventArgs e)
        {
            MySqlGrantScope selected = scopeBox.SelectedItem as MySqlGrantScope;
            if (selected == null || !selected.IsEditable)
            {
                MessageBox.Show("当前授权范围暂不支持编辑。", "无法编辑", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Result = new MySqlGrantScope
            {
                DatabaseName = selected.DatabaseName,
                ScopeText = selected.ScopeText,
                IsEditable = true,
                AllPrivileges = allBox.Checked,
                Select = selectBox.Checked,
                Insert = insertBox.Checked,
                Update = updateBox.Checked,
                Delete = deleteBox.Checked,
                Create = createBox.Checked,
                Alter = alterBox.Checked,
                Execute = executeBox.Checked,
                GrantOption = grantBox.Checked
            };
            DialogResult = DialogResult.OK;
        }

        private static Button CreateButton(string text, Color color, int x, int y, int width)
        {
            Button button = new Button
            {
                Text = text,
                Width = width,
                Height = 34,
                Location = new Point(x, y),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = color,
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = color;
            return button;
        }
    }
}
