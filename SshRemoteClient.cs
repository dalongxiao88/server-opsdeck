using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;

namespace RDPManager
{
    public sealed class SshRemoteClient : IDisposable
    {
        private readonly SshClient client;

        public SshRemoteClient(string host, int port, string username, string password)
        {
            PasswordConnectionInfo connection = new PasswordConnectionInfo(host, port, username, password)
            {
                Timeout = TimeSpan.FromSeconds(12)
            };
            client = new SshClient(connection);
        }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                client.Connect();
                if (!client.IsConnected)
                    throw new InvalidOperationException("SSH 连接未建立");
            }, cancellationToken);
        }

        public Task<SshCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!client.IsConnected)
                    throw new InvalidOperationException("SSH 连接已断开");

                using (SshCommand sshCommand = client.CreateCommand(command))
                {
                    sshCommand.CommandTimeout = timeout;
                    IAsyncResult asyncResult = sshCommand.BeginExecute();
                    while (!asyncResult.IsCompleted)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Thread.Sleep(100);
                    }

                    string output = sshCommand.EndExecute(asyncResult);
                    return new SshCommandResult
                    {
                        ExitCode = sshCommand.ExitStatus ?? -1,
                        Output = output,
                        Error = sshCommand.Error
                    };
                }
            }, cancellationToken);
        }

        public Task<SshCommandResult> ExecutePowerShellAsync(string script, TimeSpan timeout, CancellationToken cancellationToken)
        {
            string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(
                "$OutputEncoding=[Text.Encoding]::UTF8;[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script));
            return ExecuteAsync(
                "powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                timeout,
                cancellationToken);
        }

        public Task UploadAsync(string localPath, string remotePath, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!client.IsConnected)
                    throw new InvalidOperationException("SSH 连接已断开");

                using (SftpClient sftp = new SftpClient(client.ConnectionInfo))
                {
                    sftp.Connect();
                    using (FileStream stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        sftp.UploadFile(stream, remotePath, true);
                }
            }, cancellationToken);
        }

        public Task DownloadAsync(
            string remotePath,
            string localPath,
            Action<ulong> progress,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!client.IsConnected)
                    throw new InvalidOperationException("SSH 连接已断开");

                string normalized = (remotePath ?? "").Replace('\\', '/');
                string[] candidates = normalized.StartsWith("/", StringComparison.Ordinal)
                    ? new[] { normalized }
                    : normalized.Length >= 2 && normalized[1] == ':'
                        ? new[] { "/" + normalized, normalized }
                        : new[] { normalized };
                Exception last = null;
                foreach (string candidate in candidates)
                {
                    try
                    {
                        using (SftpClient sftp = new SftpClient(client.ConnectionInfo))
                        {
                            sftp.Connect();
                            using (FileStream stream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                sftp.DownloadFile(candidate, stream, downloaded =>
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    progress?.Invoke(downloaded);
                                });
                            }
                        }
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        try { if (File.Exists(localPath)) File.Delete(localPath); } catch { }
                    }
                }
                throw new InvalidOperationException("无法通过 SFTP 下载远程备份文件", last);
            }, cancellationToken);
        }

        public Task UploadTextAsync(string content, string remotePath, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!client.IsConnected)
                    throw new InvalidOperationException("SSH 连接已断开");
                string normalized = (remotePath ?? "").Replace('\\', '/');
                using (SftpClient sftp = new SftpClient(client.ConnectionInfo))
                {
                    sftp.Connect();
                    using (MemoryStream stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content ?? "")))
                        sftp.UploadFile(stream, normalized, true);
                }
            }, cancellationToken);
        }

        public void Dispose()
        {
            if (client.IsConnected)
                client.Disconnect();
            client.Dispose();
        }
    }

    public sealed class SshCommandResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
    }

}
