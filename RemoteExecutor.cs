using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RDPManager
{
    public enum RemoteTransport
    {
        SSH,
        WinRM
    }

    public sealed class RemoteCommandResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
    }

    public sealed class RemoteSystemInfo
    {
        public string HostName { get; set; }
        public string UserName { get; set; }
        public DateTime LastBootUpTime { get; set; }
        public bool IsLinux { get; set; }
        public bool IsRoot { get; set; }
        public bool HasSudo { get; set; }
        public bool CanSudo { get; set; }
        public string SudoPassword { get; set; }
        public string OperatingSystem { get; set; }
        public string DistributionId { get; set; }
        public string OsVersion { get; set; }
        public string Kernel { get; set; }
        public string Architecture { get; set; }
        public string CpuCores { get; set; }
        public string MemoryBytes { get; set; }
        public string RootFreeBytes { get; set; }
        public string PackageManager { get; set; }
        public string Firewall { get; set; }
        public bool HasSystemd { get; set; }
        public string SshPort { get; set; }
    }

    public interface IRemoteExecutor : IDisposable
    {
        RemoteTransport Transport { get; }
        Task ConnectAsync(CancellationToken cancellationToken);
        Task<RemoteSystemInfo> GetSystemInfoAsync(CancellationToken cancellationToken);
        Task<RemoteCommandResult> ExecutePowerShellAsync(string script, TimeSpan timeout, CancellationToken cancellationToken);
        Task<RemoteCommandResult> ExecuteCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken);
    }

    public interface IRemoteFileTransfer
    {
        Task DownloadFileAsync(string remotePath, string localPath, Action<ulong> progress, CancellationToken cancellationToken);
    }

    public interface ILinuxPrivilegedExecutor
    {
        Task<RemoteCommandResult> ExecuteSudoCommandAsync(string command, string sudoPassword, TimeSpan timeout, CancellationToken cancellationToken);
    }

    public sealed class SshRemoteExecutor : IRemoteExecutor, IRemoteFileTransfer, ILinuxPrivilegedExecutor
    {
        private readonly string host;
        private readonly int port;
        private readonly string username;
        private readonly string password;
        private readonly string privateKeyPath;
        private readonly string privateKeyPassphrase;
        private readonly bool isLinux;
        private SshRemoteClient client;

        public RemoteTransport Transport { get { return RemoteTransport.SSH; } }

        public SshRemoteExecutor(string host, int port, string username, string password)
            : this(host, port, username, password, null, null, false)
        {
        }

        public SshRemoteExecutor(Server server, string password)
            : this(
                server == null ? "" : server.IP,
                server == null ? 22 : RemoteExecutorFactory.GetManagementPort(server, RemoteTransport.SSH),
                server == null ? "" : server.Username,
                password,
                server != null && server.Type == ServerType.Linux && server.SshCredentialMode == SshCredentialMode.PrivateKey ? server.SshPrivateKeyPath : "",
                server != null && server.Type == ServerType.Linux && server.SshCredentialMode == SshCredentialMode.PrivateKey ? server.SshPrivateKeyPassphrase : "",
                server != null && server.Type == ServerType.Linux)
        {
        }

        public SshRemoteExecutor(Server server, string password, int managementPort)
            : this(
                server == null ? "" : server.IP,
                managementPort,
                server == null ? "" : server.Username,
                password,
                server != null && server.SshCredentialMode == SshCredentialMode.PrivateKey ? server.SshPrivateKeyPath : "",
                server != null && server.SshCredentialMode == SshCredentialMode.PrivateKey ? server.SshPrivateKeyPassphrase : "",
                server != null && server.Type == ServerType.Linux)
        {
        }

        private SshRemoteExecutor(
            string host,
            int port,
            string username,
            string password,
            string privateKeyPath,
            string privateKeyPassphrase,
            bool isLinux)
        {
            this.host = host;
            this.port = port;
            this.username = username;
            this.password = password ?? "";
            this.privateKeyPath = privateKeyPath ?? "";
            this.privateKeyPassphrase = privateKeyPassphrase ?? "";
            this.isLinux = isLinux;
        }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            client = string.IsNullOrWhiteSpace(privateKeyPath)
                ? new SshRemoteClient(host, port, username, password)
                : new SshRemoteClient(host, port, username, privateKeyPath, privateKeyPassphrase);
            await client.ConnectAsync(cancellationToken);
        }

        public async Task<RemoteSystemInfo> GetSystemInfoAsync(CancellationToken cancellationToken)
        {
            if (isLinux)
                return await GetLinuxSystemInfoAsync(cancellationToken);

            const string script = "$os=Get-CimInstance Win32_OperatingSystem; " +
                "[pscustomobject]@{ HostName=$env:COMPUTERNAME; " +
                "UserName=[Security.Principal.WindowsIdentity]::GetCurrent().Name; " +
                "LastBootUpTime=$os.LastBootUpTime.ToUniversalTime().ToString('o') } | ConvertTo-Json -Compress";
            RemoteCommandResult result = await ExecutePowerShellAsync(script, TimeSpan.FromSeconds(25), cancellationToken);
            EnsureSuccess(result, "SSH 远程权限验证");
            Dictionary<string, object> values = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(result.Output.Trim());
            DateTime bootTime;
            if (values == null || !DateTime.TryParse(GetValue(values, "LastBootUpTime"), out bootTime))
                throw new InvalidOperationException("SSH 返回的系统信息格式无法识别");
            return new RemoteSystemInfo
            {
                HostName = GetValue(values, "HostName"),
                UserName = GetValue(values, "UserName"),
                LastBootUpTime = bootTime.ToUniversalTime(),
                IsLinux = false,
                OperatingSystem = "Windows"
            };
        }

        private async Task<RemoteSystemInfo> GetLinuxSystemInfoAsync(CancellationToken cancellationToken)
        {
            const string command =
                "if [ -r /etc/os-release ]; then . /etc/os-release; else ID=unknown; VERSION_ID=unknown; PRETTY_NAME=unknown; fi; " +
                "BOOT_EPOCH=$(awk '/^btime /{print $2; exit}' /proc/stat 2>/dev/null); " +
                "HAS_SUDO=false; CAN_SUDO=false; IS_ROOT=false; command -v sudo >/dev/null 2>&1 && HAS_SUDO=true; if [ \"$(id -u 2>/dev/null)\" = \"0\" ]; then IS_ROOT=true; CAN_SUDO=true; elif [ \"$HAS_SUDO\" = true ] && sudo -n true >/dev/null 2>&1; then CAN_SUDO=true; fi; " +
                "HAS_SYSTEMD=false; command -v systemctl >/dev/null 2>&1 && HAS_SYSTEMD=true; " +
                "PACKAGE_MANAGER=unknown; if command -v apt-get >/dev/null 2>&1; then PACKAGE_MANAGER=apt; elif command -v dnf >/dev/null 2>&1; then PACKAGE_MANAGER=dnf; elif command -v yum >/dev/null 2>&1; then PACKAGE_MANAGER=yum; fi; " +
                "FIREWALL=none; if command -v ufw >/dev/null 2>&1; then FIREWALL=ufw; elif command -v firewall-cmd >/dev/null 2>&1; then FIREWALL=firewalld; elif command -v nft >/dev/null 2>&1; then FIREWALL=nftables; elif command -v iptables >/dev/null 2>&1; then FIREWALL=iptables; fi; " +
                "SSHD=$(command -v sshd 2>/dev/null || printf /usr/sbin/sshd); SSH_PORT=$(printf '%s\\n' \"$SSH_CONNECTION\" | awk '{print $4}'); [ -n \"$SSH_PORT\" ] || SSH_PORT=$(\"$SSHD\" -T 2>/dev/null | awk '$1==\"port\"{print $2; exit}'); " +
                "[ -n \"$SSH_PORT\" ] || SSH_PORT=22; " +
                "printf 'XIAOBAI_HOSTNAME=%s\\n' \"$(hostname 2>/dev/null)\"; " +
                "printf 'XIAOBAI_USERNAME=%s\\n' \"$(id -un 2>/dev/null)\"; " +
                "printf 'XIAOBAI_OS=%s\\n' \"${PRETTY_NAME:-$ID}\"; " +
                "printf 'XIAOBAI_OS_ID=%s\\n' \"${ID:-unknown}\"; " +
                "printf 'XIAOBAI_OS_VERSION=%s\\n' \"${VERSION_ID:-unknown}\"; " +
                "printf 'XIAOBAI_KERNEL=%s\\n' \"$(uname -r 2>/dev/null)\"; " +
                "printf 'XIAOBAI_ARCHITECTURE=%s\\n' \"$(uname -m 2>/dev/null)\"; " +
                "printf 'XIAOBAI_CPU_CORES=%s\\n' \"$(getconf _NPROCESSORS_ONLN 2>/dev/null || nproc 2>/dev/null || printf unknown)\"; " +
                "printf 'XIAOBAI_MEMORY_BYTES=%s\\n' \"$(awk '/MemTotal:/{printf \"%.0f\", $2 * 1024; exit}' /proc/meminfo 2>/dev/null)\"; " +
                "printf 'XIAOBAI_ROOT_FREE_BYTES=%s\\n' \"$(df -P -B1 / 2>/dev/null | awk 'NR==2{print $4; exit}')\"; " +
                "printf 'XIAOBAI_BOOT_EPOCH=%s\\n' \"$BOOT_EPOCH\"; " +
                "printf 'XIAOBAI_IS_ROOT=%s\\n' \"$IS_ROOT\"; " +
                "printf 'XIAOBAI_HAS_SUDO=%s\\n' \"$HAS_SUDO\"; " +
                "printf 'XIAOBAI_CAN_SUDO=%s\\n' \"$CAN_SUDO\"; " +
                "printf 'XIAOBAI_HAS_SYSTEMD=%s\\n' \"$HAS_SYSTEMD\"; " +
                "printf 'XIAOBAI_PACKAGE_MANAGER=%s\\n' \"$PACKAGE_MANAGER\"; " +
                "printf 'XIAOBAI_FIREWALL=%s\\n' \"$FIREWALL\"; " +
                "printf 'XIAOBAI_SSH_PORT=%s\\n' \"$SSH_PORT\"";

            RemoteCommandResult result = await ExecuteCommandAsync(command, TimeSpan.FromSeconds(25), cancellationToken);
            EnsureSuccess(result, "读取 Linux 系统信息");
            Dictionary<string, string> values = ParseLinuxFields(result.Output);
            long bootEpoch;
            DateTime bootTime = DateTime.MinValue;
            if (long.TryParse(GetLinuxValue(values, "BOOT_EPOCH"), out bootEpoch) && bootEpoch > 0)
                bootTime = DateTimeOffset.FromUnixTimeSeconds(bootEpoch).UtcDateTime;
            if (bootTime == DateTime.MinValue)
                throw new InvalidOperationException("Linux 未返回有效的系统启动时间");

            return new RemoteSystemInfo
            {
                HostName = GetLinuxValue(values, "HOSTNAME"),
                UserName = GetLinuxValue(values, "USERNAME"),
                LastBootUpTime = bootTime,
                IsLinux = true,
                IsRoot = IsTrue(GetLinuxValue(values, "IS_ROOT")),
                HasSudo = IsTrue(GetLinuxValue(values, "HAS_SUDO")),
                CanSudo = IsTrue(GetLinuxValue(values, "CAN_SUDO")),
                OperatingSystem = GetLinuxValue(values, "OS"),
                DistributionId = GetLinuxValue(values, "OS_ID"),
                OsVersion = GetLinuxValue(values, "OS_VERSION"),
                Kernel = GetLinuxValue(values, "KERNEL"),
                Architecture = GetLinuxValue(values, "ARCHITECTURE"),
                CpuCores = GetLinuxValue(values, "CPU_CORES"),
                MemoryBytes = GetLinuxValue(values, "MEMORY_BYTES"),
                RootFreeBytes = GetLinuxValue(values, "ROOT_FREE_BYTES"),
                PackageManager = GetLinuxValue(values, "PACKAGE_MANAGER"),
                Firewall = GetLinuxValue(values, "FIREWALL"),
                HasSystemd = IsTrue(GetLinuxValue(values, "HAS_SYSTEMD")),
                SshPort = GetLinuxValue(values, "SSH_PORT")
            };
        }

        private static Dictionary<string, string> ParseLinuxFields(string output)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in (output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith("XIAOBAI_", StringComparison.Ordinal))
                    continue;
                int separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;
                values[line.Substring("XIAOBAI_".Length, separator - "XIAOBAI_".Length)] = line.Substring(separator + 1).Trim();
            }
            return values;
        }

        private static string GetLinuxValue(Dictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : "";
        }

        private static bool IsTrue(string value)
        {
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
        }

        public Task<RemoteCommandResult> ExecutePowerShellAsync(string script, TimeSpan timeout, CancellationToken cancellationToken)
        {
            string utf8Script = "$OutputEncoding=[Text.Encoding]::UTF8; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " + script;
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(utf8Script));
            string command = "powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;
            if (command.Length > 7000)
                command = BuildCompressedPowerShellCommand(utf8Script);
            return ExecuteCommandAsync(command, timeout, cancellationToken);
        }

        private static string BuildCompressedPowerShellCommand(string script)
        {
            byte[] source = Encoding.UTF8.GetBytes(script);
            byte[] compressed;
            using (MemoryStream output = new MemoryStream())
            {
                using (GZipStream gzip = new GZipStream(output, CompressionLevel.Optimal, true))
                    gzip.Write(source, 0, source.Length);
                compressed = output.ToArray();
            }

            string payload = Convert.ToBase64String(compressed);
            string bootstrap =
                "$d=[Convert]::FromBase64String('" + payload + "');" +
                "$m=New-Object IO.MemoryStream(,$d);" +
                "$z=New-Object IO.Compression.GzipStream($m,[IO.Compression.CompressionMode]::Decompress);" +
                "$r=New-Object IO.StreamReader($z,[Text.Encoding]::UTF8);" +
                "&([ScriptBlock]::Create($r.ReadToEnd()))";
            string command = "powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + bootstrap + "\"";
            if (command.Length > 7900)
                throw new InvalidOperationException("远程 PowerShell 脚本压缩后仍超过 Windows OpenSSH 命令长度限制");
            return command;
        }

        public async Task<RemoteCommandResult> ExecuteCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (client == null)
                throw new InvalidOperationException("SSH 尚未连接");
            SshCommandResult result = await client.ExecuteAsync(command, timeout, cancellationToken);
            return new RemoteCommandResult
            {
                ExitCode = result.ExitCode,
                Output = result.Output,
                Error = result.Error
            };
        }

        public async Task<RemoteCommandResult> ExecuteSudoCommandAsync(
            string command,
            string sudoPassword,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (!isLinux)
                throw new InvalidOperationException("sudo 仅适用于 Linux SSH 连接");
            if (client == null)
                throw new InvalidOperationException("SSH 尚未连接");
            if (string.IsNullOrEmpty(sudoPassword) || sudoPassword.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new InvalidOperationException("sudo 密码为空或包含不支持的控制字符");
            string effective = "sudo -S -p '' sh -c " + QuoteShell(command);
            SshCommandResult result = await client.ExecuteWithInputAsync(
                effective,
                (sudoPassword ?? "") + "\n",
                timeout,
                cancellationToken);
            return new RemoteCommandResult { ExitCode = result.ExitCode, Output = result.Output, Error = result.Error };
        }

        private static string QuoteShell(string value)
        {
            return "'" + (value ?? "").Replace("'", "'\\''") + "'";
        }

        public Task DownloadFileAsync(string remotePath, string localPath, Action<ulong> progress, CancellationToken cancellationToken)
        {
            if (client == null)
                throw new InvalidOperationException("SSH 尚未连接");
            return client.DownloadAsync(remotePath, localPath, progress, cancellationToken);
        }

        public void Dispose()
        {
            if (client != null)
                client.Dispose();
        }

        private static string GetValue(Dictionary<string, object> values, string key)
        {
            object value;
            return values.TryGetValue(key, out value) && value != null ? value.ToString() : "";
        }

        private static void EnsureSuccess(RemoteCommandResult result, string operation)
        {
            if (result.ExitCode == 0)
                return;
            throw new InvalidOperationException(operation + "失败：" + RemoteErrorFormatter.Format(result));
        }

    }

    public sealed class WinRmRemoteExecutor : IRemoteExecutor
    {
        private readonly WinRmClient client;

        public RemoteTransport Transport { get { return RemoteTransport.WinRM; } }

        public WinRmRemoteExecutor(string host, string username, string password, int port = 5985)
        {
            client = new WinRmClient(host, username, password, port);
        }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public async Task<RemoteSystemInfo> GetSystemInfoAsync(CancellationToken cancellationToken)
        {
            WinRmRemoteSystemInfo info = await client.GetSystemInfoAsync(cancellationToken);
            return new RemoteSystemInfo
            {
                HostName = info.HostName,
                UserName = info.UserName,
                LastBootUpTime = info.LastBootUpTime.ToUniversalTime()
            };
        }

        public async Task<RemoteCommandResult> ExecutePowerShellAsync(string script, TimeSpan timeout, CancellationToken cancellationToken)
        {
            WinRmCommandResult result = await client.ExecuteRemoteScriptAsync(script, timeout, cancellationToken);
            return new RemoteCommandResult { ExitCode = result.ExitCode, Output = result.Output, Error = result.Error };
        }

        public Task<RemoteCommandResult> ExecuteCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            return ExecutePowerShellAsync(
                "& ([ScriptBlock]::Create([Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" + encoded + "'))))",
                timeout,
                cancellationToken);
        }

        public void Dispose() { }
    }

    public static class RemoteExecutorFactory
    {
        public static async Task<IRemoteExecutor> CreateAsync(
            Server server,
            string password,
            CancellationToken cancellationToken,
            RemoteTransport? preferredTransport = null)
        {
            string user = server.Username;
            bool trySsh = preferredTransport == null || preferredTransport.Value == RemoteTransport.SSH;
            bool tryWinRm = preferredTransport == null || preferredTransport.Value == RemoteTransport.WinRM;
            Exception sshError = null;

            if (trySsh && server.ManagementType != RemoteManagementType.WinRM)
            {
                SshRemoteExecutor ssh = new SshRemoteExecutor(server, password);
                try
                {
                    await ssh.ConnectAsync(cancellationToken);
                    return ssh;
                }
                catch (Exception ex)
                {
                    sshError = ex;
                    ssh.Dispose();
                    if (preferredTransport == RemoteTransport.SSH || server.ManagementType == RemoteManagementType.SSH || server.Type == ServerType.Linux)
                        throw new InvalidOperationException("SSH 连接失败，请检查管理端口、用户名和密码：" + RemoteErrorFormatter.Format(ex.Message, ""));
                }
            }

            if (tryWinRm && server.Type == ServerType.Windows && server.ManagementType != RemoteManagementType.SSH)
            {
                try
                {
                    WinRmRemoteExecutor winRm = new WinRmRemoteExecutor(server.IP, user, password, GetManagementPort(server, RemoteTransport.WinRM));
                    await winRm.ConnectAsync(cancellationToken);
                    await winRm.GetSystemInfoAsync(cancellationToken);
                    return winRm;
                }
                catch (Exception ex)
                {
                    string prefix = sshError == null ? "" : "SSH 不可用；";
                    throw new InvalidOperationException(prefix + "WinRM 连接失败：" + RemoteErrorFormatter.Format(ex.Message, ""));
                }
            }

            throw new InvalidOperationException("没有可用的远程管理通道");
        }

        public static int GetManagementPort(Server server, RemoteTransport transport)
        {
            int port;
            if (transport == RemoteTransport.WinRM)
            {
                if (server.ManagementType == RemoteManagementType.WinRM &&
                    int.TryParse(server.ManagementPort, out port) && port > 0 && port <= 65535)
                    return port;
                return 5985;
            }

            string configuredPort = server.Type == ServerType.Linux ? server.Port : server.ManagementPort;
            return int.TryParse(configuredPort, out port) && port > 0 && port <= 65535 ? port : 22;
        }

    }
}
