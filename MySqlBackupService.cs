using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace RDPManager
{
    public sealed class MySqlBackupRequest
    {
        public IList<string> DatabaseNames { get; set; }
        public string OutputPath { get; set; }
        public bool IncludeRoutines { get; set; } = true;
        public bool IncludeEvents { get; set; } = true;
        public bool IncludeTriggers { get; set; } = true;
        public bool OverwriteExistingTables { get; set; }
    }

    public sealed class MySqlBackupResult
    {
        public string DumpTool { get; set; }
        public long BytesWritten { get; set; }
        public string OutputPath { get; set; }
    }

    internal sealed class RemoteDumpArtifact
    {
        public string RemotePath { get; set; }
        public long Length { get; set; }
        public string Sha256 { get; set; }
        public string DumpTool { get; set; }
    }

    public sealed class MySqlBackupService
    {
        public static void ValidateBackupFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("找不到备份文件", path);
            FileInfo info = new FileInfo(path);
            if (info.Length == 0)
                throw new InvalidOperationException("备份文件为空");
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] header = new byte[3];
                int read = stream.Read(header, 0, header.Length);
                bool gzip = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
                bool validGzip = read == 3 && header[0] == 0x1f && header[1] == 0x8b && header[2] == 0x08;
                if (gzip && !validGzip)
                    throw new InvalidOperationException("备份文件不是有效的 GZip 文件");
                if (gzip)
                {
                    stream.Position = 0;
                    using (GZipStream gzipStream = new GZipStream(stream, CompressionMode.Decompress, true))
                    {
                        byte[] buffer = new byte[64 * 1024];
                        while (gzipStream.Read(buffer, 0, buffer.Length) > 0) { }
                    }
                }
                if (!gzip && read < 2)
                    throw new InvalidOperationException("备份文件内容不完整");
            }
        }

        public static bool ContainsDatabaseDump(string path, string databaseName)
        {
            ValidateDatabaseNameForCheck(databaseName);
            ValidateBackupFile(path);
            using (Stream source = OpenBackupStream(path))
            using (StreamReader reader = new StreamReader(source, Encoding.UTF8, true, 64 * 1024))
            {
                char[] buffer = new char[64 * 1024];
                string carry = "";
                int read;
                string marker = "`" + databaseName + "`";
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    string chunk = carry + new string(buffer, 0, read);
                    if (chunk.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        chunk.IndexOf("USE " + marker, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        chunk.IndexOf("Current Database: " + marker, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                    carry = chunk.Length > marker.Length ? chunk.Substring(chunk.Length - marker.Length) : chunk;
                }
                return false;
            }
        }

        private static Stream OpenBackupStream(string path)
        {
            FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                return file;
            return new GZipStream(file, CompressionMode.Decompress);
        }

        private static void ValidateDatabaseNameForCheck(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.Any(character => !(char.IsLetterOrDigit(character) || character == '_' || character == '$' || character == '-')))
                throw new InvalidOperationException("数据库名称包含不支持的字符");
        }
        public async Task<MySqlBackupResult> ExportAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            MySqlBackupRequest request,
            Action<long> progress,
            CancellationToken cancellationToken)
        {
            if (server == null)
                throw new InvalidOperationException("服务器信息为空");
            if (server.ManagementType == RemoteManagementType.WinRM)
                throw new InvalidOperationException("数据库备份第一版需要 SSH 通道，当前服务器被设置为仅 WinRM");
            if (request == null || request.DatabaseNames == null || request.DatabaseNames.Count == 0)
                throw new InvalidOperationException("至少选择一个数据库");
            if (string.IsNullOrWhiteSpace(request.OutputPath))
                throw new InvalidOperationException("备份文件路径为空");
            ValidateCredential(credential);
            foreach (string name in request.DatabaseNames)
                ValidateDatabaseName(name);

            string optionText = BuildOptionFile(credential);
            string outputPath = Path.GetFullPath(request.OutputPath);
            string outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new InvalidOperationException("备份文件目录无效");
            Directory.CreateDirectory(outputDirectory);
            string rawPath = outputPath + ".partial-" + Guid.NewGuid().ToString("N") + ".sql";
            string token = Guid.NewGuid().ToString("N");
            string remoteOptionPath = server.Type == ServerType.Linux
                ? "/tmp/xiaobai-mysql-" + token + ".cnf"
                : "C:\\Windows\\Temp\\xiaobai-mysql-" + token + ".cnf";
            string remoteDumpPath = server.Type == ServerType.Linux
                ? "/tmp/xiaobai-mysql-" + token + ".sql"
                : "C:\\Windows\\Temp\\xiaobai-mysql-" + token + ".sql";
            string remoteOptionSftpPath = server.Type == ServerType.Linux
                ? remoteOptionPath
                : "/C:/Windows/Temp/" + Path.GetFileName(remoteOptionPath).Replace('\\', '/');
            string remoteDumpSftpPath = server.Type == ServerType.Linux
                ? remoteDumpPath
                : "/C:/Windows/Temp/" + Path.GetFileName(remoteDumpPath).Replace('\\', '/');
            using (SshRemoteClient client = new SshRemoteClient(server, serverPassword))
            {
                try
                {
                    await client.ConnectAsync(cancellationToken);
                    await client.UploadTextAsync(optionText, remoteOptionSftpPath, cancellationToken);
                    SshCommandResult preparation = await client.ExecuteAsync(
                        server.Type == ServerType.Linux
                            ? BuildLinuxRemoteDumpFileCommand(request, remoteOptionPath, remoteDumpPath)
                            : BuildRemoteDumpFileCommand(request, remoteOptionPath, remoteDumpPath),
                        TimeSpan.FromMinutes(60),
                        cancellationToken);
                    if (preparation.ExitCode != 0)
                        throw new InvalidOperationException("远程备份准备失败：" + RemoteErrorFormatter.Format(new RemoteCommandResult
                        {
                            ExitCode = preparation.ExitCode,
                            Output = preparation.Output,
                            Error = preparation.Error
                        }));
                    RemoteDumpArtifact artifact = ParseRemoteDumpArtifact(preparation.Output);
                    remoteDumpPath = artifact.RemotePath;
                    await client.DownloadAsync(
                        remoteDumpSftpPath,
                        rawPath,
                        downloaded => progress?.Invoke((long)downloaded),
                        cancellationToken);
                    FileInfo downloadedFile = new FileInfo(rawPath);
                    if (downloadedFile.Length != artifact.Length)
                        throw new InvalidOperationException("备份下载大小校验失败");
                    string localHash = ComputeSha256(rawPath, cancellationToken);
                    if (!string.Equals(localHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("备份下载完整性校验失败（SHA-256 不一致）");
                    ValidateBackupFile(rawPath);
                    if (outputPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                        CompressFile(rawPath, outputPath, cancellationToken);
                    else
                        File.Move(rawPath, outputPath, true);
                    return new MySqlBackupResult
                    {
                        DumpTool = artifact.DumpTool,
                        BytesWritten = artifact.Length,
                        OutputPath = outputPath
                    };
                }
                finally
                {
                    if (!string.IsNullOrWhiteSpace(remoteDumpPath))
                    {
                        try
                        {
                            await client.ExecuteAsync(
                                server.Type == ServerType.Linux
                                    ? "rm -f -- " + QuoteShell(remoteOptionPath) + " " + QuoteShell(remoteDumpPath)
                                    : BuildRemoteCleanupCommand(remoteOptionPath, remoteDumpPath),
                                TimeSpan.FromSeconds(20),
                                CancellationToken.None);
                        }
                        catch { }
                    }
                    try { if (File.Exists(rawPath)) File.Delete(rawPath); } catch { }
                }
            }
        }

        private static RemoteDumpArtifact ParseRemoteDumpArtifact(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                throw new InvalidOperationException("远程备份没有返回文件信息");
            try
            {
                RemoteDumpArtifact artifact = JsonSerializer.Deserialize<RemoteDumpArtifact>(output.Trim());
                if (artifact == null || string.IsNullOrWhiteSpace(artifact.RemotePath) || artifact.Length <= 0 || string.IsNullOrWhiteSpace(artifact.Sha256))
                    throw new InvalidOperationException("远程备份文件信息不完整");
                return artifact;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("远程备份文件信息格式无法识别：" + ex.Message);
            }
        }

        private static string ComputeSha256(string path, CancellationToken cancellationToken)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, read, null, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return Convert.ToHexString(sha.Hash);
            }
        }

        private static string BuildRemoteDumpFileCommand(MySqlBackupRequest request, string optionFile, string dumpFile)
        {
            List<string> databaseArguments = request.DatabaseNames.Select(QuotePowerShell).ToList();
            List<string> options = new List<string>
            {
                "('--defaults-extra-file='+$optionFile)",
                "'--single-transaction'",
                "'--hex-blob'",
                request.OverwriteExistingTables ? "'--add-drop-table'" : "'--skip-add-drop-table'"
            };
            if (request.IncludeRoutines) options.Add("'--routines'");
            if (request.IncludeEvents) options.Add("'--events'");
            if (request.IncludeTriggers) options.Add("'--triggers'");
            options.Add("'--databases'");
            options.AddRange(databaseArguments);
            options.Add("('--result-file='+$dumpFile)");
            string script = @"
$ErrorActionPreference='Stop'
$optionFile=__OPTION_FILE__
$dumpFile=__DUMP_FILE__
try {
  $dump=(Get-Command mariadb-dump.exe -ErrorAction SilentlyContinue).Source
  if(-not $dump){$dump=(Get-Command mysqldump.exe -ErrorAction SilentlyContinue).Source}
  if(-not $dump){
    foreach($root in @('C:\Program Files\MySQL','C:\Program Files\MariaDB','C:\xampp')){
      if(Test-Path $root){
        $dump=Get-ChildItem -LiteralPath $root -Filter 'mariadb-dump.exe' -Recurse -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName -First 1
        if(-not $dump){$dump=Get-ChildItem -LiteralPath $root -Filter 'mysqldump.exe' -Recurse -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName -First 1}
      }
      if($dump){break}
    }
  }
  if(-not $dump){throw '服务器上未找到 mysqldump.exe 或 mariadb-dump.exe'}
  $args=@(__OPTIONS__)
  $toolOutput=(& $dump @args 2>&1 | Out-String).Trim()
  if($LASTEXITCODE -ne 0){throw ('数据库导出工具返回错误代码 '+$LASTEXITCODE+' '+$toolOutput)}
  if(-not (Test-Path $dumpFile)){throw '数据库导出工具未生成备份文件'}
  $hash=(Get-FileHash -LiteralPath $dumpFile -Algorithm SHA256).Hash
  [pscustomobject]@{RemotePath=$dumpFile;Length=[int64](Get-Item -LiteralPath $dumpFile).Length;Sha256=$hash;DumpTool=$dump} | ConvertTo-Json -Compress
}
catch {
  Remove-Item -LiteralPath $dumpFile -Force -ErrorAction SilentlyContinue
  throw
}
finally {}
".Replace("__OPTION_FILE__", QuotePowerShell(optionFile))
 .Replace("__DUMP_FILE__", QuotePowerShell(dumpFile))
 .Replace("__OPTIONS__", string.Join(",", options));
            return BuildPowerShellCommand(script);
        }

        private static string BuildLinuxRemoteDumpFileCommand(MySqlBackupRequest request, string optionFile, string dumpFile)
        {
            List<string> arguments = new List<string>
            {
                "--defaults-extra-file=" + optionFile,
                "--single-transaction",
                "--hex-blob",
                request.OverwriteExistingTables ? "--add-drop-table" : "--skip-add-drop-table"
            };
            if (request.IncludeRoutines) arguments.Add("--routines");
            if (request.IncludeEvents) arguments.Add("--events");
            if (request.IncludeTriggers) arguments.Add("--triggers");
            arguments.Add("--databases");
            arguments.AddRange(request.DatabaseNames);
            arguments.Add("--result-file=" + dumpFile);
            string quotedArguments = string.Join(" ", arguments.Select(QuoteShell));
            return "set -e; chmod 600 " + QuoteShell(optionFile) + "; " +
                "dump=$(command -v mariadb-dump 2>/dev/null || command -v mysqldump 2>/dev/null || true); " +
                "[ -n \"$dump\" ] || { printf '数据库服务器上未找到 mariadb-dump 或 mysqldump\\n' >&2; exit 20; }; " +
                "\"$dump\" " + quotedArguments + "; test -s " + QuoteShell(dumpFile) + "; " +
                "length=$(wc -c < " + QuoteShell(dumpFile) + "); hash=$(sha256sum " + QuoteShell(dumpFile) + " | awk '{print $1}'); " +
                "printf '{\"RemotePath\":\"%s\",\"Length\":%s,\"Sha256\":\"%s\",\"DumpTool\":\"%s\"}\\n' " +
                QuoteShell(dumpFile) + " \"$length\" \"$hash\" \"$dump\"";
        }

        private static string BuildRemoteCleanupCommand(string optionFile, string dumpFile)
        {
            return BuildPowerShellCommand(
                "Remove-Item -LiteralPath " + QuotePowerShell(optionFile) + "," + QuotePowerShell(dumpFile) + " -Force -ErrorAction SilentlyContinue");
        }

        private static string BuildPowerShellCommand(string script)
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            string command = "powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;
            if (command.Length <= 7000)
                return command;
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
            command = "powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + bootstrap + "\"";
            if (command.Length > 7900)
                throw new InvalidOperationException("备份脚本压缩后仍超过 Windows OpenSSH 命令长度限制");
            return command;
        }

        private static void CompressFile(string sourcePath, string outputPath, CancellationToken cancellationToken)
        {
            string temporaryPath = outputPath + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                using (FileStream source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (FileStream destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (GZipStream gzip = new GZipStream(destination, CompressionLevel.Optimal, true))
                {
                    byte[] buffer = new byte[64 * 1024];
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        gzip.Write(buffer, 0, read);
                    }
                    gzip.Flush();
                    destination.Flush(true);
                }
                File.Move(temporaryPath, outputPath, true);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
        }

        public static string BuildOptionFile(DatabaseCredentialRecord credential)
        {
            ValidateCredential(credential);
            if ((credential.Username ?? "").IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0 ||
                (credential.Password ?? "").IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new InvalidOperationException("数据库账号或密码包含不支持的换行字符");
            return "[client]" + Environment.NewLine +
                "user=" + credential.Username + Environment.NewLine +
                "password=" + credential.Password + Environment.NewLine +
                "host=" + (string.IsNullOrWhiteSpace(credential.Host) ? "127.0.0.1" : credential.Host) + Environment.NewLine +
                "port=" + credential.Port + Environment.NewLine;
        }

        private static string QuotePowerShell(string value)
        {
            return "'" + (value ?? "").Replace("'", "''") + "'";
        }

        private static string QuoteShell(string value)
        {
            return "'" + (value ?? "").Replace("'", "'\\''") + "'";
        }

        private static void ValidateCredential(DatabaseCredentialRecord credential)
        {
            if (credential == null || string.IsNullOrWhiteSpace(credential.Username) || string.IsNullOrEmpty(credential.Password))
                throw new InvalidOperationException("数据库凭据不完整");
            if (credential.Port < 1 || credential.Port > 65535)
                throw new InvalidOperationException("数据库端口无效");
        }

        private static void ValidateDatabaseName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64 ||
                name.Any(character => !(char.IsLetterOrDigit(character) || character == '_' || character == '$' || character == '-')))
                throw new InvalidOperationException("数据库名称包含不支持的字符");
        }
    }

    public sealed class LocalDatabaseTarget
    {
        public string DatabaseType { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string ImportToolPath { get; set; }
    }

    public static class LocalDatabaseTools
    {
        public static LocalDatabaseTarget Detect(string databaseType)
        {
            string tool = FindImportTool(databaseType);
            bool open = IsPortOpen("127.0.0.1", 3306, 500);
            return new LocalDatabaseTarget
            {
                DatabaseType = databaseType,
                Host = "127.0.0.1",
                Port = 3306,
                ImportToolPath = tool,
                Username = "root",
                Password = ""
            };
        }

        public static bool HasUsableLocalTarget(LocalDatabaseTarget target)
        {
            return target != null && !string.IsNullOrWhiteSpace(target.ImportToolPath) &&
                IsPortOpen(target.Host, target.Port, 700);
        }

        public static async Task ImportSqlAsync(
            LocalDatabaseTarget target,
            string sqlFile,
            Action<long, long> progress,
            CancellationToken cancellationToken)
        {
            if (!HasUsableLocalTarget(target))
                throw new InvalidOperationException("未检测到可用的本机数据库或导入工具");
            if (!File.Exists(sqlFile))
                throw new FileNotFoundException("找不到本地备份文件", sqlFile);
            if (string.IsNullOrWhiteSpace(target.Username) || string.IsNullOrEmpty(target.Password))
                throw new InvalidOperationException("本机数据库账号或密码为空");

            string importFile = sqlFile;
            MySqlBackupService.ValidateBackupFile(importFile);
            string decompressedFile = null;
            try
            {
                if (sqlFile.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                {
                    decompressedFile = Path.Combine(Path.GetTempPath(), "xiaobai-import-" + Guid.NewGuid().ToString("N") + ".sql");
                    using (FileStream source = new FileStream(sqlFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (GZipStream gzip = new GZipStream(source, CompressionMode.Decompress))
                    using (FileStream destination = new FileStream(decompressedFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        await gzip.CopyToAsync(destination, 64 * 1024, cancellationToken);
                    importFile = decompressedFile;
                    MySqlBackupService.ValidateBackupFile(importFile);
                }
            }
            catch
            {
                try { if (decompressedFile != null && File.Exists(decompressedFile)) File.Delete(decompressedFile); } catch { }
                throw;
            }

            string optionFile = Path.Combine(Path.GetTempPath(), "xiaobai-local-mysql-" + Guid.NewGuid().ToString("N") + ".cnf");
            try
            {
                string options = "[client]" + Environment.NewLine +
                    "host=" + target.Host + Environment.NewLine +
                    "port=" + target.Port + Environment.NewLine +
                    "user=" + target.Username + Environment.NewLine +
                    "password=" + target.Password + Environment.NewLine;
                File.WriteAllText(optionFile, options, new UTF8Encoding(false));
                System.Diagnostics.ProcessStartInfo info = new System.Diagnostics.ProcessStartInfo(target.ImportToolPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                info.ArgumentList.Add("--defaults-extra-file=" + optionFile);
                info.ArgumentList.Add("--binary-mode");
                using (System.Diagnostics.Process process = new System.Diagnostics.Process { StartInfo = info })
                {
                    try
                    {
                        if (!process.Start())
                            throw new InvalidOperationException("无法启动本机数据库导入工具");
                        long total = new FileInfo(importFile).Length;
                        long copied = 0;
                        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                        Task<string> errorTask = process.StandardError.ReadToEndAsync();
                        using (FileStream source = new FileStream(importFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            byte[] buffer = new byte[64 * 1024];
                            int read;
                            while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                            {
                                await process.StandardInput.BaseStream.WriteAsync(buffer, 0, read, cancellationToken);
                                copied += read;
                                progress?.Invoke(copied, total);
                            }
                        }
                        process.StandardInput.Close();
                        await process.WaitForExitAsync(cancellationToken);
                    string error = (await errorTask ?? "").Trim();
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException("本机数据库导入失败：" + (string.IsNullOrWhiteSpace(error) ? "退出代码 " + process.ExitCode : error.Replace("\r", " ").Replace("\n", " ").Trim()));
                    }
                    catch
                    {
                        try { if (!process.HasExited) process.Kill(true); } catch { }
                        throw;
                    }
                }
            }
            finally
            {
                try { if (File.Exists(optionFile)) File.Delete(optionFile); } catch { }
                try { if (decompressedFile != null && File.Exists(decompressedFile)) File.Delete(decompressedFile); } catch { }
            }
        }

        public static string FindImportTool(string databaseType)
        {
            List<string> names = string.Equals(databaseType, "MariaDB", StringComparison.OrdinalIgnoreCase)
                ? new List<string> { "mariadb.exe", "mysql.exe" }
                : new List<string> { "mysql.exe", "mariadb.exe" };
            foreach (string name in names)
            {
                string path = FindOnPath(name);
                if (!string.IsNullOrWhiteSpace(path))
                    return path;
            }
            foreach (string root in new[] { @"C:\Program Files\MySQL", @"C:\Program Files\MariaDB", @"C:\xampp" })
            {
                if (!Directory.Exists(root))
                    continue;
                foreach (string name in names)
                {
                    string path = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(path))
                        return path;
                }
            }
            return "";
        }

        public static bool IsLocalTargetAvailable(string databaseType, string host, int port)
        {
            return !string.IsNullOrWhiteSpace(FindImportTool(databaseType)) && IsPortOpen(host, port, 700);
        }

        private static string FindOnPath(string name)
        {
            string path = Environment.GetEnvironmentVariable("Path") ?? "";
            foreach (string folder in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(folder))
                    continue;
                string candidate = Path.Combine(folder.Trim(), name);
                if (File.Exists(candidate))
                    return candidate;
            }
            return "";
        }

        private static bool IsPortOpen(string host, int port, int timeoutMilliseconds)
        {
            try
            {
                using (System.Net.Sockets.TcpClient client = new System.Net.Sockets.TcpClient())
                {
                    Task task = client.ConnectAsync(host, port);
                    return task.Wait(timeoutMilliseconds) && client.Connected;
                }
            }
            catch { return false; }
        }
    }
}
