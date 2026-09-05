using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RDPManager
{
    public sealed class WebPortAdapter : IPortServiceAdapter
    {
        public string ServiceType { get { return "HTTP/HTTPS"; } }

        public async Task<IList<DetectedServicePort>> DetectAsync(IRemoteExecutor executor, CancellationToken cancellationToken)
        {
            const string script = @"
$ErrorActionPreference='Stop'
$result=@()
$iisLoaded=$false
try {
    Import-Module WebAdministration -ErrorAction Stop
    $iisLoaded=$true
    foreach($binding in @(Get-WebBinding)) {
        $info=[string]$binding.bindingInformation
        $parts=$info.Split(':',3)
        if($parts.Count -ne 3) { continue }
        $port=[int]$parts[1]
        if($binding.protocol -ne 'http' -and $binding.protocol -ne 'https') { continue }
        $result += [pscustomobject]@{
            ServiceType=$binding.protocol.ToUpperInvariant()
            DisplayName=('IIS / ' + $binding.ItemXPath.Split(""'"")[1] + ' / ' + $binding.protocol.ToUpperInvariant())
            ServiceName='W3SVC'
            ConfigPath=('IIS 站点绑定：' + $binding.ItemXPath)
            Protocol='TCP'
            Port=$port
            IsSupported=$true
            TargetKey=($binding.ItemXPath + '|' + $binding.protocol + '|' + $info)
        }
    }
} catch {}
if($result.Count -eq 0) {
    $services=Get-CimInstance Win32_Service | Where-Object { $_.PathName -match '(?i)(nginx|httpd|apache|caddy)' -or $_.Name -match '(?i)(nginx|apache|httpd|caddy)' }
    $ports=Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -eq 80 -or $_.LocalPort -eq 443 } | Select-Object -ExpandProperty LocalPort -Unique
    foreach($port in $ports) {
        $kind=if($port -eq 443){'HTTPS'}else{'HTTP'}
        $service=$services | Select-Object -First 1
        $name=if($null -eq $service){'WebService'}else{$service.Name}
        $display=if($null -eq $service){$kind + '（需确认配置）'}else{$service.DisplayName + ' / ' + $kind + '（需确认配置）'}
        $result += [pscustomobject]@{
            ServiceType=$kind
            DisplayName=$display
            ServiceName=$name
            ConfigPath='需先确认 Web 配置文件或站点绑定'
            Protocol='TCP'
            Port=[int]$port
            IsSupported=$false
            TargetKey=($name + '|' + $kind + '|' + $port)
        }
    }
}
$result | ConvertTo-Json -Compress
";
            RemoteCommandResult result = await executor.ExecutePowerShellAsync(script, TimeSpan.FromSeconds(30), cancellationToken);
            EnsureSuccess(result, "HTTP/HTTPS 检测");
            return ParseDetection(result.Output);
        }

        public async Task<PortChangeSession> ApplyAsync(
            IRemoteExecutor executor,
            DetectedServicePort target,
            int newPort,
            bool configureFirewall,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            if (!target.IsSupported || string.IsNullOrWhiteSpace(target.TargetKey) || !target.TargetKey.Contains("|"))
                throw new InvalidOperationException("该 Web 服务未识别到明确配置目标，请先确认实际配置文件或 IIS 站点绑定");

            string[] keyParts = target.TargetKey.Split(new[] { '|' }, 3);
            if (keyParts.Length != 3 || string.Equals(keyParts[0], "WebService", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("该 Web 服务需要手动确认配置文件，暂不执行自动修改");

            string backupPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows) +
                "\\System32\\inetsrv\\config\\applicationHost.config.xiao-bai-backup";
            string ruleName = "XiaoBai-Web-" + target.ServiceType + "-" + newPort + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string oldBinding = keyParts[2];
            string siteName = ExtractSiteName(keyParts[0]);
            string protocol = keyParts[1];
            string newBinding = ReplaceBindingPort(oldBinding, newPort);
            string script = @"
$ErrorActionPreference='Stop'
$appcmd='C:\Windows\System32\inetsrv\appcmd.exe'
if(-not (Test-Path $appcmd)) { throw 'IIS appcmd.exe 不存在' }
$source=Join-Path $env:windir 'System32\inetsrv\config\applicationHost.config'
Copy-Item -LiteralPath $source -Destination __BACKUP_PATH__ -Force
if(__CONFIGURE_FIREWALL__ -and -not (Get-NetFirewallRule -DisplayName __FIREWALL_RULE__ -ErrorAction SilentlyContinue)) { New-NetFirewallRule -DisplayName __FIREWALL_RULE__ -Direction Inbound -Protocol TCP -LocalPort __NEW_PORT__ -Action Allow -Profile Any -ErrorAction Stop | Out-Null }
& $appcmd set site /site.name:__SITE_NAME__ /bindings.[protocol='__PROTOCOL__',bindingInformation='__OLD_BINDING__'].bindingInformation:'__NEW_BINDING__'
if($LASTEXITCODE -ne 0) { throw 'IIS 站点绑定修改失败' }
Restart-Service -Name W3SVC -Force
Start-Sleep -Seconds 3
'WEB_PORT_CHANGE_APPLIED'
"
                .Replace("__BACKUP_PATH__", Quote(backupPath))
                .Replace("__FIREWALL_RULE__", Quote(ruleName))
                .Replace("__NEW_PORT__", newPort.ToString())
                .Replace("__SITE_NAME__", Quote(siteName))
                .Replace("__PROTOCOL__", protocol)
                .Replace("__OLD_BINDING__", oldBinding)
                .Replace("__NEW_BINDING__", newBinding);

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
            log("备份 IIS 站点绑定并修改 " + protocol + " 端口");
            try
            {
                RemoteCommandResult result = await executor.ExecutePowerShellAsync(script, TimeSpan.FromSeconds(45), cancellationToken);
                EnsureSuccess(result, "HTTP/HTTPS 修改");
                return session;
            }
            catch
            {
                try { await RollbackAsync(executor, session, log, CancellationToken.None); } catch { }
                throw;
            }
        }

        public async Task RollbackAsync(
            IRemoteExecutor executor,
            PortChangeSession session,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            string script = @"
$ErrorActionPreference='Stop'
$source=Join-Path $env:windir 'System32\inetsrv\config\applicationHost.config'
if(-not (Test-Path __BACKUP_PATH__)) { throw 'IIS 备份文件不存在' }
Copy-Item -LiteralPath __BACKUP_PATH__ -Destination $source -Force
if(__FIREWALL_RULE_CREATED__) { Remove-NetFirewallRule -DisplayName __FIREWALL_RULE__ -ErrorAction SilentlyContinue }
Restart-Service -Name W3SVC -Force
Start-Sleep -Seconds 3
'WEB_PORT_ROLLBACK_APPLIED'
"
                .Replace("__BACKUP_PATH__", Quote(session.BackupPath))
                .Replace("__FIREWALL_RULE__", Quote(session.FirewallRuleName))
                .Replace("__FIREWALL_RULE_CREATED__", session.FirewallRuleCreated ? "$true" : "$false");
            log("恢复 Web 站点绑定");
            RemoteCommandResult result = await executor.ExecutePowerShellAsync(script, TimeSpan.FromSeconds(45), cancellationToken);
            EnsureSuccess(result, "HTTP/HTTPS 回滚");
        }

        private static IList<DetectedServicePort> ParseDetection(string output)
        {
            List<DetectedServicePort> result = new List<DetectedServicePort>();
            if (string.IsNullOrWhiteSpace(output))
                return result;
            System.Text.Json.JsonElement root = System.Text.Json.JsonDocument.Parse(output.Trim()).RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                result.Add(ParseOne(root));
            else if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (System.Text.Json.JsonElement item in root.EnumerateArray())
                    result.Add(ParseOne(item));
            return result;
        }

        private static DetectedServicePort ParseOne(System.Text.Json.JsonElement item)
        {
            return new DetectedServicePort
            {
                ServiceType = ReadString(item, "ServiceType"),
                DisplayName = ReadString(item, "DisplayName"),
                ServiceName = ReadString(item, "ServiceName"),
                ConfigPath = ReadString(item, "ConfigPath"),
                Protocol = ReadString(item, "Protocol"),
                Port = ReadInt(item, "Port"),
                IsSupported = ReadBool(item, "IsSupported"),
                TargetKey = ReadString(item, "TargetKey")
            };
        }

        private static string ReadString(System.Text.Json.JsonElement item, string name)
        {
            System.Text.Json.JsonElement value;
            return item.TryGetProperty(name, out value) ? value.ToString() : "";
        }

        private static int ReadInt(System.Text.Json.JsonElement item, string name)
        {
            int value;
            System.Text.Json.JsonElement element;
            return item.TryGetProperty(name, out element) && element.TryGetInt32(out value) ? value : 0;
        }

        private static bool ReadBool(System.Text.Json.JsonElement item, string name)
        {
            System.Text.Json.JsonElement element;
            return item.TryGetProperty(name, out element) && element.ValueKind == System.Text.Json.JsonValueKind.True;
        }

        private static string ExtractSiteName(string itemXPath)
        {
            int quote = itemXPath.IndexOf("name='", StringComparison.OrdinalIgnoreCase);
            if (quote < 0)
                return itemXPath;
            int start = quote + 6;
            int end = itemXPath.IndexOf("'", start, StringComparison.Ordinal);
            return end > start ? itemXPath.Substring(start, end - start) : itemXPath;
        }

        private static string ReplaceBindingPort(string binding, int port)
        {
            string[] parts = (binding ?? "").Split(':');
            if (parts.Length != 3)
                throw new InvalidOperationException("IIS 绑定格式无法识别");
            return parts[0] + ":" + port + ":" + parts[2];
        }

        private static string Quote(string value)
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
}
