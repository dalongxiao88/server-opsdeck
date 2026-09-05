using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RDPManager
{
    public sealed class WinRmRemoteSystemInfo
    {
        public string HostName { get; set; }
        public string UserName { get; set; }
        public DateTime LastBootUpTime { get; set; }
    }

    public sealed class WinRmCommandResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
    }

    public sealed class WinRmClient
    {
        private const int DefaultPort = 5985;
        private readonly string host;
        private readonly string user;
        private readonly string password;
        private readonly int port;

        public WinRmClient(string host, string user, string password, int port = DefaultPort)
        {
            this.host = host;
            this.user = NormalizeUserName(user);
            this.password = password ?? "";
            this.port = port;
        }

        public async Task<WinRmRemoteSystemInfo> GetSystemInfoAsync(CancellationToken cancellationToken)
        {
const string script = @"
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Text.Encoding]::UTF8
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$passwordBytes = [Convert]::FromBase64String([Console]::In.ReadLine())
$plainPassword = [Text.Encoding]::UTF8.GetString($passwordBytes)
$secure = New-Object System.Security.SecureString
foreach ($character in $plainPassword.ToCharArray()) { $secure.AppendChar($character) }
$secure.MakeReadOnly()
$plainPassword = $null
$passwordBytes = $null
$credential = New-Object System.Management.Automation.PSCredential($env:XIAOBAI_WINRM_USER, $secure)
$result = Invoke-Command -ComputerName $env:XIAOBAI_WINRM_HOST -Port $env:XIAOBAI_WINRM_PORT -Credential $credential -Authentication Negotiate -ErrorAction Stop -ScriptBlock {
    $os = Get-CimInstance Win32_OperatingSystem
    [pscustomobject]@{
        HostName = $env:COMPUTERNAME
        UserName = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        LastBootUpTime = $os.LastBootUpTime.ToUniversalTime().ToString('o')
    }
}
$result | ConvertTo-Json -Compress
";
            WinRmCommandResult result = await RunPowerShellAsync(script, TimeSpan.FromSeconds(25), cancellationToken);
            EnsureSuccess(result, "WinRM 权限验证");

            string json = result.Output == null ? "" : result.Output.Trim();
            Dictionary<string, object> values = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            DateTime bootTime;
            if (values == null || !DateTime.TryParse(GetValue(values, "LastBootUpTime"), out bootTime))
                throw new InvalidOperationException("WinRM 返回的系统信息格式无法识别");

            return new WinRmRemoteSystemInfo
            {
                HostName = GetValue(values, "HostName"),
                UserName = GetValue(values, "UserName"),
                LastBootUpTime = bootTime.ToUniversalTime()
            };
        }

        public async Task SendRestartAsync(CancellationToken cancellationToken)
        {
const string script = @"
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Text.Encoding]::UTF8
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$passwordBytes = [Convert]::FromBase64String([Console]::In.ReadLine())
$plainPassword = [Text.Encoding]::UTF8.GetString($passwordBytes)
$secure = New-Object System.Security.SecureString
foreach ($character in $plainPassword.ToCharArray()) { $secure.AppendChar($character) }
$secure.MakeReadOnly()
$plainPassword = $null
$passwordBytes = $null
$credential = New-Object System.Management.Automation.PSCredential($env:XIAOBAI_WINRM_USER, $secure)
Invoke-Command -ComputerName $env:XIAOBAI_WINRM_HOST -Port $env:XIAOBAI_WINRM_PORT -Credential $credential -Authentication Negotiate -ErrorAction Stop -ScriptBlock {
    $output = (& shutdown.exe /r /t 5 /f 2>&1 | Out-String).Trim()
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw ('shutdown.exe 返回错误代码 ' + $code + $(if ($output) { ': ' + $output } else { '' })) }
    'RESTART_COMMAND_ACCEPTED'
}
";
            WinRmCommandResult result = await RunPowerShellAsync(script, TimeSpan.FromSeconds(25), cancellationToken);
            EnsureSuccess(result, "发送重启命令");
            if (string.IsNullOrEmpty(result.Output) || result.Output.IndexOf("RESTART_COMMAND_ACCEPTED", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("WinRM 未返回重启命令确认");
        }

        public Task<WinRmCommandResult> ExecuteRemoteScriptAsync(
            string remoteScript,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            string script =
                "$ErrorActionPreference = 'Stop'\n" +
                "$OutputEncoding = [Text.Encoding]::UTF8\n" +
                "[Console]::OutputEncoding = [Text.Encoding]::UTF8\n" +
                "$passwordBytes = [Convert]::FromBase64String([Console]::In.ReadLine())\n" +
                "$plainPassword = [Text.Encoding]::UTF8.GetString($passwordBytes)\n" +
                "$secure = New-Object System.Security.SecureString\n" +
                "foreach ($character in $plainPassword.ToCharArray()) { $secure.AppendChar($character) }\n" +
                "$secure.MakeReadOnly()\n" +
                "$plainPassword = $null\n" +
                "$passwordBytes = $null\n" +
                "$credential = New-Object System.Management.Automation.PSCredential($env:XIAOBAI_WINRM_USER, $secure)\n" +
                "Invoke-Command -ComputerName $env:XIAOBAI_WINRM_HOST -Port $env:XIAOBAI_WINRM_PORT " +
                "-Credential $credential -Authentication Negotiate -ErrorAction Stop -ScriptBlock {\n" +
                remoteScript + "\n}\n";
            return RunPowerShellAsync(script, timeout, cancellationToken);
        }

        private async Task<WinRmCommandResult> RunPowerShellAsync(string script, TimeSpan timeout, CancellationToken cancellationToken)
        {
            string windowsPowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            ProcessStartInfo startInfo = new ProcessStartInfo(windowsPowerShell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            startInfo.Environment["XIAOBAI_WINRM_HOST"] = host;
            startInfo.Environment["XIAOBAI_WINRM_USER"] = user;
            startInfo.Environment["XIAOBAI_WINRM_PORT"] = port.ToString();

            using (Process process = new Process { StartInfo = startInfo })
            {
                if (!process.Start())
                    throw new InvalidOperationException("无法启动 PowerShell");

                await process.StandardInput.WriteLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(password)));
                process.StandardInput.Close();
                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();
                using (CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeoutSource.CancelAfter(timeout);
                    try
                    {
                        await process.WaitForExitAsync(timeoutSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        TryKill(process);
                        if (cancellationToken.IsCancellationRequested)
                            throw;
                        throw new TimeoutException("WinRM 操作超时");
                    }
                }

                return new WinRmCommandResult
                {
                    ExitCode = process.ExitCode,
                    Output = await outputTask,
                    Error = await errorTask
                };
            }
        }

        private static void EnsureSuccess(WinRmCommandResult result, string operation)
        {
            if (result.ExitCode == 0)
                return;

            string error = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
            throw new InvalidOperationException(operation + "失败：" + RemoteErrorFormatter.Format(error, ""));
        }

        private static string GetValue(Dictionary<string, object> values, string key)
        {
            object value;
            return values.TryGetValue(key, out value) && value != null ? value.ToString() : "";
        }

        private static string NormalizeUserName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return @".\Administrator";
            if (value.Contains("\\") || value.Contains("@"))
                return value;
            return @".\" + value;
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch { }
        }
    }
}
