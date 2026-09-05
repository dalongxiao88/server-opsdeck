using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace RDPManager
{
    public sealed class LinuxSystemInfoForm : Form
    {
        private static readonly Color Surface = Color.White;
        private static readonly Color WindowBackground = Color.FromArgb(241, 243, 245);
        private static readonly Color TextColor = Color.FromArgb(35, 42, 49);
        private static readonly Color MutedColor = Color.FromArgb(104, 114, 124);
        private static readonly Color BorderColor = Color.FromArgb(211, 217, 222);
        private static readonly Color Green = Color.FromArgb(26, 134, 87);
        private static readonly Color Orange = Color.FromArgb(210, 125, 26);

        public LinuxSystemInfoForm(Server server, RemoteSystemInfo info)
        {
            Text = "Linux 系统信息 · " + (server == null ? "服务器" : server.Name);
            ClientSize = new Size(720, 520);
            MinimumSize = new Size(720, 520);
            MaximumSize = new Size(720, 520);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = WindowBackground;
            Font = new Font("Microsoft YaHei UI", 9F);
            TryLoadIcon();

            Panel header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 82,
                BackColor = Surface,
                Padding = new Padding(24, 14, 24, 8)
            };
            header.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "Linux 系统信息",
                Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
                ForeColor = TextColor,
                Location = new Point(24, 13)
            });
            header.Controls.Add(new Label
            {
                AutoEllipsis = true,
                Size = new Size(650, 24),
                Text = (server == null ? "-" : server.Name) + "   ·   SSH 已验证   ·   " + (info == null ? "-" : info.HostName),
                ForeColor = MutedColor,
                Location = new Point(26, 47)
            });

            Panel content = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(24, 18, 24, 12),
                BackColor = WindowBackground
            };
            TableLayoutPanel table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Surface,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.Single,
                ColumnCount = 4,
                RowCount = 9,
                Padding = new Padding(14)
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int index = 0; index < 9; index++)
                table.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / 9F));

            AddValue(table, 0, 0, "发行版", Value(info == null ? "" : info.OperatingSystem));
            AddValue(table, 0, 2, "版本", Value(info == null ? "" : info.OsVersion));
            AddValue(table, 1, 0, "主机名", Value(info == null ? "" : info.HostName));
            AddValue(table, 1, 2, "当前用户", Value(info == null ? "" : info.UserName));
            AddValue(table, 2, 0, "内核", Value(info == null ? "" : info.Kernel));
            AddValue(table, 2, 2, "架构", Value(info == null ? "" : info.Architecture));
            AddValue(table, 3, 0, "CPU 核心", Value(info == null ? "" : info.CpuCores));
            AddValue(table, 3, 2, "内存", FormatBytes(info == null ? "" : info.MemoryBytes));
            AddValue(table, 4, 0, "根分区可用", FormatBytes(info == null ? "" : info.RootFreeBytes));
            AddValue(table, 4, 2, "SSH 端口", Value(info == null ? "" : info.SshPort));
            AddValue(table, 5, 0, "包管理器", Value(info == null ? "" : info.PackageManager));
            AddValue(table, 5, 2, "防火墙", Value(info == null ? "" : info.Firewall));
            AddValue(table, 6, 0, "systemd", info != null && info.HasSystemd ? "可用" : "未检测到", info != null && info.HasSystemd ? Green : Orange);
            AddValue(table, 6, 2, "管理员权限", info != null && info.IsRoot ? "root" : info != null && info.CanSudo ? "免密 sudo" : info != null && info.HasSudo ? "sudo 需密码" : "无 sudo", info != null && (info.IsRoot || info.CanSudo || info.HasSudo) ? Green : Orange);
            AddValue(table, 7, 0, "启动时间", FormatDate(info == null ? DateTime.MinValue : info.LastBootUpTime));
            AddValue(table, 7, 2, "连接方式", "SSH");
            AddValue(table, 8, 0, "说明", "信息来自当前 SSH 会话");
            table.GetControlFromPosition(1, 8).Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Italic);
            table.SetColumnSpan(table.GetControlFromPosition(1, 8), 3);
            content.Controls.Add(table);

            Panel footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 58,
                BackColor = Surface,
                Padding = new Padding(24, 13, 24, 13)
            };
            Button close = new Button
            {
                Text = "关闭",
                Size = new Size(88, 30),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                FlatStyle = FlatStyle.Flat,
                BackColor = Surface,
                ForeColor = MutedColor,
                UseVisualStyleBackColor = false,
                Location = new Point(584, 13)
            };
            close.FlatAppearance.BorderColor = BorderColor;
            close.Click += (sender, args) => Close();
            footer.Controls.Add(close);

            Controls.Add(content);
            Controls.Add(footer);
            Controls.Add(header);
            AcceptButton = close;
            CancelButton = close;
        }

        private static void AddValue(TableLayoutPanel table, int row, int labelColumn, string label, string value, Color? valueColor = null)
        {
            table.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = MutedColor,
                Padding = new Padding(4, 0, 4, 0)
            }, labelColumn, row);
            table.Controls.Add(new Label
            {
                Text = value,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = valueColor ?? TextColor,
                Padding = new Padding(4, 0, 4, 0),
                AutoEllipsis = true
            }, labelColumn + 1, row);
        }

        private static string Value(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        private static string FormatBytes(string value)
        {
            long bytes;
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes) || bytes < 0)
                return "-";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double amount = bytes;
            int unit = 0;
            while (amount >= 1024 && unit < units.Length - 1)
            {
                amount /= 1024;
                unit++;
            }
            return amount.ToString(amount >= 10 || unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
        }

        private static string FormatDate(DateTime value)
        {
            return value == DateTime.MinValue ? "-" : value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }

        private void TryLoadIcon()
        {
            try
            {
                using (System.IO.Stream stream = typeof(LinuxSystemInfoForm).Assembly.GetManifestResourceStream("RDPManager.favicon.ico"))
                {
                    if (stream != null)
                        Icon = new Icon(stream);
                }
            }
            catch { }
        }
    }
}
