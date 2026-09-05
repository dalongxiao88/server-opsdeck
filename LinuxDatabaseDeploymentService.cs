using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RDPManager
{
    public sealed class LinuxDatabaseDeploymentService
    {
        private static readonly Regex SafeIdentifier = new Regex(@"^[A-Za-z0-9_$.-]{1,64}$", RegexOptions.Compiled);
        private static readonly Regex SafeServiceName = new Regex(@"^[A-Za-z][A-Za-z0-9_.@-]{0,63}$", RegexOptions.Compiled);
        private static readonly Regex SafePort = new Regex(@"^[1-9][0-9]{0,4}$", RegexOptions.Compiled);

        public async Task<int> SuggestAvailablePortAsync(
            Server server,
            string serverPassword,
            int preferredPort,
            bool randomize,
            CancellationToken cancellationToken)
        {
            ValidateServer(server);
            if (preferredPort < 1 || preferredPort > 65535)
                throw new InvalidOperationException("端口必须在 1-65535 之间");
            using (SshRemoteExecutor executor = new SshRemoteExecutor(server, serverPassword))
            {
                await executor.ConnectAsync(cancellationToken);
                RemoteCommandResult result = await executor.ExecuteCommandAsync(
                    "listeners=$(ss -ltnH 2>/dev/null | awk '{print $4}' | sed 's/.*://' | grep -E '^[0-9]+$' | sort -n -u); " +
                    "if [ \"" + preferredPort + "\" -gt 0 ] && [ \"" + (randomize ? "false" : "true") + "\" = true ] && ! printf '%s\\n' \"$listeners\" | grep -qx '" + preferredPort + "'; then printf '%s\\n' '" + preferredPort + "'; exit 0; fi; " +
                    "for p in $(if [ \"" + (randomize ? "true" : "false") + "\" = true ]; then shuf -i 10000-60000 -n 1000 2>/dev/null || seq 10000 60000; else seq 10000 60000; fi); do " +
                    "if ! printf '%s\\n' \"$listeners\" | grep -qx \"$p\"; then printf '%s\\n' \"$p\"; exit 0; fi; done; exit 20",
                    TimeSpan.FromSeconds(30),
                    cancellationToken);
                EnsureSuccess(result, "探测 Linux 可用端口");
                int port;
                if (!int.TryParse((result.Output ?? "").Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault(), out port) || port < 1 || port > 65535)
                    throw new InvalidOperationException("Linux 没有返回有效的可用端口");
                return port;
            }
        }

        public async Task<DatabaseDeploymentResult> DeployAsync(
            Server server,
            string serverPassword,
            DatabaseDeploymentDraft draft,
            Action<DatabaseDeploymentProgress> progress,
            Func<string> sudoPasswordProvider,
            CancellationToken cancellationToken)
        {
            ValidateServer(server);
            ValidateDraft(draft);
            LinuxPackage package = ResolvePackage(draft.DatabaseType, draft.VersionTrack);
            int port = await SuggestAvailablePortAsync(server, serverPassword, draft.Port, false, cancellationToken);
            if (port != draft.Port)
                draft.Port = port;

            string deploymentId = Guid.NewGuid().ToString("N");
            string temporaryPath = "/tmp/xiaobai-db-deploy-" + deploymentId;
            DatabaseDeploymentResult deployment = new DatabaseDeploymentResult
            {
                DatabaseType = draft.DatabaseType,
                ExactVersion = draft.VersionTrack,
                ServiceName = package.ServiceName,
                DisplayName = draft.DatabaseType + " Linux",
                Port = draft.Port,
                TemporaryPath = temporaryPath,
                PackageName = package.PackageName,
                InstallPath = package.PackageName,
                DataPath = package.DataPath,
                ConfigPath = package.ConfigPath
            };
            bool packageInstallationAttempted = false;
            try
            {
                Report(progress, 0, OperationStepState.Running, "检查 Linux 环境", "验证发行版、systemd、包管理器和权限", 5, true);
                using (SshRemoteExecutor executor = new SshRemoteExecutor(server, serverPassword))
                {
                    await executor.ConnectAsync(cancellationToken);
                    RemoteSystemInfo info = await executor.GetSystemInfoAsync(cancellationToken);
                    ValidateEnvironment(info, package);
                    ConfigurePackageForEnvironment(info, package);
                    deployment.ServiceName = package.ServiceName;
                    deployment.ConfigPath = package.ConfigPath;
                    deployment.AdditionalConfigPath = package.AclPath;
                    deployment.InstallPath = package.PackageName;
                    PrepareSudoPassword(server, info, sudoPasswordProvider);
                    await RunCheckedAsync(executor, info,
                        "if [ -e " + Quote(package.ConfigPath) + " ]; then printf 'XIAOBAI_CONFIG_EXISTS\\n' >&2; exit 21; fi; " +
                        "if [ -n " + Quote(package.AclPath) + " ] && [ -e " + Quote(package.AclPath) + " ]; then printf 'XIAOBAI_ACL_EXISTS\\n' >&2; exit 25; fi; " +
                        "if [ -e " + Quote(package.DataPath) + " ]; then printf 'XIAOBAI_DATA_EXISTS\\n' >&2; exit 26; fi; " +
                        "if [ " + Quote(package.PackageName) + " = 'mongodb-org' ] && ! (apt-cache show mongodb-org 2>/dev/null | grep -q '^Package:' || dnf info mongodb-org >/dev/null 2>&1); then printf 'XIAOBAI_MONGO_REPOSITORY_MISSING\\n' >&2; exit 27; fi; " +
                        "if command -v dpkg-query >/dev/null 2>&1 && dpkg-query -W -f='${Status}' " + Quote(package.PackageName) + " 2>/dev/null | grep -q 'install ok installed'; then exit 22; fi; " +
                        "if command -v rpm >/dev/null 2>&1 && rpm -q " + Quote(package.PackageName) + " >/dev/null 2>&1; then exit 23; fi; " +
                        "if ss -ltnH 2>/dev/null | awk '{print $4}' | sed 's/.*://' | grep -qx '" + draft.Port + "'; then exit 24; fi; printf 'LINUX_PREFLIGHT_OK\\n'",
                        "Linux 部署环境检查", TimeSpan.FromSeconds(45), cancellationToken);
                    Report(progress, 0, OperationStepState.Completed, "检查 Linux 环境", info.OperatingSystem + " " + info.OsVersion, 12, false);

                    Report(progress, 1, OperationStepState.Running, "准备软件源", package.PackageName, 16, true);
                    await RunCheckedAsync(executor, info, BuildRefreshPackageIndex(info), "刷新软件源", TimeSpan.FromMinutes(10), cancellationToken);
                    Report(progress, 1, OperationStepState.Completed, "准备软件源", "软件源可用", 22, false);

                    Report(progress, 2, OperationStepState.Running, "下载并安装数据库", package.PackageName, 26, true);
                    packageInstallationAttempted = true;
                    await RunCheckedAsync(executor, info, BuildInstallCommand(info, package.PackageName), "安装 Linux 数据库", TimeSpan.FromMinutes(30), cancellationToken);
                    deployment.PackageInstalledByManager = true;
                    Report(progress, 2, OperationStepState.Completed, "下载并安装数据库", "安装包已由目标服务器获取", 48, false);

                    Report(progress, 3, OperationStepState.Running, "初始化数据库配置", "只监听 127.0.0.1:" + draft.Port, 52, true);
                    await RunCheckedAsync(executor, info, BuildConfigurationCommand(package, draft, temporaryPath), "配置 Linux 数据库", TimeSpan.FromMinutes(3), cancellationToken);
                    Report(progress, 3, OperationStepState.Completed, "初始化数据库配置", "本机回环监听已配置", 62, false);

                    Report(progress, 4, OperationStepState.Running, "注册并启动服务", package.ServiceName, 66, true);
                    await RunCheckedAsync(executor, info, "systemctl enable --now " + Quote(package.ServiceName) + " && systemctl is-active --quiet " + Quote(package.ServiceName) + " && printf 'SERVICE_READY\\n'", "启动数据库服务", TimeSpan.FromMinutes(3), cancellationToken);
                    Report(progress, 4, OperationStepState.Completed, "注册并启动服务", "systemd 服务已运行", 72, false);

                    Report(progress, 5, OperationStepState.Running, "创建管理账号和初始库", "凭据通过临时权限文件传输", 76, true);
                    await InitializeCredentialsAsync(server, serverPassword, executor, info, package, draft, temporaryPath, cancellationToken);
                    if (package.Family == "mongo")
                        await RunCheckedAsync(executor, info, BuildEnableMongoAuthenticationCommand(package), "启用 MongoDB 认证", TimeSpan.FromMinutes(3), cancellationToken);
                    else if (package.Family == "redis")
                        await RunCheckedAsync(executor, info, "systemctl restart " + Quote(package.ServiceName) + " && systemctl is-active --quiet " + Quote(package.ServiceName) + " && printf 'REDIS_SERVICE_RESTARTED\\n'", "重启 Redis 服务", TimeSpan.FromMinutes(2), cancellationToken);
                    Report(progress, 5, OperationStepState.Completed, "创建管理账号和初始库", "账号、密码和初始库已验证", 84, false);

                    Report(progress, 6, OperationStepState.Running, "验证数据库连接", "通过 SSH 隧道验证新端口", 88, true);
                    deployment.ServerVersion = await ReadVersionAsync(server, serverPassword, package, draft, temporaryPath, cancellationToken);
                    ValidateVersion(package, deployment.ServerVersion);
                    deployment.Credential = CreateCredential(package, draft, deployment);
                    Report(progress, 6, OperationStepState.Completed, "验证数据库连接", deployment.ServerVersion, 96, false);
                }
                return deployment;
            }
            catch (Exception error)
            {
                if (packageInstallationAttempted)
                {
                    try
                    {
                        Report(progress, 7, OperationStepState.Running, "部署失败，正在回滚", "仅清理本次安装创建的服务和配置", 90, true);
                        await RollbackAsync(server, serverPassword, deployment, CancellationToken.None);
                        Report(progress, 7, OperationStepState.Completed, "部署失败，回滚完成", "未保留本次部署的服务入口", 100, false);
                    }
                    catch (Exception rollbackError)
                    {
                        throw new InvalidOperationException(error.Message + "\n\n自动回滚未完整完成：" + rollbackError.Message, error);
                    }
                }
                throw;
            }
        }

        public async Task RollbackAsync(Server server, string serverPassword, DatabaseDeploymentResult deployment, CancellationToken cancellationToken)
        {
            ValidateServer(server);
            if (deployment == null || !SafeServiceName.IsMatch(deployment.ServiceName ?? "") || string.IsNullOrWhiteSpace(deployment.PackageName))
                return;
            using (SshRemoteExecutor executor = new SshRemoteExecutor(server, serverPassword))
            {
                await executor.ConnectAsync(cancellationToken);
                RemoteSystemInfo info = await executor.GetSystemInfoAsync(cancellationToken);
                PrepareSudoPassword(server, info, null);
                string cleanup = "systemctl disable --now " + Quote(deployment.ServiceName) + " >/dev/null 2>&1 || true; " +
                    "rm -f " + Quote(deployment.ConfigPath) + (string.IsNullOrWhiteSpace(deployment.AdditionalConfigPath) ? "" : " " + Quote(deployment.AdditionalConfigPath)) + " " + Quote(deployment.TemporaryPath) + " " + Quote(deployment.TemporaryPath + ".cnf") + " " + Quote(deployment.TemporaryPath + ".sql") + " " + Quote(deployment.TemporaryPath + ".js") + " " + Quote(deployment.TemporaryPath + ".acl") + " " + Quote(deployment.TemporaryPath + ".sh") + "; rm -rf " + Quote(deployment.DataPath) + "; " +
                    BuildRemovePackageCommand(info, deployment.PackageName) + "; printf 'LINUX_ROLLBACK_DONE\\n'";
                await RunCheckedAsync(executor, info, cleanup, "Linux 部署回滚", TimeSpan.FromMinutes(10), cancellationToken);
            }
        }

        public Task CleanupAsync(Server server, string serverPassword, DatabaseDeploymentResult deployment, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        private static async Task InitializeCredentialsAsync(
            Server server,
            string serverPassword,
            SshRemoteExecutor executor,
            RemoteSystemInfo info,
            LinuxPackage package,
            DatabaseDeploymentDraft draft,
            string temporaryPath,
            CancellationToken cancellationToken)
        {
            if (package.Family == "mysql")
            {
                string sql = BuildMySqlInitSql(draft);
                await UploadTextAsync(server, serverPassword, sql, temporaryPath + ".sql", cancellationToken);
                await RunCheckedAsync(executor, info, "set -e; chmod 600 " + Quote(temporaryPath + ".sql") + " && tool=$(command -v mariadb 2>/dev/null || command -v mysql 2>/dev/null || true); [ -n \"$tool\" ] || exit 30; \"$tool\" --protocol=socket -uroot < " + Quote(temporaryPath + ".sql") + " && rm -f " + Quote(temporaryPath + ".sql") + " && printf 'MYSQL_INITIALIZED\\n'", "初始化 MySQL/MariaDB", TimeSpan.FromMinutes(3), cancellationToken);
                return;
            }
            if (package.Family == "mongo")
            {
                string script = BuildMongoInitScript(draft);
                await UploadTextAsync(server, serverPassword, script, temporaryPath + ".js", cancellationToken);
                await RunCheckedAsync(executor, info, "set -e; command -v mongosh >/dev/null 2>&1 && mongosh --quiet --host 127.0.0.1 --port " + draft.Port + " " + Quote(temporaryPath + ".js") + " && rm -f " + Quote(temporaryPath + ".js") + " && printf 'MONGO_INITIALIZED\\n'", "初始化 MongoDB", TimeSpan.FromMinutes(5), cancellationToken);
                return;
            }
            if (package.Family == "redis")
            {
                string acl = "user default off\\nuser " + draft.AdminUser + " on >" + draft.AdminPassword + " ~* &* +@all\\n";
                await UploadTextAsync(server, serverPassword, acl.Replace("\\n", Environment.NewLine), temporaryPath + ".acl", cancellationToken);
                await RunCheckedAsync(executor, info, "set -e; chmod 600 " + Quote(temporaryPath + ".acl") + " && mkdir -p " + Quote(Path.GetDirectoryName(package.AclPath).Replace('\\', '/')) + " && (install -o redis -g redis -m 600 " + Quote(temporaryPath + ".acl") + " " + Quote(package.AclPath) + " 2>/dev/null || { install -m 600 " + Quote(temporaryPath + ".acl") + " " + Quote(package.AclPath) + "; chown redis:redis " + Quote(package.AclPath) + "; }) && printf 'REDIS_INITIALIZED\\n'", "初始化 Redis ACL", TimeSpan.FromMinutes(2), cancellationToken);
                await RunCheckedAsync(executor, info, "rm -f " + Quote(temporaryPath + ".acl"), "清理 Redis 临时凭据", TimeSpan.FromSeconds(30), cancellationToken);
            }
        }

        private static async Task<string> ReadVersionAsync(Server server, string serverPassword, LinuxPackage package, DatabaseDeploymentDraft draft, string temporaryPath, CancellationToken cancellationToken)
        {
            if (package.Family == "mysql")
            {
                string option = "[client]" + Environment.NewLine + "user=root" + Environment.NewLine + "password=" + draft.AdminPassword + Environment.NewLine + "host=127.0.0.1" + Environment.NewLine + "port=" + draft.Port + Environment.NewLine;
                await UploadTextAsync(server, serverPassword, option, temporaryPath + ".cnf", cancellationToken);
                using (SshRemoteExecutor executor = new SshRemoteExecutor(server, serverPassword))
                {
                    await executor.ConnectAsync(cancellationToken);
                    RemoteSystemInfo info = await executor.GetSystemInfoAsync(cancellationToken);
                    info.SudoPassword = server.SudoPassword;
                    string output = await RunCheckedAsync(executor, info, "set -e; chmod 600 " + Quote(temporaryPath + ".cnf") + " && tool=$(command -v mariadb 2>/dev/null || command -v mysql 2>/dev/null || true); [ -n \"$tool\" ] || exit 31; \"$tool\" --defaults-extra-file=" + Quote(temporaryPath + ".cnf") + " -Nse 'SELECT VERSION()' ; rm -f " + Quote(temporaryPath + ".cnf"), "读取数据库版本", TimeSpan.FromMinutes(2), cancellationToken);
                    return output.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
                }
            }
            if (package.Family == "mongo")
            {
                string script = "db.getSiblingDB('admin').auth(" + QuoteJavaScript(draft.AdminUser) + "," + QuoteJavaScript(draft.AdminPassword) + "); print(db.version());";
                await UploadTextAsync(server, serverPassword, script, temporaryPath + ".js", cancellationToken);
                using (SshRemoteExecutor executor = new SshRemoteExecutor(server, serverPassword))
                {
                    await executor.ConnectAsync(cancellationToken);
                    RemoteSystemInfo info = await executor.GetSystemInfoAsync(cancellationToken);
                    info.SudoPassword = server.SudoPassword;
                    string output = await RunCheckedAsync(executor, info, "set -e; mongosh --quiet --host 127.0.0.1 --port " + draft.Port + " --authenticationDatabase admin " + Quote(temporaryPath + ".js") + " && rm -f " + Quote(temporaryPath + ".js"), "读取 MongoDB 版本", TimeSpan.FromMinutes(3), cancellationToken);
                    return output.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
                }
            }
            if (package.Family == "redis")
            {
                string script = "#!/bin/sh\nset -e\nREDISCLI_AUTH=" + QuoteShellScript(draft.AdminPassword) + " redis-cli -h 127.0.0.1 -p " + draft.Port + " --user " + QuoteShellScript(draft.AdminUser) + " PING\nREDISCLI_AUTH=" + QuoteShellScript(draft.AdminPassword) + " redis-cli -h 127.0.0.1 -p " + draft.Port + " --user " + QuoteShellScript(draft.AdminUser) + " INFO server | awk -F: '$1==\"redis_version\"{print $2; exit}'\n";
                await UploadTextAsync(server, serverPassword, script, temporaryPath + ".sh", cancellationToken);
                using (SshRemoteExecutor executor = new SshRemoteExecutor(server, serverPassword))
                {
                    await executor.ConnectAsync(cancellationToken);
                    RemoteSystemInfo info = await executor.GetSystemInfoAsync(cancellationToken);
                    info.SudoPassword = server.SudoPassword;
                    string output = await RunCheckedAsync(executor, info, "set -e; chmod 700 " + Quote(temporaryPath + ".sh") + " && " + Quote(temporaryPath + ".sh") + " && rm -f " + Quote(temporaryPath + ".sh"), "读取 Redis 版本", TimeSpan.FromMinutes(2), cancellationToken);
                    return output.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
                }
            }
            throw new InvalidOperationException("不支持读取该数据库版本");
        }

        private static string BuildConfigurationCommand(LinuxPackage package, DatabaseDeploymentDraft draft, string temporaryPath)
        {
            if (package.Family == "mysql")
            {
                string dropIn = package.ConfigPath;
                return "mkdir -p " + Quote(Path.GetDirectoryName(dropIn).Replace('\\', '/')) + " && printf '%s\\n' '[mysqld]' 'bind-address=127.0.0.1' 'port=" + draft.Port + "' > " + Quote(dropIn) + " && chmod 644 " + Quote(dropIn) + " && systemctl restart " + Quote(package.ServiceName) + " && systemctl is-active --quiet " + Quote(package.ServiceName) + " && printf 'MYSQL_CONFIGURED\\n'";
            }
            if (package.Family == "mongo")
            {
                string config = Quote(package.ConfigPath);
                return "set -e; test -f " + config + " && " +
                    "if grep -Eq '^[[:space:]]*port:' " + config + "; then sed -Ei 's/^[[:space:]]*port:.*/  port: " + draft.Port + "/' " + config + "; else printf '\\nnet:\\n  port: " + draft.Port + "\\n  bindIp: 127.0.0.1\\n' >> " + config + "; fi; " +
                    "if grep -Eq '^[[:space:]]*bindIp:' " + config + "; then sed -Ei 's/^[[:space:]]*bindIp:.*/  bindIp: 127.0.0.1/' " + config + "; fi; " +
                    "if grep -Eq '^[[:space:]]*authorization:' " + config + "; then sed -Ei 's/^[[:space:]]*authorization:.*/  authorization: disabled/' " + config + "; else printf '\\nsecurity:\\n  authorization: disabled\\n' >> " + config + "; fi; systemctl restart " + Quote(package.ServiceName) + " && systemctl is-active --quiet " + Quote(package.ServiceName) + " && printf 'MONGO_CONFIGURED\\n'";
            }
            if (package.Family == "redis")
            {
                string config = Quote(package.ConfigPath);
                return "test -f " + config + " && sed -Ei 's/^[[:space:]]*#?[[:space:]]*bind .*/bind 127.0.0.1/' " + config + " && sed -Ei 's/^[[:space:]]*#?[[:space:]]*port [0-9]+/port " + draft.Port + "/' " + config + " && printf '\\naclfile " + package.AclPath + "\\nprotected-mode yes\\n' >> " + config + " && chmod 644 " + config + " && printf 'REDIS_CONFIGURED\\n'";
            }
            throw new InvalidOperationException("不支持配置该数据库");
        }

        private static string BuildRefreshPackageIndex(RemoteSystemInfo info)
        {
            return info.PackageManager == "apt"
                ? "DEBIAN_FRONTEND=noninteractive apt-get update -y"
                : "dnf -y makecache --timer || dnf -y makecache";
        }

        private static string BuildInstallCommand(RemoteSystemInfo info, string packageName)
        {
            return info.PackageManager == "apt"
                ? "DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends " + Quote(packageName)
                : "dnf install -y " + Quote(packageName);
        }

        private static string BuildRemovePackageCommand(RemoteSystemInfo info, string packageName)
        {
            return info.PackageManager == "apt"
                ? "DEBIAN_FRONTEND=noninteractive apt-get remove -y " + Quote(packageName) + " >/dev/null 2>&1 || true"
                : "dnf remove -y " + Quote(packageName) + " >/dev/null 2>&1 || true";
        }

        private static async Task<string> RunCheckedAsync(SshRemoteExecutor executor, RemoteSystemInfo info, string command, string operation, TimeSpan timeout, CancellationToken cancellationToken)
        {
            RemoteCommandResult result = await RunPrivilegedAsync(executor, info, command, timeout, cancellationToken);
            EnsureSuccess(result, operation);
            return result.Output ?? "";
        }

        private static async Task<RemoteCommandResult> RunPrivilegedAsync(SshRemoteExecutor executor, RemoteSystemInfo info, string command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (info.IsRoot)
                return await executor.ExecuteCommandAsync(command, timeout, cancellationToken);
            if (info.CanSudo)
                return await executor.ExecuteCommandAsync("sudo -n sh -c " + Quote(command), timeout, cancellationToken);
            if (string.IsNullOrEmpty(info.SudoPassword))
                throw new InvalidOperationException("Linux 提权密码不可用");
            return await executor.ExecuteSudoCommandAsync(command, info.SudoPassword, timeout, cancellationToken);
        }

        private static async Task UploadTextAsync(Server server, string serverPassword, string content, string remotePath, CancellationToken cancellationToken)
        {
            using (SshRemoteClient client = new SshRemoteClient(server, serverPassword))
            {
                await client.ConnectAsync(cancellationToken);
                await client.UploadTextAsync(content, remotePath, cancellationToken);
            }
        }

        private static LinuxPackage ResolvePackage(string type, string track)
        {
            if (type == "MySQL")
                return new LinuxPackage { Family = "mysql", PackageName = "mysql-server", ServiceName = "mysql", ConfigPath = "/etc/mysql/mysql.conf.d/99-xiaobai-manager.cnf", DataPath = "/var/lib/mysql", Major = "8" };
            if (type == "MariaDB")
                return new LinuxPackage { Family = "mysql", PackageName = "mariadb-server", ServiceName = "mariadb", ConfigPath = "/etc/mysql/mariadb.conf.d/99-xiaobai-manager.cnf", DataPath = "/var/lib/mysql", Major = track != null && track.StartsWith("11", StringComparison.Ordinal) ? "11" : "10" };
            if (type == "MongoDB")
                return new LinuxPackage { Family = "mongo", PackageName = "mongodb-org", ServiceName = "mongod", ConfigPath = "/etc/mongod.conf", DataPath = "/var/lib/mongodb", Major = track != null && track.StartsWith("7", StringComparison.Ordinal) ? "7" : "8" };
            if (type == "Redis")
                return new LinuxPackage { Family = "redis", PackageName = "redis-server", ServiceName = "redis-server", ConfigPath = "/etc/redis/redis.conf", DataPath = "/var/lib/redis", Major = track != null && track.StartsWith("7", StringComparison.Ordinal) ? "7" : "8" };
            throw new InvalidOperationException("Linux 暂不支持该数据库类型的一键部署");
        }

        private static void ValidateEnvironment(RemoteSystemInfo info, LinuxPackage package)
        {
            if (info == null || !info.IsLinux || !info.HasSystemd)
                throw new InvalidOperationException("目标服务器不是首期支持的 Linux systemd 环境");
            string id = (info.DistributionId ?? "").ToLowerInvariant();
            string version = info.OsVersion ?? "";
            bool supported = (id == "ubuntu" && (version.StartsWith("22.04") || version.StartsWith("24.04"))) ||
                (id == "debian" && (version == "12" || version.StartsWith("12."))) ||
                ((id == "rocky" || id == "almalinux") && (version == "9" || version.StartsWith("9.")));
            if (!supported)
                throw new InvalidOperationException("当前发行版未通过首期部署验证，仅支持 Ubuntu 22.04/24.04、Debian 12、Rocky Linux 9 和 AlmaLinux 9");
            if (info.PackageManager != "apt" && info.PackageManager != "dnf")
                throw new InvalidOperationException("未识别 apt 或 dnf 包管理器");
        }

        private static void ConfigurePackageForEnvironment(RemoteSystemInfo info, LinuxPackage package)
        {
            bool apt = info.PackageManager == "apt";
            if (package.Family == "mysql")
            {
                package.ServiceName = package.PackageName == "mysql-server" ? (apt ? "mysql" : "mysqld") : "mariadb";
                package.ConfigPath = apt
                    ? package.PackageName == "mysql-server" ? "/etc/mysql/conf.d/99-xiaobai-manager.cnf" : "/etc/mysql/mariadb.conf.d/99-xiaobai-manager.cnf"
                    : "/etc/my.cnf.d/99-xiaobai-manager.cnf";
            }
            else if (package.Family == "redis")
            {
                package.ServiceName = apt ? "redis-server" : "redis";
                package.ConfigPath = apt ? "/etc/redis/redis.conf" : "/etc/redis.conf";
                package.AclPath = "/etc/redis/users.acl";
            }
        }

        private static void ValidateDraft(DatabaseDeploymentDraft draft)
        {
            if (draft == null || !SafeIdentifier.IsMatch(draft.DatabaseType ?? "") || !SafeIdentifier.IsMatch(draft.ServiceName ?? "") ||
                !SafeIdentifier.IsMatch(draft.DatabaseName ?? "") || !SafeIdentifier.IsMatch(draft.AdminUser ?? "") ||
                string.IsNullOrEmpty(draft.AdminPassword) || draft.AdminPassword.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0 ||
                draft.Port < 1 || draft.Port > 65535 || !SafePort.IsMatch(draft.Port.ToString(CultureInfo.InvariantCulture)))
                throw new InvalidOperationException("Linux 数据库部署配置不完整或包含不支持的字符");
            if (draft.DatabaseType == "Oracle")
                throw new InvalidOperationException("Oracle Linux 一键部署暂未开放");
            if (draft.DatabaseType == "Redis" && draft.AdminPassword.Any(char.IsWhiteSpace))
                throw new InvalidOperationException("Redis ACL 密码不能包含空白字符，请使用字母、数字和常用符号");
        }

        private static void ValidateServer(Server server)
        {
            if (server == null || server.Type != ServerType.Linux || string.IsNullOrWhiteSpace(server.IP) || string.IsNullOrWhiteSpace(server.Username))
                throw new InvalidOperationException("该操作需要完整的 Linux SSH 服务器信息");
        }

        private static void PrepareSudoPassword(Server server, RemoteSystemInfo info, Func<string> provider)
        {
            if (info.IsRoot || info.CanSudo)
                return;
            if (!info.HasSudo)
                throw new InvalidOperationException("当前 Linux 账号没有 sudo 权限");
            if (string.IsNullOrEmpty(server.SudoPassword))
                server.SudoPassword = provider == null ? null : provider();
            if (string.IsNullOrEmpty(server.SudoPassword))
                throw new InvalidOperationException("未提供 Linux sudo 密码");
            info.SudoPassword = server.SudoPassword;
        }

        private static string BuildMySqlInitSql(DatabaseDeploymentDraft draft)
        {
            string user = QuoteSqlLiteral(draft.AdminUser);
            string account = user + "@'localhost'";
            string accountSetup = string.Equals(draft.AdminUser, "root", StringComparison.OrdinalIgnoreCase)
                ? "ALTER USER 'root'@'localhost' IDENTIFIED BY " + QuoteSqlLiteral(draft.AdminPassword) + ";" + Environment.NewLine
                : "CREATE USER IF NOT EXISTS " + account + " IDENTIFIED BY " + QuoteSqlLiteral(draft.AdminPassword) + ";" + Environment.NewLine;
            return "CREATE DATABASE IF NOT EXISTS " + QuoteSqlIdentifier(draft.DatabaseName) + ";" + Environment.NewLine +
                accountSetup +
                "GRANT ALL PRIVILEGES ON " + QuoteSqlIdentifier(draft.DatabaseName) + ".* TO " + account + ";" + Environment.NewLine +
                "FLUSH PRIVILEGES;" + Environment.NewLine;
        }

        private static string BuildMongoInitScript(DatabaseDeploymentDraft draft)
        {
            return "const admin=db.getSiblingDB('admin'); admin.createUser({user:" + QuoteJavaScript(draft.AdminUser) + ",pwd:" + QuoteJavaScript(draft.AdminPassword) + ",roles:[{role:'root',db:'admin'}]}); db.getSiblingDB(" + QuoteJavaScript(draft.DatabaseName) + ").createCollection('_xiaobai_init'); print('MONGO_INITIALIZED');";
        }

        private static string BuildEnableMongoAuthenticationCommand(LinuxPackage package)
        {
            string config = Quote(package.ConfigPath);
            return "set -e; test -f " + config + " && if grep -Eq '^[[:space:]]*authorization:' " + config + "; then sed -Ei 's/^[[:space:]]*authorization:.*/  authorization: enabled/' " + config + "; else printf '\\nsecurity:\\n  authorization: enabled\\n' >> " + config + "; fi; systemctl restart " + Quote(package.ServiceName) + " && systemctl is-active --quiet " + Quote(package.ServiceName) + " && printf 'MONGO_AUTH_ENABLED\\n'";
        }

        private static DatabaseCredentialRecord CreateCredential(LinuxPackage package, DatabaseDeploymentDraft draft, DatabaseDeploymentResult deployment)
        {
            return new DatabaseCredentialRecord
            {
                DatabaseType = draft.DatabaseType,
                ServiceName = package.ServiceName,
                Host = "127.0.0.1",
                Port = deployment.Port,
                Username = draft.AdminUser,
                Password = draft.AdminPassword,
                AuthenticationDatabase = package.Family == "mongo" ? "admin" : "",
                DatabaseName = draft.DatabaseName,
                IsVerified = true,
                IsManagerDeployed = true,
                InstalledVersion = deployment.ServerVersion,
                InstallPath = package.PackageName,
                DeployedAt = DateTime.Now
            };
        }

        private static void ValidateVersion(LinuxPackage package, string version)
        {
            if (string.IsNullOrWhiteSpace(version) || (!string.IsNullOrWhiteSpace(package.Major) && !version.StartsWith(package.Major + ".", StringComparison.Ordinal)))
                throw new InvalidOperationException("安装后的数据库版本为 " + (version ?? "未知") + "，与当前部署线路不匹配，已停止保存凭据");
        }

        private static string QuoteSqlIdentifier(string value) { return "`" + value.Replace("`", "``") + "`"; }
        private static string QuoteSqlLiteral(string value) { return "'" + value.Replace("'", "''") + "'"; }
        private static string QuoteJavaScript(string value) { return "'" + value.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "\\r").Replace("\n", "\\n") + "'"; }
        private static string QuoteShellScript(string value) { return "'" + value.Replace("'", "'\\''") + "'"; }
                private static string Quote(string value) { return "'" + (value ?? "").Replace("'", "'\\''") + "'"; }

        private static void Report(Action<DatabaseDeploymentProgress> progress, int step, OperationStepState state, string title, string detail, int percent, bool indeterminate)
        {
            progress?.Invoke(new DatabaseDeploymentProgress { Step = step, State = state, Title = title, Detail = detail, Percent = percent, Indeterminate = indeterminate });
        }

        private static void EnsureSuccess(RemoteCommandResult result, string operation)
        {
            if (result != null && result.ExitCode == 0)
                return;
            throw new InvalidOperationException(operation + "失败：" + RemoteErrorFormatter.Format(result));
        }

        private sealed class LinuxPackage
        {
            public string Family { get; set; }
            public string PackageName { get; set; }
            public string ServiceName { get; set; }
            public string ConfigPath { get; set; }
            public string AclPath { get; set; }
            public string DataPath { get; set; }
            public string Major { get; set; }
        }
    }
}
