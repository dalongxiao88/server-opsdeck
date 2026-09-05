using System;
using System.Drawing;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace RDPManager
{
    public sealed class MySqlUserForm : Form
    {
        private TextBox usernameBox;
        private TextBox passwordBox;
        private TextBox confirmPasswordBox;
        private ComboBox hostBox;
        private TextBox databaseBox;
        private CheckBox allBox;
        private CheckBox selectBox;
        private CheckBox insertBox;
        private CheckBox updateBox;
        private CheckBox deleteBox;
        private CheckBox createBox;
        private CheckBox alterBox;
        private CheckBox executeBox;
        private CheckBox grantBox;

        public MySqlUserRequest Request { get; private set; }

        public MySqlUserForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Text = "新建 MySQL 用户";
            ClientSize = new Size(650, 570);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F);

            Controls.Add(Heading("新建 MySQL / MariaDB 用户", 24, 18, 14F));
            Controls.Add(Note("创建成功后会立即使用新账号测试登录，验证通过才保存到保险库。", 26, 50));
            AddLabel("用户名", 26, 90);
            AddLabel("密码", 26, 145);
            AddLabel("确认密码", 330, 145);
            AddLabel("来源主机", 26, 200);
            AddLabel("授权数据库（非全部权限时必填）", 330, 200);
            usernameBox = TextBoxAt(26, 112, 260);
            passwordBox = TextBoxAt(26, 167, 260);
            confirmPasswordBox = TextBoxAt(330, 167, 260);
            passwordBox.UseSystemPasswordChar = true;
            confirmPasswordBox.UseSystemPasswordChar = true;
            hostBox = new ComboBox { Location = new Point(26, 222), Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
            hostBox.Items.AddRange(new object[] { "localhost", "127.0.0.1", "%" });
            hostBox.SelectedIndex = 0;
            databaseBox = TextBoxAt(330, 222, 260);
            databaseBox.PlaceholderText = "例如：app_db";
            Controls.Add(usernameBox);
            Controls.Add(passwordBox);
            Controls.Add(confirmPasswordBox);
            Controls.Add(hostBox);
            Controls.Add(databaseBox);
            Button generate = ButtonAt("生成密码", Color.FromArgb(42, 125, 185), 26, 257, 100);
            generate.Click += (sender, args) => passwordBox.Text = GeneratePassword();
            Controls.Add(generate);

            Controls.Add(Heading("权限选择", 26, 300, 10F));
            Controls.Add(Note("默认不勾选高危权限；“全部权限”和“授权其他用户”需要明确确认。", 28, 329));
            allBox = CheckAt("全部权限", 26, 360);
            selectBox = CheckAt("读取数据", 160, 360);
            insertBox = CheckAt("新增数据", 290, 360);
            updateBox = CheckAt("修改数据", 420, 360);
            deleteBox = CheckAt("删除数据", 26, 390);
            createBox = CheckAt("创建表", 160, 390);
            alterBox = CheckAt("修改表结构", 290, 390);
            executeBox = CheckAt("执行存储过程", 420, 390);
            grantBox = CheckAt("授权其他用户", 26, 420);
            Controls.Add(allBox);
            Controls.Add(selectBox);
            Controls.Add(insertBox);
            Controls.Add(updateBox);
            Controls.Add(deleteBox);
            Controls.Add(createBox);
            Controls.Add(alterBox);
            Controls.Add(executeBox);
            Controls.Add(grantBox);
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

            Button cancel = ButtonAt("取消", Color.FromArgb(104, 114, 124), 394, 500, 92);
            cancel.Click += (sender, args) => DialogResult = DialogResult.Cancel;
            Button create = ButtonAt("创建并验证", Color.FromArgb(26, 134, 87), 494, 500, 120);
            create.Click += Create_Click;
            Controls.Add(cancel);
            Controls.Add(create);
            AcceptButton = create;
            CancelButton = cancel;
        }

        private void Create_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(usernameBox.Text) || string.IsNullOrEmpty(passwordBox.Text))
            {
                MessageBox.Show("用户名和密码不能为空。", "信息不完整", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!string.Equals(passwordBox.Text, confirmPasswordBox.Text, StringComparison.Ordinal))
            {
                MessageBox.Show("两次输入的密码不一致。", "密码不一致", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Request = new MySqlUserRequest
            {
                Username = usernameBox.Text.Trim(),
                Password = passwordBox.Text,
                HostPattern = hostBox.Text,
                Permissions = new MySqlPermissionSelection
                {
                    DatabaseName = databaseBox.Text.Trim(),
                    AllPrivileges = allBox.Checked,
                    Select = selectBox.Checked,
                    Insert = insertBox.Checked,
                    Update = updateBox.Checked,
                    Delete = deleteBox.Checked,
                    Create = createBox.Checked,
                    Alter = alterBox.Checked,
                    Execute = executeBox.Checked,
                    GrantOption = grantBox.Checked
                }
            };
            DialogResult = DialogResult.OK;
        }

        private void AddLabel(string text, int x, int y)
        {
            Controls.Add(new Label { AutoSize = true, Text = text, ForeColor = Color.FromArgb(104, 114, 124), Location = new Point(x, y) });
        }

        private static Label Heading(string text, int x, int y, float size)
        {
            return new Label { AutoSize = true, Text = text, Font = new Font("Microsoft YaHei UI", size, FontStyle.Bold), ForeColor = Color.FromArgb(35, 42, 49), Location = new Point(x, y) };
        }

        private static Label Note(string text, int x, int y)
        {
            return new Label { AutoEllipsis = true, Size = new Size(570, 26), Text = text, ForeColor = Color.FromArgb(104, 114, 124), Location = new Point(x, y) };
        }

        private static TextBox TextBoxAt(int x, int y, int width)
        {
            return new TextBox { Location = new Point(x, y), Size = new Size(width, 27), BorderStyle = BorderStyle.FixedSingle };
        }

        private static CheckBox CheckAt(string text, int x, int y)
        {
            return new CheckBox { AutoSize = true, Text = text, Location = new Point(x, y), ForeColor = Color.FromArgb(35, 42, 49) };
        }

        private static Button ButtonAt(string text, Color color, int x, int y, int width)
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
