using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RDPManager
{
    public sealed class LinuxPortManagementService
    {
        private static readonly Regex SafePort = new Regex(@"^[1-9][0-9]{0,4}$", RegexOptions.Compiled);
        private static readonly Regex SafeConfigPath = new Regex(@"^/[A-Za-z0-9_./-]+$", RegexOptions.Compiled);

        public async Task<PortInspectionResult> InspectAsync(Server server, string password, CancellationToken cancellationToken)
        {
            EnsureLinux(server);
            using (IRemoteExecutor executor = await RemoteExecutorFactory.CreateAsync(server, password, cancellationToken, RemoteTransport.SSH))
            {
                RemoteSystemInfo info = await executor.GetSystemInfoAsync(cancellationToken);
                PortInspectionResult result = new PortInspectionResult
                {
                    HostName = info.HostName,
                    Transport = "SSH"
                };
                LinuxSshTarget target = await DetectSshTargetAsync(executor, info, cancellationToken);
                if (target != null)
                {
                    result.Services.Add(new DetectedServicePort
                    {
                        ServiceType = "SSH",
                        DisplayName = "OpenSSH Server",
                        ServiceName = target.ServiceName,
                        ConfigPath = target.ConfigPath,
                        Protocol = "TCP",
                        Port = target.Port,
                        IsSupported = target.IsSupported,
                        TargetKey = target.ConfigPath + "|" + target.ServiceName,
                        ServiceStatus = target.ServiceStatus
                    });
                }
                else
                {
                    result.Warnings.Add("未找到可识别的 sshd_config 或 systemd SSH 服务");
                }
                result.Services.AddRange(await DetectDatabaseTargetsAsync(executor, cancellationToken));
                result.Services.AddRange(await DetectWebTargetsAsync(executor, cancellationToken));
                return result;
            }
        }

        public async Task<IList<int>> GetAvailablePortsAsync(
            Server server,
            string password,
            DetectedServicePort target,
            CancellationToken cancellationToken)
        {
            EnsureLinux(server);
            using (IRemoteExecutor executor = await RemoteExecutorFactory.CreateAsync(server, password, cancellationToken, RemoteTransport.SSH))
            {
                RemoteCommandResult result = await executor.ExecuteCommandAsync(
                    "ss -ltnH 2>/dev/null | awk '{print $4}' | sed 's/.*://' | grep -E '^[0-9]+$' | sort -n -u",
                    TimeSpan.FromSeconds(20),
                    cancellationToken);
                EnsureSuccess(result, "检测 Linux 监听端口");
                HashSet<int> occupied = new HashSet<int>();
                foreach (string line in (result.Output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int port;
                    if (int.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port >= 1 && port <= 65535)
                        occupied.Add(port);
                }

                List<int> available = new List<int>();
                for (int port = 1024; port <= 65535; port++)
                {
                    if (!occupied.Contains(port))
                        available.Add(port);
                }
                return available;
            }
        }

        public async Task ExecuteAsync(
            Server server,
            string password,
            PortChangeRequest request,
            Action<int, string, string> report,
            Func<string> sudoPasswordProvider,
            CancellationToken cancellationToken)
        {
            EnsureLinux(server);
            if (request == null || request.Target == null || !string.Equals(request.Target.ServiceType, "SSH", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Linux 首期端口管理只支持 SSH");
            if (!request.Target.IsSupported)
                throw new InvalidOperationException("未识别到可靠的 sshd 配置文件或 systemd 服务，已停止修改");
            if (request.NewPort < 1 || request.NewPort > 65535 || request.NewPort == request.Target.Port)
                throw new InvalidOperationException("新端口无效或与当前 SSH 端口相同");
            if (!SafePort.IsMatch(request.NewPort.ToString(CultureInfo.InvariantCulture)))
                throw new InvalidOperationException("新端口格式无效");

            string[] targetParts = (request.Target.TargetKey ?? "").Split('|');
            string configPath = targetParts.Length > 0 ? targetParts[0] : request.Target.ConfigPath;
            string serviceName = targetParts.Length > 1 ? targetParts[1] : request.Target.ServiceName;
            if (!SafeConfigPath.IsMatch(configPath ?? "") || !Regex.IsMatch(serviceName ?? "", @"^[A-Za-z][A-Za-z0-9_.@-]{0,63}$"))
                throw new InvalidOperationException("SSH 配置目标无法安全确认");

            if (!string.Equals(request.Target.ServiceType, "SSH", StringComparison.OrdinalIgnoreCase) &&
                IsDatabaseType(request.Target.ServiceType))
            {
                await ExecuteDatabasePortChangeAsync(
                    server,
                    password,
                    request,
                    configPath,
                    serviceName,
                    sudoPasswordProvider,
                    report,
                    cancellationToken);
                return;
            }
            if (!string.Equals(request.Target.ServiceType, "SSH", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Target.ServiceType != "HTTP" && request.Target.ServiceType != "HTTPS")
                    throw new InvalidOperationException("Linux 暂不支持该服务类型的端口修改");
                await ExecuteWebPortChangeAsync(server, password, request, configPath, serviceName, sudoPasswordProvider, report, cancellationToken);
                return;
            }

            using (IRemoteExecutor oldExecutor = await RemoteExecutorFactory.CreateAsync(server, password, cancellationToken, RemoteTransport.SSH))
            {
                PortChangeSession session = new PortChangeSession
                {
                    Target = request.Target,
                    OldPort = request.Target.Port,
                    NewPort = request.NewPort,
                    BackupPath = "/tmp/xiaobai-sshd-" + Guid.NewGuid().ToString("N") + ".bak",
                    FirewallRuleName = "XiaoBai SSH " + request.NewPort,
                    ServiceRestarted = false
                };
                SshRemoteExecutor newExecutor = null;
                try
                {
                    report(8, "正在检查 Linux 环境", "确认 sshd 配置、systemd 和端口状态");
                    RemoteSystemInfo info = await oldExecutor.GetSystemInfoAsync(cancellationToken);
                    if (!info.IsRoot && !info.CanSudo)
                    {
                        if (!info.HasSudo)
                            throw new InvalidOperationException("当前 Linux 账号不是 root，且系统没有可用的 sudo");
                        if (string.IsNullOrEmpty(server.SudoPassword))
                            server.SudoPassword = sudoPasswordProvider == null ? null : sudoPasswordProvider();
                        if (string.IsNullOrEmpty(server.SudoPassword))
                            throw new InvalidOperationException("未提供 sudo 密码，已停止修改 SSH 端口");
                        info.SudoPassword = server.SudoPassword;
                        EnsureSuccess(await RunPrivilegedAsync(oldExecutor, info, "true", TimeSpan.FromSeconds(15), cancellationToken), "验证 sudo 密码");
                    }
                    if (!info.HasSystemd)
                        throw new InvalidOperationException("当前 Linux 未检测到 systemd，首期不自动修改 SSH 服务");
                    if (!IsSupportedDistribution(info))
                        throw new InvalidOperationException("当前发行版尚未通过高风险操作验证，仅支持 Ubuntu 22.04/24.04、Debian 12、Rocky Linux 9 和 AlmaLinux 9");
                    report(14, "正在检查新端口", "确认 " + request.NewPort + " 未被占用");
                    RemoteCommandResult occupied = await RunPrivilegedAsync(
                        oldExecutor,
                        info,
                        "if ss -ltnH 2>/dev/null | awk '{print $4}' | sed 's/.*://' | grep -qx '" + request.NewPort + "'; then printf 'PORT_OCCUPIED\\n'; exit 20; fi; printf 'PORT_AVAILABLE\\n'",
                        TimeSpan.FromSeconds(20),
                        cancellationToken);
                    EnsureSuccess(occupied, "检查 Linux 新端口");
                    if ((occupied.Output ?? "").IndexOf("PORT_OCCUPIED", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new InvalidOperationException("新端口已被 Linux 其他服务占用");

                    report(22, "正在备份 SSH 配置", configPath);
                    string backupCommand = "test ! -e " + ShellQuote(session.BackupPath) + " && mode=$(stat -c '%a' " + ShellQuote(configPath) + ") && umask 077 && cp -p " + ShellQuote(configPath) + " " + ShellQuote(session.BackupPath) + " && chmod 600 " + ShellQuote(session.BackupPath) + " && test -s " + ShellQuote(session.BackupPath) + " && printf 'XIAOBAI_BACKUP_MODE=%s\\n' \"$mode\"";
                    RemoteCommandResult backupResult = await RunPrivilegedAsync(oldExecutor, info, backupCommand, TimeSpan.FromSeconds(20), cancellationToken);
                    EnsureSuccess(backupResult, "备份 SSH 配置");
                    session.OriginalMode = ReadBackupMode(backupResult.Output);
                    session.BackupCreated = true;

                    report(30, "正在配置 Linux 防火墙", request.ConfigureFirewall ? "为新 SSH 端口创建可回滚规则" : "已跳过自动防火墙配置");
                    if (request.ConfigureFirewall)
                    {
                    string sourceIp = await GetClientSourceIpAsync(oldExecutor, cancellationToken);
                    FirewallResult firewall = await AddFirewallRuleAsync(oldExecutor, info, request.NewPort, session.FirewallRuleName, sourceIp, cancellationToken);
                        session.FirewallBackend = firewall.Backend;
                        session.FirewallPortSpec = firewall.PortSpec;
                        session.FirewallSourceIp = sourceIp;
                        session.FirewallRuleCreated = firewall.Created;
                    }
                    else if (!await IsFirewallInactiveAsync(oldExecutor, info, cancellationToken))
                    {
                        throw new InvalidOperationException("Linux 防火墙处于启用状态；请勾选自动配置防火墙，避免修改后无法连接");
                    }

                    await EnsureSelinuxPortAsync(oldExecutor, info, request.NewPort, session, "ssh_port_t", cancellationToken);

                    report(42, "正在准备 SSH 双端口", "保留 " + session.OldPort + "，临时加入 " + session.NewPort);
                    string dualCommand = BuildDualListenCommand(configPath, session.BackupPath, session.OldPort, session.NewPort, serviceName);
                    EnsureSuccess(await RunPrivilegedAsync(oldExecutor, info, dualCommand, TimeSpan.FromSeconds(45), cancellationToken), "准备 SSH 双端口");
                    session.ServiceRestarted = true;

                    report(66, "正在验证新 SSH 连接", "通过新端口建立独立 SSH 会话");
                    if (!await WaitForPortAsync(server.IP, request.NewPort, TimeSpan.FromSeconds(25), cancellationToken))
                        throw new TimeoutException("新 SSH 端口没有开始监听");
                    newExecutor = new SshRemoteExecutor(server, password, request.NewPort);
                    try
                    {
                        await newExecutor.ConnectAsync(cancellationToken);
                        RemoteSystemInfo newInfo = await newExecutor.GetSystemInfoAsync(cancellationToken);
                        session.VerifiedWithNewConnection = true;
                        report(78, "新 SSH 连接验证成功", newInfo.UserName + " 已通过 " + request.NewPort + " 连接");

                        report(86, "正在关闭旧 SSH 端口", "通过新连接移除旧配置");
                        string finalizeCommand = BuildFinalizeCommand(configPath, session.BackupPath, session.OldPort, session.NewPort, serviceName);
                        EnsureSuccess(await RunPrivilegedAsync(newExecutor, newInfo, finalizeCommand, TimeSpan.FromSeconds(45), cancellationToken), "关闭旧 SSH 端口");
                    }
                    catch
                    {
                        if (!session.VerifiedWithNewConnection)
                            newExecutor.Dispose();
                        if (!session.VerifiedWithNewConnection)
                            newExecutor = null;
                        throw;
                    }

                    report(96, "正在再次验证 SSH", "确认新端口可重复连接");
                    using (SshRemoteExecutor verify = new SshRemoteExecutor(server, password, request.NewPort))
                    {
                        await verify.ConnectAsync(cancellationToken);
                        RemoteSystemInfo verifyInfo = await verify.GetSystemInfoAsync(cancellationToken);
                        verifyInfo.SudoPassword = info.SudoPassword;
                        if (session.FirewallRuleCreated)
                        {
                            await RemoveFirewallRuleAsync(verify, verifyInfo, session, cancellationToken);
                            session.FirewallRuleCreated = false;
                        }
                        EnsureSuccess(await RunPrivilegedAsync(verify, verifyInfo, "test -f " + ShellQuote(session.BackupPath) + " && rm -f " + ShellQuote(session.BackupPath) + " && printf 'SSH_BACKUP_CLEANED\\n'", TimeSpan.FromSeconds(20), cancellationToken), "清理 SSH 临时备份");
                    }
                    request.Target.Port = request.NewPort;
                    report(100, "修改完成", "SSH 已安全切换到 " + request.NewPort);
                }
                catch
                {
                    report(80, "正在自动回滚 SSH", "恢复配置、防火墙和原端口");
                    if (session.BackupCreated)
                    {
                        IRemoteExecutor rollbackExecutor = session.VerifiedWithNewConnection ? newExecutor : oldExecutor;
                        try
                        {
                            if (rollbackExecutor == null)
                                throw new InvalidOperationException("没有可用的 SSH 回滚连接");
                            RemoteSystemInfo rollbackInfo = await rollbackExecutor.GetSystemInfoAsync(CancellationToken.None);
                            rollbackInfo.SudoPassword = session.Target == null ? null : server.SudoPassword;
                            await RollbackAsync(rollbackExecutor, rollbackInfo, session, serviceName, CancellationToken.None);
                        }
                        catch (Exception rollbackError)
                        {
                            throw new InvalidOperationException("Linux SSH 修改失败，且自动回滚未完成：" + rollbackError.Message);
                        }
                    }
                    else if (session.FirewallRuleCreated)
                    {
                        RemoteSystemInfo cleanupInfo = await oldExecutor.GetSystemInfoAsync(CancellationToken.None);
                        await RemoveFirewallRuleAsync(oldExecutor, cleanupInfo, session, CancellationToken.None);
                    }
                    throw;
                }
                finally
                {
                    newExecutor?.Dispose();
                }
            }
        }

        private static async Task RollbackAsync(
            IRemoteExecutor executor,
            RemoteSystemInfo info,
            PortChangeSession session,
            string serviceName,
            CancellationToken cancellationToken)
        {
            string restore = "test -s " + ShellQuote(session.BackupPath) + " && cp -p " + ShellQuote(session.BackupPath) + " " + ShellQuote(session.Target.ConfigPath) + " && chmod " + session.OriginalMode + " " + ShellQuote(session.Target.ConfigPath) + " && " + ServiceRestartCommand(serviceName) + " && rm -f " + ShellQuote(session.BackupPath) + " && printf 'SSH_ROLLBACK_APPLIED\\n'";
            EnsureSuccess(await RunPrivilegedAsync(executor, info, restore, TimeSpan.FromSeconds(45), cancellationToken), "恢复 SSH 配置");
            if (session.FirewallRuleCreated)
                await RemoveFirewallRuleAsync(executor, info, session, cancellationToken);
            if (session.SelinuxRuleCreated)
                await RemoveSelinuxPortAsync(executor, info, session.NewPort, session.SelinuxPortType, cancellationToken);
        }

        private static async Task ExecuteDatabasePortChangeAsync(
            Server server,
            string password,
            PortChangeRequest request,
            string configPath,
            string serviceName,
            Func<string> sudoPasswordProvider,
            Action<int, string, string> report,
            CancellationToken cancellationToken)
        {
            using (IRemoteExecutor executor = await RemoteExecutorFactory.CreateAsync(server, password, cancellationToken, RemoteTransport.SSH))
            {
                PortChangeSession session = new PortChangeSession
                {
                    Target = request.Target,
                    OldPort = request.Target.Port,
                    NewPort = request.NewPort,
                    BackupPath = "/tmp/xiaobai-db-port-" + Guid.NewGuid().ToString("N") + ".bak",
                    FirewallRuleName = "XiaoBai " + request.Target.ServiceType + " " + request.NewPort,
                    ServiceRestarted = false
                };
                try
                {
                    report(8, "正在检查 Linux 数据库环境", "确认服务、配置文件和权限");
                    RemoteSystemInfo info = await executor.GetSystemInfoAsync(cancellationToken);
                    PrepareSudoPassword(server, info, sudoPasswordProvider, cancellationToken);
                    if (!info.HasSystemd)
                        throw new InvalidOperationException("当前 Linux 未检测到 systemd，首期不自动修改数据库服务");
                    if (!IsSupportedDistribution(info))
                        throw new InvalidOperationException("当前发行版尚未通过高风险操作验证，仅支持 Ubuntu 22.04/24.04、Debian 12、Rocky Linux 9 和 AlmaLinux 9");

                    report(16, "正在检查数据库新端口", "确认 " + request.NewPort + " 未被其他服务占用");
                    RemoteCommandResult occupied = await RunPrivilegedAsync(
                        executor,
                        info,
                        "if ss -ltnH 2>/dev/null | awk '{print $4}' | sed 's/.*://' | grep -qx '" + request.NewPort + "'; then printf 'PORT_OCCUPIED\\n'; exit 20; fi; printf 'PORT_AVAILABLE\\n'",
                        TimeSpan.FromSeconds(20),
                        cancellationToken);
                    EnsureSuccess(occupied, "检查 Linux 数据库新端口");
                    if ((occupied.Output ?? "").IndexOf("PORT_OCCUPIED", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new InvalidOperationException("新端口已被 Linux 其他服务占用");

                    report(24, "正在备份数据库配置", configPath);
                    RemoteCommandResult databaseBackup = await RunPrivilegedAsync(
                        executor,
                        info,
                        "test -f " + ShellQuote(configPath) + " && test ! -e " + ShellQuote(session.BackupPath) + " && mode=$(stat -c '%a' " + ShellQuote(configPath) + ") && umask 077 && cp -p " + ShellQuote(configPath) + " " + ShellQuote(session.BackupPath) + " && chmod 600 " + ShellQuote(session.BackupPath) + " && test -s " + ShellQuote(session.BackupPath) + " && printf 'XIAOBAI_BACKUP_MODE=%s\\n' \"$mode\"",
                        TimeSpan.FromSeconds(20),
                        cancellationToken);
                    EnsureSuccess(databaseBackup, "备份数据库配置");
                    session.OriginalMode = ReadBackupMode(databaseBackup.Output);
                    session.BackupCreated = true;

                    report(34, "正在配置 Linux 防火墙", request.ConfigureFirewall ? "创建新端口的可回滚规则" : "检查防火墙状态");
                    if (request.ConfigureFirewall)
                    {
                        string sourceIp = await GetClientSourceIpAsync(executor, cancellationToken);
                        FirewallResult firewall = await AddFirewallRuleAsync(executor, info, request.NewPort, session.FirewallRuleName, sourceIp, cancellationToken);
                        session.FirewallBackend = firewall.Backend;
                        session.FirewallPortSpec = firewall.PortSpec;
                        session.FirewallSourceIp = sourceIp;
                        session.FirewallRuleCreated = firewall.Created;
                    }
                    else if (!await IsFirewallInactiveAsync(executor, info, cancellationToken))
                    {
                        throw new InvalidOperationException("Linux 防火墙处于启用状态；请勾选自动配置防火墙，避免数据库新端口无法访问");
                    }

                    await EnsureSelinuxPortAsync(executor, info, request.NewPort, session, GetSelinuxPortType(request.Target.ServiceType), cancellationToken);

                    report(44, "正在修改数据库配置", request.Target.ServiceType + " · " + configPath);
                    string tempPath = "/tmp/xiaobai-db-port-" + Guid.NewGuid().ToString("N") + ".conf";
                    string command = BuildDatabasePortChangeCommand(request.Target.ServiceType, configPath, tempPath, request.NewPort, serviceName, session.OriginalMode);
                    RemoteCommandResult changed = await RunPrivilegedAsync(executor, info, command, TimeSpan.FromSeconds(45), cancellationToken);
                    EnsureSuccess(changed, "修改数据库端口");
                    session.ServiceRestarted = true;

                    report(72, "正在验证数据库新端口", "等待服务在 " + request.NewPort + " 监听");
                    if (!await WaitForPortAsync(server.IP, request.NewPort, TimeSpan.FromSeconds(35), cancellationToken))
                        throw new TimeoutException(request.Target.ServiceType + " 重启后没有在新端口监听");

                    report(90, "正在清理临时资源", "删除本次创建的防火墙规则和配置备份");
                    if (session.FirewallRuleCreated)
                    {
                        await RemoveFirewallRuleAsync(executor, info, session, cancellationToken);
                        session.FirewallRuleCreated = false;
                    }
                    EnsureSuccess(await RunPrivilegedAsync(executor, info, "test -f " + ShellQuote(session.BackupPath) + " && rm -f " + ShellQuote(session.BackupPath) + " && printf 'DB_BACKUP_CLEANED\\n'", TimeSpan.FromSeconds(20), cancellationToken), "清理数据库临时备份");
                    request.Target.Port = request.NewPort;
                    report(100, "修改完成", request.Target.ServiceType + " 已切换到 " + request.NewPort);
                }
                catch
                {
                    report(80, "正在自动回滚数据库端口", "恢复原配置并重启服务");
                    try
                    {
                        RemoteSystemInfo rollbackInfo = await executor.GetSystemInfoAsync(CancellationToken.None);
                        rollbackInfo.SudoPassword = server.SudoPassword;
                        EnsureSuccess(await RunPrivilegedAsync(
                            executor,
                            rollbackInfo,
                            "test -s " + ShellQuote(session.BackupPath) + " && cp -p " + ShellQuote(session.BackupPath) + " " + ShellQuote(configPath) + " && chmod " + session.OriginalMode + " " + ShellQuote(configPath) + " && " + ServiceRestartCommand(serviceName) + " && systemctl is-active --quiet " + ShellQuote(serviceName) + " && rm -f " + ShellQuote(session.BackupPath) + " && printf 'DB_ROLLBACK_APPLIED\\n'",
                            TimeSpan.FromSeconds(45),
                            CancellationToken.None), "恢复数据库配置");
                        if (session.FirewallRuleCreated)
                            await RemoveFirewallRuleAsync(executor, rollbackInfo, session, CancellationToken.None);
                        if (session.SelinuxRuleCreated)
                            await RemoveSelinuxPortAsync(executor, rollbackInfo, session.NewPort, session.SelinuxPortType, CancellationToken.None);
                    }
                    catch (Exception rollbackError)
                    {
                        throw new InvalidOperationException("Linux 数据库端口修改失败，且自动回滚未完成：" + rollbackError.Message);
                    }
                    throw;
                }
            }
        }

        private static async Task ExecuteWebPortChangeAsync(
            Server server,
            string password,
            PortChangeRequest request,
            string configPath,
            string serviceName,
            Func<string> sudoPasswordProvider,
            Action<int, string, string> report,
            CancellationToken cancellationToken)
        {
            using (IRemoteExecutor executor = await RemoteExecutorFactory.CreateAsync(server, password, cancellationToken, RemoteTransport.SSH))
            {
                PortChangeSession session = new PortChangeSession
                {
                    Target = request.Target,
                    OldPort = request.Target.Port,
                    NewPort = request.NewPort,
                    BackupPath = "/tmp/xiaobai-web-port-" + Guid.NewGuid().ToString("N") + ".bak",
                    FirewallRuleName = "XiaoBai " + request.Target.ServiceType + " " + request.NewPort,
                    ServiceRestarted = false
                };
                try
                {
                    if (!request.ConfirmWebConfiguration)
                        throw new InvalidOperationException("HTTP/HTTPS 修改前必须确认服务器实际配置端口");
                    report(8, "正在检查 Linux Web 环境", "确认配置文件、服务和权限");
                    RemoteSystemInfo info = await executor.GetSystemInfoAsync(cancellationToken);
                    PrepareSudoPassword(server, info, sudoPasswordProvider, cancellationToken);
                    if (!info.HasSystemd)
                        throw new InvalidOperationException("当前 Linux 未检测到 systemd，首期不自动修改 Web 服务");
                    if (!IsSupportedDistribution(info))
                        throw new InvalidOperationException("当前发行版尚未通过高风险操作验证，仅支持 Ubuntu 22.04/24.04、Debian 12、Rocky Linux 9 和 AlmaLinux 9");

                    report(16, "正在检查 Web 新端口", "确认 " + request.NewPort + " 未被占用");
                    RemoteCommandResult occupied = await RunPrivilegedAsync(
                        executor,
                        info,
                        "if ss -ltnH 2>/dev/null | awk '{print $4}' | sed 's/.*://' | grep -qx '" + request.NewPort + "'; then printf 'PORT_OCCUPIED\\n'; exit 20; fi; printf 'PORT_AVAILABLE\\n'",
                        TimeSpan.FromSeconds(20),
                        cancellationToken);
                    EnsureSuccess(occupied, "检查 Linux Web 新端口");
                    if ((occupied.Output ?? "").IndexOf("PORT_OCCUPIED", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new InvalidOperationException("新端口已被 Linux 其他服务占用");

                    report(24, "正在备份 Web 配置", configPath);
                    RemoteCommandResult backup = await RunPrivilegedAsync(
                        executor,
                        info,
                        "test -f " + ShellQuote(configPath) + " && test ! -e " + ShellQuote(session.BackupPath) + " && mode=$(stat -c '%a' " + ShellQuote(configPath) + ") && umask 077 && cp -p " + ShellQuote(configPath) + " " + ShellQuote(session.BackupPath) + " && chmod 600 " + ShellQuote(session.BackupPath) + " && test -s " + ShellQuote(session.BackupPath) + " && printf 'XIAOBAI_BACKUP_MODE=%s\\n' \"$mode\"",
                        TimeSpan.FromSeconds(20),
                        cancellationToken);
                    EnsureSuccess(backup, "备份 Web 配置");
                    session.OriginalMode = ReadBackupMode(backup.Output);
                    session.BackupCreated = true;

                    report(36, "正在配置 Linux 防火墙", request.ConfigureFirewall ? "创建新端口的可回滚规则" : "检查防火墙状态");
                    if (request.ConfigureFirewall)
                    {
                        string sourceIp = await GetClientSourceIpAsync(executor, cancellationToken);
                        FirewallResult firewall = await AddFirewallRuleAsync(executor, info, request.NewPort, session.FirewallRuleName, sourceIp, cancellationToken);
                        session.FirewallBackend = firewall.Backend;
                        session.FirewallPortSpec = firewall.PortSpec;
                        session.FirewallSourceIp = sourceIp;
                        session.FirewallRuleCreated = firewall.Created;
                    }
                    else if (!await IsFirewallInactiveAsync(executor, info, cancellationToken))
                    {
                        throw new InvalidOperationException("Linux 防火墙处于启用状态；请勾选自动配置防火墙，避免 Web 新端口无法访问");
                    }

                    report(48, "正在修改 Web 配置", request.Target.DisplayName + " · " + configPath);
                    string tempPath = configPath + ".xiaobai-" + Guid.NewGuid().ToString("N");
                    string command = BuildWebPortChangeCommand(request.Target.ServiceType, serviceName, configPath, tempPath, request.Target.Port, request.NewPort, session.OriginalMode);
                    EnsureSuccess(await RunPrivilegedAsync(executor, info, command, TimeSpan.FromSeconds(60), cancellationToken), "修改 Linux Web 端口");
                    session.ServiceRestarted = true;

                    report(74, "正在验证 Web 新端口", "等待服务在 " + request.NewPort + " 监听");
                    if (!await WaitForPortAsync(server.IP, request.NewPort, TimeSpan.FromSeconds(35), cancellationToken))
                        throw new TimeoutException(request.Target.DisplayName + " 重载后没有在新端口监听");

                    if (session.FirewallRuleCreated)
                    {
                        await RemoveFirewallRuleAsync(executor, info, session, cancellationToken);
                        session.FirewallRuleCreated = false;
                    }
                    EnsureSuccess(await RunPrivilegedAsync(executor, info, "test -f " + ShellQuote(session.BackupPath) + " && rm -f " + ShellQuote(session.BackupPath) + " && printf 'WEB_BACKUP_CLEANED\\n'", TimeSpan.FromSeconds(20), cancellationToken), "清理 Web 临时备份");
                    request.Target.Port = request.NewPort;
                    report(100, "修改完成", request.Target.DisplayName + " 已切换到 " + request.NewPort);
                }
                catch
                {
                    report(82, "正在自动回滚 Web 端口", "恢复原配置并重载服务");
                    if (session.BackupCreated)
                    {
                        try
                        {
                            RemoteSystemInfo rollbackInfo = await executor.GetSystemInfoAsync(CancellationToken.None);
                            rollbackInfo.SudoPassword = server.SudoPassword;
                            EnsureSuccess(await RunPrivilegedAsync(executor, rollbackInfo,
                                "test -s " + ShellQuote(session.BackupPath) + " && cp -p " + ShellQuote(session.BackupPath) + " " + ShellQuote(configPath) + " && chmod " + session.OriginalMode + " " + ShellQuote(configPath) + " && systemctl restart " + ShellQuote(serviceName) + " && systemctl is-active --quiet " + ShellQuote(serviceName) + " && rm -f " + ShellQuote(session.BackupPath) + " && printf 'WEB_ROLLBACK_APPLIED\\n'",
                                TimeSpan.FromSeconds(60),
                                CancellationToken.None), "恢复 Web 配置");
                            if (session.FirewallRuleCreated)
                                await RemoveFirewallRuleAsync(executor, rollbackInfo, session, CancellationToken.None);
                        }
                        catch (Exception rollbackError)
                        {
                            throw new InvalidOperationException("Linux Web 端口修改失败，且自动回滚未完成：" + rollbackError.Message);
                        }
                    }
                    else if (session.FirewallRuleCreated)
                    {
                        RemoteSystemInfo cleanupInfo = await executor.GetSystemInfoAsync(CancellationToken.None);
                        await RemoveFirewallRuleAsync(executor, cleanupInfo, session, CancellationToken.None);
                    }
                    throw;
                }
            }
        }

        private static void PrepareSudoPassword(
            Server server,
            RemoteSystemInfo info,
            Func<string> provider,
            CancellationToken cancellationToken)
        {
            if (info.IsRoot || info.CanSudo)
                return;
            if (!info.HasSudo)
                throw new InvalidOperationException("当前 Linux 账号没有可用的 sudo");
            server.SudoPassword = provider == null ? null : provider();
            if (string.IsNullOrEmpty(server.SudoPassword))
                throw new InvalidOperationException("未提供 sudo 密码，已停止修改");
            info.SudoPassword = server.SudoPassword;
        }

        private static string BuildDatabasePortChangeCommand(string type, string configPath, string tempPath, int newPort, string serviceName, string originalMode)
        {
            string config = ShellQuote(configPath);
            string temp = ShellQuote(tempPath);
            string content;
            if (type == "MySQL" || type == "MariaDB")
            {
                content = "awk -v new='" + newPort + "' 'BEGIN{section=0; changed=0} /^[[:space:]]*\\[/{section=(tolower($0) ~ /^[[:space:]]*\\[mysqld\\]/)} section && /^[[:space:]]*port[[:space:]]*=/{sub(/[0-9]+[[:space:]]*$/,new); changed=1} {print} END{if(!changed) print \"[mysqld]\\nport=\" new}' " + config + " > " + temp;
            }
            else if (type == "MongoDB")
            {
                content = "awk -v new='" + newPort + "' 'BEGIN{changed=0} /^[[:space:]]*port[[:space:]]*:/ {sub(/[0-9]+[[:space:]]*$/,new); changed=1} {print} END{if(!changed) print \"net:\\n  port: \" new}' " + config + " > " + temp;
            }
            else if (type == "Redis")
            {
                content = "awk -v new='" + newPort + "' 'BEGIN{changed=0} !/^[[:space:]]*#/ && /^[[:space:]]*port[[:space:]]+[0-9]+[[:space:]]*$/ {sub(/[0-9]+[[:space:]]*$/,new); changed=1} {print} END{if(!changed) print \"port \" new}' " + config + " > " + temp;
            }
            else
            {
                throw new InvalidOperationException("Linux 暂不支持该数据库类型的端口修改");
            }
            if (!Regex.IsMatch(originalMode ?? "", @"^[0-7]{3,4}$"))
                throw new InvalidOperationException("数据库配置文件原权限无效，已停止修改");
            return content + " && test -s " + temp + " && mv " + temp + " " + config + " && chmod " + originalMode + " " + config + " && " +
                "systemctl restart " + ShellQuote(serviceName) + " && systemctl is-active --quiet " + ShellQuote(serviceName) + " && printf 'DB_PORT_CHANGE_APPLIED\\n' || { rm -f " + temp + "; exit 30; }";
        }

        private static string BuildWebPortChangeCommand(string type, string serviceName, string configPath, string tempPath, int oldPort, int newPort, string originalMode)
        {
            string config = ShellQuote(configPath);
            string temp = ShellQuote(tempPath);
            if (!Regex.IsMatch(originalMode ?? "", @"^[0-7]{3,4}$"))
                throw new InvalidOperationException("Web 配置文件原权限无效，已停止修改");
            string command;
            if (serviceName == "nginx")
            {
                command = "sed -E 's/^([[:space:]]*listen[[:space:]]+)" + oldPort + "([[:space:];].*)$/\\1" + newPort + "\\2/' " + config + " > " + temp + "; " +
                    "grep -Eiq '^[[:space:]]*listen[[:space:]]+" + newPort + "([[:space:];]|$)' " + temp + " && nginx -t";
            }
            else if (serviceName == "apache2" || serviceName == "httpd")
            {
                command = "sed -E -e 's/^([[:space:]]*Listen[[:space:]]+)" + oldPort + "([[:space:]].*)?$/\\1" + newPort + "\\2/' -e 's/(<VirtualHost[[:space:]]+\\*:)" + oldPort + "(>)/\\1" + newPort + "\\2/' " + config + " > " + temp + "; " +
                    "grep -Eiq '^[[:space:]]*Listen[[:space:]]+" + newPort + "([[:space:]]|$)' " + temp + " && (command -v apachectl >/dev/null 2>&1 && apachectl -t || httpd -t)";
            }
            else
            {
                throw new InvalidOperationException("Linux 暂不支持该 Web 服务的端口修改");
            }
            return command + " && test -s " + temp + " && mv " + temp + " " + config + " && chmod " + originalMode + " " + config + " && systemctl reload " + ShellQuote(serviceName) + " && systemctl is-active --quiet " + ShellQuote(serviceName) + " && printf 'WEB_PORT_CHANGE_APPLIED\\n' || { rm -f " + temp + "; exit 30; }";
        }

        private static string BuildDualListenCommand(string configPath, string backupPath, int oldPort, int newPort, string serviceName)
        {
            string config = ShellQuote(configPath);
            return "SSHD=$(command -v sshd 2>/dev/null || printf /usr/sbin/sshd); test -x \"$SSHD\" && test -f " + config + " && " +
                "if grep -Eiq '^[[:space:]]*Port[[:space:]]+[0-9]+' " + config + "; then " +
                "grep -Eiq '^[[:space:]]*Port[[:space:]]+" + newPort + "([[:space:]]|$)' " + config + " || printf '\\nPort " + newPort + "\\n' >> " + config + "; " +
                "else printf '\\nPort " + oldPort + "\\nPort " + newPort + "\\n' >> " + config + "; fi; " +
                "\"$SSHD\" -t -f " + config + " && " + ServiceReloadCommand(serviceName) + " && systemctl is-active --quiet " + ShellQuote(serviceName) + " && sleep 2 && printf 'SSH_DUAL_LISTEN_APPLIED\\n'";
        }

        private static string BuildFinalizeCommand(string configPath, string backupPath, int oldPort, int newPort, string serviceName)
        {
            string config = ShellQuote(configPath);
            string temporary = ShellQuote(configPath + ".xiaobai-final");
            return "SSHD=$(command -v sshd 2>/dev/null || printf /usr/sbin/sshd); " +
                "awk -v old='" + oldPort + "' -v new='" + newPort + "' '/^[[:space:]]*#/ {print; next} /^[[:space:]]*[Pp][Oo][Rr][Tt][[:space:]]+/ {if ($2 == old) next; print; next} {print}' " + config + " > " + temporary + " && " +
                "grep -Eiq '^[[:space:]]*Port[[:space:]]+" + newPort + "([[:space:]]|$)' " + temporary + " && \"$SSHD\" -t -f " + temporary + " && " +
                "cat " + temporary + " > " + config + " && rm -f " + temporary + " && " + ServiceReloadCommand(serviceName) + " && systemctl is-active --quiet " + ShellQuote(serviceName) + " && sleep 2 && printf 'SSH_OLD_PORT_REMOVED\\n'";
        }

        private static async Task<RemoteCommandResult> RunPrivilegedAsync(
            IRemoteExecutor executor,
            RemoteSystemInfo info,
            string command,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (info != null && info.IsRoot)
                return await executor.ExecuteCommandAsync(command, timeout, cancellationToken);
            if (info != null && info.CanSudo)
                return await executor.ExecuteCommandAsync("sudo -n sh -c " + ShellQuote(command), timeout, cancellationToken);
            ILinuxPrivilegedExecutor privileged = executor as ILinuxPrivilegedExecutor;
            if (info == null || !info.HasSudo || string.IsNullOrEmpty(info.SudoPassword) || privileged == null)
                throw new InvalidOperationException("当前 SSH 会话没有可用的 Linux 提权通道");
            return await privileged.ExecuteSudoCommandAsync(command, info.SudoPassword, timeout, cancellationToken);
        }

        private static async Task<LinuxSshTarget> DetectSshTargetAsync(IRemoteExecutor executor, RemoteSystemInfo info, CancellationToken cancellationToken)
        {
            const string command =
                "config=/etc/ssh/sshd_config; [ -f \"$config\" ] || config=/etc/sshd_config; " +
                "service=sshd; systemctl cat sshd.service >/dev/null 2>&1 || service=ssh; " +
                "sshd_bin=$(command -v sshd 2>/dev/null || printf /usr/sbin/sshd); port=$(\"$sshd_bin\" -T -f \"$config\" 2>/dev/null | awk '$1==\"port\"{print $2; exit}'); [ -n \"$port\" ] || port=$(printf '%s\\n' \"$SSH_CONNECTION\" | awk '{print $4}'); [ -n \"$port\" ] || port=22; " +
                "state=$(systemctl is-active \"$service\" 2>/dev/null); [ -n \"$state\" ] || state=unknown; " +
                "printf 'XIAOBAI_CONFIG=%s\\nXIAOBAI_SERVICE=%s\\nXIAOBAI_PORT=%s\\nXIAOBAI_STATE=%s\\n' \"$config\" \"$service\" \"$port\" \"$state\"";
            RemoteCommandResult result = await executor.ExecuteCommandAsync(command, TimeSpan.FromSeconds(25), cancellationToken);
            EnsureSuccess(result, "识别 Linux SSH 服务");
            Dictionary<string, string> values = ParseFields(result.Output);
            string configPath = GetValue(values, "CONFIG");
            string serviceName = GetValue(values, "SERVICE");
            int port;
            if (!SafeConfigPath.IsMatch(configPath) || !int.TryParse(GetValue(values, "PORT"), out port) || port < 1 || port > 65535)
                return null;
            return new LinuxSshTarget
            {
                ConfigPath = configPath,
                ServiceName = serviceName,
                Port = port,
                ServiceStatus = GetValue(values, "STATE"),
                IsSupported = (info != null && info.HasSystemd) && (serviceName == "ssh" || serviceName == "sshd")
            };
        }

        private static async Task<IList<DetectedServicePort>> DetectDatabaseTargetsAsync(
            IRemoteExecutor executor,
            CancellationToken cancellationToken)
        {
            const string command =
                "for svc in mysql mariadb mysqld mongod mongodb redis redis-server; do " +
                "if systemctl cat \"$svc.service\" >/dev/null 2>&1; then " +
                "state=$(systemctl is-active \"$svc\" 2>/dev/null); [ -n \"$state\" ] || state=unknown; type=; config=; port=; " +
                "case \"$svc\" in " +
                "mysql|mysqld) type=MySQL; port=3306; for f in /etc/mysql/mysql.conf.d/mysqld.cnf /etc/mysql/mariadb.conf.d/50-server.cnf /etc/my.cnf /etc/my.cnf.d/server.cnf; do [ -f \"$f\" ] && config=\"$f\" && break; done;; " +
                "mariadb) type=MariaDB; port=3306; for f in /etc/mysql/mariadb.conf.d/50-server.cnf /etc/my.cnf /etc/my.cnf.d/server.cnf; do [ -f \"$f\" ] && config=\"$f\" && break; done;; " +
                "mongod|mongodb) type=MongoDB; port=27017; for f in /etc/mongod.conf /etc/mongodb.conf; do [ -f \"$f\" ] && config=\"$f\" && break; done;; " +
                "redis|redis-server) type=Redis; port=6379; for f in /etc/redis/redis.conf /etc/redis.conf; do [ -f \"$f\" ] && config=\"$f\" && break; done;; esac; " +
                "if [ \"$type\" = MySQL ] || [ \"$type\" = MariaDB ]; then if [ -n \"$config\" ]; then p=$(awk 'BEGIN{s=0} /^[[:space:]]*\\[/{s=tolower($0) ~ /\\[mysqld\\]/} s && /^[[:space:]]*port[[:space:]]*=/{gsub(/[^0-9]/,\"\",$0); print; exit}' \"$config\"); [ -n \"$p\" ] && port=$p; fi; " +
                "elif [ \"$type\" = MongoDB ]; then if [ -n \"$config\" ]; then p=$(awk '/^[[:space:]]*port[[:space:]]*:/ {gsub(/[^0-9]/,\"\",$0); print; exit}' \"$config\"); [ -n \"$p\" ] && port=$p; fi; " +
                "elif [ \"$type\" = Redis ]; then if [ -n \"$config\" ]; then p=$(awk '!/^[[:space:]]*#/ && /^[[:space:]]*port[[:space:]]+[0-9]+/ {print $2; exit}' \"$config\"); [ -n \"$p\" ] && port=$p; fi; fi; " +
                "printf 'XIAOBAI_DB|%s|%s|%s|%s|%s\\n' \"$type\" \"$svc\" \"$config\" \"$port\" \"$state\"; fi; done";
            RemoteCommandResult result = await executor.ExecuteCommandAsync(command, TimeSpan.FromSeconds(30), cancellationToken);
            EnsureSuccess(result, "识别 Linux 数据库服务");
            List<DetectedServicePort> services = new List<DetectedServicePort>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in (result.Output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = line.Split(new[] { '|' }, 6);
                if (fields.Length != 6 || fields[0] != "XIAOBAI_DB")
                    continue;
                int port;
                if (!int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                    continue;
                string type = fields[1];
                string service = fields[2];
                string dedupe = type;
                if (!seen.Add(dedupe))
                    continue;
                services.Add(new DetectedServicePort
                {
                    ServiceType = type,
                    DisplayName = type,
                    ServiceName = service,
                    ConfigPath = fields[3],
                    Protocol = "TCP",
                    Port = port,
                    IsSupported = (type == "MySQL" || type == "MariaDB" || type == "MongoDB" || type == "Redis") && SafeConfigPath.IsMatch(fields[3]),
                    TargetKey = fields[3] + "|" + service,
                    ServiceStatus = fields[5]
                });
            }
            return services;
        }

        private static async Task<IList<DetectedServicePort>> DetectWebTargetsAsync(
            IRemoteExecutor executor,
            CancellationToken cancellationToken)
        {
            const string command =
                "for svc in nginx apache2 httpd; do " +
                "if systemctl cat \"$svc.service\" >/dev/null 2>&1; then " +
                "state=$(systemctl is-active \"$svc\" 2>/dev/null); [ -n \"$state\" ] || state=unknown; " +
                "if [ \"$svc\" = nginx ]; then files=\"/etc/nginx/nginx.conf /etc/nginx/sites-enabled/default /etc/nginx/conf.d/default.conf\"; " +
                "else files=\"/etc/apache2/ports.conf /etc/httpd/conf/httpd.conf\"; fi; " +
                "for f in $files; do if [ -f \"$f\" ]; then " +
                "if [ \"$svc\" = nginx ]; then ports=$(grep -hE '^[[:space:]]*listen[[:space:]]+[0-9]+' \"$f\" 2>/dev/null | sed -E 's/^[[:space:]]*listen[[:space:]]+([0-9]+).*/\\1/' | sort -n -u); " +
                "else ports=$(grep -hE '^[[:space:]]*Listen[[:space:]]+[0-9]+' \"$f\" 2>/dev/null | sed -E 's/^[[:space:]]*Listen[[:space:]]+([0-9]+).*/\\1/' | sort -n -u); fi; " +
                "for p in $ports; do type=HTTP; [ \"$p\" = 443 ] && type=HTTPS; printf 'XIAOBAI_WEB|%s|%s|%s|%s|%s\\n' \"$type\" \"$svc\" \"$f\" \"$p\" \"$state\"; done; " +
                "fi; done; fi; done";
            RemoteCommandResult result = await executor.ExecuteCommandAsync(command, TimeSpan.FromSeconds(30), cancellationToken);
            EnsureSuccess(result, "识别 Linux Web 服务");
            List<DetectedServicePort> services = new List<DetectedServicePort>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in (result.Output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = line.Split(new[] { '|' }, 6);
                if (fields.Length != 6 || fields[0] != "XIAOBAI_WEB")
                    continue;
                int port;
                if (!int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                    continue;
                string key = fields[1] + "|" + fields[2] + "|" + port;
                if (!seen.Add(key))
                    continue;
                services.Add(new DetectedServicePort
                {
                    ServiceType = fields[1],
                    DisplayName = fields[1] + " · " + fields[2],
                    ServiceName = fields[2],
                    ConfigPath = fields[3],
                    Protocol = "TCP",
                    Port = port,
                    IsSupported = true,
                    TargetKey = fields[3] + "|" + fields[2],
                    ServiceStatus = fields[5]
                });
            }
            return services;
        }

        private static bool IsDatabaseType(string type)
        {
            return type == "MySQL" || type == "MariaDB" || type == "MongoDB" || type == "Redis";
        }

        private static async Task<FirewallResult> AddFirewallRuleAsync(IRemoteExecutor executor, RemoteSystemInfo info, int port, string ruleName, string sourceIp, CancellationToken cancellationToken)
        {
            IPAddress parsedSource;
            if (!IPAddress.TryParse(sourceIp, out parsedSource))
                throw new InvalidOperationException("无法识别当前 SSH 管理电脑的 IP，未自动修改 Linux 防火墙");
            string source = ShellQuote(parsedSource.ToString());
            string name = ShellQuote(ruleName);
            string family = parsedSource.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4";
            string ufw = "if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -qi '^Status: active'; then " +
                "if ufw status 2>/dev/null | awk -v src=" + source + " -v wanted='" + port + "/tcp' 'index($0,src) && index($0,wanted){found=1} END{exit !found}'; then printf 'XIAOBAI_FIREWALL=ufw-existing\\n'; else ufw allow from " + source + " to any port " + port + " proto tcp comment " + name + " >/dev/null && printf 'XIAOBAI_FIREWALL=ufw\\n'; fi; exit 0; fi; " +
                "if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state 2>/dev/null | grep -q running; then " +
                "if firewall-cmd --query-rich-rule=\"rule family='" + family + "' source address='" + parsedSource.ToString() + "' port port='" + port + "' protocol='tcp' accept\" >/dev/null 2>&1; then printf 'XIAOBAI_FIREWALL=firewalld-existing\\n'; else firewall-cmd --permanent --add-rich-rule=\"rule family='" + family + "' source address='" + parsedSource.ToString() + "' port port='" + port + "' protocol='tcp' accept\" >/dev/null && firewall-cmd --reload >/dev/null && printf 'XIAOBAI_FIREWALL=firewalld\\n'; fi; exit 0; fi; " +
                "if command -v nft >/dev/null 2>&1 && nft list ruleset 2>/dev/null | grep -Eq 'hook input|policy (drop|reject)'; then printf 'XIAOBAI_FIREWALL=unsupported-nftables\\n'; exit 30; fi; if command -v iptables >/dev/null 2>&1 && iptables -S INPUT 2>/dev/null | grep -Eq -- '^-P INPUT (DROP|REJECT)|-j (DROP|REJECT)'; then printf 'XIAOBAI_FIREWALL=unsupported-iptables\\n'; exit 30; fi; printf 'XIAOBAI_FIREWALL=none\\n'; exit 0";
            RemoteCommandResult result = await RunPrivilegedAsync(executor, info, ufw, TimeSpan.FromSeconds(30), cancellationToken);
            string backend = GetValue(ParseFields(result.Output), "FIREWALL");
            if (result.ExitCode != 0 || (backend != "none" && backend != "ufw" && backend != "ufw-existing" && backend != "firewalld" && backend != "firewalld-existing"))
                throw new InvalidOperationException("未识别可安全回滚的 Linux 防火墙（仅支持活动的 ufw 或 firewalld）");
            return new FirewallResult
            {
                Backend = backend.Replace("-existing", ""),
                PortSpec = port + "/tcp",
                Created = backend != "none" && backend.IndexOf("-existing", StringComparison.OrdinalIgnoreCase) < 0
            };
        }

        private static async Task RemoveFirewallRuleAsync(IRemoteExecutor executor, RemoteSystemInfo info, PortChangeSession session, CancellationToken cancellationToken)
        {
            IPAddress parsedSource;
            if (!IPAddress.TryParse(session.FirewallSourceIp, out parsedSource))
                throw new InvalidOperationException("无法确认本次 Linux 防火墙规则的来源 IP，拒绝执行宽泛删除");
            string family = parsedSource.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4";
            string command = session.FirewallBackend == "ufw"
                ? "ufw delete allow from " + ShellQuote(parsedSource.ToString()) + " to any port " + session.NewPort + " proto tcp >/dev/null || true"
                : "firewall-cmd --permanent --remove-rich-rule=\"rule family='" + family + "' source address='" + parsedSource.ToString() + "' port port='" + session.NewPort + "' protocol='tcp' accept\" >/dev/null && firewall-cmd --reload >/dev/null || true";
            EnsureSuccess(await RunPrivilegedAsync(executor, info, command + "; printf 'XIAOBAI_FIREWALL_REMOVED\\n'", TimeSpan.FromSeconds(30), cancellationToken), "回滚 Linux 防火墙规则");
        }

        private static async Task<bool> IsFirewallInactiveAsync(IRemoteExecutor executor, RemoteSystemInfo info, CancellationToken cancellationToken)
        {
            RemoteCommandResult result = await RunPrivilegedAsync(executor, info,
                "if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -qi '^Status: active'; then exit 10; fi; if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state 2>/dev/null | grep -q running; then exit 10; fi; if command -v nft >/dev/null 2>&1 && nft list ruleset 2>/dev/null | grep -Eq 'hook input|policy (drop|reject)'; then exit 10; fi; if command -v iptables >/dev/null 2>&1 && iptables -S INPUT 2>/dev/null | grep -Eq -- '^-P INPUT (DROP|REJECT)|-j (DROP|REJECT)'; then exit 10; fi; exit 0",
                TimeSpan.FromSeconds(20), cancellationToken);
            return result.ExitCode == 0;
        }

        private static async Task EnsureSelinuxPortAsync(
            IRemoteExecutor executor,
            RemoteSystemInfo info,
            int port,
            PortChangeSession session,
            string portType,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(portType))
                return;
            RemoteCommandResult state = await RunPrivilegedAsync(
                executor,
                info,
                "if command -v getenforce >/dev/null 2>&1 && [ \"$(getenforce 2>/dev/null)\" = \"Enforcing\" ]; then printf 'XIAOBAI_SELINUX=enforcing\\n'; else printf 'XIAOBAI_SELINUX=inactive\\n'; fi",
                TimeSpan.FromSeconds(15),
                cancellationToken);
            EnsureSuccess(state, "检测 SELinux");
            if (GetValue(ParseFields(state.Output), "SELINUX") != "enforcing")
                return;

            RemoteCommandResult configure = await RunPrivilegedAsync(
                executor,
                info,
                "if ! command -v semanage >/dev/null 2>&1; then printf 'XIAOBAI_SELINUX=missing-semanage\\n'; exit 40; fi; " +
                "if semanage port -l 2>/dev/null | awk '$1==\"" + portType + "\" && $2==\"tcp\" {for(i=3;i<=NF;i++) print $i}' | tr ',' '\\n' | grep -qx '" + port + "'; then printf 'XIAOBAI_SELINUX=existing\\n'; " +
                "else semanage port -a -t " + portType + " -p tcp " + port + " && printf 'XIAOBAI_SELINUX=created\\n'; fi",
                TimeSpan.FromSeconds(30),
                cancellationToken);
            EnsureSuccess(configure, "配置 SELinux SSH 端口");
            session.SelinuxPortType = portType;
            session.SelinuxRuleCreated = GetValue(ParseFields(configure.Output), "SELINUX") == "created";
        }

        private static async Task RemoveSelinuxPortAsync(
            IRemoteExecutor executor,
            RemoteSystemInfo info,
            int port,
            string portType,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(portType))
                return;
            RemoteCommandResult result = await RunPrivilegedAsync(
                executor,
                info,
                "if command -v semanage >/dev/null 2>&1; then semanage port -d -t " + portType + " -p tcp " + port + " >/dev/null 2>&1 || true; fi; printf 'XIAOBAI_SELINUX_REMOVED\\n'",
                TimeSpan.FromSeconds(30),
                cancellationToken);
            EnsureSuccess(result, "回滚 SELinux SSH 端口");
        }

        private static string GetSelinuxPortType(string databaseType)
        {
            if (databaseType == "MySQL" || databaseType == "MariaDB")
                return "mysqld_port_t";
            if (databaseType == "MongoDB")
                return "mongod_port_t";
            if (databaseType == "Redis")
                return "redis_port_t";
            return "";
        }

        private static bool IsSupportedDistribution(RemoteSystemInfo info)
        {
            if (info == null)
                return false;
            string id = (info.DistributionId ?? "").Trim().ToLowerInvariant();
            string version = (info.OsVersion ?? "").Trim();
            if (id == "ubuntu")
                return version.StartsWith("22.04", StringComparison.Ordinal) || version.StartsWith("24.04", StringComparison.Ordinal);
            if (id == "debian")
                return version == "12" || version.StartsWith("12.", StringComparison.Ordinal);
            if (id == "rocky" || id == "almalinux")
                return version == "9" || version.StartsWith("9.", StringComparison.Ordinal);
            return false;
        }

        private static async Task<string> GetClientSourceIpAsync(IRemoteExecutor executor, CancellationToken cancellationToken)
        {
            RemoteCommandResult result = await executor.ExecuteCommandAsync(
                "printf '%s\\n' \"$SSH_CONNECTION\" | awk '{print $1}'",
                TimeSpan.FromSeconds(15),
                cancellationToken);
            EnsureSuccess(result, "识别 SSH 管理电脑 IP");
            string source = (result.Output ?? "").Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            IPAddress parsed;
            if (!IPAddress.TryParse(source, out parsed))
                throw new InvalidOperationException("无法识别当前 SSH 管理电脑的 IP，未自动修改 Linux 防火墙");
            return parsed.ToString();
        }

        private static async Task<bool> WaitForPortAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
        {
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using (System.Net.Sockets.TcpClient client = new System.Net.Sockets.TcpClient())
                    using (CancellationTokenSource probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        probe.CancelAfter(TimeSpan.FromSeconds(2));
                        await client.ConnectAsync(host, port, probe.Token);
                        if (client.Connected)
                            return true;
                    }
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw;
                }
                catch { }
                await Task.Delay(1000, cancellationToken);
            }
            return false;
        }

        private static string ServiceRestartCommand(string serviceName)
        {
            return "systemctl restart " + ShellQuote(serviceName);
        }

        private static string ServiceReloadCommand(string serviceName)
        {
            return "systemctl reload " + ShellQuote(serviceName);
        }

        private static string ShellQuote(string value)
        {
            return "'" + (value ?? "").Replace("'", "'\\''") + "'";
        }

        private static string ReadBackupMode(string output)
        {
            string mode = ParseFields(output).TryGetValue("BACKUP_MODE", out string value) ? value : "";
            if (!Regex.IsMatch(mode, @"^[0-7]{3,4}$"))
                throw new InvalidOperationException("未能读取原配置文件权限，已停止修改");
            return mode;
        }

        private static Dictionary<string, string> ParseFields(string output)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in (output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith("XIAOBAI_", StringComparison.Ordinal))
                    continue;
                int separator = line.IndexOf('=');
                if (separator > 0)
                    values[line.Substring("XIAOBAI_".Length, separator - "XIAOBAI_".Length)] = line.Substring(separator + 1).Trim();
            }
            return values;
        }

        private static string GetValue(Dictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : "";
        }

        private static void EnsureLinux(Server server)
        {
            if (server == null || server.Type != ServerType.Linux)
                throw new InvalidOperationException("该操作仅支持 Linux 服务器");
        }

        private static void EnsureSuccess(RemoteCommandResult result, string operation)
        {
            if (result != null && result.ExitCode == 0)
                return;
            throw new InvalidOperationException(operation + "失败：" + RemoteErrorFormatter.Format(result));
        }

        private sealed class LinuxSshTarget
        {
            public string ConfigPath { get; set; }
            public string ServiceName { get; set; }
            public int Port { get; set; }
            public string ServiceStatus { get; set; }
            public bool IsSupported { get; set; }
        }

        private sealed class FirewallResult
        {
            public string Backend { get; set; }
            public string PortSpec { get; set; }
            public bool Created { get; set; }
        }
    }
}
