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

    public sealed class SshRemoteExecutor : IRemoteExecutor, IRemoteFileTransfer
    {
        private readonly string host;
        private readonly int port;
        private readonly string username;
        private readonly string password;
        private SshRemoteClient client;

        public RemoteTransport Transport { get { return RemoteTransport.SSH; } }

        public SshRemoteExecutor(string host, int port, string username, string password)
        {
            this.host = host;
            this.port = port;
            this.username = username;
            this.password = password ?? "";
        }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            client = new SshRemoteClient(host, port, username, password);
            await client.ConnectAsync(cancellationToken);
        }

        public async Task<RemoteSystemInfo> GetSystemInfoAsync(CancellationToken cancellationToken)
        {
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
                LastBootUpTime = bootTime.ToUniversalTime()
            };
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
                SshRemoteExecutor ssh = new SshRemoteExecutor(server.IP, GetManagementPort(server, RemoteTransport.SSH), user, password);
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

            return int.TryParse(server.ManagementPort, out port) && port > 0 && port <= 65535 ? port : 22;
        }

    }
}
