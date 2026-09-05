using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RDPManager
{
    public sealed class PortManagementService
    {
        private readonly List<IPortServiceAdapter> adapters = new List<IPortServiceAdapter>
        {
            new RdpPortAdapter(),
            new SshPortAdapter(),
            new WebPortAdapter(),
            new OraclePortAdapter(),
            new MySqlPortAdapter(),
            new RedisPortAdapter(),
            new MongoDbPortAdapter()
        };

        public async Task<PortInspectionResult> InspectAsync(Server server, string password, CancellationToken cancellationToken)
        {
            if (server.Type != ServerType.Windows)
                throw new InvalidOperationException("当前首期端口管理仅支持 Windows 服务器");

            using (IRemoteExecutor executor = await RemoteExecutorFactory.CreateAsync(server, password, cancellationToken))
            {
                RemoteSystemInfo info = await executor.GetSystemInfoAsync(cancellationToken);
                PortInspectionResult result = new PortInspectionResult
                {
                    HostName = info.HostName,
                    Transport = executor.Transport.ToString()
                };
                foreach (IPortServiceAdapter adapter in adapters)
                {
                    try
                    {
                        IList<DetectedServicePort> found = await adapter.DetectAsync(executor, cancellationToken);
                        result.Services.AddRange(found);
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add(adapter.ServiceType + "：" + ex.Message);
                    }
                }
                return result;
            }
        }

        public async Task<IList<int>> GetAvailablePortsAsync(
            Server server,
            string password,
            DetectedServicePort target,
            CancellationToken cancellationToken)
        {
            if (server.Type != ServerType.Windows)
                throw new InvalidOperationException("当前端口快速检测暂时只支持 Windows 服务器");

            using (IRemoteExecutor executor = await RemoteExecutorFactory.CreateAsync(server, password, cancellationToken))
            {
                RemoteCommandResult result = await executor.ExecutePowerShellAsync(
                    "@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty LocalPort -Unique | Sort-Object) | ConvertTo-Json -Compress",
                    TimeSpan.FromSeconds(20),
                    cancellationToken);
                EnsureSuccess(result, "检测服务器端口");

                HashSet<int> occupied = new HashSet<int>();
                if (!string.IsNullOrWhiteSpace(result.Output))
                {
                    System.Text.Json.JsonElement root = System.Text.Json.JsonDocument.Parse(result.Output.Trim()).RootElement;
                    if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (System.Text.Json.JsonElement item in root.EnumerateArray())
                        {
                            int port;
                            if (item.TryGetInt32(out port))
                                occupied.Add(port);
                        }
                    }
                    else if (root.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        int port;
                        if (root.TryGetInt32(out port))
                            occupied.Add(port);
                    }
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
            CancellationToken cancellationToken)
        {
            if (request == null || request.Target == null)
                throw new ArgumentNullException(nameof(request));
            if (request.NewPort < 1 || request.NewPort > 65535)
                throw new InvalidOperationException("新端口必须在 1-65535 之间");
            if (request.NewPort == request.Target.Port)
                throw new InvalidOperationException("新端口与当前端口相同");
            if (!request.Target.IsSupported)
                throw new InvalidOperationException("该服务未识别到明确的配置目标，请先确认配置文件或站点绑定");
            if ((request.Target.ServiceType == "HTTP" || request.Target.ServiceType == "HTTPS") &&
                !request.ConfirmWebConfiguration)
                throw new InvalidOperationException("HTTP/HTTPS 修改前必须确认服务器实际配置端口");

            IPortServiceAdapter adapter = FindAdapter(request.Target);
            using (IRemoteExecutor executor = await RemoteExecutorFactory.CreateAsync(server, password, cancellationToken))
            {
                PortChangeSession session = null;
                FirewallPreparation firewallPreparation = null;
                try
                {
                    report(6, "正在确认本机 IP 通行", "检测服务器防火墙是否允许当前管理电脑访问新端口");
                    firewallPreparation = await EnsureClientFirewallAccessAsync(
                        executor,
                        request.NewPort,
                        request.Target.Protocol,
                        cancellationToken);
                    report(12, "本机 IP 通行已确认", firewallPreparation.AllowedBefore
                        ? firewallPreparation.SourceIp + " 已被允许"
                        : firewallPreparation.SourceIp + " 已临时加入允许规则");

                    report(16, "正在检查新端口", "确认端口未被占用");
                    RemoteCommandResult occupied = await executor.ExecutePowerShellAsync(
                        "if (Get-NetTCPConnection -State Listen -LocalPort " + request.NewPort + " -ErrorAction SilentlyContinue) { throw '新端口已被占用' }",
                        TimeSpan.FromSeconds(20),
                        cancellationToken);
                    EnsureSuccess(occupied, "端口占用检查");

                    if (string.Equals(request.Target.ServiceType, "SSH", StringComparison.OrdinalIgnoreCase))
                    {
                        await ExecuteSshSafeMigrationAsync(
                            (SshPortAdapter)adapter,
                            executor,
                            server,
                            password,
                            request,
                            firewallPreparation,
                            report,
                            cancellationToken);
                        return;
                    }

                    report(20, "正在备份配置", request.Target.ConfigPath);
                    session = await adapter.ApplyAsync(
                        executor,
                        request.Target,
                        request.NewPort,
                        request.ConfigureFirewall,
                        message => report(36, "正在修改 " + request.Target.DisplayName, message),
                        cancellationToken);

                    bool isOracle = string.Equals(request.Target.ServiceType, "Oracle", StringComparison.OrdinalIgnoreCase);
                    report(72,
                        isOracle ? "正在验证 Oracle 服务" : "正在验证新端口",
                        request.Target.DisplayName + " · " + request.NewPort +
                        (isOracle ? " · 等待 XE/XEPDB1 注册" : ""));
                    bool open = isOracle
                        ? await WaitForOracleServiceAsync(server.IP, request.NewPort, request.Target, TimeSpan.FromSeconds(35), cancellationToken)
                        : await WaitForPortAsync(server.IP, request.NewPort, TimeSpan.FromSeconds(25), cancellationToken);
                    if (!open)
                        throw new TimeoutException(isOracle
                            ? "Oracle Listener 已修改，但 XE/XEPDB1 未在新端口完成注册"
                            : "新端口在服务重启后没有监听");

                    report(90,
                        isOracle ? "Oracle 服务验证成功" : "端口验证成功",
                        isOracle ? "TNS 已识别数据库服务，可接受客户端连接" : "新端口已监听，保留旧配置等待确认");
                    if (firewallPreparation != null && firewallPreparation.RuleCreated)
                    {
                        await RemoveFirewallRuleAsync(executor, firewallPreparation.RuleName, cancellationToken);
                        firewallPreparation.RuleCreated = false;
                    }
                    request.Target.Port = request.NewPort;
                    report(100, "修改完成", request.Target.DisplayName + " 已切换到 " + request.NewPort);
                }
                catch
                {
                    if (session != null)
                    {
                        try
                        {
                            report(78, "正在自动回滚", "恢复原配置和防火墙规则");
                            await adapter.RollbackAsync(executor, session, message => report(86, "回滚中", message), cancellationToken);
                        }
                        catch (Exception rollbackException)
                        {
                            throw new InvalidOperationException("端口修改失败，且自动回滚未完成：" + rollbackException.Message);
                        }
                    }
                    if (firewallPreparation != null && firewallPreparation.RuleCreated)
                    {
                        try { await RemoveFirewallRuleAsync(executor, firewallPreparation.RuleName, CancellationToken.None); }
                        catch { }
                    }
                    throw;
                }
            }
        }

        private async Task ExecuteSshSafeMigrationAsync(
            SshPortAdapter adapter,
            IRemoteExecutor oldExecutor,
            Server server,
            string password,
            PortChangeRequest request,
            FirewallPreparation firewallPreparation,
            Action<int, string, string> report,
            CancellationToken cancellationToken)
        {
            PortChangeSession session = null;
            IRemoteExecutor newExecutor = null;
            try
            {
                report(20, "正在准备 SSH 双端口", "保留旧端口，同时加入新端口");
                session = await adapter.PrepareDualListenAsync(
                    oldExecutor,
                    request.Target,
                    request.NewPort,
                    request.ConfigureFirewall,
                    message => report(36, "正在准备 SSH", message),
                    cancellationToken);

                report(68, "正在验证新 SSH 端口", "建立独立的 SSH 连接");
                bool open = await WaitForPortAsync(server.IP, request.NewPort, TimeSpan.FromSeconds(25), cancellationToken);
                if (!open)
                    throw new TimeoutException("新 SSH 端口没有监听，保留旧端口并开始回滚");

                newExecutor = new SshRemoteExecutor(server.IP, request.NewPort, server.Username, password);
                await newExecutor.ConnectAsync(cancellationToken);
                RemoteSystemInfo info = await newExecutor.GetSystemInfoAsync(cancellationToken);
                session.VerifiedWithNewConnection = true;
                report(78, "新 SSH 端口验证成功", info.UserName + " 已通过 " + request.NewPort + " 连接");

                report(86, "正在清理旧 SSH 端口", "通过新端口移除旧端口和临时规则");
                await adapter.FinalizeDualListenAsync(
                    newExecutor,
                    session,
                    message => report(90, "正在清理旧端口", message),
                    cancellationToken);

                report(96, "正在再次验证", "确认服务器只保留新 SSH 端口");
                newExecutor.Dispose();
                newExecutor = null;
                newExecutor = new SshRemoteExecutor(server.IP, request.NewPort, server.Username, password);
                await newExecutor.ConnectAsync(cancellationToken);
                await newExecutor.GetSystemInfoAsync(cancellationToken);
                if (firewallPreparation != null && firewallPreparation.RuleCreated)
                {
                    await RemoveFirewallRuleAsync(newExecutor, firewallPreparation.RuleName, cancellationToken);
                    firewallPreparation.RuleCreated = false;
                }
                await adapter.CleanupBackupAsync(newExecutor, session, cancellationToken);
                request.Target.Port = request.NewPort;
                report(100, "修改完成", "SSH 已切换到 " + request.NewPort);
            }
            catch
            {
                if (session != null)
                {
                    IRemoteExecutor rollbackExecutor = session.VerifiedWithNewConnection ? newExecutor : oldExecutor;
                    try
                    {
                        report(82, "正在自动回滚 SSH", "恢复原配置和旧端口");
                        if (rollbackExecutor == null)
                            throw new InvalidOperationException("没有可用的 SSH 回滚连接");
                        await adapter.RollbackAsync(rollbackExecutor, session, message => report(88, "SSH 回滚中", message), CancellationToken.None);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new InvalidOperationException("SSH 修改失败，且自动回滚未完成：" + rollbackException.Message);
                    }
                }
                if (firewallPreparation != null && firewallPreparation.RuleCreated)
                {
                    try
                    {
                        IRemoteExecutor cleanupExecutor = session != null && session.VerifiedWithNewConnection ? newExecutor : oldExecutor;
                        if (cleanupExecutor != null)
                            await RemoveFirewallRuleAsync(cleanupExecutor, firewallPreparation.RuleName, CancellationToken.None);
                    }
                    catch { }
                }
                throw;
            }
            finally
            {
                newExecutor?.Dispose();
            }
        }

        private IPortServiceAdapter FindAdapter(DetectedServicePort target)
        {
            IPortServiceAdapter adapter = adapters.FirstOrDefault(item =>
                string.Equals(item.ServiceType, target.ServiceType, StringComparison.OrdinalIgnoreCase) ||
                (item is MySqlPortAdapter && (target.ServiceType == "MySQL" || target.ServiceType == "MariaDB")) ||
                (item is WebPortAdapter && (target.ServiceType == "HTTP" || target.ServiceType == "HTTPS")));
            if (adapter == null)
                throw new InvalidOperationException("没有找到 " + target.DisplayName + " 的端口适配器");
            return adapter;
        }

        private static async Task<bool> WaitForPortAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
        {
            DateTime deadline = DateTime.Now.Add(timeout);
            while (DateTime.Now < deadline)
            {
                try
                {
                    using (System.Net.Sockets.TcpClient client = new System.Net.Sockets.TcpClient())
                    using (CancellationTokenSource probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        probeTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                        await client.ConnectAsync(host, port, probeTimeout.Token);
                        if (client.Connected)
                            return true;
                    }
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw;
                }
                catch
                {
                }
                await Task.Delay(1000, cancellationToken);
            }
            return false;
        }

        private static async Task<bool> WaitForOracleServiceAsync(
            string host,
            int port,
            DetectedServicePort target,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            DateTime deadline = DateTime.Now.Add(timeout);
            List<string> serviceNames = GetOracleServiceNames(target);
            while (DateTime.Now < deadline)
            {
                foreach (string serviceName in serviceNames)
                {
                    if (await ProbeOracleServiceAsync(host, port, serviceName, cancellationToken))
                        return true;
                }
                await Task.Delay(1000, cancellationToken);
            }
            return false;
        }

        private static List<string> GetOracleServiceNames(DetectedServicePort target)
        {
            List<string> names = new List<string>();
            if (target != null && !string.IsNullOrWhiteSpace(target.TargetKey))
            {
                string[] parts = target.TargetKey.Split('|');
                if (parts.Length >= 3)
                {
                    foreach (string name in parts[2].Split(';'))
                    {
                        string value = name.Trim();
                        if (value.Length > 0 && value.All(character =>
                            char.IsLetterOrDigit(character) || character == '.' || character == '_' ||
                            character == '$' || character == '#' || character == '-'))
                            names.Add(value);
                    }
                }
            }
            foreach (string fallback in new[] { "XE.localdomain", "XEPDB1", "XE" })
            {
                if (!names.Any(value => string.Equals(value, fallback, StringComparison.OrdinalIgnoreCase)))
                    names.Add(fallback);
            }
            return names
                .OrderByDescending(value => value.IndexOf("XEPDB", StringComparison.OrdinalIgnoreCase) >= 0)
                .ThenBy(value => value.IndexOf('.', StringComparison.OrdinalIgnoreCase) < 0)
                .ToList();
        }

        private static async Task<bool> ProbeOracleServiceAsync(
            string host,
            int port,
            string serviceName,
            CancellationToken cancellationToken)
        {
            try
            {
                using (System.Net.Sockets.TcpClient client = new System.Net.Sockets.TcpClient())
                using (CancellationTokenSource probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    probeTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                    await client.ConnectAsync(host, port, probeTimeout.Token);
                    byte[] packet = BuildOracleConnectPacket(host, port, serviceName);
                    System.Net.Sockets.NetworkStream stream = client.GetStream();
                    await stream.WriteAsync(packet.AsMemory(0, packet.Length), probeTimeout.Token);
                    byte[] response = new byte[4096];
                    int count = await stream.ReadAsync(response.AsMemory(0, response.Length), probeTimeout.Token);
                    if (count < 5)
                        return false;

                    byte packetType = response[4];
                    return packetType == 2 || packetType == 5 || packetType == 6 || packetType == 11;
                }
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw;
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static byte[] BuildOracleConnectPacket(string host, int port, string serviceName)
        {
            string descriptor = "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=" + host + ")(PORT=" + port +
                "))(CONNECT_DATA=(SERVER=DEDICATED)(SERVICE_NAME=" + serviceName +
                ")(CID=(PROGRAM=xiaobai)(HOST=client)(USER=xiaobai))))";
            byte[] connectData = System.Text.Encoding.ASCII.GetBytes(descriptor);
            int packetLength = 58 + connectData.Length;
            byte[] packet = new byte[packetLength];

            WriteUInt16BigEndian(packet, 0, packetLength);
            packet[4] = 1;
            WriteUInt16BigEndian(packet, 8, 314);
            WriteUInt16BigEndian(packet, 10, 300);
            WriteUInt16BigEndian(packet, 12, 0x0c41);
            WriteUInt16BigEndian(packet, 14, 8192);
            WriteUInt16BigEndian(packet, 16, 32767);
            WriteUInt16BigEndian(packet, 18, 32520);
            WriteUInt16BigEndian(packet, 22, 0x0100);
            WriteUInt16BigEndian(packet, 24, connectData.Length);
            WriteUInt16BigEndian(packet, 26, 58);
            WriteUInt32BigEndian(packet, 28, 512);
            packet[32] = 0x41;
            packet[33] = 0x41;
            Buffer.BlockCopy(connectData, 0, packet, 58, connectData.Length);
            return packet;
        }

        private static void WriteUInt16BigEndian(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)((value >> 8) & 0xff);
            buffer[offset + 1] = (byte)(value & 0xff);
        }

        private static void WriteUInt32BigEndian(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)((value >> 24) & 0xff);
            buffer[offset + 1] = (byte)((value >> 16) & 0xff);
            buffer[offset + 2] = (byte)((value >> 8) & 0xff);
            buffer[offset + 3] = (byte)(value & 0xff);
        }

        private static async Task<FirewallPreparation> EnsureClientFirewallAccessAsync(
            IRemoteExecutor executor,
            int port,
            string protocol,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(protocol, "TCP", StringComparison.OrdinalIgnoreCase))
                return new FirewallPreparation { SourceIp = "未知", AllowedBefore = true };

            RemoteCommandResult sourceResult = await executor.ExecutePowerShellAsync(
                "$ip=''; " +
                "if($env:SSH_CONNECTION) { $ip=($env:SSH_CONNECTION -split '\\s+')[0] } " +
                "if(-not $ip -and $PSSenderInfo -and $PSSenderInfo.ConnectionString -match '://([^/:]+)') { $ip=$matches[1] } " +
                "[pscustomobject]@{SourceIp=$ip} | ConvertTo-Json -Compress",
                TimeSpan.FromSeconds(20),
                cancellationToken);
            EnsureSuccess(sourceResult, "识别本机 IP");
            System.Text.Json.JsonElement sourceJson = System.Text.Json.JsonDocument.Parse(sourceResult.Output.Trim()).RootElement;
            string sourceIp = sourceJson.TryGetProperty("SourceIp", out System.Text.Json.JsonElement value) ? value.ToString() : "";
            System.Net.IPAddress parsedAddress;
            if (!System.Net.IPAddress.TryParse(sourceIp, out parsedAddress))
                throw new InvalidOperationException("无法识别当前管理电脑的 IP，未自动修改防火墙");
            sourceIp = parsedAddress.ToString();

            string ruleName = "XiaoBai-Client-" + port + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string script = @"
$ErrorActionPreference='Stop'
$allowed=$false
foreach($rule in @(Get-NetFirewallRule -Direction Inbound -Action Allow -Enabled True -ErrorAction SilentlyContinue)) {
    $portFilter=$rule | Get-NetFirewallPortFilter
    if($null -eq $portFilter) { continue }
    $protocol=[string]$portFilter.Protocol
    if($protocol -ne 'TCP' -and $protocol -ne 'Any') { continue }
    $local=[string]$portFilter.LocalPort
    $portMatch=$local -eq 'Any'
    if(-not $portMatch) {
        foreach($part in ($local -split ',')) { if($part.Trim() -eq __PORT__) { $portMatch=$true } }
    }
    if(-not $portMatch) { continue }
    $addressFilter=$rule | Get-NetFirewallAddressFilter
    $remote=@($addressFilter.RemoteAddress)
    if($remote.Count -eq 0 -or $remote -contains 'Any' -or $remote -contains __SOURCE_IP__) { $allowed=$true; break }
}
$created=$false
if(-not $allowed) {
    New-NetFirewallRule -DisplayName __RULE_NAME__ -Direction Inbound -Protocol TCP -LocalPort __PORT__ -RemoteAddress __SOURCE_IP__ -Action Allow -Profile Any -ErrorAction Stop | Out-Null
    $created=$true
}
[pscustomobject]@{AllowedBefore=$allowed;RuleCreated=$created} | ConvertTo-Json -Compress
"
                .Replace("__PORT__", port.ToString())
                .Replace("__SOURCE_IP__", QuotePowerShell(sourceIp))
                .Replace("__RULE_NAME__", QuotePowerShell(ruleName));
            RemoteCommandResult firewallResult = await executor.ExecutePowerShellAsync(script, TimeSpan.FromSeconds(30), cancellationToken);
            EnsureSuccess(firewallResult, "确认服务器防火墙");
            System.Text.Json.JsonElement resultJson = System.Text.Json.JsonDocument.Parse(firewallResult.Output.Trim()).RootElement;
            return new FirewallPreparation
            {
                SourceIp = sourceIp,
                AllowedBefore = resultJson.TryGetProperty("AllowedBefore", out System.Text.Json.JsonElement allowed) && allowed.ValueKind == System.Text.Json.JsonValueKind.True,
                RuleCreated = resultJson.TryGetProperty("RuleCreated", out System.Text.Json.JsonElement created) && created.ValueKind == System.Text.Json.JsonValueKind.True,
                RuleName = ruleName
            };
        }

        private static async Task RemoveFirewallRuleAsync(
            IRemoteExecutor executor,
            string ruleName,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(ruleName))
                return;
            RemoteCommandResult result = await executor.ExecutePowerShellAsync(
                "Remove-NetFirewallRule -DisplayName " + QuotePowerShell(ruleName) + " -ErrorAction SilentlyContinue",
                TimeSpan.FromSeconds(20),
                cancellationToken);
            EnsureSuccess(result, "清理本机 IP 防火墙规则");
        }

        private static string QuotePowerShell(string value)
        {
            return "'" + (value ?? "").Replace("'", "''") + "'";
        }

        private static void EnsureSuccess(RemoteCommandResult result, string operation)
        {
            if (result.ExitCode == 0)
                return;
            throw new InvalidOperationException(operation + "失败：" + RemoteErrorFormatter.Format(result));
        }
    }

    public sealed class RdpPortAdapter : IPortServiceAdapter
    {
        private const string RegistryPath = "HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server\\WinStations\\RDP-Tcp";
        public string ServiceType { get { return "RDP"; } }

        public async Task<IList<DetectedServicePort>> DetectAsync(IRemoteExecutor executor, CancellationToken cancellationToken)
        {
            RemoteCommandResult result = await executor.ExecutePowerShellAsync(
                "$item=Get-ItemProperty -Path '" + RegistryPath + "' -Name PortNumber -ErrorAction Stop; " +
                "[pscustomobject]@{ServiceType='RDP';DisplayName='Windows RDP';ServiceName='TermService';ConfigPath='Registry RDP-Tcp';Protocol='TCP';Port=[int]$item.PortNumber} | ConvertTo-Json -Compress",
                TimeSpan.FromSeconds(20),
                cancellationToken);
            EnsureSuccess(result, "RDP 检测");
            return ParseOne(result.Output);
        }

        public async Task<PortChangeSession> ApplyAsync(IRemoteExecutor executor, DetectedServicePort target, int newPort, bool configureFirewall, Action<string> log, CancellationToken cancellationToken)
        {
            string rule = "XiaoBai-RDP-" + newPort + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string script = "$ErrorActionPreference='Stop'; " +
                "$old=(Get-ItemProperty -Path '" + RegistryPath + "' -Name PortNumber).PortNumber; " +
                "if (" + (configureFirewall ? "$true" : "$false") + ") { New-NetFirewallRule -DisplayName '" + rule + "' -Direction Inbound -Protocol TCP -LocalPort " + newPort + " -Action Allow -Profile Any -ErrorAction SilentlyContinue | Out-Null }; " +
                "Set-ItemProperty -Path '" + RegistryPath + "' -Name PortNumber -Value " + newPort + "; " +
                "Restart-Service -Name TermService -Force; Start-Sleep -Seconds 3; 'PORT_CHANGE_APPLIED'";
            log("修改 RDP-Tcp 端口");
            PortChangeSession session = new PortChangeSession { Target = target, OldPort = target.Port, NewPort = newPort, FirewallRuleName = rule, FirewallRuleCreated = configureFirewall, ServiceRestarted = true };
            try
            {
                RemoteCommandResult result = await executor.ExecutePowerShellAsync(script, TimeSpan.FromSeconds(35), cancellationToken);
                EnsureSuccess(result, "RDP 修改");
                return session;
            }
            catch
            {
                try
                {
                    await RollbackAsync(executor, session, log, CancellationToken.None);
                }
                catch
                {
                }
                throw;
            }
        }

        public async Task RollbackAsync(IRemoteExecutor executor, PortChangeSession session, Action<string> log, CancellationToken cancellationToken)
        {
            string script = "$ErrorActionPreference='Stop'; " +
                "Set-ItemProperty -Path '" + RegistryPath + "' -Name PortNumber -Value " + session.OldPort + "; " +
                "if (" + (session.FirewallRuleCreated ? "$true" : "$false") + ") { Remove-NetFirewallRule -DisplayName '" + session.FirewallRuleName + "' -ErrorAction SilentlyContinue }; " +
                "Restart-Service -Name TermService -Force";
            log("恢复 RDP-Tcp 原端口 " + session.OldPort);
            RemoteCommandResult result = await executor.ExecutePowerShellAsync(script, TimeSpan.FromSeconds(35), cancellationToken);
            EnsureSuccess(result, "RDP 回滚");
        }

        private static IList<DetectedServicePort> ParseOne(string output)
        {
            System.Text.Json.JsonElement item = System.Text.Json.JsonDocument.Parse(output.Trim()).RootElement;
            return new List<DetectedServicePort>
            {
                new DetectedServicePort
                {
                    ServiceType = item.GetProperty("ServiceType").ToString(),
                    DisplayName = item.GetProperty("DisplayName").ToString(),
                    ServiceName = item.GetProperty("ServiceName").ToString(),
                    ConfigPath = item.GetProperty("ConfigPath").ToString(),
                    Protocol = item.GetProperty("Protocol").ToString(),
                    Port = item.GetProperty("Port").GetInt32(),
                    IsSupported = true
                }
            };
        }

        private static void EnsureSuccess(RemoteCommandResult result, string operation)
        {
            if (result.ExitCode == 0)
                return;
            throw new InvalidOperationException(operation + "失败：" + RemoteErrorFormatter.Format(result));
        }
    }

    public sealed class SshPortAdapter : WindowsPortServiceAdapter
    {
        public override string ServiceType { get { return "SSH"; } }

        public async Task<PortChangeSession> PrepareDualListenAsync(
            IRemoteExecutor executor,
            DetectedServicePort target,
            int newPort,
            bool configureFirewall,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            string backupPath = target.ConfigPath + ".xiao-bai-backup";
            string ruleName = "XiaoBai-SSH-" + newPort + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string script = @"
$ErrorActionPreference='Stop'
Copy-Item -LiteralPath __CONFIG_PATH__ -Destination __BACKUP_PATH__ -Force
$lines=[System.Collections.Generic.List[string]](Get-Content -LiteralPath __CONFIG_PATH__)
$foundOld=$false
$foundNew=$false
for($i=0;$i -lt $lines.Count;$i++) {
    if($lines[$i] -notmatch '^\s*#' -and $lines[$i] -match '^\s*Port\s+(\d+)') {
        $value=[int]$matches[1]
        if($value -eq __OLD_PORT__) { $foundOld=$true }
        if($value -eq __NEW_PORT__) { $foundNew=$true }
    }
}
if(-not $foundOld) { $lines.Add('Port __OLD_PORT__') }
if(-not $foundNew) { $lines.Add('Port __NEW_PORT__') }
                [IO.File]::WriteAllLines(__CONFIG_PATH__, $lines, (New-Object Text.UTF8Encoding($false)))
if(__CONFIGURE_FIREWALL__) { New-NetFirewallRule -DisplayName __FIREWALL_RULE__ -Direction Inbound -Protocol TCP -LocalPort __NEW_PORT__ -Action Allow -Profile Any -ErrorAction Stop | Out-Null }
$sshd='C:\Windows\System32\OpenSSH\sshd.exe'
if(-not (Test-Path $sshd)) { $sshd=(Get-Command sshd.exe -ErrorAction Stop).Source }
& $sshd -t -f __CONFIG_PATH__
if($LASTEXITCODE -ne 0) { throw 'sshd 配置语法检查失败' }
                Restart-Service -Name __SERVICE_NAME__ -Force
                Start-Sleep -Seconds 3
                if((Get-Service -Name __SERVICE_NAME__).Status -ne 'Running'){ throw 'sshd 服务未恢复运行' }
'SSH_DUAL_LISTEN_APPLIED'
"
                .Replace("__CONFIG_PATH__", Quote(target.ConfigPath))
                .Replace("__BACKUP_PATH__", Quote(backupPath))
                .Replace("__SERVICE_NAME__", Quote(target.ServiceName))
                .Replace("__OLD_PORT__", target.Port.ToString())
                .Replace("__NEW_PORT__", newPort.ToString())
                .Replace("__FIREWALL_RULE__", Quote(ruleName))
                .Replace("__CONFIGURE_FIREWALL__", configureFirewall ? "$true" : "$false");

            PortChangeSession session = new PortChangeSession
            {
                Target = target,
                OldPort = target.Port,
                NewPort = newPort,
                BackupPath = backupPath,
                FirewallRuleName = ruleName,
                FirewallRuleCreated = configureFirewall,
                ServiceRestarted = true
            };
            log("保留旧端口 " + target.Port + "，加入新端口 " + newPort);
            try
            {
                RemoteCommandResult result = await executor.ExecutePowerShellAsync(script, TimeSpan.FromSeconds(45), cancellationToken);
                EnsureSuccess(result, "SSH 双端口准备");
                return session;
            }
            catch
            {
                try { await RollbackAsync(executor, session, log, CancellationToken.None); } catch { }
                throw;
            }
        }

        public async Task FinalizeDualListenAsync(
            IRemoteExecutor executor,
            PortChangeSession session,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            string script = @"
$ErrorActionPreference='Stop'
$lines=[System.Collections.Generic.List[string]](Get-Content -LiteralPath __CONFIG_PATH__)
$result=[System.Collections.Generic.List[string]]::new()
$foundNew=$false
foreach($line in $lines) {
    if($line -notmatch '^\s*#' -and $line -match '^\s*Port\s+(\d+)' -and [int]$matches[1] -eq __OLD_PORT__) { continue }
    if($line -notmatch '^\s*#' -and $line -match '^\s*Port\s+(\d+)' -and [int]$matches[1] -eq __NEW_PORT__) { $foundNew=$true }
    $result.Add($line)
}
if(-not $foundNew) { $result.Add('Port __NEW_PORT__') }
[IO.File]::WriteAllLines(__CONFIG_PATH__, $result, (New-Object Text.UTF8Encoding($false)))
$sshd='C:\Windows\System32\OpenSSH\sshd.exe'
if(-not (Test-Path $sshd)) { $sshd=(Get-Command sshd.exe -ErrorAction Stop).Source }
& $sshd -t -f __CONFIG_PATH__
if($LASTEXITCODE -ne 0) { throw 'sshd 最终配置语法检查失败' }
Restart-Service -Name __SERVICE_NAME__ -Force
Start-Sleep -Seconds 3
if((Get-Service -Name __SERVICE_NAME__).Status -ne 'Running') { throw 'sshd 服务在清理旧端口后未恢复运行' }
'SSH_OLD_PORT_REMOVED'
"
                .Replace("__CONFIG_PATH__", Quote(session.Target.ConfigPath))
                .Replace("__SERVICE_NAME__", Quote(session.Target.ServiceName))
                .Replace("__OLD_PORT__", session.OldPort.ToString())
                .Replace("__NEW_PORT__", session.NewPort.ToString());
            log("删除旧 SSH 端口 " + session.OldPort);
            RemoteCommandResult result = await executor.ExecutePowerShellAsync(script, TimeSpan.FromSeconds(45), cancellationToken);
            EnsureSuccess(result, "SSH 清理旧端口");
        }

        public async Task CleanupBackupAsync(IRemoteExecutor executor, PortChangeSession session, CancellationToken cancellationToken)
        {
            RemoteCommandResult result = await executor.ExecutePowerShellAsync(
                "Remove-Item -LiteralPath " + Quote(session.BackupPath) + " -Force -ErrorAction SilentlyContinue",
                TimeSpan.FromSeconds(20),
                cancellationToken);
            EnsureSuccess(result, "清理 SSH 备份");
        }

        protected override string DetectionScript
        {
            get
            {
                return @"
$item=Get-CimInstance Win32_Service | Where-Object { $_.Name -eq 'sshd' -or $_.PathName -match '(?i)sshd.exe' } | Select-Object -First 1
$config='C:\ProgramData\ssh\sshd_config'
$port=22
if (Test-Path $config) { $line=Get-Content $config | Where-Object { $_ -match '^\s*Port\s+\d+' } | Select-Object -First 1; if ($line -match '^\s*Port\s+(\d+)') { $port=[int]$matches[1] } }
if ($null -ne $item) { [pscustomobject]@{ServiceType='SSH';DisplayName='OpenSSH Server';ServiceName=$item.Name;ConfigPath=$config;Protocol='TCP';Port=$port} | ConvertTo-Json -Compress }
";
            }
        }
        protected override string ChangeScript
        {
            get
            {
                return @"
$ErrorActionPreference='Stop'
Copy-Item -LiteralPath __CONFIG_PATH__ -Destination __BACKUP_PATH__ -Force
$text=Get-Content -LiteralPath __CONFIG_PATH__ -Raw
if ($text -match '(?m)^\s*Port\s+\d+') { $text=[regex]::Replace($text,'(?m)^\s*Port\s+\d+','Port __NEW_PORT__') } else { $text += [Environment]::NewLine + 'Port __NEW_PORT__' }
[IO.File]::WriteAllText(__CONFIG_PATH__, $text, (New-Object Text.UTF8Encoding($false)))
if (__CONFIGURE_FIREWALL__) { New-NetFirewallRule -DisplayName __FIREWALL_RULE__ -Direction Inbound -Protocol TCP -LocalPort __NEW_PORT__ -Action Allow -Profile Any -ErrorAction SilentlyContinue | Out-Null }
Restart-Service -Name __SERVICE_NAME__ -Force
Start-Sleep -Seconds 3
'PORT_CHANGE_APPLIED'
";
            }
        }
        protected override string RollbackScript
        {
            get
            {
                return @"
$ErrorActionPreference='Stop'
Copy-Item -LiteralPath __BACKUP_PATH__ -Destination __CONFIG_PATH__ -Force
if (__FIREWALL_RULE_CREATED__) { Remove-NetFirewallRule -DisplayName __FIREWALL_RULE__ -ErrorAction SilentlyContinue }
Restart-Service -Name __SERVICE_NAME__ -Force
'PORT_ROLLBACK_APPLIED'
";
            }
        }
    }
}
