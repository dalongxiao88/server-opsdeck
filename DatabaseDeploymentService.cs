using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using MySqlConnector;

namespace RDPManager
{
    public sealed class DatabaseDeploymentProgress
    {
        public int Step { get; set; }
        public OperationStepState State { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }
        public int Percent { get; set; }
        public bool Indeterminate { get; set; }
    }

    public sealed class DatabaseDeploymentResult
    {
        public string DatabaseType { get; set; }
        public string ExactVersion { get; set; }
        public string ServiceName { get; set; }
        public string DisplayName { get; set; }
        public int Port { get; set; }
        public string ConfigPath { get; set; }
        public string InstallPath { get; set; }
        public string DataPath { get; set; }
        public string TemporaryPath { get; set; }
        public string PackageName { get; set; }
        public string AdditionalConfigPath { get; set; }
        public bool PackageInstalledByManager { get; set; }
        public string ServerVersion { get; set; }
        public DatabaseCredentialRecord Credential { get; set; }
    }

    internal sealed class DatabaseDeploymentPackage
    {
        public string DatabaseType { get; set; }
        public string VersionTrack { get; set; }
        public string ExactVersion { get; set; }
        public string Url { get; set; }
        public string Sha256 { get; set; }
        public long MinimumFreeBytes { get; set; }
        public string AdditionalUrl { get; set; }
        public string AdditionalSha256 { get; set; }
    }

    public sealed class DatabaseDeploymentService
    {
        private static readonly Regex SafeServiceName = new Regex(@"^[A-Za-z][A-Za-z0-9_-]{2,48}$", RegexOptions.Compiled);
        private static readonly Regex SafeDatabaseName = new Regex(@"^[A-Za-z0-9_$.-]{1,64}$", RegexOptions.Compiled);
        private static readonly Regex SafeUserName = new Regex(@"^[A-Za-z0-9_.-]{1,64}$", RegexOptions.Compiled);

        public async Task<int> SuggestAvailablePortAsync(
            Server server,
            string serverPassword,
            int preferredPort,
            bool randomize,
            CancellationToken cancellationToken)
        {
            ValidateServer(server, serverPassword);
            using (IRemoteExecutor executor = await RemoteExecutorFactory.CreateAsync(
                server, serverPassword, cancellationToken, RemoteTransport.SSH))
            {
                string script = @"
$listeners=@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty LocalPort)
$preferred=__PREFERRED__
$randomize=__RANDOMIZE__
if(!$randomize -and $listeners -notcontains $preferred){$preferred;exit 0}
$candidates=if($randomize){10000..60000|Get-Random -Count 1000}else{@($preferred)+(10000..60000|Get-Random -Count 1000)}
$available=$candidates|Where-Object{$listeners -notcontains $_}|Select-Object -First 1
if(!$available){throw '未找到可用端口'}
$available
".Replace("__PREFERRED__", preferredPort.ToString())
 .Replace("__RANDOMIZE__", randomize ? "$true" : "$false");
                RemoteCommandResult result = await executor.ExecutePowerShellAsync(script, TimeSpan.FromSeconds(30), cancellationToken);
                EnsureSuccess(result, "探测服务器可用端口");
                int port;
                if (!int.TryParse((result.Output ?? "").Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault(), out port) || port < 1 || port > 65535)
                    throw new InvalidOperationException("服务器没有返回有效端口");
                return port;
            }
        }

        public async Task<DatabaseDeploymentResult> DeployAsync(
            Server server,
            string serverPassword,
            DatabaseDeploymentDraft draft,
            Action<DatabaseDeploymentProgress> progress,
            CancellationToken cancellationToken)
        {
            ValidateServer(server, serverPassword);
            ValidateDraft(draft);
            DatabaseDeploymentPackage package = ResolvePackage(draft.DatabaseType, draft.VersionTrack);
            int requestedPort = draft.Port;
            int availablePort = await SuggestAvailablePortAsync(
                server,
                serverPassword,
                requestedPort,
                false,
                cancellationToken);
            if (availablePort != requestedPort)
                draft.Port = availablePort;
            string deploymentId = Guid.NewGuid().ToString("N");
            string installPath = @"C:\Program Files\XiaoBai Databases\" + draft.ServiceName;
            string dataPath = @"C:\ProgramData\XiaoBai Databases\" + draft.ServiceName;
            string temporaryPath = @"C:\Windows\Temp\xiaobai-db-deploy-" + deploymentId;
            DatabaseDeploymentResult deployment = new DatabaseDeploymentResult
            {
                DatabaseType = draft.DatabaseType,
                ExactVersion = package.ExactVersion,
                ServiceName = draft.ServiceName,
                DisplayName = GetDisplayName(draft),
                Port = draft.Port,
                InstallPath = installPath,
                DataPath = dataPath,
                TemporaryPath = temporaryPath
            };
            bool rollbackAuthorized = false;

            try
            {
                Report(progress, 0, OperationStepState.Running, "检查服务器环境", "验证 Windows、管理员权限和磁盘空间", 5, true);
                using (IRemoteExecutor executor = await RemoteExecutorFactory.CreateAsync(
                    server, serverPassword, cancellationToken, RemoteTransport.SSH))
                {
                    await ExecuteCheckedAsync(
                        executor,
                        BuildPreflightScript(draft, package, installPath, dataPath),
                        TimeSpan.FromMinutes(2),
                        "部署环境检查",
                        cancellationToken);
                    rollbackAuthorized = true;
                    Report(progress, 0, OperationStepState.Completed, "检查服务器环境", "环境符合要求", 10, false);

                    Report(progress, 1, OperationStepState.Running, "检查端口与现有服务", "确认服务名、数据库类型和端口未被占用", 14, true);
                    string portDetail = availablePort == requestedPort
                        ? "端口 " + requestedPort + " 可用"
                        : "端口 " + requestedPort + " 已占用，自动改用 " + availablePort;
                    Report(progress, 1, OperationStepState.Completed, "检查端口与现有服务", portDetail, 18, false);

                    Report(progress, 2, OperationStepState.Running, "下载并校验安装包", package.DatabaseType + " " + package.ExactVersion, 22, true);
                    await ExecuteCheckedAsync(
                        executor,
                        BuildDownloadScript(package, draft, temporaryPath, installPath, dataPath),
                        TimeSpan.FromMinutes(60),
                        "下载安装包",
                        cancellationToken);
                    Report(progress, 2, OperationStepState.Completed, "下载并校验安装包", "SHA-256 校验通过", 42, false);

                    Report(progress, 3, OperationStepState.Running, "初始化数据库", "解压并创建独立数据目录", 46, true);
                    Report(progress, 4, OperationStepState.Running, "注册并启动服务", draft.ServiceName, 52, true);
                    RemoteCommandResult install = await executor.ExecutePowerShellAsync(
                        BuildInstallScript(draft, package, installPath, dataPath, temporaryPath),
                        TimeSpan.FromMinutes(30),
                        cancellationToken);
                    EnsureSuccess(install, "安装并启动数据库服务");
                    Dictionary<string, string> installResult = ParseJson(install.Output, "数据库安装结果");
                    deployment.ConfigPath = GetValue(installResult, "ConfigPath");
                    Report(progress, 3, OperationStepState.Completed, "初始化数据库", "数据目录已初始化", 60, false);
                    Report(progress, 4, OperationStepState.Completed, "注册并启动服务", "服务正在运行", 66, false);

                    Report(progress, 5, OperationStepState.Running, "创建管理账号与初始数据库", "凭据通过 SSH 隧道发送", 70, true);
                    await InitializeCredentialsAsync(server, serverPassword, draft, cancellationToken);
                    if (string.Equals(draft.DatabaseType, "MongoDB", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(draft.DatabaseType, "Redis", StringComparison.OrdinalIgnoreCase))
                    {
                        await ExecuteCheckedAsync(
                            executor,
                            BuildEnableAuthenticationScript(draft, installPath, deployment.ConfigPath),
                            TimeSpan.FromMinutes(3),
                            "启用数据库认证",
                            cancellationToken);
                    }
                    Report(progress, 5, OperationStepState.Completed, "创建管理账号与初始数据库", "账号和初始库已创建", 78, false);
                }

                Report(progress, 6, OperationStepState.Running, "SSH 隧道连接验证", "使用新凭据重新登录", 82, true);
                DatabaseCredentialRecord credential = CreateCredential(draft, deployment);
                deployment.ServerVersion = await VerifyAsync(server, serverPassword, credential, cancellationToken);
                credential.InstalledVersion = deployment.ServerVersion;
                deployment.Credential = credential;
                Report(progress, 6, OperationStepState.Completed, "SSH 隧道连接验证", deployment.ServerVersion, 90, false);

                return deployment;
            }
            catch (Exception deploymentError)
            {
                if (rollbackAuthorized)
                {
                    try
                    {
                        await RollbackAsync(server, serverPassword, deployment, CancellationToken.None);
                    }
                    catch (Exception rollbackError)
                    {
                        throw new InvalidOperationException(
                            deploymentError.Message + "\n\n自动回滚未能完整完成：" + rollbackError.Message,
                            deploymentError);
                    }
                }
                throw;
            }
        }

        public async Task RollbackAsync(
            Server server,
            string serverPassword,
            DatabaseDeploymentResult deployment,
            CancellationToken cancellationToken)
        {
            if (server == null || deployment == null || !SafeServiceName.IsMatch(deployment.ServiceName ?? ""))
                return;
            using (IRemoteExecutor executor = await RemoteExecutorFactory.CreateAsync(
                server, serverPassword, cancellationToken, RemoteTransport.SSH))
            {
                RemoteCommandResult result = await executor.ExecutePowerShellAsync(
                    BuildRollbackScript(deployment),
                    TimeSpan.FromMinutes(5),
                    cancellationToken);
                EnsureSuccess(result, "自动回滚");
            }
        }

        public Task CleanupAsync(
            Server server,
            string serverPassword,
            DatabaseDeploymentResult deployment,
            CancellationToken cancellationToken)
        {
            if (deployment == null || string.IsNullOrWhiteSpace(deployment.TemporaryPath))
                return Task.CompletedTask;
            return CleanupTemporaryAsync(server, serverPassword, deployment.TemporaryPath, cancellationToken);
        }

        private static async Task InitializeCredentialsAsync(
            Server server,
            string serverPassword,
            DatabaseDeploymentDraft draft,
            CancellationToken cancellationToken)
        {
            if (string.Equals(draft.DatabaseType, "MySQL", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(draft.DatabaseType, "MariaDB", StringComparison.OrdinalIgnoreCase))
            {
                await InitializeMySqlFamilyAsync(server, serverPassword, draft, cancellationToken);
                return;
            }
            if (string.Equals(draft.DatabaseType, "MongoDB", StringComparison.OrdinalIgnoreCase))
            {
                await InitializeMongoAsync(server, serverPassword, draft, cancellationToken);
                return;
            }
            if (string.Equals(draft.DatabaseType, "Redis", StringComparison.OrdinalIgnoreCase))
            {
                await InitializeRedisAsync(server, serverPassword, draft, cancellationToken);
                return;
            }
            throw new InvalidOperationException("当前数据库类型不支持自动初始化");
        }

        private static async Task InitializeMySqlFamilyAsync(
            Server server,
            string serverPassword,
            DatabaseDeploymentDraft draft,
            CancellationToken cancellationToken)
        {
            using (SshDatabaseTunnel tunnel = await SshDatabaseTunnel.OpenAsync(server, serverPassword, "127.0.0.1", draft.Port, cancellationToken))
            {
                MySqlConnectionStringBuilder builder = new MySqlConnectionStringBuilder
                {
                    Server = "127.0.0.1",
                    Port = (uint)tunnel.LocalPort,
                    UserID = "root",
                    Password = "",
                    SslMode = MySqlSslMode.None,
                    AllowPublicKeyRetrieval = true,
                    Pooling = false,
                    ConnectionTimeout = 15,
                    DefaultCommandTimeout = 30
                };
                using (MySqlConnection connection = new MySqlConnection(builder.ConnectionString))
                {
                    await connection.OpenAsync(cancellationToken);
                    using (MySqlCommand create = connection.CreateCommand())
                    {
                        create.CommandText = "CREATE DATABASE `" + draft.DatabaseName.Replace("`", "``") + "` CHARACTER SET utf8mb4";
                        await create.ExecuteNonQueryAsync(cancellationToken);
                    }
                    using (MySqlCommand password = connection.CreateCommand())
                    {
                        password.CommandText = "ALTER USER 'root'@'localhost' IDENTIFIED BY @password";
                        password.Parameters.AddWithValue("@password", draft.AdminPassword);
                        await password.ExecuteNonQueryAsync(cancellationToken);
                    }
                }
            }
        }

        private static async Task InitializeMongoAsync(
            Server server,
            string serverPassword,
            DatabaseDeploymentDraft draft,
            CancellationToken cancellationToken)
        {
            using (SshDatabaseTunnel tunnel = await SshDatabaseTunnel.OpenAsync(server, serverPassword, "127.0.0.1", draft.Port, cancellationToken))
            {
                MongoClientSettings settings = new MongoClientSettings
                {
                    Server = new MongoServerAddress("127.0.0.1", tunnel.LocalPort),
                    DirectConnection = true,
                    ServerSelectionTimeout = TimeSpan.FromSeconds(15),
                    ConnectTimeout = TimeSpan.FromSeconds(10)
                };
                MongoClient client = new MongoClient(settings);
                IMongoDatabase admin = client.GetDatabase("admin");
                BsonDocument command = new BsonDocument
                {
                    { "createUser", draft.AdminUser },
                    { "pwd", draft.AdminPassword },
                    { "roles", new BsonArray { new BsonDocument { { "role", "root" }, { "db", "admin" } } } }
                };
                await admin.RunCommandAsync<BsonDocument>(command, cancellationToken: cancellationToken);
                await client.GetDatabase(draft.DatabaseName).CreateCollectionAsync("_xiaobai_init", cancellationToken: cancellationToken);
            }
        }

        private static async Task InitializeRedisAsync(
            Server server,
            string serverPassword,
            DatabaseDeploymentDraft draft,
            CancellationToken cancellationToken)
        {
            using (SshDatabaseTunnel tunnel = await SshDatabaseTunnel.OpenAsync(server, serverPassword, "127.0.0.1", draft.Port, cancellationToken))
            using (RedisRespConnection initial = await RedisRespConnection.ConnectAsync("127.0.0.1", tunnel.LocalPort, cancellationToken))
            {
                string pong = Convert.ToString(await initial.CommandAsync(cancellationToken, "PING"));
                if (!string.Equals(pong, "PONG", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Redis 初始化连接没有返回 PONG");
                await initial.CommandAsync(cancellationToken, "ACL", "SETUSER", draft.AdminUser, "reset", "on", ">" + draft.AdminPassword, "~*", "&*", "+@all");

                using (RedisRespConnection admin = await RedisRespConnection.ConnectAsync("127.0.0.1", tunnel.LocalPort, cancellationToken))
                {
                    await admin.CommandAsync(cancellationToken, "AUTH", draft.AdminUser, draft.AdminPassword);
                    await admin.CommandAsync(cancellationToken, "SELECT", draft.DatabaseName);
                    await admin.CommandAsync(cancellationToken, "FLUSHDB");
                    await admin.CommandAsync(cancellationToken, "ACL", "SETUSER", "default", "off", "resetpass");
                    await admin.CommandAsync(cancellationToken, "ACL", "SAVE");
                }
            }
        }

        private static async Task<string> VerifyAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            CancellationToken cancellationToken)
        {
            if (credential.DatabaseType == "MySQL" || credential.DatabaseType == "MariaDB")
                return (await new MySqlDatabaseService().TestConnectionAsync(server, serverPassword, credential, cancellationToken)).ServerVersion;
            if (credential.DatabaseType == "MongoDB")
                return (await new MongoDatabaseService().TestConnectionAsync(server, serverPassword, credential, cancellationToken)).ServerVersion;
            if (credential.DatabaseType == "Redis")
                return (await new RedisDatabaseService().TestConnectionAsync(server, serverPassword, credential, cancellationToken)).Version;
            throw new InvalidOperationException("当前数据库类型没有验证适配器");
        }

        private static DatabaseCredentialRecord CreateCredential(DatabaseDeploymentDraft draft, DatabaseDeploymentResult deployment)
        {
            return new DatabaseCredentialRecord
            {
                DatabaseType = draft.DatabaseType,
                ServiceName = deployment.DisplayName,
                Host = "127.0.0.1",
                Port = draft.Port,
                Username = draft.AdminUser,
                Password = draft.AdminPassword,
                AuthenticationDatabase = draft.DatabaseType == "MongoDB" ? "admin" : "",
                DatabaseName = draft.DatabaseName,
                LastVerifiedAt = DateTime.Now,
                IsVerified = true,
                IsManagerDeployed = true,
                InstalledVersion = deployment.ExactVersion,
                InstallPath = deployment.InstallPath,
                DeployedAt = DateTime.Now,
                Users = new List<DatabaseUserRecord>()
            };
        }

        private static string BuildPreflightScript(DatabaseDeploymentDraft draft, DatabaseDeploymentPackage package, string installPath, string dataPath)
        {
            string predicate;
            switch (draft.DatabaseType)
            {
                case "MySQL":
                    predicate = "$_.Name -match '(?i)^mysql' -or ($_.PathName -match '(?i)mysql' -and $_.PathName -notmatch '(?i)maria')";
                    break;
                case "MariaDB":
                    predicate = "$_.Name -match '(?i)maria' -or $_.PathName -match '(?i)maria'";
                    break;
                case "MongoDB":
                    predicate = "$_.Name -match '(?i)mongo' -or $_.PathName -match '(?i)mongod'";
                    break;
                default:
                    predicate = "$_.Name -match '(?i)redis' -or $_.PathName -match '(?i)redis-server|RedisService'";
                    break;
            }
            return @"
$ErrorActionPreference='Stop'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=New-Object Security.Principal.WindowsPrincipal($identity)
if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw '远程账号没有管理员权限'}
$os=Get-CimInstance Win32_OperatingSystem
if($os.OSArchitecture -notmatch '64'){throw '只支持 64 位 Windows'}
$free=[int64](Get-PSDrive C).Free
if($free -lt __MIN_FREE__){throw '系统盘可用空间不足'}
if(Get-Service -Name __SERVICE__ -ErrorAction SilentlyContinue){throw '服务名称已被占用'}
$sameType=@(Get-CimInstance Win32_Service|Where-Object{__TYPE_PREDICATE__})
if($sameType.Count -gt 0){throw ('已检测到同类型数据库服务：'+(($sameType|Select-Object -ExpandProperty Name)-join '、'))}
if(Test-Path -LiteralPath __INSTALL__){throw '安装目录已存在'}
if(Test-Path -LiteralPath __DATA__){throw '数据目录已存在'}
if(Get-NetTCPConnection -State Listen -LocalPort __PORT__ -ErrorAction SilentlyContinue){throw '端口已被占用'}
'PREFLIGHT_OK'
".Replace("__MIN_FREE__", package.MinimumFreeBytes.ToString())
 .Replace("__SERVICE__", QuotePowerShell(draft.ServiceName))
 .Replace("__TYPE_PREDICATE__", predicate)
 .Replace("__INSTALL__", QuotePowerShell(installPath))
 .Replace("__DATA__", QuotePowerShell(dataPath))
 .Replace("__PORT__", draft.Port.ToString());
        }

        private static string BuildDownloadScript(
            DatabaseDeploymentPackage package,
            DatabaseDeploymentDraft draft,
            string temporaryPath,
            string installPath,
            string dataPath)
        {
            string additional = string.IsNullOrWhiteSpace(package.AdditionalUrl) ? "" : @"
$extra=Join-Path $root 'extra.zip'
& curl.exe --fail --location --retry 5 --silent --show-error --output $extra __EXTRA_URL__
if($LASTEXITCODE -ne 0){throw '附加工具下载失败'}
$extraHash=(Get-FileHash -LiteralPath $extra -Algorithm SHA256).Hash
if($extraHash -ne __EXTRA_HASH__){throw '附加工具 SHA-256 校验失败'}
".Replace("__EXTRA_URL__", QuotePowerShell(package.AdditionalUrl))
 .Replace("__EXTRA_HASH__", QuotePowerShell(package.AdditionalSha256));
            return @"
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$root=__TEMP__
if(Test-Path -LiteralPath $root){throw '部署临时目录已存在'}
New-Item -ItemType Directory -Path $root|Out-Null
$owner=[pscustomobject]@{ManagedBy='XiaoBaiServerManager';ServiceName=__SERVICE__;InstallPath=__INSTALL__;DataPath=__DATA__}
$owner|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'xiaobai-deployment-owner.json') -Encoding UTF8
$archive=Join-Path $root 'server.zip'
& curl.exe --fail --location --retry 5 --silent --show-error --output $archive __URL__
if($LASTEXITCODE -ne 0){throw '数据库安装包下载失败'}
$hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if($hash -ne __HASH__){throw '数据库安装包 SHA-256 校验失败'}
__ADDITIONAL__
'DOWNLOAD_OK'
".Replace("__TEMP__", QuotePowerShell(temporaryPath))
 .Replace("__SERVICE__", QuotePowerShell(draft.ServiceName))
 .Replace("__INSTALL__", QuotePowerShell(installPath))
 .Replace("__DATA__", QuotePowerShell(dataPath))
 .Replace("__URL__", QuotePowerShell(package.Url))
 .Replace("__HASH__", QuotePowerShell(package.Sha256))
 .Replace("__ADDITIONAL__", additional);
        }

        private static string BuildInstallScript(DatabaseDeploymentDraft draft, DatabaseDeploymentPackage package, string installPath, string dataPath, string temporaryPath)
        {
            if (draft.DatabaseType == "MySQL")
                return BuildMySqlInstallScript(draft, package, installPath, dataPath, temporaryPath);
            if (draft.DatabaseType == "MariaDB")
                return BuildMariaDbInstallScript(draft, package, installPath, dataPath, temporaryPath);
            if (draft.DatabaseType == "MongoDB")
                return BuildMongoInstallScript(draft, package, installPath, dataPath, temporaryPath);
            if (draft.DatabaseType == "Redis")
                return BuildRedisInstallScript(draft, package, installPath, dataPath, temporaryPath);
            throw new InvalidOperationException("当前数据库类型不支持部署");
        }

        private static string BuildMySqlInstallScript(DatabaseDeploymentDraft draft, DatabaseDeploymentPackage package, string installPath, string dataPath, string temporaryPath)
        {
            string configPath = Path.Combine(dataPath, "my.ini");
            string body = @"
$extract=Join-Path $temp 'extract'
New-Item -ItemType Directory -Path $extract -Force|Out-Null
& tar.exe -xf (Join-Path $temp 'server.zip') -C $extract
if($LASTEXITCODE-ne 0){throw 'MySQL 安装包解压失败'}
$source=Get-ChildItem -LiteralPath $extract -Directory|Select-Object -First 1
if($null-eq $source){throw 'MySQL 安装包结构无效'}
Move-Item -LiteralPath $source.FullName -Destination $install
$databaseData=Join-Path $data 'data'
New-Item -ItemType Directory -Path $databaseData -Force|Out-Null
$config=__CONFIG__
$baseForward=$install.Replace('\','/')
$dataForward=$databaseData.Replace('\','/')
$ini=""[mysqld]`r`nbasedir=$baseForward`r`ndatadir=$dataForward`r`nport=$port`r`nbind-address=127.0.0.1`r`nmysqlx=0`r`ncharacter-set-server=utf8mb4`r`ncollation-server=utf8mb4_unicode_ci`r`n[client]`r`nport=$port`r`ndefault-character-set=utf8mb4`r`n""
[IO.File]::WriteAllText($config,$ini,(New-Object Text.UTF8Encoding($false)))
$server=Join-Path $install 'bin\mysqld.exe'
if(!(Test-Path $server)){throw '未找到 mysqld.exe'}
$oldPreference=$ErrorActionPreference;$ErrorActionPreference='Continue'
$initOutput=@(& $server ('--defaults-file='+$config) --initialize-insecure --console 2>&1);$initExit=$LASTEXITCODE
$ErrorActionPreference=$oldPreference
if($initExit-ne 0){throw ('MySQL 初始化失败：'+(($initOutput|ForEach-Object{$_.ToString()})-join ' '))}
$oldPreference=$ErrorActionPreference;$ErrorActionPreference='Continue'
$installOutput=@(& $server --install $service ('--defaults-file='+$config) 2>&1);$installExit=$LASTEXITCODE
$ErrorActionPreference=$oldPreference
if($installExit-ne 0){throw ('MySQL 服务注册失败：'+(($installOutput|ForEach-Object{$_.ToString()})-join ' '))}
Start-Service -Name $service
".Replace("__CONFIG__", QuotePowerShell(configPath));
            return CommonInstallPrefix(draft, package, installPath, dataPath, temporaryPath) + body + CommonInstallSuffix(draft, package, installPath, dataPath, configPath);
        }

        private static string BuildMariaDbInstallScript(DatabaseDeploymentDraft draft, DatabaseDeploymentPackage package, string installPath, string dataPath, string temporaryPath)
        {
            string configPath = Path.Combine(dataPath, "data", "my.ini");
            string body = @"
$extract=Join-Path $temp 'extract'
New-Item -ItemType Directory -Path $extract -Force|Out-Null
& tar.exe -xf (Join-Path $temp 'server.zip') -C $extract
if($LASTEXITCODE-ne 0){throw 'MariaDB 安装包解压失败'}
$source=Get-ChildItem -LiteralPath $extract -Directory|Select-Object -First 1
if($null-eq $source){throw 'MariaDB 安装包结构无效'}
Move-Item -LiteralPath $source.FullName -Destination $install
$databaseData=Join-Path $data 'data'
New-Item -ItemType Directory -Path $databaseData -Force|Out-Null
$installer=Join-Path $install 'bin\mariadb-install-db.exe'
if(!(Test-Path $installer)){$installer=Join-Path $install 'bin\mysql_install_db.exe'}
if(!(Test-Path $installer)){throw '未找到 MariaDB 初始化工具'}
$oldPreference=$ErrorActionPreference;$ErrorActionPreference='Continue'
$initOutput=@(& $installer ('--datadir='+$databaseData) ('--service='+$service) ('--port='+$port) 2>&1);$initExit=$LASTEXITCODE
$ErrorActionPreference=$oldPreference
if($initExit-ne 0){throw ('MariaDB 初始化失败：'+(($initOutput|ForEach-Object{$_.ToString()})-join ' '))}
$config=__CONFIG__
if(!(Test-Path $config)){throw 'MariaDB 初始化后未生成 my.ini'}
$ini=Get-Content -LiteralPath $config -Raw
if($ini-match '(?im)^\s*bind-address\s*='){$ini=[regex]::Replace($ini,'(?im)^\s*bind-address\s*=.*$','bind-address=127.0.0.1')}
elseif($ini-match '(?im)^\s*\[mysqld\]\s*$'){$ini=[regex]::Replace($ini,'(?im)^\s*\[mysqld\]\s*$',""[mysqld]`r`nbind-address=127.0.0.1"",1)}
else{$ini+=""`r`n[mysqld]`r`nbind-address=127.0.0.1`r`n""}
[IO.File]::WriteAllText($config,$ini,(New-Object Text.UTF8Encoding($false)))
Start-Service -Name $service
".Replace("__CONFIG__", QuotePowerShell(configPath));
            return CommonInstallPrefix(draft, package, installPath, dataPath, temporaryPath) + body + CommonInstallSuffix(draft, package, installPath, dataPath, configPath);
        }

        private static string BuildMongoInstallScript(DatabaseDeploymentDraft draft, DatabaseDeploymentPackage package, string installPath, string dataPath, string temporaryPath)
        {
            string configPath = Path.Combine(dataPath, "mongod.cfg");
            string body = @"
$extract=Join-Path $temp 'extract'
New-Item -ItemType Directory -Path $extract -Force|Out-Null
& tar.exe -xf (Join-Path $temp 'server.zip') -C $extract
if($LASTEXITCODE-ne 0){throw 'MongoDB 安装包解压失败'}
$source=Get-ChildItem -LiteralPath $extract -Directory|Select-Object -First 1
if($null-eq $source){throw 'MongoDB 安装包结构无效'}
Move-Item -LiteralPath $source.FullName -Destination $install
$toolsExtract=Join-Path $temp 'tools-extract'
New-Item -ItemType Directory -Path $toolsExtract -Force|Out-Null
& tar.exe -xf (Join-Path $temp 'extra.zip') -C $toolsExtract
if($LASTEXITCODE-ne 0){throw 'MongoDB Database Tools 解压失败'}
$toolsSource=Get-ChildItem -LiteralPath $toolsExtract -Directory|Select-Object -First 1
if($null-eq $toolsSource){throw 'MongoDB Database Tools 安装包结构无效'}
Move-Item -LiteralPath $toolsSource.FullName -Destination (Join-Path $install 'DatabaseTools')
$databaseData=Join-Path $data 'data'
$log=Join-Path $data 'log'
New-Item -ItemType Directory -Path $databaseData,$log -Force|Out-Null
$config=__CONFIG__
$dataForward=$databaseData.Replace('\','/')
$logForward=(Join-Path $log 'mongod.log').Replace('\','/')
$yaml=""storage:`r`n  dbPath: '$dataForward'`r`nsystemLog:`r`n  destination: file`r`n  logAppend: true`r`n  path: '$logForward'`r`nnet:`r`n  bindIp: 127.0.0.1`r`n  port: $port`r`nsecurity:`r`n  authorization: disabled`r`n""
[IO.File]::WriteAllText($config,$yaml,(New-Object Text.UTF8Encoding($false)))
$server=Join-Path $install 'bin\mongod.exe'
if(!(Test-Path $server)){throw '未找到 mongod.exe'}
$oldPreference=$ErrorActionPreference;$ErrorActionPreference='Continue'
$installOutput=@(& $server --config $config --install --serviceName $service --serviceDisplayName $service 2>&1);$installExit=$LASTEXITCODE
$ErrorActionPreference=$oldPreference
if($installExit-ne 0){throw ('MongoDB 服务注册失败：'+(($installOutput|ForEach-Object{$_.ToString()})-join ' '))}
Start-Service -Name $service
".Replace("__CONFIG__", QuotePowerShell(configPath));
            return CommonInstallPrefix(draft, package, installPath, dataPath, temporaryPath) + body + CommonInstallSuffix(draft, package, installPath, dataPath, configPath);
        }

        private static string BuildRedisInstallScript(DatabaseDeploymentDraft draft, DatabaseDeploymentPackage package, string installPath, string dataPath, string temporaryPath)
        {
            string configPath = Path.Combine(dataPath, "redis.conf");
            string body = @"
$extract=Join-Path $temp 'extract'
New-Item -ItemType Directory -Path $extract -Force|Out-Null
& tar.exe -xf (Join-Path $temp 'server.zip') -C $extract
if($LASTEXITCODE-ne 0){throw 'Redis 安装包解压失败'}
$source=Get-ChildItem -LiteralPath $extract -Directory|Select-Object -First 1
if($null-eq $source){throw 'Redis 安装包结构无效'}
Move-Item -LiteralPath $source.FullName -Destination $install
$databaseData=Join-Path $data 'data'
$log=Join-Path $data 'log'
New-Item -ItemType Directory -Path $databaseData,$log -Force|Out-Null
$config=__CONFIG__
$acl=Join-Path $data 'users.acl'
$dataForward=$databaseData.Replace('\','/')
$logForward=(Join-Path $log 'redis.log').Replace('\','/')
$aclForward=$acl.Replace('\','/')
$redisConfig=""bind 127.0.0.1`r`nprotected-mode yes`r`nport $port`r`ntimeout 0`r`ntcp-keepalive 300`r`nloglevel notice`r`nlogfile `""$logForward`""`r`ndatabases 16`r`nsave 900 1`r`nsave 300 10`r`nsave 60 10000`r`ndbfilename dump.rdb`r`ndir `""$dataForward`""`r`naclfile `""$aclForward`""`r`nappendonly no`r`n""
[IO.File]::WriteAllText($config,$redisConfig,(New-Object Text.UTF8Encoding($false)))
[IO.File]::WriteAllText($acl,'user default on nopass ~* &* +@all'+[Environment]::NewLine,(New-Object Text.UTF8Encoding($false)))
$wrapper=Join-Path $install 'RedisService.exe'
if(!(Test-Path $wrapper)){throw '未找到 RedisService.exe'}
$oldPreference=$ErrorActionPreference;$ErrorActionPreference='Continue'
$installOutput=@(& $wrapper install --service-name $service -c $config --dir $databaseData --port $port --start-mode auto 2>&1);$installExit=$LASTEXITCODE
$ErrorActionPreference=$oldPreference
if($installExit-ne 0){throw ('Redis 服务注册失败：'+(($installOutput|ForEach-Object{$_.ToString()})-join ' '))}
Start-Service -Name $service
".Replace("__CONFIG__", QuotePowerShell(configPath));
            return CommonInstallPrefix(draft, package, installPath, dataPath, temporaryPath) + body + CommonInstallSuffix(draft, package, installPath, dataPath, configPath);
        }

        private static string CommonInstallPrefix(DatabaseDeploymentDraft draft, DatabaseDeploymentPackage package, string installPath, string dataPath, string temporaryPath)
        {
            return @"
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$temp=__TEMP__
$install=__INSTALL__
$data=__DATA__
$service=__SERVICE__
$port=__PORT__
New-Item -ItemType Directory -Path (Split-Path $install),(Split-Path $data),$data -Force|Out-Null
".Replace("__TEMP__", QuotePowerShell(temporaryPath))
 .Replace("__INSTALL__", QuotePowerShell(installPath))
 .Replace("__DATA__", QuotePowerShell(dataPath))
 .Replace("__SERVICE__", QuotePowerShell(draft.ServiceName))
 .Replace("__PORT__", draft.Port.ToString());
        }

        private static string CommonInstallSuffix(DatabaseDeploymentDraft draft, DatabaseDeploymentPackage package, string installPath, string dataPath, string configPath)
        {
            return @"
$deadline=(Get-Date).AddSeconds(45)
do{Start-Sleep -Milliseconds 500;$running=(Get-Service -Name $service -ErrorAction SilentlyContinue).Status -eq 'Running';$listening=[bool](Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)}while((!$running -or !$listening)-and(Get-Date)-lt $deadline)
if(!$running -or !$listening){throw '数据库服务启动后未监听目标端口'}
$marker=[pscustomobject]@{ManagedBy='XiaoBaiServerManager';DatabaseType=__TYPE__;ExactVersion=__VERSION__;ServiceName=$service;Port=$port;InstallPath=$install;DataPath=$data;DeployedAt=(Get-Date).ToUniversalTime().ToString('o')}
$marker|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $data 'xiaobai-deployment.json') -Encoding UTF8
[pscustomobject]@{ConfigPath=__CONFIG__;ServiceName=$service;Port=$port}|ConvertTo-Json -Compress
".Replace("__TYPE__", QuotePowerShell(draft.DatabaseType))
 .Replace("__VERSION__", QuotePowerShell(package.ExactVersion))
 .Replace("__CONFIG__", QuotePowerShell(configPath));
        }

        private static string BuildEnableAuthenticationScript(DatabaseDeploymentDraft draft, string installPath, string configPath)
        {
            if (draft.DatabaseType == "MongoDB")
            {
                return @"
$ErrorActionPreference='Stop'
$config=__CONFIG__
$text=Get-Content -LiteralPath $config -Raw
if($text -notmatch 'authorization:\s*disabled'){throw 'MongoDB 配置中未找到临时认证状态'}
$text=$text -replace 'authorization:\s*disabled','authorization: enabled'
[IO.File]::WriteAllText($config,$text,(New-Object Text.UTF8Encoding($false)))
Restart-Service -Name __SERVICE__ -Force
(Get-Service -Name __SERVICE__).WaitForStatus('Running',[TimeSpan]::FromSeconds(45))
'AUTH_ENABLED'
".Replace("__CONFIG__", QuotePowerShell(configPath))
 .Replace("__SERVICE__", QuotePowerShell(draft.ServiceName));
            }
            return @"
$ErrorActionPreference='Stop'
Restart-Service -Name __SERVICE__ -Force
(Get-Service -Name __SERVICE__).WaitForStatus('Running',[TimeSpan]::FromSeconds(45))
'AUTH_ENABLED'
".Replace("__SERVICE__", QuotePowerShell(draft.ServiceName));
        }

        private static string BuildRollbackScript(DatabaseDeploymentResult deployment)
        {
            return @"
$ErrorActionPreference='SilentlyContinue'
$service=__SERVICE__
$install=[IO.Path]::GetFullPath(__INSTALL__)
$data=[IO.Path]::GetFullPath(__DATA__)
$temp=[IO.Path]::GetFullPath(__TEMP__)
$allowedInstall=[IO.Path]::GetFullPath('C:\Program Files\XiaoBai Databases')+'\'
$allowedData=[IO.Path]::GetFullPath('C:\ProgramData\XiaoBai Databases')+'\'
$allowedTemp=[IO.Path]::GetFullPath('C:\Windows\Temp')+'\'
if(!$install.StartsWith($allowedInstall,[StringComparison]::OrdinalIgnoreCase)-or!$data.StartsWith($allowedData,[StringComparison]::OrdinalIgnoreCase)-or!$temp.StartsWith($allowedTemp,[StringComparison]::OrdinalIgnoreCase)){exit 1}
$owned=$false
foreach($markerPath in @((Join-Path $temp 'xiaobai-deployment-owner.json'),(Join-Path $data 'xiaobai-deployment.json'))){
 if(Test-Path -LiteralPath $markerPath){
  try{$marker=Get-Content -LiteralPath $markerPath -Raw|ConvertFrom-Json;if($marker.ManagedBy-eq'XiaoBaiServerManager'-and$marker.ServiceName-eq$service-and[IO.Path]::GetFullPath([string]$marker.InstallPath)-eq$install-and[IO.Path]::GetFullPath([string]$marker.DataPath)-eq$data){$owned=$true}}catch{}
 }
}
if(!$owned){throw '缺少匹配的部署所有权标记，已拒绝删除目录'}
$svc=Get-CimInstance Win32_Service -Filter (""Name='""+$service.Replace(""'"",""''"")+""'"")
if($svc -and $svc.PathName -like ('*'+$install+'*')){
 Stop-Service -Name $service -Force -ErrorAction SilentlyContinue
 Start-Sleep -Seconds 1
 & sc.exe delete $service|Out-Null
 $deadline=(Get-Date).AddSeconds(30)
 do{Start-Sleep -Milliseconds 500;$remaining=Get-Service -Name $service -ErrorAction SilentlyContinue}while($remaining-and(Get-Date)-lt$deadline)
}
Get-CimInstance Win32_Process|Where-Object{$_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath).StartsWith($install+'\',[StringComparison]::OrdinalIgnoreCase)}|ForEach-Object{Stop-Process -Id $_.ProcessId -Force}
Start-Sleep -Milliseconds 500
foreach($path in @($install,$data,$temp)){if([IO.Directory]::Exists($path)){for($i=0;$i-lt 5;$i++){try{[IO.Directory]::Delete($path,$true);break}catch{Start-Sleep -Seconds 1}}}}
if(Get-Service -Name $service -ErrorAction SilentlyContinue){throw '部署服务仍存在'}
if((Test-Path -LiteralPath $install)-or(Test-Path -LiteralPath $data)-or(Test-Path -LiteralPath $temp)){throw '部署目录未完全删除'}
'ROLLBACK_COMPLETE'
".Replace("__SERVICE__", QuotePowerShell(deployment.ServiceName))
 .Replace("__INSTALL__", QuotePowerShell(deployment.InstallPath))
 .Replace("__DATA__", QuotePowerShell(deployment.DataPath))
 .Replace("__TEMP__", QuotePowerShell(deployment.TemporaryPath));
        }

        private static async Task CleanupTemporaryAsync(Server server, string password, string temporaryPath, CancellationToken cancellationToken)
        {
            using (IRemoteExecutor executor = await RemoteExecutorFactory.CreateAsync(server, password, cancellationToken, RemoteTransport.SSH))
            {
                string script = @"
$path=[IO.Path]::GetFullPath(__TEMP__)
$allowed=[IO.Path]::GetFullPath('C:\Windows\Temp')+'\'
if(!$path.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw '临时目录路径无效'}
if([IO.Directory]::Exists($path)){[IO.Directory]::Delete($path,$true)}
'CLEANUP_OK'
".Replace("__TEMP__", QuotePowerShell(temporaryPath));
                await ExecuteCheckedAsync(executor, script, TimeSpan.FromMinutes(3), "清理部署临时文件", cancellationToken);
            }
        }

        private static async Task ExecuteCheckedAsync(IRemoteExecutor executor, string script, TimeSpan timeout, string context, CancellationToken cancellationToken)
        {
            RemoteCommandResult result = await executor.ExecutePowerShellAsync(script, timeout, cancellationToken);
            EnsureSuccess(result, context);
        }

        private static void EnsureSuccess(RemoteCommandResult result, string context)
        {
            if (result == null || result.ExitCode != 0)
                throw new InvalidOperationException(context + "失败：" + RemoteErrorFormatter.Format(result));
        }

        private static Dictionary<string, string> ParseJson(string value, string context)
        {
            try
            {
                string candidate = (value ?? "")
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Reverse()
                    .FirstOrDefault(line => line.TrimStart().StartsWith("{", StringComparison.Ordinal) && line.TrimEnd().EndsWith("}", StringComparison.Ordinal));
                if (string.IsNullOrWhiteSpace(candidate))
                    throw new InvalidOperationException("远程输出中没有找到结果对象");
                using (JsonDocument document = JsonDocument.Parse(candidate.Trim()))
                {
                    return document.RootElement.EnumerateObject().ToDictionary(
                        property => property.Name,
                        property => property.Value.ToString(),
                        StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(context + "无法解析", ex);
            }
        }

        private static string GetValue(IDictionary<string, string> values, string key)
        {
            string value;
            return values != null && values.TryGetValue(key, out value) ? value : "";
        }

        private static void Report(Action<DatabaseDeploymentProgress> callback, int step, OperationStepState state, string title, string detail, int percent, bool indeterminate)
        {
            callback?.Invoke(new DatabaseDeploymentProgress
            {
                Step = step,
                State = state,
                Title = title,
                Detail = detail,
                Percent = percent,
                Indeterminate = indeterminate
            });
        }

        private static string GetDisplayName(DatabaseDeploymentDraft draft)
        {
            return draft.DatabaseType == "Redis" ? "Redis Server" : draft.ServiceName;
        }

        private static void ValidateServer(Server server, string password)
        {
            if (server == null || server.Type != ServerType.Windows)
                throw new InvalidOperationException("一键部署当前只支持 Windows 服务器");
            if (server.ManagementType == RemoteManagementType.WinRM)
                throw new InvalidOperationException("一键部署需要 SSH 管理通道，用于安装后的安全隧道验证");
            if (string.IsNullOrWhiteSpace(server.IP) || string.IsNullOrWhiteSpace(server.Username) || string.IsNullOrEmpty(password))
                throw new InvalidOperationException("服务器 SSH 管理凭据不完整");
        }

        private static void ValidateDraft(DatabaseDeploymentDraft draft)
        {
            if (draft == null)
                throw new InvalidOperationException("部署配置为空");
            if (!new[] { "MySQL", "MariaDB", "MongoDB", "Redis" }.Contains(draft.DatabaseType))
                throw new InvalidOperationException("当前数据库类型不支持一键部署");
            if (!SafeServiceName.IsMatch(draft.ServiceName ?? ""))
                throw new InvalidOperationException("服务名称只能包含字母、数字、下划线和短横线，且必须以字母开头");
            if (draft.Port < 1 || draft.Port > 65535)
                throw new InvalidOperationException("数据库端口无效");
            if (string.IsNullOrEmpty(draft.AdminPassword) || draft.AdminPassword.Length < 10)
                throw new InvalidOperationException("管理密码至少需要 10 个字符");
            if (!SafeDatabaseName.IsMatch(draft.DatabaseName ?? ""))
                throw new InvalidOperationException("初始数据库名称格式无效");
            if (draft.DatabaseType == "MongoDB" && draft.DatabaseName.Any(character => "/\\. \"$*<>:|?".Contains(character)))
                throw new InvalidOperationException("MongoDB 数据库名称包含不支持的字符");
            if (!SafeUserName.IsMatch(draft.AdminUser ?? ""))
                throw new InvalidOperationException("管理账号格式无效");
            if ((draft.DatabaseType == "MySQL" || draft.DatabaseType == "MariaDB") && draft.AdminUser != "root")
                throw new InvalidOperationException("MySQL/MariaDB 初始管理账号必须为 root");
            int redisDatabase;
            if (draft.DatabaseType == "Redis" && (!int.TryParse(draft.DatabaseName, out redisDatabase) || redisDatabase < 0 || redisDatabase > 15))
                throw new InvalidOperationException("Redis 逻辑库编号必须是 0 到 15");
        }

        private static DatabaseDeploymentPackage ResolvePackage(string type, string track)
        {
            string key = type + "|" + track;
            Dictionary<string, DatabaseDeploymentPackage> packages = new Dictionary<string, DatabaseDeploymentPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["MySQL|8.4 LTS（推荐）"] = Package("MySQL", track, "8.4.7", "https://cdn.mysql.com/archives/mysql-8.4/mysql-8.4.7-winx64.zip", "FD9BDBD4B5A878D31C8E4067078BD60665B1B3C4677FA1F099416D194B458AFF", 4L * 1024 * 1024 * 1024),
                ["MySQL|8.0（兼容）"] = Package("MySQL", track, "8.0.46", "https://cdn.mysql.com/Downloads/MySQL-8.0/mysql-8.0.46-winx64.zip", "28E9EDA019D88EFF4478D811EA2110B83F02A3966BE157FE91CC55DEF3AB0D4D", 4L * 1024 * 1024 * 1024),
                ["MariaDB|11.4 LTS（推荐）"] = Package("MariaDB", track, "11.4.8", "https://archive.mariadb.org/mariadb-11.4.8/winx64-packages/mariadb-11.4.8-winx64.zip", "ED86E93157AF46317BB49161451C2EC258498A6FA8E68CA821EF1D780D855E6B", 2L * 1024 * 1024 * 1024),
                ["MariaDB|10.11 LTS（兼容）"] = Package("MariaDB", track, "10.11.14", "https://archive.mariadb.org/mariadb-10.11.14/winx64-packages/mariadb-10.11.14-winx64.zip", "838B7D91B871A30C2E42E5CEFF9C6859DDD061B57328FB7CB51154AF9BD8DDF4", 2L * 1024 * 1024 * 1024),
                ["MongoDB|8.0（推荐）"] = MongoPackage(track, "8.0.12", "https://fastdl.mongodb.org/windows/mongodb-windows-x86_64-8.0.12.zip", "D1B4A8CE75F0D474218768FACB91F07B7DE6D3D8126F3732C90D72978639049A"),
                ["MongoDB|7.0（兼容）"] = MongoPackage(track, "7.0.22", "https://fastdl.mongodb.org/windows/mongodb-windows-x86_64-7.0.22.zip", "58BF3786E95D95C8A4A45E3785A864AC3305205E85D431E36EAFD0EB0DAE3BA9"),
                ["Redis|8.x（推荐）"] = Package("Redis", track, "8.10.1", "https://github.com/redis-windows/redis-windows/releases/download/8.10.1/Redis-8.10.1-Windows-x64-cygwin-with-Service.zip", "E70EEA271F2B8D4BC113FE4A95331BEAA2CD4B22C4ED07C93C54E342CB1788DB", 1L * 1024 * 1024 * 1024),
                ["Redis|7.x（兼容）"] = Package("Redis", track, "7.4.8", "https://github.com/redis-windows/redis-windows/releases/download/7.4.8/Redis-7.4.8-Windows-x64-cygwin-with-Service.zip", "03C9D3C311072C1A9B6B09B88C53CF17F5F6763BF025240FA7024984CFC2A14F", 1L * 1024 * 1024 * 1024)
            };
            DatabaseDeploymentPackage package;
            if (!packages.TryGetValue(key, out package))
                throw new InvalidOperationException("所选数据库版本不在受控部署目录中");
            if (package.Sha256.StartsWith("__", StringComparison.Ordinal) || package.AdditionalSha256?.StartsWith("__", StringComparison.Ordinal) == true)
                throw new InvalidOperationException("所选版本的安装包校验值尚未完成，请选择其他版本");
            return package;
        }

        private static DatabaseDeploymentPackage Package(string type, string track, string exact, string url, string sha256, long minimumFreeBytes)
        {
            return new DatabaseDeploymentPackage { DatabaseType = type, VersionTrack = track, ExactVersion = exact, Url = url, Sha256 = sha256, MinimumFreeBytes = minimumFreeBytes };
        }

        private static DatabaseDeploymentPackage MongoPackage(string track, string exact, string url, string sha256)
        {
            DatabaseDeploymentPackage package = Package("MongoDB", track, exact, url, sha256, 5L * 1024 * 1024 * 1024);
            package.AdditionalUrl = "https://fastdl.mongodb.org/tools/db/mongodb-database-tools-windows-x86_64-100.14.1.zip";
            package.AdditionalSha256 = "C8A811E013B2B35DA1FA0A09BF2C828E6ECB7AD62AEFAC0F2E6B8048D7FF043A";
            return package;
        }

        private static string QuotePowerShell(string value)
        {
            return "'" + (value ?? "").Replace("'", "''") + "'";
        }
    }
}
