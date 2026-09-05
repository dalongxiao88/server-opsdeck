using System;
using System.Drawing;
using System.Windows.Forms;

namespace RDPManager
{
    public enum DatabaseMigrationDecision
    {
        Cancel,
        ExportBackup,
        ConfigureLocalTarget,
        InstallLocalDatabase,
        OtherTarget
    }

    public sealed class DatabaseMigrationDecisionForm : Form
    {
        public DatabaseMigrationDecision Decision { get; private set; }

        public DatabaseMigrationDecisionForm(string databaseType, bool localTargetAvailable)
        {
            Decision = DatabaseMigrationDecision.Cancel;
            Text = "迁移到本地 · " + databaseType;
            ClientSize = new Size(540, localTargetAvailable ? 300 : 360);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F);

            Controls.Add(new Label
            {
                AutoSize = true,
                Text = localTargetAvailable ? "检测到本机数据库目标" : "未检测到本机数据库",
                Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
                ForeColor = Color.FromArgb(35, 42, 49),
                Location = new Point(24, 22)
            });
            Controls.Add(new Label
            {
                AutoEllipsis = true,
                Size = new Size(475, 46),
                Text = localTargetAvailable
                    ? "可以继续配置本机 MySQL / MariaDB 账号，然后执行远程导出和本地导入。"
                    : "当前电脑没有可用的本机数据库实例。你可以先保存备份，之后再安装数据库并导入。",
                ForeColor = Color.FromArgb(104, 114, 124),
                Location = new Point(26, 60)
            });

            int top = 128;
            Button backup = CreateButton("仅导出备份", Color.FromArgb(42, 125, 185), 26, top, 140);
            backup.Click += (sender, args) => Complete(DatabaseMigrationDecision.ExportBackup);
            Controls.Add(backup);
            if (localTargetAvailable)
            {
                Button local = CreateButton("配置本机目标并迁移", Color.FromArgb(26, 134, 87), 180, top, 170);
                local.Click += (sender, args) => Complete(DatabaseMigrationDecision.ConfigureLocalTarget);
                Controls.Add(local);
            }
            else
            {
                Button install = CreateButton("安装本机数据库", Color.FromArgb(210, 125, 26), 180, top, 150);
                install.Click += (sender, args) => Complete(DatabaseMigrationDecision.InstallLocalDatabase);
                Controls.Add(install);
            }
            Button other = CreateButton("其他目标", Color.FromArgb(116, 86, 166), localTargetAvailable ? 364 : 344, top, 120);
            other.Click += (sender, args) => Complete(DatabaseMigrationDecision.OtherTarget);
            Controls.Add(other);

            Controls.Add(new Label
            {
                AutoEllipsis = true,
                Size = new Size(470, 42),
                Text = "本版本的“安装本机数据库”和“其他目标”先保留入口，后续再接入安装向导和远程目标选择。",
                ForeColor = Color.FromArgb(210, 125, 26),
                Location = new Point(28, 188)
            });
            Button cancel = CreateButton("取消", Color.FromArgb(104, 114, 124), 410, localTargetAvailable ? 242 : 300, 92);
            cancel.Click += (sender, args) => Complete(DatabaseMigrationDecision.Cancel);
            Controls.Add(cancel);
            CancelButton = cancel;
        }

        private void Complete(DatabaseMigrationDecision decision)
        {
            Decision = decision;
            DialogResult = decision == DatabaseMigrationDecision.Cancel ? DialogResult.Cancel : DialogResult.OK;
        }

        private static Button CreateButton(string text, Color color, int x, int y, int width)
        {
            Button button = new Button { Text = text, Width = width, Height = 34, Location = new Point(x, y), FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = color, UseVisualStyleBackColor = false, Cursor = Cursors.Hand };
            button.FlatAppearance.BorderColor = color;
            return button;
        }
    }
}
