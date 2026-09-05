using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RDPManager
{
    public sealed class LocalMySqlTargetForm : Form
    {
        private readonly string databaseType;
        private TextBox hostBox;
        private NumericUpDown portBox;
        private TextBox usernameBox;
        private TextBox passwordBox;
        private readonly MySqlDatabaseService service = new MySqlDatabaseService();

        public LocalDatabaseTarget Target { get; private set; }

        public LocalMySqlTargetForm(string databaseType)
        {
            this.databaseType = databaseType ?? "MySQL";
            Text = "本机目标数据库 · " + databaseType;
            ClientSize = new Size(500, 340);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F);
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Controls.Add(new Label { AutoSize = true, Text = "配置本机数据库目标", Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold), ForeColor = Color.FromArgb(35, 42, 49), Location = new Point(24, 20) });
            Controls.Add(new Label { AutoEllipsis = true, Size = new Size(440, 28), Text = "密码只在本次迁移过程中使用，不会自动保存到服务器资料。", ForeColor = Color.FromArgb(104, 114, 124), Location = new Point(26, 54) });
            AddLabel("地址", 26, 94);
            hostBox = Box("127.0.0.1", 26, 116, 260);
            Controls.Add(hostBox);
            AddLabel("端口", 314, 94);
            portBox = new NumericUpDown { Location = new Point(314, 116), Width = 130, Minimum = 1, Maximum = 65535, Value = 3306 };
            Controls.Add(portBox);
            AddLabel("用户名", 26, 154);
            usernameBox = Box("root", 26, 176, 418);
            Controls.Add(usernameBox);
            AddLabel("密码", 26, 214);
            passwordBox = Box("", 26, 236, 418);
            passwordBox.UseSystemPasswordChar = true;
            Controls.Add(passwordBox);
            Button cancel = Button("取消", Color.FromArgb(104, 114, 124), 278, 288, 82);
            cancel.Click += (sender, args) => DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);
            Button test = Button("测试并开始", Color.FromArgb(26, 134, 87), 368, 288, 108);
            test.Click += async (sender, args) => await Test_Click();
            Controls.Add(test);
            AcceptButton = test;
            CancelButton = cancel;
        }

        private async Task Test_Click()
        {
            Target = new LocalDatabaseTarget
            {
                DatabaseType = databaseType,
                Host = hostBox.Text.Trim(),
                Port = (int)portBox.Value,
                Username = usernameBox.Text.Trim(),
                Password = passwordBox.Text,
                ImportToolPath = LocalDatabaseTools.FindImportTool(databaseType)
            };
            if (string.IsNullOrWhiteSpace(Target.ImportToolPath))
            {
                MessageBox.Show("未找到本机 mysql.exe 或 mariadb.exe，无法导入。", "缺少本机客户端", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Cursor previous = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                await service.TestLocalConnectionAsync(Target, CancellationToken.None);
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex)
            {
                MessageBox.Show("本机数据库连接失败：" + ex.Message, "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { Cursor = previous; }
        }

        private void AddLabel(string text, int x, int y) => Controls.Add(new Label { AutoSize = true, Text = text, ForeColor = Color.FromArgb(104, 114, 124), Location = new Point(x, y) });
        private static TextBox Box(string text, int x, int y, int width) => new TextBox { Text = text, Location = new Point(x, y), Width = width, Height = 27, BorderStyle = BorderStyle.FixedSingle };
        private static Button Button(string text, Color color, int x, int y, int width) { Button b = new Button { Text = text, Width = width, Height = 34, Location = new Point(x, y), FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = color, UseVisualStyleBackColor = false, Cursor = Cursors.Hand }; b.FlatAppearance.BorderColor = color; return b; }
    }
}
