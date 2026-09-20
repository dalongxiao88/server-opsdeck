using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ServerForge
{
    public sealed class ServerResourceMonitorService
    {
        private static string WindowsPayloadMarker => ProtectedText.WindowsPayloadMarker;
        private const string LinuxPayloadBegin = "SERVERFORGE_RESOURCE_BEGIN";
        private const string LinuxPayloadEnd = "SERVERFORGE_RESOURCE_END";

        private static readonly HashSet<string> LinuxSystemProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "systemd", "init", "kthreadd", "ksoftirqd", "migration", "rcu_sched", "rcu_bh",
            "rcu_tasks_kthread", "watchdog", "cpuhp", "kswapd0", "kcompactd0", "khugepaged",
            "oom_reaper", "writeback", "kintegrityd", "kblockd", "md", "edac-poller",
            "devfreq_wq", "ksmd", "kthrotld", "acpi_thermal_pm", "scsi_eh", "scsi_tmf",
            "ipv6_addrconf", "kauditd", "khungtaskd", "jbd2", "ext4-rsv-conver", "xfsalloc",
            "xfs_mru_cache", "xfs-buf", "xfs-conv", "xfs-cil", "xfs-reclaim", "xfs-log",
            "xfs-eofblocks", "xfsaild", "systemd-journald", "systemd-udevd", "systemd-logind",
            "systemd-networkd", "systemd-resolved", "dbus-daemon", "rsyslogd", "cron", "crond",
            "agetty", "sshd", "polkitd", "networkmanager", "irqbalance", "auditd", "tuned",
            "chronyd", "systemd-timesyncd", "accounts-daemon", "udisksd", "bash", "sh", "dash",
            "ps", "head", "sleep", "awk", "sed", "grep"
        };

        private const string WindowsScript = @"
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$os = Get-CimInstance Win32_OperatingSystem
$logicalProcessors = [Math]::Max(1, [int]$env:NUMBER_OF_PROCESSORS)
$cpu = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter ""Name='_Total'"" | Select-Object -First 1
$cpuPercent = if ($null -eq $cpu) { 0 } else { [double]$cpu.PercentProcessorTime }
$memoryTotal = [long]$os.TotalVisibleMemorySize * 1KB
$memoryAvailable = [long]$os.FreePhysicalMemory * 1KB
$memoryUsed = [Math]::Max([long]0, $memoryTotal - $memoryAvailable)
$disks = @(Get-CimInstance Win32_LogicalDisk -Filter ""DriveType=3"" | Sort-Object DeviceID | ForEach-Object {
    [pscustomobject]@{
        Name = [string]$_.DeviceID
        Label = [string]$_.VolumeName
        TotalBytes = [long]$_.Size
        FreeBytes = [long]$_.FreeSpace
    }
})

$networkRates = @(Get-CimInstance Win32_PerfFormattedData_Tcpip_NetworkInterface -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notmatch 'Loopback|isatap|Teredo' })
$receivedRateTotal = ($networkRates | Measure-Object -Property BytesReceivedPersec -Sum).Sum
$sentRateTotal = ($networkRates | Measure-Object -Property BytesSentPersec -Sum).Sum
$networkSamples = @([pscustomobject]@{
    ReceivedBytesPerSecond = if ($null -eq $receivedRateTotal) { 0 } else { [Math]::Round([double]$receivedRateTotal, 2) }
    SentBytesPerSecond = if ($null -eq $sentRateTotal) { 0 } else { [Math]::Round([double]$sentRateTotal, 2) }
})

$systemNames = @(
    'idle','system idle process','system','registry','memory compression','secure system','smss','csrss','wininit','services',
    'lsass','svchost','fontdrvhost','wmiprvse','dllhost','logonui','winlogon','dwm','explorer',
    'taskhostw','taskhostex','conhost','runtimebroker','searchindexer','searchui','shellexperiencehost',
    'startmenuexperiencehost','sihost','ctfmon','securityhealthservice','securityhealthsystray','msmpeng',
    'nissrv','spoolsv','rdpclip','rdpinput','servermanager','powershell','pwsh','cmd','sshd','ssh-shellhost',
    'openwith','useroobebroker','applicationframehost','textinputhost','audiodg','wudfhost','dashost',
    'smartscreen','backgroundtaskhost','lockapp','systemsettings','tiworker','trustedinstaller','msiexec',
    'werfault','wermgr','mousocoreworker','usocoreworker','sppsvc','vssvc','searchprotocolhost',
    'searchfilterhost','unsecapp','taskmgr','mmc','winrshost','wsmprovhost','qemu-ga','blnsvr',
    'vmtoolsd','vgauthservice','amazonssmagent','ec2config','ec2launch','waappagent',
    'windowsazureguestagent','aliyunservice','aliyun_assist_service','assistdaemon','cloudbase-init',
    'guestagent','google_osconfig_agent','google_guest_agent'
)
$windowsPrefix = [IO.Path]::GetFullPath($env:windir).TrimEnd('\') + '\'
$extraProcesses = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | ForEach-Object {
    $processName = [IO.Path]::GetFileNameWithoutExtension([string]$_.Name)
    $normalizedName = $processName.ToLowerInvariant()
    $processPath = [string]$_.ExecutablePath
    $isWindowsBinary = -not [string]::IsNullOrWhiteSpace($processPath) -and
        $processPath.StartsWith($windowsPrefix, [StringComparison]::OrdinalIgnoreCase)
    if (-not $isWindowsBinary -and $systemNames -notcontains $normalizedName) {
        [pscustomobject]@{
            Id = [int]$_.ProcessId
            Name = $processName
            CpuPercent = 0
            WorkingSetBytes = [long]$_.WorkingSetSize
            Path = $processPath
        }
    }
} | Sort-Object @{ Expression = 'WorkingSetBytes'; Descending = $true } | Select-Object -First 24)

$snapshot = [pscustomobject]@{
    HostName = [string]$env:COMPUTERNAME
    OperatingSystem = ([string]$os.Caption).Trim()
    CapturedAtUtc = [DateTime]::UtcNow.ToString('o')
    UptimeSeconds = [Math]::Max(0, ((Get-Date) - $os.LastBootUpTime).TotalSeconds)
    LogicalProcessorCount = $logicalProcessors
    CpuUsagePercent = [Math]::Round([Math]::Max(0, [Math]::Min(100, $cpuPercent)), 1)
    MemoryTotalBytes = $memoryTotal
    MemoryAvailableBytes = $memoryAvailable
    MemoryUsedBytes = $memoryUsed
    NetworkTotalReceivedBytes = [long]0
    NetworkTotalSentBytes = [long]0
    Disks = $disks
    NetworkSamples = @($networkSamples)
    ExtraProcesses = $extraProcesses
}
Write-Output ('SERVERFORGE_RESOURCE_JSON:' + ($snapshot | ConvertTo-Json -Depth 5 -Compress))
";

        private const string LinuxCommand = """
LC_ALL=C
sf_cpu() { awk '/^cpu / { idle=$5+$6; total=0; for(i=2;i<=NF;i++) total+=$i; printf "%.0f %.0f",total,idle; exit }' /proc/stat; }
sf_net() { awk 'NR>2 { gsub(/:/," "); if($1!="lo"){ rx+=$2; tx+=$10 } } END { printf "%.0f %.0f",rx,tx }' /proc/net/dev; }
set -- $(sf_cpu); cpu_total_start=$1; cpu_idle_start=$2
set -- $(sf_net); net_rx_previous=$1; net_tx_previous=$2
network_lines=''
sample_index=0
while [ "$sample_index" -lt 5 ]; do
    sleep 0.4
    set -- $(sf_net); net_rx_current=$1; net_tx_current=$2
    rx_rate=$(( (net_rx_current - net_rx_previous) * 5 / 2 ))
    tx_rate=$(( (net_tx_current - net_tx_previous) * 5 / 2 ))
    [ "$rx_rate" -lt 0 ] && rx_rate=0
    [ "$tx_rate" -lt 0 ] && tx_rate=0
    network_lines="${network_lines}NET|${rx_rate}|${tx_rate}
"
    net_rx_previous=$net_rx_current
    net_tx_previous=$net_tx_current
    sample_index=$((sample_index + 1))
done
set -- $(sf_cpu); cpu_total_end=$1; cpu_idle_end=$2
cpu_percent=$(awk -v t0="$cpu_total_start" -v i0="$cpu_idle_start" -v t1="$cpu_total_end" -v i1="$cpu_idle_end" 'BEGIN { dt=t1-t0; if(dt<=0){print 0}else{printf "%.1f",(dt-(i1-i0))*100/dt} }')
memory_total=$(awk '/^MemTotal:/ { printf "%.0f",$2*1024; exit }' /proc/meminfo)
memory_available=$(awk '/^MemAvailable:/ { printf "%.0f",$2*1024; exit }' /proc/meminfo)
[ -n "$memory_available" ] || memory_available=$(awk '/^MemFree:/ { printf "%.0f",$2*1024; exit }' /proc/meminfo)
os_name=$(awk -F= '/^PRETTY_NAME=/{gsub(/^"|"$/,"",$2); print $2; exit}' /etc/os-release 2>/dev/null | tr '|' '/')
[ -n "$os_name" ] || os_name=$(uname -srm | tr '|' '/')
uptime_seconds=$(awk '{printf "%.0f",$1}' /proc/uptime)
printf '%s\n' 'SERVERFORGE_RESOURCE_BEGIN'
printf 'META|%s|%s|%s|%s|%s|%s|%s|%s|%s\n' "$(hostname 2>/dev/null)" "$os_name" "$uptime_seconds" "$cpu_percent" "$(getconf _NPROCESSORS_ONLN 2>/dev/null || printf 1)" "$memory_total" "$memory_available" "$net_rx_previous" "$net_tx_previous"
printf '%b' "$network_lines"
df -P -B1 -x tmpfs -x devtmpfs -x squashfs 2>/dev/null | awk 'NR>1 { printf "DISK|%s|%s|%s\n",$6,$2,$4 }'
ps -eo pid=,comm=,pcpu=,rss= --sort=-pcpu 2>/dev/null | head -n 80 | awk '{ printf "PROC|%s|%s|%s|%s\n",$1,$2,$3,$4 }'
printf '%s\n' 'SERVERFORGE_RESOURCE_END'
""";

        public async Task<ServerResourceSnapshot> CollectAsync(
            Server server,
            string password,
            IProgress<ResourceCollectionProgress> progress,
            CancellationToken cancellationToken)
        {
            if (server == null)
                throw new ArgumentNullException(nameof(server));

            Report(progress, 8, "正在连接管理通道");
            using (IRemoteExecutor executor = await RemoteExecutorFactory.CreateAsync(server, password ?? "", cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Report(progress, 25, "连接成功，正在采样资源");
                ServerResourceSnapshot snapshot;
                if (server.Type == ServerType.Windows)
                {
                    RemoteCommandResult result = await executor.ExecutePowerShellAsync(
                        WindowsScript,
                        TimeSpan.FromSeconds(45),
                        cancellationToken);
                    EnsureSuccess(result, "读取 Windows 资源");
                    Report(progress, 88, "正在整理监控数据");
                    snapshot = ParseWindowsSnapshot(result.Output);
                }
                else
                {
                    RemoteCommandResult result = await executor.ExecuteCommandAsync(
                        LinuxCommand,
                        TimeSpan.FromSeconds(45),
                        cancellationToken);
                    EnsureSuccess(result, "读取 Linux 资源");
                    Report(progress, 88, "正在整理监控数据");
                    snapshot = ParseLinuxSnapshot(result.Output);
                }

                snapshot.ServerName = server.Name;
                snapshot.Transport = executor.Transport == RemoteTransport.SSH ? "SSH" : "WinRM";
                Normalize(snapshot);
                Report(progress, 100, "资源读取完成");
                return snapshot;
            }
        }

        private static ServerResourceSnapshot ParseWindowsSnapshot(string output)
        {
            string payload = ExtractMarkedLine(output, WindowsPayloadMarker);
            ServerResourceSnapshot snapshot = JsonSerializer.Deserialize(
                payload, ServerForgeJsonContext.Default.ServerResourceSnapshot);
            if (snapshot == null)
                throw new InvalidOperationException("Windows 未返回有效的资源数据");
            return snapshot;
        }

        private static ServerResourceSnapshot ParseLinuxSnapshot(string output)
        {
            string payload = ExtractBlock(output, LinuxPayloadBegin, LinuxPayloadEnd);
            ServerResourceSnapshot snapshot = new ServerResourceSnapshot
            {
                CapturedAtUtc = DateTime.UtcNow
            };

            foreach (string rawLine in payload.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                string[] fields = line.Split('|');
                if (fields.Length == 0)
                    continue;

                if (fields[0] == "META" && fields.Length >= 10)
                {
                    snapshot.HostName = fields[1];
                    snapshot.OperatingSystem = fields[2];
                    snapshot.UptimeSeconds = ParseDouble(fields[3]);
                    snapshot.CpuUsagePercent = ParseDouble(fields[4]);
                    snapshot.LogicalProcessorCount = (int)ParseLong(fields[5]);
                    snapshot.MemoryTotalBytes = ParseLong(fields[6]);
                    snapshot.MemoryAvailableBytes = ParseLong(fields[7]);
                    snapshot.MemoryUsedBytes = Math.Max(0, snapshot.MemoryTotalBytes - snapshot.MemoryAvailableBytes);
                    snapshot.NetworkTotalReceivedBytes = ParseLong(fields[8]);
                    snapshot.NetworkTotalSentBytes = ParseLong(fields[9]);
                }
                else if (fields[0] == "NET" && fields.Length >= 3)
                {
                    snapshot.NetworkSamples.Add(new NetworkRateSample
                    {
                        ReceivedBytesPerSecond = Math.Max(0, ParseDouble(fields[1])),
                        SentBytesPerSecond = Math.Max(0, ParseDouble(fields[2]))
                    });
                }
                else if (fields[0] == "DISK" && fields.Length >= 4)
                {
                    snapshot.Disks.Add(new DiskResourceSnapshot
                    {
                        Name = fields[1],
                        TotalBytes = ParseLong(fields[2]),
                        FreeBytes = ParseLong(fields[3])
                    });
                }
                else if (fields[0] == "PROC" && fields.Length >= 5 && !IsLinuxSystemProcess(fields[2]))
                {
                    snapshot.ExtraProcesses.Add(new ProcessResourceSnapshot
                    {
                        Id = (int)ParseLong(fields[1]),
                        Name = fields[2],
                        CpuPercent = Math.Max(0, ParseDouble(fields[3])),
                        WorkingSetBytes = Math.Max(0, ParseLong(fields[4]) * 1024)
                    });
                }
            }

            snapshot.ExtraProcesses = snapshot.ExtraProcesses
                .OrderByDescending(item => item.CpuPercent)
                .ThenByDescending(item => item.WorkingSetBytes)
                .Take(24)
                .ToList();
            if (string.IsNullOrWhiteSpace(snapshot.HostName))
                throw new InvalidOperationException("Linux 未返回有效的资源数据");
            return snapshot;
        }

        private static bool IsLinuxSystemProcess(string name)
        {
            string value = (name ?? "").Trim('[', ']');
            return LinuxSystemProcesses.Contains(value) ||
                value.StartsWith("kworker/", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("migration/", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("watchdog/", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("cpuhp/", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("rcu_", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("irq/", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("scsi_", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("xfs-", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractMarkedLine(string output, string marker)
        {
            string source = output ?? "";
            int markerIndex = source.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
                throw new InvalidOperationException("远程命令未返回资源数据标记");
            int start = markerIndex + marker.Length;
            int end = source.IndexOfAny(new[] { '\r', '\n' }, start);
            string payload = (end < 0 ? source.Substring(start) : source.Substring(start, end - start)).Trim();
            if (payload.Length == 0)
                throw new InvalidOperationException("远程命令返回了空的资源数据");
            return payload;
        }

        private static string ExtractBlock(string output, string beginMarker, string endMarker)
        {
            string source = output ?? "";
            int begin = source.IndexOf(beginMarker, StringComparison.Ordinal);
            if (begin < 0)
                throw new InvalidOperationException("远程命令未返回资源数据起始标记");
            begin += beginMarker.Length;
            int end = source.IndexOf(endMarker, begin, StringComparison.Ordinal);
            if (end < 0)
                throw new InvalidOperationException("远程命令未返回资源数据结束标记");
            return source.Substring(begin, end - begin);
        }

        private static void Normalize(ServerResourceSnapshot snapshot)
        {
            snapshot.CpuUsagePercent = Math.Max(0, Math.Min(100, snapshot.CpuUsagePercent));
            snapshot.LogicalProcessorCount = Math.Max(1, snapshot.LogicalProcessorCount);
            snapshot.MemoryTotalBytes = Math.Max(0, snapshot.MemoryTotalBytes);
            snapshot.MemoryAvailableBytes = Math.Max(0, snapshot.MemoryAvailableBytes);
            snapshot.MemoryUsedBytes = Math.Max(0, Math.Min(snapshot.MemoryTotalBytes, snapshot.MemoryUsedBytes));
            snapshot.NetworkTotalReceivedBytes = Math.Max(0, snapshot.NetworkTotalReceivedBytes);
            snapshot.NetworkTotalSentBytes = Math.Max(0, snapshot.NetworkTotalSentBytes);
            snapshot.Disks = snapshot.Disks ?? new List<DiskResourceSnapshot>();
            snapshot.NetworkSamples = snapshot.NetworkSamples ?? new List<NetworkRateSample>();
            snapshot.ExtraProcesses = snapshot.ExtraProcesses ?? new List<ProcessResourceSnapshot>();
            if (snapshot.CapturedAtUtc == DateTime.MinValue)
                snapshot.CapturedAtUtc = DateTime.UtcNow;
        }

        private static void EnsureSuccess(RemoteCommandResult result, string operation)
        {
            if (result != null && result.ExitCode == 0)
                return;
            throw new InvalidOperationException(operation + "失败：" + RemoteErrorFormatter.Format(result));
        }

        private static long ParseLong(string value)
        {
            long result;
            return long.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out result) ? result : 0;
        }

        private static double ParseDouble(string value)
        {
            double result;
            return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out result) ? result : 0;
        }

        private static void Report(IProgress<ResourceCollectionProgress> progress, int percentage, string message)
        {
            if (progress != null)
                progress.Report(new ResourceCollectionProgress(percentage, message));
        }
    }
}
