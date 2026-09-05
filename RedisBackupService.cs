using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RDPManager
{
    public enum RedisBackupMode
    {
        Rdb,
        Aof
    }

    public sealed class RedisBackupRequest
    {
        public RedisBackupMode Mode { get; set; }
        public string OutputPath { get; set; }
    }

    public sealed class RedisBackupResult
    {
        public RedisBackupMode Mode { get; set; }
        public string OutputPath { get; set; }
        public long BytesWritten { get; set; }
        public string Sha256 { get; set; }
    }

    internal sealed class RedisRemoteArtifact
    {
        public string RemotePath { get; set; }
        public long Length { get; set; }
        public string Sha256 { get; set; }
        public string FileName { get; set; }
    }

    public sealed class RedisLocalTarget
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 6379;
        public string Username { get; set; } = "default";
        public string Password { get; set; }
    }

    public sealed class RedisBackupService
    {
        public async Task<RedisBackupResult> ExportRdbAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            RedisBackupRequest request,
            Action<long> progress,
            CancellationToken cancellationToken)
        {
            if (request == null || request.Mode != RedisBackupMode.Rdb)
                throw new InvalidOperationException("当前只支持 RDB 快照备份");
            if (string.IsNullOrWhiteSpace(request.OutputPath))
                throw new InvalidOperationException("备份文件路径为空");
            ValidateCredential(credential);
            if (server == null || server.ManagementType == RemoteManagementType.WinRM)
                throw new InvalidOperationException("Redis 备份第一版需要 SSH 通道");

            string outputPath = Path.GetFullPath(request.OutputPath);
            string directory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("备份文件目录无效");
            Directory.CreateDirectory(directory);

            string remoteCopy = server.Type == ServerType.Linux
                ? "/tmp/xiaobai-redis-" + Guid.NewGuid().ToString("N") + ".rdb"
                : "C:\\Windows\\Temp\\xiaobai-redis-" + Guid.NewGuid().ToString("N") + ".rdb";
            string sftpCopy = server.Type == ServerType.Linux ? remoteCopy : "/C:/Windows/Temp/" + Path.GetFileName(remoteCopy);
            string localPartial = outputPath + ".partial-" + Guid.NewGuid().ToString("N");

            using (SshRemoteClient client = new SshRemoteClient(server, serverPassword))
            {
                try
                {
                    await client.ConnectAsync(cancellationToken);
                    RedisRemoteConfig config;
                    using (SshDatabaseTunnel tunnel = await SshDatabaseTunnel.OpenAsync(server, serverPassword, credential.Host, credential.Port, cancellationToken))
                    using (RedisRespConnection connection = await OpenRedisAsync(tunnel.LocalPort, credential, cancellationToken))
                    {
                        config = await ReadRedisConfigAsync(connection, cancellationToken);
                        string before = Convert.ToString(await connection.CommandAsync(cancellationToken, "LASTSAVE"));
                        try { await connection.CommandAsync(cancellationToken, "BGSAVE"); }
                        catch (RedisCommandException ex) when ((ex.Message ?? "").IndexOf("already in progress", StringComparison.OrdinalIgnoreCase) >= 0) { }
                        await WaitForSnapshotAsync(connection, before, TimeSpan.FromMinutes(5), cancellationToken);
                    }

                    SshCommandResult copy = server.Type == ServerType.Linux
                        ? await client.ExecuteAsync(
                            "set -e; cp -- " + QuoteShell(config.DirectoryPath.TrimEnd('/') + "/" + config.DbFileName) + " " + QuoteShell(remoteCopy) + "; length=$(wc -c < " + QuoteShell(remoteCopy) + "); hash=$(sha256sum " + QuoteShell(remoteCopy) + " | awk '{print $1}'); printf '{\"RemotePath\":\"%s\",\"Length\":%s,\"Sha256\":\"%s\",\"FileName\":\"%s\"}\\n' " + QuoteShell(remoteCopy) + " \"$length\" \"$hash\" " + QuoteShell(config.DbFileName),
                            TimeSpan.FromSeconds(30),
                            cancellationToken)
                        : await client.ExecutePowerShellAsync(
                            "Copy-Item -LiteralPath " + Quote(config.DirectoryPath + "\\" + config.DbFileName) + " -Destination " + Quote(remoteCopy) + " -Force; $hash=(Get-FileHash -LiteralPath " + Quote(remoteCopy) + " -Algorithm SHA256).Hash; [pscustomobject]@{RemotePath=" + Quote(remoteCopy) + ";Length=[int64](Get-Item -LiteralPath " + Quote(remoteCopy) + ").Length;Sha256=$hash;FileName=" + Quote(config.DbFileName) + "}|ConvertTo-Json -Compress",
                            TimeSpan.FromSeconds(30),
                            cancellationToken);
                    if (copy.ExitCode != 0)
                        throw new InvalidOperationException("复制 Redis RDB 文件失败：" + RemoteErrorFormatter.Format(new RemoteCommandResult { ExitCode = copy.ExitCode, Output = copy.Output, Error = copy.Error }));
                    RedisRemoteArtifact artifact = JsonSerializer.Deserialize<RedisRemoteArtifact>((copy.Output ?? "").Trim());
                    if (artifact == null || artifact.Length <= 0 || string.IsNullOrWhiteSpace(artifact.Sha256)) throw new InvalidOperationException("Redis RDB 文件信息不完整");
                    await client.DownloadAsync(sftpCopy, localPartial, bytes => progress?.Invoke((long)bytes), cancellationToken);
                    FileInfo file = new FileInfo(localPartial);
                    if (file.Length != artifact.Length || !string.Equals(ComputeSha256(localPartial, cancellationToken), artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Redis RDB 下载完整性校验失败");
                    File.Move(localPartial, outputPath, true);
                    return new RedisBackupResult { Mode = RedisBackupMode.Rdb, OutputPath = outputPath, BytesWritten = artifact.Length, Sha256 = artifact.Sha256 };
                }
                finally
                {
                    try
                    {
                        if (server.Type == ServerType.Linux)
                            await client.ExecuteAsync("rm -f -- " + QuoteShell(remoteCopy), TimeSpan.FromSeconds(20), CancellationToken.None);
                        else
                            await client.ExecutePowerShellAsync("Remove-Item -LiteralPath " + Quote(remoteCopy) + " -Force -ErrorAction SilentlyContinue", TimeSpan.FromSeconds(20), CancellationToken.None);
                    }
                    catch { }
                    try { if (File.Exists(localPartial)) File.Delete(localPartial); } catch { }
                }
            }
        }

        public Task<RedisBackupResult> ExportAofAsync(Server server, string serverPassword, DatabaseCredentialRecord credential, RedisBackupRequest request, Action<long> progress, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("Redis AOF 多文件备份将在确认部署版本后接入，当前请使用 RDB 快照");
        }

        public async Task RestoreRdbAsync(RedisLocalTarget target, string backupPath, bool replaceExisting, CancellationToken cancellationToken)
        {
            if (target == null || string.IsNullOrWhiteSpace(target.Password)) throw new InvalidOperationException("Redis 本机目标凭据不完整");
            if (!File.Exists(backupPath) || new FileInfo(backupPath).Length == 0) throw new InvalidOperationException("Redis RDB 备份文件为空或不存在");
            if (!replaceExisting) throw new InvalidOperationException("恢复 Redis RDB 前必须明确允许替换本机数据文件");
            throw new NotSupportedException("Redis RDB 恢复需要停止本机 Redis 服务和确认数据目录，安装目标确认后接入");
        }

        private static async Task<RedisRemoteConfig> ReadRedisConfigAsync(RedisRespConnection connection, CancellationToken cancellationToken)
        {
            object dirResponse = await connection.CommandAsync(cancellationToken, "CONFIG", "GET", "dir");
            object fileResponse = await connection.CommandAsync(cancellationToken, "CONFIG", "GET", "dbfilename");
            string directory = ReadConfigValue(dirResponse);
            string fileName = ReadConfigValue(fileResponse);
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
                throw new InvalidOperationException("Redis 未返回 RDB 数据目录或文件名");
            return new RedisRemoteConfig { DirectoryPath = directory.TrimEnd('\\', '/'), DbFileName = fileName };
        }

        private static string ReadConfigValue(object response)
        {
            List<object> values = response as List<object>;
            if (values == null || values.Count < 2)
                return "";
            return Convert.ToString(values[1]);
        }

        private static async Task WaitForSnapshotAsync(RedisRespConnection connection, string before, TimeSpan timeout, CancellationToken cancellationToken)
        {
            DateTime deadline = DateTime.Now.Add(timeout);
            while (DateTime.Now < deadline)
            {
                string running = Convert.ToString(await connection.CommandAsync(cancellationToken, "INFO", "persistence"));
                if (ReadInfo(running, "rdb_bgsave_in_progress") == "0")
                {
                    string status = ReadInfo(running, "rdb_last_bgsave_status");
                    string current = Convert.ToString(await connection.CommandAsync(cancellationToken, "LASTSAVE"));
                    if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) && long.TryParse(current, out long now) && long.TryParse(before, out long old) && now >= old)
                        return;
                }
                await Task.Delay(1000, cancellationToken);
            }
            throw new TimeoutException("Redis RDB 快照超时");
        }

        private static async Task<RedisRespConnection> OpenRedisAsync(int port, DatabaseCredentialRecord credential, CancellationToken cancellationToken)
        {
            RedisRespConnection connection = await RedisRespConnection.ConnectAsync("127.0.0.1", port, cancellationToken);
            try
            {
                if (string.Equals(credential.Username, "default", StringComparison.OrdinalIgnoreCase)) await connection.CommandAsync(cancellationToken, "AUTH", credential.Password);
                else await connection.CommandAsync(cancellationToken, "AUTH", credential.Username, credential.Password);
                return connection;
            }
            catch { connection.Dispose(); throw; }
        }

        private static string ReadInfo(string text, string key) { return (text ?? "").Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)).Select(line => line.Substring(key.Length + 1).Trim()).FirstOrDefault() ?? ""; }
        private static string ComputeSha256(string path, CancellationToken token) { using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) using (SHA256 sha = SHA256.Create()) { byte[] buffer = new byte[65536]; int n; while ((n=stream.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();sha.TransformBlock(buffer,0,n,null,0);} sha.TransformFinalBlock(Array.Empty<byte>(),0,0);return Convert.ToHexString(sha.Hash); } }
        private static string Quote(string value) => "'" + (value ?? "").Replace("'", "''") + "'";
        private static string QuoteShell(string value) => "'" + (value ?? "").Replace("'", "'\\''") + "'";
        private static void ValidateCredential(DatabaseCredentialRecord credential) { if (credential == null || credential.Port < 1 || credential.Port > 65535 || string.IsNullOrEmpty(credential.Password)) throw new InvalidOperationException("Redis 凭据不完整"); }
    }

    internal sealed class RedisRemoteConfig
    {
        public string DirectoryPath { get; set; }
        public string DbFileName { get; set; }
    }
}
