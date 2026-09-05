using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace RDPManager
{
    public sealed class LocalDatabaseInstallForm : Form
    {
        private readonly string databaseType;
        private TextBox packageBox;

        public bool InstallerStarted { get; private set; }

        public LocalDatabaseInstallForm(string databaseType)
        {
            this.databaseType = databaseType ?? "MySQL / MariaDB";
            Text = "安装本机数据库 · " + this.databaseType;
            ClientSize = new Size(620, 330);
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
            Controls.Add(new Label
            {
                AutoSize = true,
                Text = "安装本机 " + databaseType,
                Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
                ForeColor = Color.FromArgb(35, 42, 49),
                Location = new Point(24, 20)
            });
            Controls.Add(new Label
            {
                AutoEllipsis = true,
                Size = new Size(560, 52),
                Text = "管理器不会静默下载或安装数据库。请先从官方渠道下载 Windows 安装包，选择后由你确认启动安装程序。安装完成后重新打开迁移功能，程序会再次检测本机数据库。",
                ForeColor = Color.FromArgb(104, 114, 124),
                Location = new Point(26, 56)
            });
            Controls.Add(new Label { AutoSize = true, Text = "安装包路径", ForeColor = Color.FromArgb(104, 114, 124), Location = new Point(26, 126) });
            packageBox = new TextBox { Location = new Point(26, 150), Width = 430, Height = 27, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle };
            Controls.Add(packageBox);
            Button browse = CreateButton("选择安装包", Color.FromArgb(42, 125, 185), 466, 148, 110);
            browse.Click += Browse_Click;
            Controls.Add(browse);
            LinkLabel official = new LinkLabel
            {
                AutoSize = true,
                Text = "打开 MySQL 官方下载页",
                LinkColor = Color.FromArgb(42, 125, 185),
                Location = new Point(26, 198)
            };
            official.Click += (sender, args) => OpenUrl("https://dev.mysql.com/downloads/installer/");
            Controls.Add(official);
            LinkLabel maria = new LinkLabel
            {
                AutoSize = true,
                Text = "打开 MariaDB 官方下载页",
                LinkColor = Color.FromArgb(42, 125, 185),
                Location = new Point(190, 198)
            };
            maria.Click += (sender, args) => OpenUrl("https://mariadb.org/download/");
            Controls.Add(maria);
            Button cancel = CreateButton("关闭", Color.FromArgb(104, 114, 124), 414, 266, 82);
            cancel.Click += (sender, args) => DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);
            Button start = CreateButton("确认启动安装", Color.FromArgb(210, 125, 26), 506, 266, 90);
            start.Click += Start_Click;
            Controls.Add(start);
            CancelButton = cancel;
        }

        private void Browse_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "选择 MySQL / MariaDB 安装包",
                Filter = "安装程序 (*.msi;*.exe)|*.msi;*.exe|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    packageBox.Text = dialog.FileName;
            }
        }

        private void Start_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(packageBox.Text) || !File.Exists(packageBox.Text))
            {
                MessageBox.Show("请先选择有效的安装包。", "未选择安装包", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show("即将启动数据库安装程序。安装过程可能需要管理员权限，并可能注册 Windows 服务。\n\n是否继续？", "确认启动安装", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            try
            {
                Process.Start(new ProcessStartInfo(packageBox.Text) { UseShellExecute = true });
                InstallerStarted = true;
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动安装程序失败：" + ex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        private static Button CreateButton(string text, Color color, int x, int y, int width)
        {
            Button button = new Button { Text = text, Width = width, Height = 34, Location = new Point(x, y), FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = color, UseVisualStyleBackColor = false, Cursor = Cursors.Hand };
            button.FlatAppearance.BorderColor = color;
            return button;
        }
    }
}
