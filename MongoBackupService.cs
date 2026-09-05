using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RDPManager
{
    public sealed class MongoBackupRequest
    {
        public string DatabaseName { get; set; }
        public string OutputPath { get; set; }
        public bool IncludeUsersAndRoles { get; set; } = true;
    }

    public sealed class MongoBackupResult
    {
        public string OutputPath { get; set; }
        public long BytesWritten { get; set; }
        public string DumpTool { get; set; }
    }

    internal sealed class MongoRemoteArtifact
    {
        public string RemotePath { get; set; }
        public long Length { get; set; }
        public string Sha256 { get; set; }
        public string DumpTool { get; set; }
    }

    public sealed class MongoLocalTarget
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 27017;
        public string Username { get; set; }
        public string Password { get; set; }
        public string AuthenticationDatabase { get; set; } = "admin";
        public string RestoreToolPath { get; set; }
    }

    public sealed class MongoBackupService
    {
        public async Task<MongoBackupResult> ExportAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            MongoBackupRequest request,
            Action<long> progress,
            CancellationToken cancellationToken)
        {
            if (server == null || server.ManagementType == RemoteManagementType.WinRM)
                throw new InvalidOperationException("MongoDB 备份第一版需要 SSH 通道");
            if (request == null || string.IsNullOrWhiteSpace(request.OutputPath))
                throw new InvalidOperationException("备份文件路径为空");
            ValidateCredential(credential);
            if (!string.IsNullOrWhiteSpace(request.DatabaseName))
                ValidateName(request.DatabaseName);

            string outputPath = Path.GetFullPath(request.OutputPath);
            string directory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("备份文件目录无效");
            Directory.CreateDirectory(directory);
            string token = Guid.NewGuid().ToString("N");
            string remoteConfig = server.Type == ServerType.Linux
                ? "/tmp/xiaobai-mongo-" + token + ".yaml"
                : "C:\\Windows\\Temp\\xiaobai-mongo-" + token + ".yaml";
            string remoteArchive = server.Type == ServerType.Linux
                ? "/tmp/xiaobai-mongo-" + token + ".archive.gz"
                : "C:\\Windows\\Temp\\xiaobai-mongo-" + token + ".archive.gz";
            string sftpConfig = server.Type == ServerType.Linux ? remoteConfig : "/C:/Windows/Temp/" + Path.GetFileName(remoteConfig);
            string sftpArchive = server.Type == ServerType.Linux ? remoteArchive : "/C:/Windows/Temp/" + Path.GetFileName(remoteArchive);
            string rawPath = outputPath + ".partial-" + Guid.NewGuid().ToString("N") + ".archive.gz";

            using (SshRemoteClient client = new SshRemoteClient(server, serverPassword))
            {
                try
                {
                    await client.ConnectAsync(cancellationToken);
                    await client.UploadTextAsync(BuildConfigFile(credential), sftpConfig, cancellationToken);
                    SshCommandResult result = await client.ExecuteAsync(
                        server.Type == ServerType.Linux
                            ? BuildLinuxDumpCommand(credential, request, remoteConfig, remoteArchive)
                            : BuildDumpCommand(credential, request, remoteConfig, remoteArchive),
                        TimeSpan.FromMinutes(90),
                        cancellationToken);
                    if (result.ExitCode != 0)
                        throw new InvalidOperationException("MongoDB 远程备份失败：" + RemoteErrorFormatter.Format(new RemoteCommandResult { ExitCode = result.ExitCode, Output = result.Output, Error = result.Error }));
                    MongoRemoteArtifact artifact = ParseArtifact(result.Output);
                    await client.DownloadAsync(sftpArchive, rawPath, bytes => progress?.Invoke((long)bytes), cancellationToken);
                    FileInfo file = new FileInfo(rawPath);
                    if (file.Length != artifact.Length)
                        throw new InvalidOperationException("MongoDB 备份大小校验失败");
                    if (!string.Equals(ComputeSha256(rawPath, cancellationToken), artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("MongoDB 备份 SHA-256 校验失败");
                    ValidateBackupFile(rawPath);
                    File.Move(rawPath, outputPath, true);
                    return new MongoBackupResult { OutputPath = outputPath, BytesWritten = artifact.Length, DumpTool = artifact.DumpTool };
                }
                finally
                {
                    try
                    {
                        await client.ExecuteAsync(
                            server.Type == ServerType.Linux
                                ? "rm -f -- " + QuoteShell(remoteConfig) + " " + QuoteShell(remoteArchive)
                                : BuildCleanupCommand(remoteConfig, remoteArchive),
                            TimeSpan.FromSeconds(20),
                            CancellationToken.None);
                    }
                    catch { }
                    try { if (File.Exists(rawPath)) File.Delete(rawPath); } catch { }
                }
            }
        }

        public async Task RestoreAsync(
            MongoLocalTarget target,
            string archivePath,
            bool dropExisting,
            Action<long, long> progress,
            CancellationToken cancellationToken)
        {
            if (target == null || string.IsNullOrWhiteSpace(target.RestoreToolPath))
                throw new InvalidOperationException("未找到本机 mongorestore.exe");
            ValidateBackupFile(archivePath);
            if (string.IsNullOrWhiteSpace(target.Username) || string.IsNullOrEmpty(target.Password))
                throw new InvalidOperationException("本机 MongoDB 凭据不完整");
            string config = Path.Combine(Path.GetTempPath(), "xiaobai-mongo-local-" + Guid.NewGuid().ToString("N") + ".yaml");
            try
            {
                File.WriteAllText(config, BuildConfigFile(target), new UTF8Encoding(false));
                System.Diagnostics.ProcessStartInfo info = new System.Diagnostics.ProcessStartInfo(target.RestoreToolPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                info.ArgumentList.Add("--config=" + config);
                info.ArgumentList.Add("--host=" + (string.IsNullOrWhiteSpace(target.Host) ? "127.0.0.1" : target.Host));
                info.ArgumentList.Add("--port=" + target.Port);
                info.ArgumentList.Add("--username=" + target.Username);
                info.ArgumentList.Add("--authenticationDatabase=" + (string.IsNullOrWhiteSpace(target.AuthenticationDatabase) ? "admin" : target.AuthenticationDatabase));
                info.ArgumentList.Add("--archive=" + archivePath);
                info.ArgumentList.Add("--gzip");
                if (dropExisting) info.ArgumentList.Add("--drop");
                using (System.Diagnostics.Process process = new System.Diagnostics.Process { StartInfo = info })
                {
                    if (!process.Start()) throw new InvalidOperationException("无法启动 mongorestore.exe");
                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    Task<string> error = process.StandardError.ReadToEndAsync();
                    try
                    {
                        await process.WaitForExitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        try { if (!process.HasExited) process.Kill(true); } catch { }
                        try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
                        throw;
                    }
                    progress?.Invoke(new FileInfo(archivePath).Length, new FileInfo(archivePath).Length);
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException("MongoDB 本机恢复失败：" + (await error ?? "退出代码 " + process.ExitCode).Replace("\r", " ").Replace("\n", " ").Trim());
                }
            }
            finally { try { if (File.Exists(config)) File.Delete(config); } catch { } }
        }

        public static string FindDumpTool()
        {
            return FindTool(new[] { "mongodump.exe" }, new[] { @"C:\Program Files\MongoDB", @"C:\Program Files\XiaoBai Databases", @"C:\MongoDB" });
        }

        public static string FindRestoreTool()
        {
            return FindTool(new[] { "mongorestore.exe" }, new[] { @"C:\Program Files\MongoDB", @"C:\Program Files\XiaoBai Databases", @"C:\MongoDB" });
        }

        public static void ValidateBackupFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || new FileInfo(path).Length == 0)
                throw new InvalidOperationException("MongoDB 备份文件为空或不存在");
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] header = new byte[3];
                int read = stream.Read(header, 0, header.Length);
                if (read != 3 || header[0] != 0x1f || header[1] != 0x8b || header[2] != 0x08)
                    throw new InvalidOperationException("MongoDB 备份不是有效的 GZip archive");
                stream.Position = 0;
                using (GZipStream gzip = new GZipStream(stream, CompressionMode.Decompress, true))
                {
                    byte[] buffer = new byte[64 * 1024];
                    while (gzip.Read(buffer, 0, buffer.Length) > 0) { }
                }
            }
        }

        private static string BuildConfigFile(DatabaseCredentialRecord credential)
        {
            ValidateCredential(credential);
            return "password: " + YamlValue(credential.Password) + Environment.NewLine;
        }

        private static string BuildConfigFile(MongoLocalTarget target)
        {
            return "password: " + YamlValue(target.Password) + Environment.NewLine;
        }

        private static string BuildDumpCommand(DatabaseCredentialRecord credential, MongoBackupRequest request, string config, string archive)
        {
            List<string> args = new List<string>
            {
                "('--config='+$config)",
                "('--username='+$username)",
                "('--host='+$remoteHost)",
                "('--port='+$port)",
                "('--authenticationDatabase='+$authDb)",
                "('--archive='+$archive)",
                "'--gzip'"
            };
            if (request.IncludeUsersAndRoles && !string.IsNullOrWhiteSpace(request.DatabaseName)) args.Add("'--dumpDbUsersAndRoles'");
            if (!string.IsNullOrWhiteSpace(request.DatabaseName)) args.Add("('--db='+$database)");
            string script = @"
$ErrorActionPreference='Stop'
$config=__CONFIG__
$archive=__ARCHIVE__
$username=__USERNAME__
$remoteHost=__HOST__
$port=__PORT__
$authDb=__AUTHDB__
$database=__DATABASE__
$dump=(Get-Command mongodump.exe -ErrorAction SilentlyContinue).Source
if(-not $dump){$dump=Get-ChildItem -LiteralPath 'C:\Program Files\MongoDB','C:\Program Files\XiaoBai Databases','C:\MongoDB' -Filter 'mongodump.exe' -Recurse -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName -First 1}
if(-not $dump){throw '服务器上未找到 mongodump.exe'}
$args=@(__OPTIONS__)
$previousErrorActionPreference=$ErrorActionPreference
$ErrorActionPreference='Continue'
$dumpOutput=@(& $dump @args 2>&1)
$dumpExitCode=$LASTEXITCODE
$ErrorActionPreference=$previousErrorActionPreference
if($dumpExitCode -ne 0){$detail=($dumpOutput|ForEach-Object{$_.ToString()}) -join ' ';throw ('mongodump 返回错误代码 '+$dumpExitCode+': '+$detail)}
if(-not(Test-Path $archive)){throw 'mongodump 未生成归档文件'}
$hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
[pscustomobject]@{RemotePath=$archive;Length=[int64](Get-Item -LiteralPath $archive).Length;Sha256=$hash;DumpTool=$dump}|ConvertTo-Json -Compress
".Replace("__CONFIG__", QuotePowerShell(config)).Replace("__ARCHIVE__", QuotePowerShell(archive)).Replace("__USERNAME__", QuotePowerShell(credential.Username)).Replace("__HOST__", QuotePowerShell(credential.Host)).Replace("__PORT__", credential.Port.ToString()).Replace("__AUTHDB__", QuotePowerShell(string.IsNullOrWhiteSpace(credential.AuthenticationDatabase) ? "admin" : credential.AuthenticationDatabase)).Replace("__DATABASE__", QuotePowerShell(request.DatabaseName ?? "")).Replace("__OPTIONS__", string.Join(",", args));
            return BuildPowerShellCommand(script);
        }

        private static string BuildLinuxDumpCommand(DatabaseCredentialRecord credential, MongoBackupRequest request, string config, string archive)
        {
            List<string> args = new List<string>
            {
                "--config=" + config,
                "--username=" + credential.Username,
                "--host=" + credential.Host,
                "--port=" + credential.Port,
                "--authenticationDatabase=" + (string.IsNullOrWhiteSpace(credential.AuthenticationDatabase) ? "admin" : credential.AuthenticationDatabase),
                "--archive=" + archive,
                "--gzip"
            };
            if (request.IncludeUsersAndRoles && !string.IsNullOrWhiteSpace(request.DatabaseName))
                args.Add("--dumpDbUsersAndRoles");
            if (!string.IsNullOrWhiteSpace(request.DatabaseName))
                args.Add("--db=" + request.DatabaseName);
            return "set -e; chmod 600 " + QuoteShell(config) + "; dump=$(command -v mongodump 2>/dev/null || true); " +
                "[ -n \"$dump\" ] || { printf '数据库服务器上未找到 mongodump\\n' >&2; exit 20; }; " +
                "\"$dump\" " + string.Join(" ", args.Select(QuoteShell)) + " >/dev/null; test -s " + QuoteShell(archive) + "; " +
                "length=$(wc -c < " + QuoteShell(archive) + "); hash=$(sha256sum " + QuoteShell(archive) + " | awk '{print $1}'); " +
                "printf '{\"RemotePath\":\"%s\",\"Length\":%s,\"Sha256\":\"%s\",\"DumpTool\":\"%s\"}\\n' " +
                QuoteShell(archive) + " \"$length\" \"$hash\" \"$dump\"";
        }

        private static string BuildCleanupCommand(string config, string archive) => BuildPowerShellCommand("Remove-Item -LiteralPath " + QuotePowerShell(config) + "," + QuotePowerShell(archive) + " -Force -ErrorAction SilentlyContinue");
        private static string BuildPowerShellCommand(string script)
        {
            string utf8Script = "$OutputEncoding=[Text.Encoding]::UTF8;[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script;
            return "powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(utf8Script));
        }
        private static MongoRemoteArtifact ParseArtifact(string output) { MongoRemoteArtifact artifact = JsonSerializer.Deserialize<MongoRemoteArtifact>((output ?? "").Trim()); if (artifact == null || string.IsNullOrWhiteSpace(artifact.RemotePath) || artifact.Length <= 0 || string.IsNullOrWhiteSpace(artifact.Sha256)) throw new InvalidOperationException("MongoDB 备份信息不完整"); return artifact; }
        private static string ComputeSha256(string path, CancellationToken cancellationToken) { using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) using (SHA256 sha = SHA256.Create()) { byte[] buffer = new byte[64 * 1024]; int read; while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) { cancellationToken.ThrowIfCancellationRequested(); sha.TransformBlock(buffer, 0, read, null, 0); } sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0); return Convert.ToHexString(sha.Hash); } }
        private static string QuotePowerShell(string value) => "'" + (value ?? "").Replace("'", "''") + "'";
        private static string QuoteShell(string value) => "'" + (value ?? "").Replace("'", "'\\''") + "'";
        private static string YamlValue(string value) => "'" + (value ?? "").Replace("'", "''") + "'";
        private static void ValidateCredential(DatabaseCredentialRecord credential) { if (credential == null || string.IsNullOrWhiteSpace(credential.Username) || string.IsNullOrEmpty(credential.Password) || credential.Port < 1 || credential.Port > 65535) throw new InvalidOperationException("MongoDB 凭据不完整"); if (string.IsNullOrWhiteSpace(credential.Host)) credential.Host = "127.0.0.1"; }
        private static void ValidateName(string value) { if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.'))) throw new InvalidOperationException("MongoDB 数据库名称格式无效"); }
        private static string FindTool(IEnumerable<string> names, IEnumerable<string> roots) { string path = Environment.GetEnvironmentVariable("Path") ?? ""; foreach (string folder in path.Split(Path.PathSeparator)) foreach (string name in names) { string candidate = Path.Combine(folder.Trim(), name); if (File.Exists(candidate)) return candidate; } foreach (string root in roots) if (Directory.Exists(root)) foreach (string name in names) { string candidate = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault(); if (!string.IsNullOrWhiteSpace(candidate)) return candidate; } return ""; }
    }
}
