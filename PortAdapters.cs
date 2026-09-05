using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RDPManager
{
    public interface IPortServiceAdapter
    {
        string ServiceType { get; }
        Task<IList<DetectedServicePort>> DetectAsync(IRemoteExecutor executor, CancellationToken cancellationToken);
        Task<PortChangeSession> ApplyAsync(
            IRemoteExecutor executor,
            DetectedServicePort target,
            int newPort,
            bool configureFirewall,
            Action<string> log,
            CancellationToken cancellationToken);
        Task RollbackAsync(
            IRemoteExecutor executor,
            PortChangeSession session,
            Action<string> log,
            CancellationToken cancellationToken);
    }

    public abstract class WindowsPortServiceAdapter : IPortServiceAdapter
    {
        public abstract string ServiceType { get; }
        protected abstract string DetectionScript { get; }
        protected abstract string ChangeScript { get; }
        protected abstract string RollbackScript { get; }
        protected virtual TimeSpan ApplyTimeout { get { return TimeSpan.FromSeconds(35); } }
        protected virtual TimeSpan RollbackTimeout { get { return TimeSpan.FromSeconds(35); } }

        public async Task<IList<DetectedServicePort>> DetectAsync(IRemoteExecutor executor, CancellationToken cancellationToken)
        {
            RemoteCommandResult result = await executor.ExecutePowerShellAsync(DetectionScript, TimeSpan.FromSeconds(25), cancellationToken);
            EnsureSuccess(result, ServiceType + " 检测");
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
            string backupPath = target.ConfigPath + ".xiao-bai-backup";
            string ruleName = "XiaoBai-" + target.ServiceType + "-" + newPort + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string script = ChangeScript
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
            log("备份 " + target.ConfigPath);
            try
            {
                RemoteCommandResult result = await executor.ExecutePowerShellAsync(script, ApplyTimeout, cancellationToken);
                EnsureSuccess(result, ServiceType + " 修改");
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

        public async Task RollbackAsync(
            IRemoteExecutor executor,
            PortChangeSession session,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            string script = RollbackScript
                .Replace("__CONFIG_PATH__", Quote(session.Target.ConfigPath))
                .Replace("__BACKUP_PATH__", Quote(session.BackupPath))
                .Replace("__SERVICE_NAME__", Quote(session.Target.ServiceName))
                .Replace("__FIREWALL_RULE__", Quote(session.FirewallRuleName))
                .Replace("__FIREWALL_RULE_CREATED__", session.FirewallRuleCreated ? "$true" : "$false");

            log("恢复 " + session.Target.ConfigPath);
            RemoteCommandResult result = await executor.ExecutePowerShellAsync(script, RollbackTimeout, cancellationToken);
            EnsureSuccess(result, ServiceType + " 回滚");
        }

        private static IList<DetectedServicePort> ParseDetection(string output)
        {
            List<DetectedServicePort> services = new List<DetectedServicePort>();
            if (string.IsNullOrWhiteSpace(output))
                return services;

            System.Text.Json.JsonElement root = System.Text.Json.JsonDocument.Parse(output.Trim()).RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                services.Add(ParseOne(root));
            else if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (System.Text.Json.JsonElement item in root.EnumerateArray())
                    services.Add(ParseOne(item));
            }
            return services;
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
                IsSupported = !item.TryGetProperty("IsSupported", out System.Text.Json.JsonElement supported) ||
                    supported.ValueKind == System.Text.Json.JsonValueKind.True,
                TargetKey = ReadString(item, "TargetKey"),
                ServiceStatus = ReadString(item, "ServiceStatus")
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

        protected static string Quote(string value)
        {
            return "'" + (value ?? "").Replace("'", "''") + "'";
        }

        protected static void EnsureSuccess(RemoteCommandResult result, string operation)
        {
            if (result.ExitCode == 0)
                return;
            throw new InvalidOperationException(operation + "失败：" + RemoteErrorFormatter.Format(result));
        }
    }

    public class MySqlPortAdapter : WindowsPortServiceAdapter
    {
        public override string ServiceType { get { return "MySQL"; } }

        protected override string DetectionScript
        {
            get
            {
                return @"
$items = Get-CimInstance Win32_Service | Where-Object { $_.PathName -match '(?i)(mysqld|mariadbd)' -or $_.Name -match '(?i)mysql|maria' }
$result = @()
foreach ($item in $items) {
    $config = ''
    $pathMatch = [regex]::Match($item.PathName, '(?i)--defaults-file\s*=\s*""?([^""]+?\.(?:ini|cnf))""?(?:\s|$)')
    if ($pathMatch.Success) { $config = $pathMatch.Groups[1].Value }
    $port = 0
    if (Test-Path $config) {
        $section = ''
        foreach ($line in Get-Content -LiteralPath $config) {
            if ($line -match '^\s*\[(.+)\]\s*$') { $section = $matches[1].ToLowerInvariant() }
            elseif ($section -eq 'mysqld' -and $line -match '^\s*port\s*=\s*(\d+)') { $port = [int]$matches[1] }
        }
    }
    if ($port -le 0 -and $item.ProcessId -gt 0) {
        $livePort = Get-NetTCPConnection -State Listen -OwningProcess $item.ProcessId -ErrorAction SilentlyContinue |
            Where-Object { $_.LocalPort -gt 0 } |
            Select-Object -ExpandProperty LocalPort -First 1
        if ($livePort) { $port = [int]$livePort }
    }
    if ($port -le 0) { $port = 3306 }
    $kind = if ($item.Name -match '(?i)maria' -or $item.PathName -match '(?i)mariadbd') { 'MariaDB' } else { 'MySQL' }
    $result += [pscustomobject]@{ ServiceType=$kind; DisplayName=$item.DisplayName; ServiceName=$item.Name; ConfigPath=$config; Protocol='TCP'; Port=$port; ServiceStatus=[string]$item.State }
}
$result | ConvertTo-Json -Compress
";
            }
        }

        protected override string ChangeScript
        {
            get
            {
                return @"
$ErrorActionPreference = 'Stop'
Copy-Item -LiteralPath __CONFIG_PATH__ -Destination __BACKUP_PATH__ -Force
$lines = [System.Collections.Generic.List[string]](Get-Content -LiteralPath __CONFIG_PATH__)
$section = ''
$changed = $false
for ($i=0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^\s*\[(.+)\]\s*$') { $section = $matches[1].ToLowerInvariant(); continue }
    if ($section -eq 'mysqld' -and $lines[$i] -match '^\s*port\s*=') { $lines[$i] = 'port=__NEW_PORT__'; $changed = $true }
}
if (-not $changed) { $lines.Add('[mysqld]'); $lines.Add('port=__NEW_PORT__') }
            [IO.File]::WriteAllLines(__CONFIG_PATH__, $lines, (New-Object Text.UTF8Encoding($false)))
if (__CONFIGURE_FIREWALL__) { New-NetFirewallRule -DisplayName __FIREWALL_RULE__ -Direction Inbound -Protocol TCP -LocalPort __NEW_PORT__ -Action Allow -Profile Any -ErrorAction SilentlyContinue | Out-Null }
            Restart-Service -Name __SERVICE_NAME__ -Force
            Start-Sleep -Seconds 3
            if((Get-Service -Name __SERVICE_NAME__).Status -ne 'Running'){ throw '服务重启后未处于运行状态' }
'PORT_CHANGE_APPLIED'
";
            }
        }

        protected override string RollbackScript
        {
            get
            {
                return @"
$ErrorActionPreference = 'Stop'
Copy-Item -LiteralPath __BACKUP_PATH__ -Destination __CONFIG_PATH__ -Force
if (__FIREWALL_RULE_CREATED__) { Remove-NetFirewallRule -DisplayName __FIREWALL_RULE__ -ErrorAction SilentlyContinue }
Restart-Service -Name __SERVICE_NAME__ -Force
'PORT_ROLLBACK_APPLIED'
";
            }
        }
    }

    public sealed class MariaDbPortAdapter : MySqlPortAdapter
    {
        public override string ServiceType { get { return "MariaDB"; } }
    }

    public sealed class RedisPortAdapter : WindowsPortServiceAdapter
    {
        public override string ServiceType { get { return "Redis"; } }

        protected override string DetectionScript
        {
            get
            {
                return @"
$items = Get-CimInstance Win32_Service | Where-Object { $_.PathName -match '(?i)redis-server' -or $_.Name -match '(?i)redis' }
$result = @()
foreach ($item in $items) {
    $config = ''
    $configMatch = [regex]::Match($item.PathName, '(?i)(?:^|\s)(?:-c|--config)\s+(?:""([^""]+)""|(\S+))')
    if ($configMatch.Success) {
        $config = if ($configMatch.Groups[1].Success) { $configMatch.Groups[1].Value } else { $configMatch.Groups[2].Value.Trim('""') }
    }
    elseif ($item.PathName -match '(?i)redis-server(?:.exe)?\s+(?:""([^""]+)""|(\S+))') {
        $config = if ($matches[1]) { $matches[1] } else { $matches[2].Trim('""') }
    }
    $port = 6379
    if ($item.PathName -match '(?i)(?:^|\s)--port\s+(\d+)') {
        $port = [int]$matches[1]
    }
    if (Test-Path -LiteralPath $config -PathType Leaf) {
        $line = Get-Content -LiteralPath $config | Where-Object { $_ -match '^\s*port\s+\d+' } | Select-Object -First 1
        if ($line -match '^\s*port\s+(\d+)') { $port = [int]$matches[1] }
    }
    $result += [pscustomobject]@{ ServiceType='Redis'; DisplayName=$item.DisplayName; ServiceName=$item.Name; ConfigPath=$config; Protocol='TCP'; Port=$port; ServiceStatus=[string]$item.State }
}
$result | ConvertTo-Json -Compress
";
            }
        }

        protected override string ChangeScript
        {
            get
            {
                return @"
$ErrorActionPreference = 'Stop'
Copy-Item -LiteralPath __CONFIG_PATH__ -Destination __BACKUP_PATH__ -Force
$text = Get-Content -LiteralPath __CONFIG_PATH__ -Raw
if ($text -match '(?m)^\s*port\s+\d+') { $text = [regex]::Replace($text, '(?m)^\s*port\s+\d+', 'port __NEW_PORT__') } else { $text += [Environment]::NewLine + 'port __NEW_PORT__' }
            [IO.File]::WriteAllText(__CONFIG_PATH__, $text, (New-Object Text.UTF8Encoding($false)))
if (__CONFIGURE_FIREWALL__) { New-NetFirewallRule -DisplayName __FIREWALL_RULE__ -Direction Inbound -Protocol TCP -LocalPort __NEW_PORT__ -Action Allow -Profile Any -ErrorAction SilentlyContinue | Out-Null }
            Restart-Service -Name __SERVICE_NAME__ -Force
            Start-Sleep -Seconds 3
            if((Get-Service -Name __SERVICE_NAME__).Status -ne 'Running'){ throw '服务重启后未处于运行状态' }
'PORT_CHANGE_APPLIED'
";
            }
        }

        protected override string RollbackScript
        {
            get
            {
                return @"
$ErrorActionPreference = 'Stop'
Copy-Item -LiteralPath __BACKUP_PATH__ -Destination __CONFIG_PATH__ -Force
if (__FIREWALL_RULE_CREATED__) { Remove-NetFirewallRule -DisplayName __FIREWALL_RULE__ -ErrorAction SilentlyContinue }
Restart-Service -Name __SERVICE_NAME__ -Force
'PORT_ROLLBACK_APPLIED'
";
            }
        }
    }

    public sealed class MongoDbPortAdapter : WindowsPortServiceAdapter
    {
        public override string ServiceType { get { return "MongoDB"; } }
        protected override string DetectionScript
        {
            get
            {
                return @"
$items = Get-CimInstance Win32_Service | Where-Object { $_.PathName -match '(?i)mongod' -or $_.Name -match '(?i)mongo' }
$result = @()
foreach ($item in $items) {
    $config = ''
    $pathMatch = [regex]::Match($item.PathName, '(?i)--config\s+""([^""]+)""')
    if ($pathMatch.Success) { $config = $pathMatch.Groups[1].Value }
    elseif ($item.PathName -match '(?i)--config\s+(\S+)') { $config = $matches[1].Trim('""') }
    $port = 27017
    if (Test-Path $config) {
        $line = Get-Content -LiteralPath $config | Where-Object { $_ -match '^\s*port\s*:' } | Select-Object -First 1
        if ($line -match '^\s*port\s*:\s*(\d+)') { $port = [int]$matches[1] }
    }
    $result += [pscustomobject]@{ ServiceType='MongoDB'; DisplayName=$item.DisplayName; ServiceName=$item.Name; ConfigPath=$config; Protocol='TCP'; Port=$port; ServiceStatus=[string]$item.State }
}
$result | ConvertTo-Json -Compress
";
            }
        }

        protected override string ChangeScript
        {
            get
            {
                return @"
$ErrorActionPreference = 'Stop'
Copy-Item -LiteralPath __CONFIG_PATH__ -Destination __BACKUP_PATH__ -Force
$text = Get-Content -LiteralPath __CONFIG_PATH__ -Raw
if ($text -match '(?m)^\s*port\s*:') { $text = [regex]::Replace($text, '(?m)^\s*port\s*:\s*\d+', 'port: __NEW_PORT__') } else { $text += [Environment]::NewLine + 'net:' + [Environment]::NewLine + '  port: __NEW_PORT__' }
            [IO.File]::WriteAllText(__CONFIG_PATH__, $text, (New-Object Text.UTF8Encoding($false)))
if (__CONFIGURE_FIREWALL__) { New-NetFirewallRule -DisplayName __FIREWALL_RULE__ -Direction Inbound -Protocol TCP -LocalPort __NEW_PORT__ -Action Allow -Profile Any -ErrorAction SilentlyContinue | Out-Null }
            Restart-Service -Name __SERVICE_NAME__ -Force
            Start-Sleep -Seconds 3
            if((Get-Service -Name __SERVICE_NAME__).Status -ne 'Running'){ throw '服务重启后未处于运行状态' }
'PORT_CHANGE_APPLIED'
";
            }
        }
        protected override string RollbackScript
        {
            get
            {
                return @"
$ErrorActionPreference = 'Stop'
Copy-Item -LiteralPath __BACKUP_PATH__ -Destination __CONFIG_PATH__ -Force
if (__FIREWALL_RULE_CREATED__) { Remove-NetFirewallRule -DisplayName __FIREWALL_RULE__ -ErrorAction SilentlyContinue }
Restart-Service -Name __SERVICE_NAME__ -Force
'PORT_ROLLBACK_APPLIED'
";
            }
        }
    }
}
