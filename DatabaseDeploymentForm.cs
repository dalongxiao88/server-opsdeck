using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RDPManager
{
    public sealed class DatabaseDeploymentDraft
    {
        public string DatabaseType { get; set; }
        public string VersionTrack { get; set; }
        public string ServiceName { get; set; }
        public int Port { get; set; }
        public string DatabaseName { get; set; }
        public string AdminUser { get; set; }
        public string AdminPassword { get; set; }
    }

    internal sealed class DatabaseDeploymentOption
    {
        public string Type { get; set; }
        public string Description { get; set; }
        public string[] Versions { get; set; }
        public string DefaultServiceName { get; set; }
        public int DefaultPort { get; set; }
        public string DefaultDatabaseName { get; set; }
        public string DefaultAdminUser { get; set; }
        public bool IsSupported { get; set; }

        public override string ToString()
        {
            return Type;
        }
    }

    public sealed class DatabaseDeploymentForm : Form
    {
        private static readonly Color Surface = Color.White;
        private static readonly Color WindowBackground = Color.FromArgb(241, 243, 245);
        private static readonly Color TextColor = Color.FromArgb(35, 42, 49);
        private static readonly Color MutedColor = Color.FromArgb(104, 114, 124);
        private static readonly Color BorderColor = Color.FromArgb(211, 217, 222);
        private static readonly Color Green = Color.FromArgb(26, 134, 87);
        private static readonly Color Blue = Color.FromArgb(42, 125, 185);
        private static readonly Color Orange = Color.FromArgb(210, 125, 26);

        private readonly Server server;
        private readonly string serverPassword;
        private readonly Func<bool> persistChanges;
        private readonly List<DatabaseDeploymentOption> options;
        private readonly DatabaseDeploymentService deploymentService = new DatabaseDeploymentService();
        private readonly LinuxDatabaseDeploymentService linuxDeploymentService = new LinuxDatabaseDeploymentService();
        private readonly bool isLinux;
        private int currentStep;
        private string selectedDatabaseType;
        private Label stepDatabaseLabel;
        private Label stepConfigurationLabel;
        private Label stepConfirmationLabel;
        private Panel pageHost;
        private Button backButton;
        private Button nextButton;
        private ListBox databaseList;
        private ComboBox versionBox;
        private TextBox serviceNameBox;
        private NumericUpDown portBox;
        private TextBox databaseNameBox;
        private TextBox adminUserBox;
        private TextBox passwordBox;
        private CheckBox showPasswordCheck;
        private CheckBox confirmationCheck;
        private Label summaryLabel;
        private Label databaseDescriptionLabel;

        public DatabaseDeploymentDraft Draft { get; private set; }
        public bool DeploymentCompleted { get; private set; }

        public DatabaseDeploymentForm(Server server)
            : this(server, "", null)
        {
        }

        public DatabaseDeploymentForm(Server server, string serverPassword, Func<bool> persistChanges)
        {
            this.server = server;
            this.serverPassword = serverPassword ?? "";
            this.persistChanges = persistChanges;
            isLinux = server != null && server.Type == ServerType.Linux;
            options = CreateOptions(server);
            Text = "部署数据库 · " + (server == null ? "服务器" : server.Name);
            ClientSize = new Size(820, 680);
            MinimumSize = new Size(780, 650);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = WindowBackground;
            Font = new Font("Microsoft YaHei UI", 9F);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            InitializeComponent();
            ShowStep(0);
        }

        private void InitializeComponent()
        {
            Panel header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 78,
                BackColor = Surface,
                Padding = new Padding(24, 14, 24, 8)
            };
            header.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "部署数据库",
                Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
                ForeColor = TextColor,
                Location = new Point(24, 13)
            });
            header.Controls.Add(new Label
            {
                AutoEllipsis = true,
                Size = new Size(710, 24),
                Text = "目标服务器：" + (server == null ? "-" : server.Name) + "   ·   安装任务将在目标服务器执行",
                ForeColor = MutedColor,
                Location = new Point(26, 46)
            });

            Panel steps = new Panel
            {
                Dock = DockStyle.Top,
                Height = 64,
                BackColor = WindowBackground,
                Padding = new Padding(24, 13, 24, 9)
            };
            stepDatabaseLabel = CreateStepLabel("1  选择数据库", 24);
            stepConfigurationLabel = CreateStepLabel("2  版本与配置", 270);
            stepConfirmationLabel = CreateStepLabel("3  安全确认", 516);
            steps.Controls.Add(stepDatabaseLabel);
            steps.Controls.Add(stepConfigurationLabel);
            steps.Controls.Add(stepConfirmationLabel);

            pageHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = WindowBackground,
                Padding = new Padding(24, 0, 24, 12)
            };

            Panel footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 68,
                BackColor = Surface,
                Padding = new Padding(24, 16, 24, 16)
            };
            Button cancel = CreateButton("取消", MutedColor, 88);
            cancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            cancel.Location = new Point(596, 16);
            cancel.Click += (sender, args) => DialogResult = DialogResult.Cancel;
            backButton = CreateButton("上一步", MutedColor, 88);
            backButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            backButton.Location = new Point(500, 16);
            backButton.Click += (sender, args) =>
            {
                if (currentStep == 1 && versionBox != null)
                    Draft = CaptureDraft(GetSelectedOption());
                ShowStep(currentStep - 1);
            };
            nextButton = CreateButton("下一步", Blue, 96, true);
            nextButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            nextButton.Location = new Point(692, 16);
            nextButton.Click += NextButton_Click;
            footer.Controls.Add(backButton);
            footer.Controls.Add(cancel);
            footer.Controls.Add(nextButton);
            footer.Resize += (sender, args) =>
            {
                nextButton.Left = footer.ClientSize.Width - nextButton.Width - 24;
                cancel.Left = nextButton.Left - cancel.Width - 8;
                backButton.Left = cancel.Left - backButton.Width - 8;
            };

            Controls.Add(pageHost);
            Controls.Add(steps);
            Controls.Add(header);
            Controls.Add(footer);
            CancelButton = cancel;
        }

        private void ShowStep(int step)
        {
            currentStep = Math.Max(0, Math.Min(2, step));
            pageHost.Controls.Clear();
            if (currentStep == 0)
                pageHost.Controls.Add(CreateDatabaseSelectionPage());
            else if (currentStep == 1)
                pageHost.Controls.Add(CreateConfigurationPage());
            else
                pageHost.Controls.Add(CreateConfirmationPage());

            UpdateStepStyles();
            backButton.Enabled = currentStep > 0;
            nextButton.Text = currentStep == 2 ? "开始部署" : "下一步";
            nextButton.Enabled = currentStep == 0
                ? GetSelectedOption() != null && GetSelectedOption().IsSupported
                : currentStep == 2 ? confirmationCheck != null && confirmationCheck.Checked : true;
        }

        private Panel CreateDatabaseSelectionPage()
        {
            Panel surface = CreateSurfacePanel();
            surface.Controls.Add(CreateHeading("选择要部署的数据库", 20, 18));
            surface.Controls.Add(CreateText("仅提供经过验证的常用版本；旧版本或特殊版本请手动安装。", 22, 49, MutedColor, 700));

            databaseList = new ListBox
            {
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 68,
                IntegralHeight = false,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Surface,
                ForeColor = TextColor,
                Location = new Point(22, 84),
                Size = new Size(300, 340),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left
            };
            foreach (DatabaseDeploymentOption option in options)
                databaseList.Items.Add(option);
            databaseList.DrawItem += DrawDatabaseOption;
            databaseList.SelectedIndexChanged += (sender, args) =>
            {
                DatabaseDeploymentOption selected = GetSelectedOption();
                if (selected != null)
                {
                    if (Draft != null && !string.Equals(Draft.DatabaseType, selected.Type, StringComparison.OrdinalIgnoreCase))
                        Draft = null;
                    selectedDatabaseType = selected.Type;
                }
                if (databaseDescriptionLabel != null)
                {
                    databaseDescriptionLabel.Text = selected == null ? "请选择数据库" : selected.Description;
                    databaseDescriptionLabel.ForeColor = selected != null && selected.IsSupported ? MutedColor : Orange;
                }
                nextButton.Enabled = selected != null && selected.IsSupported;
            };

            Panel detail = new Panel
            {
                BackColor = Color.FromArgb(247, 249, 250),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(344, 84),
                Size = new Size(400, 340),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            detail.Controls.Add(CreateHeading("部署说明", 20, 20));
            databaseDescriptionLabel = CreateText("请选择一种数据库查看支持版本和部署说明。", 22, 58, MutedColor, 350, 100);
            detail.Controls.Add(databaseDescriptionLabel);
            detail.Controls.Add(CreateText("默认安全策略", 22, 174, MutedColor, 180));
            detail.Controls.Add(CreateText("只监听 127.0.0.1", 22, 200, Green, 300));
            detail.Controls.Add(CreateText("不开放数据库公网端口", 22, 226, Green, 300));
            detail.Controls.Add(CreateText("凭据保存到 AES-256-GCM 保险库", 22, 252, Blue, 340));

            surface.Controls.Add(databaseList);
            surface.Controls.Add(detail);
            if (!string.IsNullOrWhiteSpace(selectedDatabaseType))
            {
                int selectedIndex = options.FindIndex(option => string.Equals(option.Type, selectedDatabaseType, StringComparison.OrdinalIgnoreCase));
                if (selectedIndex >= 0)
                    databaseList.SelectedIndex = selectedIndex;
            }
            return surface;
        }

        private Panel CreateConfigurationPage()
        {
            DatabaseDeploymentOption option = GetSelectedOption() ?? options.First(item => item.IsSupported);
            Panel surface = CreateSurfacePanel();
            surface.Controls.Add(CreateHeading(option.Type + " 版本与初始化配置", 20, 18));
            surface.Controls.Add(CreateText(isLinux ? "软件包将在目标 Linux 服务器通过 apt/dnf 获取；请确认版本、服务名称和端口。" : "安装包将在目标服务器下载；请确认版本、服务名称和端口。", 22, 49, MutedColor, 700));

            AddFieldLabel(surface, "版本", 22, 91);
            versionBox = new ComboBox
            {
                Location = new Point(22, 115),
                Width = 330,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            versionBox.Items.AddRange(option.Versions.Cast<object>().ToArray());
            int versionIndex = Draft != null && string.Equals(Draft.DatabaseType, option.Type, StringComparison.OrdinalIgnoreCase)
                ? Array.FindIndex(option.Versions, value => string.Equals(value, Draft.VersionTrack, StringComparison.Ordinal))
                : -1;
            versionBox.SelectedIndex = versionIndex >= 0 ? versionIndex : 0;
            surface.Controls.Add(versionBox);

            AddFieldLabel(surface, "服务名称", 390, 91);
            serviceNameBox = CreateInput(Draft != null && Draft.DatabaseType == option.Type ? Draft.ServiceName : option.DefaultServiceName, 390, 115, 330);
            if (isLinux)
            {
                serviceNameBox.ReadOnly = true;
                serviceNameBox.BackColor = Color.FromArgb(247, 249, 250);
            }
            surface.Controls.Add(serviceNameBox);

            AddFieldLabel(surface, "监听地址", 22, 163);
            TextBox host = CreateInput("127.0.0.1", 22, 187, 210);
            host.ReadOnly = true;
            host.BackColor = Color.FromArgb(247, 249, 250);
            surface.Controls.Add(host);

            AddFieldLabel(surface, "端口", 260, 163);
            portBox = new NumericUpDown
            {
                Location = new Point(260, 187),
                Width = 150,
                Minimum = 1,
                Maximum = 65535,
                Value = Draft != null && Draft.DatabaseType == option.Type && Draft.Port >= 1 && Draft.Port <= 65535 ? Draft.Port : option.DefaultPort
            };
            surface.Controls.Add(portBox);
            Button probe = CreateButton("探测端口", Blue, 94);
            probe.Location = new Point(420, 184);
            probe.Click += async (sender, args) => await ProbePortAsync(probe, false);
            surface.Controls.Add(probe);
            Button random = CreateButton("随机端口", MutedColor, 94);
            random.Location = new Point(522, 184);
            random.Click += async (sender, args) => await ProbePortAsync(random, true);
            surface.Controls.Add(random);

            AddFieldLabel(surface, GetDatabaseNameCaption(option.Type), 22, 235);
            databaseNameBox = CreateInput(Draft != null && Draft.DatabaseType == option.Type ? Draft.DatabaseName : option.DefaultDatabaseName, 22, 259, 330);
            surface.Controls.Add(databaseNameBox);

            AddFieldLabel(surface, "管理账号", 390, 235);
            adminUserBox = CreateInput(Draft != null && Draft.DatabaseType == option.Type ? Draft.AdminUser : option.DefaultAdminUser, 390, 259, 330);
            if (option.Type == "MySQL" || option.Type == "MariaDB")
            {
                adminUserBox.ReadOnly = true;
                adminUserBox.BackColor = Color.FromArgb(247, 249, 250);
            }
            surface.Controls.Add(adminUserBox);

            AddFieldLabel(surface, "管理密码", 22, 307);
            passwordBox = CreateInput(Draft != null && Draft.DatabaseType == option.Type && !string.IsNullOrEmpty(Draft.AdminPassword) ? Draft.AdminPassword : GeneratePassword(), 22, 331, 470);
            passwordBox.UseSystemPasswordChar = true;
            surface.Controls.Add(passwordBox);
            showPasswordCheck = new CheckBox
            {
                AutoSize = true,
                Text = "显示密码",
                ForeColor = MutedColor,
                Location = new Point(506, 334)
            };
            showPasswordCheck.CheckedChanged += (sender, args) => passwordBox.UseSystemPasswordChar = !showPasswordCheck.Checked;
            surface.Controls.Add(showPasswordCheck);
            Button generate = CreateButton("重新生成", Blue, 94);
            generate.Location = new Point(626, 328);
            generate.Click += (sender, args) => passwordBox.Text = GeneratePassword();
            surface.Controls.Add(generate);

            surface.Controls.Add(CreateText("安装包由目标服务器下载；部署成功并完成连接验证后，凭据才会写入保险库。", 22, 387, MutedColor, 700));
            return surface;
        }

        private Panel CreateConfirmationPage()
        {
            DatabaseDeploymentOption option = GetSelectedOption() ?? options.First(item => item.IsSupported);
            Panel surface = CreateSurfacePanel();
            surface.Controls.Add(CreateHeading("确认部署配置", 20, 18));
            surface.Controls.Add(CreateText("请核对以下配置。确认后将直接在目标服务器执行安装。", 22, 49, MutedColor, 700));

            summaryLabel = CreateText(BuildSummary(option), 22, 92, TextColor, 700, 245);
            summaryLabel.Font = new Font("Microsoft YaHei UI", 10F);
            summaryLabel.BackColor = Color.FromArgb(247, 249, 250);
            summaryLabel.BorderStyle = BorderStyle.FixedSingle;
            summaryLabel.Padding = new Padding(18, 14, 18, 14);
            surface.Controls.Add(summaryLabel);

            confirmationCheck = new CheckBox
            {
                AutoSize = false,
                Size = new Size(700, 46),
                Text = "我已确认数据库类型、版本、端口和安全策略",
                ForeColor = TextColor,
                Location = new Point(22, 356)
            };
            confirmationCheck.CheckedChanged += (sender, args) => nextButton.Enabled = confirmationCheck.Checked;
            surface.Controls.Add(confirmationCheck);
            surface.Controls.Add(CreateText("部署失败时会自动回滚本次创建的服务、目录和临时文件。", 44, 397, Orange, 650));
            return surface;
        }

        private async void NextButton_Click(object sender, EventArgs e)
        {
            if (currentStep == 0)
            {
                if (GetSelectedOption() == null || !GetSelectedOption().IsSupported)
                {
                    MessageBox.Show("请选择当前支持部署的数据库。", "尚未选择", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                ShowStep(1);
                return;
            }
            if (currentStep == 1)
            {
                if (!ValidateConfiguration())
                    return;
                if (!await NormalizePortBeforeConfirmationAsync())
                    return;
                Draft = CaptureDraft(GetSelectedOption());
                ShowStep(2);
                return;
            }
            if (confirmationCheck == null || !confirmationCheck.Checked)
                return;

            if (Draft == null)
                Draft = CaptureDraft(GetSelectedOption());
            await RunDeploymentAsync();
        }

        private DatabaseDeploymentDraft CaptureDraft(DatabaseDeploymentOption option)
        {
            if (option == null || versionBox == null || serviceNameBox == null || portBox == null ||
                databaseNameBox == null || adminUserBox == null || passwordBox == null)
                return Draft;
            return new DatabaseDeploymentDraft
            {
                DatabaseType = option.Type,
                VersionTrack = versionBox.Text,
                ServiceName = serviceNameBox.Text.Trim(),
                Port = (int)portBox.Value,
                DatabaseName = databaseNameBox.Text.Trim(),
                AdminUser = adminUserBox.Text.Trim(),
                AdminPassword = passwordBox.Text
            };
        }

        private async Task ProbePortAsync(Button button, bool randomize)
        {
            if (server == null || !HasSshCredential())
            {
                MessageBox.Show("当前没有可用的服务器 SSH 管理凭据。", "无法探测端口", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string oldText = button.Text;
            button.Enabled = false;
            button.Text = "探测中...";
            try
            {
                int port = isLinux
                    ? await linuxDeploymentService.SuggestAvailablePortAsync(server, serverPassword, (int)portBox.Value, randomize, CancellationToken.None)
                    : await deploymentService.SuggestAvailablePortAsync(server, serverPassword, (int)portBox.Value, randomize, CancellationToken.None);
                portBox.Value = port;
                MessageBox.Show(
                    randomize ? "已选择服务器上的可用端口：" + port : "端口 " + port + " 当前可用。",
                    "端口探测完成",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("探测服务器端口失败：" + ex.Message, "探测失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                button.Text = oldText;
                button.Enabled = true;
            }
        }

        private async Task<bool> NormalizePortBeforeConfirmationAsync()
        {
            if (server == null || !HasSshCredential())
                return true;

            int requestedPort = (int)portBox.Value;
            string oldText = nextButton.Text;
            Cursor previousCursor = Cursor;
            nextButton.Enabled = false;
            nextButton.Text = "检查端口...";
            Cursor = Cursors.WaitCursor;
            try
            {
                int availablePort = isLinux
                    ? await linuxDeploymentService.SuggestAvailablePortAsync(server, serverPassword, requestedPort, false, CancellationToken.None)
                    : await deploymentService.SuggestAvailablePortAsync(server, serverPassword, requestedPort, false, CancellationToken.None);
                if (availablePort != requestedPort)
                {
                    portBox.Value = availablePort;
                    MessageBox.Show(
                        "端口 " + requestedPort + " 已被服务器占用，已自动改用可用端口 " + availablePort + "。",
                        "端口已自动调整",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("检查服务器端口失败：" + ex.Message, "无法继续", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            finally
            {
                Cursor = previousCursor;
                nextButton.Text = oldText;
                nextButton.Enabled = true;
            }
        }

        private async Task RunDeploymentAsync()
        {
            if (server == null || !HasSshCredential())
            {
                MessageBox.Show("当前没有可用的服务器 SSH 管理凭据。", "无法部署", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DatabaseDeploymentResult completed = null;
            DatabaseCredentialRecord previousCredential = null;
            ServicePortRecord previousPort = null;
            using (OperationProgressForm progress = new OperationProgressForm(
                "部署 " + Draft.DatabaseType,
                server.Name + "   ·   " + Draft.DatabaseType + " " + Draft.VersionTrack,
                new[]
                {
                    "检查服务器环境",
                    "检查端口与现有服务",
                    "下载并校验安装包",
                    "初始化数据库",
                    "注册并启动服务",
                    "创建管理账号与初始数据库",
                    "SSH 隧道连接验证",
                    "保存凭据到保险库",
                    "清理临时资源"
                }))
            {
                progress.Operation = async (window, token) =>
                {
                    Action<DatabaseDeploymentProgress> report = update =>
                    {
                        window.SetStep(update.Step, update.State, update.Detail);
                        window.SetProgress(update.Title, update.Detail, update.Percent, Blue, update.Indeterminate);
                    };
                    completed = isLinux
                        ? await linuxDeploymentService.DeployAsync(server, serverPassword, Draft, report, () => PromptSudoPassword(window), token)
                        : await deploymentService.DeployAsync(server, serverPassword, Draft, report, token);

                    window.SetStep(7, OperationStepState.Running, "正在写入加密保险库");
                    window.SetProgress("保存部署凭据", "写入 AES-256-GCM 保险库", 98, Blue, false);
                    previousCredential = AddOrReplaceCredential(completed.Credential);
                    previousPort = AddOrReplacePortRecord(completed);
                    bool saved = false;
                    try
                    {
                        saved = persistChanges != null && persistChanges();
                    }
                    catch
                    {
                        saved = false;
                    }
                    if (!saved)
                    {
                        RestoreCredential(previousCredential, completed.Credential);
                        RestorePortRecord(previousPort, completed);
                        try
                        {
                            if (isLinux)
                                await linuxDeploymentService.RollbackAsync(server, serverPassword, completed, CancellationToken.None);
                            else
                                await deploymentService.RollbackAsync(server, serverPassword, completed, CancellationToken.None);
                        }
                        catch (Exception rollbackError)
                        {
                            throw new InvalidOperationException("保险库存储失败，并且自动回滚未能完整完成：" + rollbackError.Message);
                        }
                        throw new InvalidOperationException("保险库存储失败，本次部署已自动回滚");
                    }
                    window.SetStep(7, OperationStepState.Completed, "凭据已加密保存");
                    window.SetStep(8, OperationStepState.Running, "删除远程安装包和解压缓存");
                    window.SetProgress("清理临时资源", "删除远程安装包和解压缓存", 99, Blue, true);
                    string cleanupDetail = "已清理";
                    try
                    {
                        if (isLinux)
                            await linuxDeploymentService.CleanupAsync(server, serverPassword, completed, token);
                        else
                            await deploymentService.CleanupAsync(server, serverPassword, completed, token);
                        window.SetStep(8, OperationStepState.Completed, cleanupDetail);
                    }
                    catch
                    {
                        cleanupDetail = "清理失败，可稍后手动删除临时缓存";
                        window.SetStep(8, OperationStepState.Failed, cleanupDetail);
                    }
                    DeploymentCompleted = true;
                    window.MarkSuccess(Draft.DatabaseType + " " + completed.ServerVersion + " 已部署到端口 " + Draft.Port + "；" + cleanupDetail);
                };
                progress.ShowDialog(this);
            }

            if (DeploymentCompleted)
            {
                if (passwordBox != null)
                    passwordBox.Clear();
                if (Draft != null)
                    Draft.AdminPassword = "";
                DialogResult = DialogResult.OK;
                Close();
            }
        }

        private DatabaseCredentialRecord AddOrReplaceCredential(DatabaseCredentialRecord credential)
        {
            if (server.DatabaseCredentials == null)
                server.DatabaseCredentials = new List<DatabaseCredentialRecord>();
            DatabaseCredentialRecord existing = server.DatabaseCredentials.FirstOrDefault(item =>
                string.Equals(item.DatabaseType, credential.DatabaseType, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(item.ServiceName, credential.ServiceName, StringComparison.OrdinalIgnoreCase) || item.Port == credential.Port));
            if (existing != null)
                server.DatabaseCredentials.Remove(existing);
            server.DatabaseCredentials.Add(credential);
            return existing;
        }

        private ServicePortRecord AddOrReplacePortRecord(DatabaseDeploymentResult deployment)
        {
            if (server.ServicePorts == null)
                server.ServicePorts = new List<ServicePortRecord>();
            ServicePortRecord existing = server.ServicePorts.FirstOrDefault(item =>
                string.Equals(item.ServiceType, deployment.DatabaseType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ServiceName, deployment.ServiceName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                server.ServicePorts.Remove(existing);
            server.ServicePorts.Add(new ServicePortRecord
            {
                ServiceType = deployment.DatabaseType,
                ServiceName = deployment.ServiceName,
                Port = deployment.Port,
                Protocol = "TCP",
                ConfigPath = deployment.ConfigPath,
                TargetKey = "XiaoBaiDeployment:" + deployment.ServiceName,
                UpdatedAt = DateTime.Now
            });
            return existing;
        }

        private void RestoreCredential(DatabaseCredentialRecord previous, DatabaseCredentialRecord added)
        {
            server.DatabaseCredentials.Remove(added);
            if (previous != null)
                server.DatabaseCredentials.Add(previous);
        }

        private void RestorePortRecord(ServicePortRecord previous, DatabaseDeploymentResult deployment)
        {
            server.ServicePorts.RemoveAll(item =>
                string.Equals(item.ServiceType, deployment.DatabaseType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ServiceName, deployment.ServiceName, StringComparison.OrdinalIgnoreCase));
            if (previous != null)
                server.ServicePorts.Add(previous);
        }

        private bool ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(serviceNameBox.Text) || string.IsNullOrWhiteSpace(databaseNameBox.Text) ||
                string.IsNullOrWhiteSpace(adminUserBox.Text) || string.IsNullOrEmpty(passwordBox.Text))
            {
                MessageBox.Show("请完整填写服务名称、初始数据库、管理账号和管理密码。", "配置不完整", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            return true;
        }

        private string BuildSummary(DatabaseDeploymentOption option)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("目标服务器       " + (server == null ? "-" : server.Name));
            builder.AppendLine("数据库           " + option.Type + "  " + versionBox.Text);
            builder.AppendLine("服务名称         " + serviceNameBox.Text.Trim());
            builder.AppendLine("监听地址         127.0.0.1:" + portBox.Value);
            builder.AppendLine(GetDatabaseNameCaption(option.Type).PadRight(13) + databaseNameBox.Text.Trim());
            builder.AppendLine("管理账号         " + adminUserBox.Text.Trim());
            builder.AppendLine("公网端口         不开放");
            builder.AppendLine("凭据保存         AES-256-GCM 加密保险库");
            builder.Append("执行位置         目标服务器");
            return builder.ToString();
        }

        private void UpdateStepStyles()
        {
            Label[] labels = { stepDatabaseLabel, stepConfigurationLabel, stepConfirmationLabel };
            for (int index = 0; index < labels.Length; index++)
            {
                labels[index].BackColor = index == currentStep ? Blue : Surface;
                labels[index].ForeColor = index == currentStep ? Color.White : index < currentStep ? Green : MutedColor;
            }
        }

        private void DrawDatabaseOption(object sender, DrawItemEventArgs args)
        {
            if (args.Index < 0 || args.Index >= databaseList.Items.Count)
                return;
            DatabaseDeploymentOption option = (DatabaseDeploymentOption)databaseList.Items[args.Index];
            bool selected = (args.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color background = selected ? Color.FromArgb(226, 239, 247) : Surface;
            using (SolidBrush brush = new SolidBrush(background))
                args.Graphics.FillRectangle(brush, args.Bounds);
            using (SolidBrush dot = new SolidBrush(option.IsSupported ? Green : Color.FromArgb(128, 136, 144)))
                args.Graphics.FillEllipse(dot, args.Bounds.Left + 14, args.Bounds.Top + 18, 10, 10);
            using (SolidBrush title = new SolidBrush(TextColor))
                args.Graphics.DrawString(option.Type, new Font(Font, FontStyle.Bold), title, args.Bounds.Left + 34, args.Bounds.Top + 10);
            string status = option.IsSupported ? option.Versions[0] + "   ·   可部署" : "开发中   ·   暂不支持";
            using (SolidBrush detail = new SolidBrush(option.IsSupported ? Blue : Orange))
                args.Graphics.DrawString(status, Font, detail, args.Bounds.Left + 34, args.Bounds.Top + 37);
            if (selected)
                using (Pen pen = new Pen(Blue, 2F))
                    args.Graphics.DrawLine(pen, args.Bounds.Left + 1, args.Bounds.Top + 1, args.Bounds.Left + 1, args.Bounds.Bottom - 1);
        }

        private DatabaseDeploymentOption GetSelectedOption()
        {
            return databaseList == null ? null : databaseList.SelectedItem as DatabaseDeploymentOption;
        }

        private static string GetDatabaseNameCaption(string type)
        {
            return type == "Redis" ? "逻辑库编号" : type == "MongoDB" ? "初始数据库" : "初始数据库";
        }

        private static string GeneratePassword()
        {
            const string characters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%";
            byte[] random = RandomNumberGenerator.GetBytes(16);
            StringBuilder builder = new StringBuilder(random.Length);
            foreach (byte value in random)
                builder.Append(characters[value % characters.Length]);
            return builder.ToString();
        }

        private bool HasSshCredential()
        {
            if (server == null)
                return false;
            if (!isLinux || server.SshCredentialMode != SshCredentialMode.PrivateKey)
                return !string.IsNullOrWhiteSpace(serverPassword);
            return !string.IsNullOrWhiteSpace(server.SshPrivateKeyPath) && File.Exists(server.SshPrivateKeyPath);
        }

        private string PromptSudoPassword(IWin32Window owner)
        {
            using (PasswordForm form = new PasswordForm("请输入 Linux sudo 密码：" + (server == null ? "服务器" : server.Name)))
                return form.ShowDialog(owner ?? this) == DialogResult.OK ? form.Password : null;
        }

        private static List<DatabaseDeploymentOption> CreateOptions(Server server)
        {
            if (server != null && server.Type == ServerType.Linux)
            {
                return new List<DatabaseDeploymentOption>
                {
                    new DatabaseDeploymentOption { Type = "MariaDB", Description = "Linux apt/dnf 系统仓库版本，实际版本以目标发行版软件源为准。", Versions = new[] { "系统仓库版本" }, DefaultServiceName = "mariadb", DefaultPort = 3306, DefaultDatabaseName = "app_database", DefaultAdminUser = "root", IsSupported = true },
                    new DatabaseDeploymentOption { Type = "MySQL", Description = "Linux apt/dnf 软件源版本，实际小版本以目标发行版软件源为准。", Versions = new[] { "8.x（系统仓库）" }, DefaultServiceName = "mysql", DefaultPort = 3306, DefaultDatabaseName = "app_database", DefaultAdminUser = "root", IsSupported = true },
                    new DatabaseDeploymentOption { Type = "MongoDB", Description = "需要目标服务器已配置 MongoDB 官方软件源；程序不会使用未知第三方源。", Versions = new[] { "8.x（官方仓库）", "7.x（官方仓库）" }, DefaultServiceName = "mongod", DefaultPort = 27017, DefaultDatabaseName = "app_database", DefaultAdminUser = "manager_admin", IsSupported = true },
                    new DatabaseDeploymentOption { Type = "Redis", Description = "Linux 系统仓库版本，实际主版本以目标发行版软件源为准，并只监听 127.0.0.1。", Versions = new[] { "系统仓库版本" }, DefaultServiceName = "redis-server", DefaultPort = 6379, DefaultDatabaseName = "0", DefaultAdminUser = "manager_admin", IsSupported = true },
                    new DatabaseDeploymentOption { Type = "Oracle", Description = "Oracle Linux 自动部署涉及安装介质、许可和环境要求，当前只保留入口。", Versions = new[] { "开发中" }, DefaultServiceName = "Oracle", DefaultPort = 1521, DefaultDatabaseName = "", DefaultAdminUser = "", IsSupported = false }
                };
            }
            return new List<DatabaseDeploymentOption>
            {
                new DatabaseDeploymentOption { Type = "MariaDB", Description = "官方 Windows 发行版。首期逻辑建议优先实现，用于验证下载、安装、回滚和保险库存储的完整流程。", Versions = new[] { "11.4 LTS（推荐）", "10.11 LTS（兼容）" }, DefaultServiceName = "MariaDB", DefaultPort = 3306, DefaultDatabaseName = "app_database", DefaultAdminUser = "root", IsSupported = true },
                new DatabaseDeploymentOption { Type = "MySQL", Description = "官方 Windows 发行版。提供 8.4 LTS 和 8.0 两条经过验证的版本线路，不包含历史版本。", Versions = new[] { "8.4 LTS（推荐）", "8.0（兼容）" }, DefaultServiceName = "MySQL84", DefaultPort = 3306, DefaultDatabaseName = "app_database", DefaultAdminUser = "root", IsSupported = true },
                new DatabaseDeploymentOption { Type = "MongoDB", Description = "官方 MongoDB Community Server，并准备匹配的 Database Tools，以支持后续备份和恢复。", Versions = new[] { "8.0（推荐）", "7.0（兼容）" }, DefaultServiceName = "MongoDB", DefaultPort = 27017, DefaultDatabaseName = "app_database", DefaultAdminUser = "manager_admin", IsSupported = true },
                new DatabaseDeploymentOption { Type = "Redis", Description = "Windows 社区构建，仅建议用于兼容和轻量场景。默认启用 ACL、禁用无密码访问并只监听回环地址。", Versions = new[] { "8.x（推荐）", "7.x（兼容）" }, DefaultServiceName = "Redis", DefaultPort = 6379, DefaultDatabaseName = "0", DefaultAdminUser = "manager_admin", IsSupported = true },
                new DatabaseDeploymentOption { Type = "Oracle", Description = "Oracle 自动部署涉及安装介质、许可、环境要求和初始化工具，当前只保留入口。", Versions = new[] { "开发中" }, DefaultServiceName = "Oracle", DefaultPort = 1521, DefaultDatabaseName = "", DefaultAdminUser = "", IsSupported = false }
            };
        }

        private Panel CreateSurfacePanel()
        {
            return new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Surface,
                BorderStyle = BorderStyle.FixedSingle
            };
        }

        private Label CreateStepLabel(string text, int left)
        {
            return new Label
            {
                Text = text,
                Size = new Size(226, 38),
                Location = new Point(left, 13),
                TextAlign = ContentAlignment.MiddleCenter,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
            };
        }

        private static Label CreateHeading(string text, int left, int top)
        {
            return new Label
            {
                AutoSize = true,
                Text = text,
                Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
                ForeColor = TextColor,
                Location = new Point(left, top)
            };
        }

        private static Label CreateText(string text, int left, int top, Color color, int width, int height = 26)
        {
            return new Label
            {
                AutoEllipsis = false,
                Text = text,
                ForeColor = color,
                Location = new Point(left, top),
                Size = new Size(width, height)
            };
        }

        private static void AddFieldLabel(Control parent, string text, int left, int top)
        {
            parent.Controls.Add(new Label
            {
                AutoSize = true,
                Text = text,
                ForeColor = MutedColor,
                Location = new Point(left, top)
            });
        }

        private static TextBox CreateInput(string text, int left, int top, int width)
        {
            return new TextBox
            {
                Text = text ?? "",
                Location = new Point(left, top),
                Width = width,
                BorderStyle = BorderStyle.FixedSingle
            };
        }

        private static Button CreateButton(string text, Color color, int width, bool primary = false)
        {
            Button button = new Button
            {
                Text = text,
                Width = width,
                Height = 36,
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? color : Surface,
                ForeColor = primary ? Color.White : color,
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = color;
            button.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(color) : Color.FromArgb(239, 244, 241);
            return button;
        }
    }
}
