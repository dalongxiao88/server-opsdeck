using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RDPManager
{
    public sealed class OraclePortAdapter : WindowsPortServiceAdapter
    {
        public override string ServiceType { get { return "Oracle"; } }
        protected override TimeSpan ApplyTimeout { get { return TimeSpan.FromSeconds(70); } }
        protected override TimeSpan RollbackTimeout { get { return TimeSpan.FromSeconds(70); } }

        protected override string DetectionScript
        {
            get
            {
                return @"
$items=@(Get-CimInstance Win32_Service | Where-Object { $_.Name -match '(?i)TNSListener|Oracle.*Listener' -or $_.PathName -match '(?i)tnslsnr.exe' })
$result=@()
foreach($item in $items) {
    $raw=[string]$item.PathName
    $exe=''
    if($raw -match '^\s*""([^""]+)""'){ $exe=$matches[1] }
    elseif($raw -match '^\s*(\S+)'){ $exe=$matches[1] }
    $bin=if($exe){Split-Path $exe -Parent}else{''}
    $oracleHome=if($bin){Split-Path $bin -Parent}else{''}
    $oracleBase=if($oracleHome){Split-Path $oracleHome -Parent}else{''}
    $candidates=@()
    if($oracleHome){$candidates += Join-Path $oracleHome 'network\admin\listener.ora'}
    if($oracleBase -and (Test-Path (Join-Path $oracleBase 'homes'))) {
        $candidates += @(Get-ChildItem -LiteralPath (Join-Path $oracleBase 'homes') -Filter 'listener.ora' -Recurse -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
    }
    $config=$candidates | Where-Object {
        Test-Path $_ -PathType Leaf -and
        @(Get-Content -LiteralPath $_ -ErrorAction SilentlyContinue | Where-Object { $_ -notmatch '^\s*#' -and $_ -match '(?i)PORT\s*=\s*\d+' }).Count -gt 0
    } | Select-Object -First 1
    if(-not $config) { $config=$candidates | Where-Object { Test-Path $_ -PathType Leaf } | Select-Object -First 1 }
    $port=1521
    if($config -and (Test-Path $config)) {
        $content=Get-Content -LiteralPath $config -Raw
        $tcp=[regex]::Match($content,'(?is)\(ADDRESS\s*=\s*\(PROTOCOL\s*=\s*TCP\).*?\(PORT\s*=\s*(\d+)\)')
        if($tcp.Success){ $port=[int]$tcp.Groups[1].Value }
    }
    $serviceNames=[System.Collections.Generic.List[string]]::new()
    $sqlplus=if($oracleHome){ Join-Path $oracleHome 'bin\sqlplus.exe' }else{''}
    if($sqlplus -and (Test-Path $sqlplus)) {
        $oldOracleHome=$env:ORACLE_HOME
        $oldOracleSid=$env:ORACLE_SID
        $oldPath=$env:PATH
        $sqlFile=Join-Path ([IO.Path]::GetTempPath()) ('xiaobai-oracle-detect-'+[Guid]::NewGuid().ToString('N')+'.sql')
        try {
            $env:ORACLE_HOME=$oracleHome
            $env:ORACLE_SID='XE'
            $env:PATH=(Join-Path $oracleHome 'bin')+';'+$env:PATH
            $sqlLines=@(
                'set heading off feedback off pagesize 0 verify off'
                'select ''DOMAIN=''||value from v$parameter where name=''db_domain'';'
                'select ''PRIMARY=''||value from v$parameter where name=''service_names'';'
                'select ''ACTIVE=''||network_name from v$active_services where network_name is not null order by network_name;'
                'exit'
            )
            [IO.File]::WriteAllLines($sqlFile,$sqlLines,(New-Object Text.UTF8Encoding($false)))
            $sqlOutput=(& $sqlplus -L -S '/ as sysdba' ('@'+$sqlFile) 2>&1 | Out-String)
            $domain=''
            foreach($line in ($sqlOutput -split ""`r?`n"")) {
                $value=$line.Trim()
                if($value -match '^DOMAIN=(.*)$') { $domain=$matches[1].Trim(); continue }
                if($value -match '^(PRIMARY|ACTIVE)=(.+)$') {
                    $kind=$matches[1]
                    foreach($name in ($matches[2] -split ',')) {
                        $name=$name.Trim()
                        if($name -match '^[A-Za-z0-9._$#-]+$' -and -not $serviceNames.Contains($name)) { $serviceNames.Add($name) }
                        if($kind -eq 'ACTIVE' -and $domain -and $name -notmatch '\.' ) {
                            $qualified=$name+'.'+$domain
                            if($qualified -match '^[A-Za-z0-9._$#-]+$' -and -not $serviceNames.Contains($qualified)) { $serviceNames.Add($qualified) }
                        }
                    }
                }
            }
        }
        catch { }
        finally {
            Remove-Item -LiteralPath $sqlFile -Force -ErrorAction SilentlyContinue
            $env:ORACLE_HOME=$oldOracleHome
            $env:ORACLE_SID=$oldOracleSid
            $env:PATH=$oldPath
        }
    }
    if($serviceNames.Count -eq 0) { $serviceNames.Add('XE.localdomain'); $serviceNames.Add('XEPDB1') }
    $result += [pscustomobject]@{
        ServiceType='Oracle'
        DisplayName=($item.DisplayName + ' / Listener')
        ServiceName=$item.Name
        ConfigPath=$config
        Protocol='TCP'
        Port=$port
        ServiceStatus=[string]$item.State
        IsSupported=($config -and (Test-Path $config))
        TargetKey=($item.Name + '|' + $config + '|' + ($serviceNames -join ';'))
    }
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
$ErrorActionPreference='Stop'
Copy-Item -LiteralPath __CONFIG_PATH__ -Destination __BACKUP_PATH__ -Force
$text=Get-Content -LiteralPath __CONFIG_PATH__ -Raw
$tcpPattern='(?is)(\(ADDRESS\s*=\s*\(PROTOCOL\s*=\s*TCP\).*?\(HOST\s*=\s*([^)]+)\).*?\(PORT\s*=\s*)\d+'
$tcpMatch=[regex]::Match($text,$tcpPattern)
if(-not $tcpMatch.Success){ throw 'listener.ora 中没有找到 TCP HOST/PORT 配置' }
$listenerHost=$tcpMatch.Groups[2].Value.Trim()
$text=[regex]::Replace($text,$tcpPattern,'${1}__NEW_PORT__',1)
[IO.File]::WriteAllText(__CONFIG_PATH__, $text, (New-Object Text.UTF8Encoding($false)))
if(__CONFIGURE_FIREWALL__ -and -not (Get-NetFirewallRule -DisplayName __FIREWALL_RULE__ -ErrorAction SilentlyContinue)) { New-NetFirewallRule -DisplayName __FIREWALL_RULE__ -Direction Inbound -Protocol TCP -LocalPort __NEW_PORT__ -Action Allow -Profile Any -ErrorAction Stop | Out-Null }

$serviceItem=Get-CimInstance Win32_Service | Where-Object { $_.Name -eq __SERVICE_NAME__ } | Select-Object -First 1
if($null -eq $serviceItem){ throw '无法读取 Oracle Listener 服务信息' }
$raw=[string]$serviceItem.PathName
$listenerExe=''
if($raw -match '^\s*""([^""]+)""'){ $listenerExe=$matches[1] }
elseif($raw -match '^\s*(\S+)'){ $listenerExe=$matches[1] }
$oracleHome=if($listenerExe){ Split-Path (Split-Path $listenerExe -Parent) -Parent }else{''}
$sqlplus=if($oracleHome){ Join-Path $oracleHome 'bin\sqlplus.exe' }else{''}
if(-not $sqlplus -or -not (Test-Path $sqlplus)){
    $searchRoot=Split-Path (Split-Path __CONFIG_PATH__ -Parent) -Parent
    $sqlplus=Get-ChildItem -LiteralPath $searchRoot -Filter 'sqlplus.exe' -Recurse -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName -First 1
}
if(-not $sqlplus -or -not (Test-Path $sqlplus)){ throw '没有找到 Oracle sqlplus.exe，无法更新数据库监听注册' }

Restart-Service -Name __SERVICE_NAME__ -Force
Start-Sleep -Seconds 4
if((Get-Service -Name __SERVICE_NAME__).Status -ne 'Running'){ throw 'Oracle Listener 服务未恢复运行' }

$env:ORACLE_HOME=$oracleHome
$env:ORACLE_SID='XE'
$env:PATH=(Join-Path $oracleHome 'bin')+';'+$env:PATH
$address='(ADDRESS=(PROTOCOL=TCP)(HOST='+$listenerHost+')(PORT=__NEW_PORT__))'
$sqlFile=Join-Path ([IO.Path]::GetTempPath()) ('xiaobai-oracle-'+[Guid]::NewGuid().ToString('N')+'.sql')
try {
    $sqlLines=@(
        'whenever sqlerror exit sql.sqlcode'
        'set echo off heading off feedback off pagesize 0 verify off'
        (""alter system set local_listener='$address' scope=both;"")
        'alter system register;'
        'exit'
    )
    [IO.File]::WriteAllLines($sqlFile,$sqlLines,(New-Object Text.UTF8Encoding($false)))
    $sqlOutput=(& $sqlplus -L -S '/ as sysdba' ('@'+$sqlFile) 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $sqlOutput -match '(?i)(ORA|SP2)-\d+'){
        throw ('Oracle 数据库注册新监听端口失败：'+$sqlOutput)
    }
}
finally { Remove-Item -LiteralPath $sqlFile -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 3
'ORACLE_PORT_CHANGE_APPLIED'
";
            }
        }

        protected override string RollbackScript
        {
            get
            {
                return @"
$ErrorActionPreference='Stop'
Copy-Item -LiteralPath __BACKUP_PATH__ -Destination __CONFIG_PATH__ -Force
$text=Get-Content -LiteralPath __CONFIG_PATH__ -Raw
$tcpPattern='(?is)\(ADDRESS\s*=\s*\(PROTOCOL\s*=\s*TCP\).*?\(HOST\s*=\s*([^)]+)\).*?\(PORT\s*=\s*(\d+)\)'
$tcpMatch=[regex]::Match($text,$tcpPattern)
if(-not $tcpMatch.Success){ throw '备份 listener.ora 中没有找到原 TCP HOST/PORT 配置' }
$listenerHost=$tcpMatch.Groups[1].Value.Trim()
$oldPort=[int]$tcpMatch.Groups[2].Value

$serviceItem=Get-CimInstance Win32_Service | Where-Object { $_.Name -eq __SERVICE_NAME__ } | Select-Object -First 1
$raw=[string]$serviceItem.PathName
$listenerExe=''
if($raw -match '^\s*""([^""]+)""'){ $listenerExe=$matches[1] }
elseif($raw -match '^\s*(\S+)'){ $listenerExe=$matches[1] }
$oracleHome=if($listenerExe){ Split-Path (Split-Path $listenerExe -Parent) -Parent }else{''}
$sqlplus=if($oracleHome){ Join-Path $oracleHome 'bin\sqlplus.exe' }else{''}
if(-not $sqlplus -or -not (Test-Path $sqlplus)){ throw '回滚时没有找到 Oracle sqlplus.exe' }

Restart-Service -Name __SERVICE_NAME__ -Force
Start-Sleep -Seconds 4
if((Get-Service -Name __SERVICE_NAME__).Status -ne 'Running'){ throw 'Oracle Listener 回滚后未恢复运行' }

$env:ORACLE_HOME=$oracleHome
$env:ORACLE_SID='XE'
$env:PATH=(Join-Path $oracleHome 'bin')+';'+$env:PATH
$address='(ADDRESS=(PROTOCOL=TCP)(HOST='+$listenerHost+')(PORT='+$oldPort+'))'
$sqlFile=Join-Path ([IO.Path]::GetTempPath()) ('xiaobai-oracle-rollback-'+[Guid]::NewGuid().ToString('N')+'.sql')
try {
    $sqlLines=@(
        'whenever sqlerror exit sql.sqlcode'
        'set echo off heading off feedback off pagesize 0 verify off'
        (""alter system set local_listener='$address' scope=both;"")
        'alter system register;'
        'exit'
    )
    [IO.File]::WriteAllLines($sqlFile,$sqlLines,(New-Object Text.UTF8Encoding($false)))
    $sqlOutput=(& $sqlplus -L -S '/ as sysdba' ('@'+$sqlFile) 2>&1 | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $sqlOutput -match '(?i)(ORA|SP2)-\d+'){
        throw ('Oracle 数据库恢复原监听注册失败：'+$sqlOutput)
    }
}
finally { Remove-Item -LiteralPath $sqlFile -Force -ErrorAction SilentlyContinue }
if(__FIREWALL_RULE_CREATED__){ Remove-NetFirewallRule -DisplayName __FIREWALL_RULE__ -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 3
'ORACLE_PORT_ROLLBACK_APPLIED'
";
            }
        }
    }
}
