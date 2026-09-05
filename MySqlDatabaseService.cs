using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace RDPManager
{
    public sealed class MySqlPermissionSelection
    {
        public string DatabaseName { get; set; }
        public bool Select { get; set; }
        public bool Insert { get; set; }
        public bool Update { get; set; }
        public bool Delete { get; set; }
        public bool Create { get; set; }
        public bool Alter { get; set; }
        public bool Execute { get; set; }
        public bool CreateDatabase { get; set; }
        public bool GrantOption { get; set; }
        public bool AllPrivileges { get; set; }
    }

    public sealed class MySqlUserRequest
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public string HostPattern { get; set; }
        public MySqlPermissionSelection Permissions { get; set; }
    }

    public sealed class MySqlUserInfo
    {
        public string Username { get; set; }
        public string HostPattern { get; set; }
        public string Plugin { get; set; }
        public string PermissionSummary { get; set; }
    }

    public sealed class MySqlGrantScope
    {
        public string DatabaseName { get; set; }
        public string ScopeText { get; set; }
        public bool IsEditable { get; set; }
        public bool AllPrivileges { get; set; }
        public bool Select { get; set; }
        public bool Insert { get; set; }
        public bool Update { get; set; }
        public bool Delete { get; set; }
        public bool Create { get; set; }
        public bool Alter { get; set; }
        public bool Execute { get; set; }
        public bool GrantOption { get; set; }

        public string DisplayName
        {
            get
            {
                if (!IsEditable)
                    return ScopeText + "（暂不支持编辑）";
                return DatabaseName == "*" ? "全部数据库（*.*）" : DatabaseName + ".*";
            }
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    public sealed class MySqlConnectionTestResult
    {
        public string ServerVersion { get; set; }
        public string UserName { get; set; }
        public int LocalPort { get; set; }
    }

    public sealed class MySqlDatabaseService
    {
        private static readonly Regex SafeIdentifier = new Regex(@"^[A-Za-z0-9_$.-]{1,64}$", RegexOptions.Compiled);
        private static readonly Regex SafeHost = new Regex(@"^[A-Za-z0-9%_.:-]{1,255}$", RegexOptions.Compiled);

        public async Task<MySqlConnectionTestResult> TestConnectionAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            CancellationToken cancellationToken)
        {
            ValidateCredential(credential);
            using (SshDatabaseTunnel tunnel = await OpenTunnelAsync(server, serverPassword, credential, cancellationToken))
            using (MySqlConnection connection = CreateConnection(tunnel.LocalPort, credential))
            {
                await connection.OpenAsync(cancellationToken);
                using (MySqlCommand command = new MySqlCommand("SELECT VERSION(), CURRENT_USER()", connection))
                using (System.Data.Common.DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    if (!await reader.ReadAsync(cancellationToken))
                        throw new InvalidOperationException("数据库没有返回连接信息");
                    return new MySqlConnectionTestResult
                    {
                        ServerVersion = reader.IsDBNull(0) ? "" : reader.GetString(0),
                        UserName = reader.IsDBNull(1) ? credential.Username : reader.GetString(1),
                        LocalPort = tunnel.LocalPort
                    };
                }
            }
        }

        public async Task<MySqlConnectionTestResult> TestLocalConnectionAsync(
            LocalDatabaseTarget target,
            CancellationToken cancellationToken)
        {
            if (target == null || string.IsNullOrWhiteSpace(target.Host) || target.Port < 1 || target.Port > 65535 ||
                string.IsNullOrWhiteSpace(target.Username) || string.IsNullOrEmpty(target.Password))
                throw new InvalidOperationException("本机数据库目标信息不完整");
            MySqlConnectionStringBuilder builder = new MySqlConnectionStringBuilder
            {
                Server = target.Host,
                Port = (uint)target.Port,
                UserID = target.Username,
                Password = target.Password,
                SslMode = MySqlSslMode.None,
                AllowPublicKeyRetrieval = true,
                ConnectionTimeout = 10,
                Pooling = false
            };
            using (MySqlConnection connection = new MySqlConnection(builder.ConnectionString))
            {
                await connection.OpenAsync(cancellationToken);
                using (MySqlCommand command = new MySqlCommand("SELECT VERSION(), CURRENT_USER()", connection))
                using (System.Data.Common.DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    if (!await reader.ReadAsync(cancellationToken))
                        throw new InvalidOperationException("本机数据库没有返回连接信息");
                    return new MySqlConnectionTestResult
                    {
                        ServerVersion = reader.IsDBNull(0) ? "" : reader.GetString(0),
                        UserName = reader.IsDBNull(1) ? target.Username : reader.GetString(1),
                        LocalPort = target.Port
                    };
                }
            }
        }

        public async Task<IList<string>> ListDatabasesAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            CancellationToken cancellationToken)
        {
            ValidateCredential(credential);
            using (SshDatabaseTunnel tunnel = await OpenTunnelAsync(server, serverPassword, credential, cancellationToken))
            using (MySqlConnection connection = CreateConnection(tunnel.LocalPort, credential))
            {
                await connection.OpenAsync(cancellationToken);
                using (MySqlCommand command = new MySqlCommand("SHOW DATABASES", connection))
                using (System.Data.Common.DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    List<string> result = new List<string>();
                    while (await reader.ReadAsync(cancellationToken))
                        result.Add(reader.GetString(0));
                    return result;
                }
            }
        }

        public async Task<IList<MySqlUserInfo>> ListUsersAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            CancellationToken cancellationToken)
        {
            ValidateCredential(credential);
            using (SshDatabaseTunnel tunnel = await OpenTunnelAsync(server, serverPassword, credential, cancellationToken))
            using (MySqlConnection connection = CreateConnection(tunnel.LocalPort, credential))
            {
                await connection.OpenAsync(cancellationToken);
                List<MySqlUserInfo> users = new List<MySqlUserInfo>();
                using (MySqlCommand command = new MySqlCommand("SELECT User, Host, plugin FROM mysql.user ORDER BY User, Host", connection))
                using (System.Data.Common.DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        users.Add(new MySqlUserInfo
                        {
                            Username = reader.IsDBNull(0) ? "" : reader.GetString(0),
                            HostPattern = reader.IsDBNull(1) ? "" : reader.GetString(1),
                            Plugin = reader.IsDBNull(2) ? "" : reader.GetString(2)
                        });
                    }
                }

                foreach (MySqlUserInfo user in users)
                    user.PermissionSummary = await GetPermissionSummaryAsync(connection, user.Username, user.HostPattern, cancellationToken);
                return users;
            }
        }

        public async Task CreateUserAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            MySqlUserRequest request,
            CancellationToken cancellationToken)
        {
            ValidateCredential(credential);
            ValidateUserRequest(request);
            using (SshDatabaseTunnel tunnel = await OpenTunnelAsync(server, serverPassword, credential, cancellationToken))
            using (MySqlConnection connection = CreateConnection(tunnel.LocalPort, credential))
            {
                await connection.OpenAsync(cancellationToken);
                string user = QuoteSqlString(request.Username);
                string host = QuoteSqlString(request.HostPattern);
                string createSql = "CREATE USER " + user + "@" + host + " IDENTIFIED BY " + QuoteSqlString(request.Password);
                await ExecuteNonQueryAsync(connection, createSql, cancellationToken);
                try
                {
                    string grantSql = BuildGrantSql(request);
                    if (!string.IsNullOrWhiteSpace(grantSql))
                        await ExecuteNonQueryAsync(connection, grantSql, cancellationToken);
                    await ExecuteNonQueryAsync(connection, "FLUSH PRIVILEGES", cancellationToken);
                }
                catch
                {
                    try { await ExecuteNonQueryAsync(connection, "DROP USER " + user + "@" + host, CancellationToken.None); } catch { }
                    throw;
                }
            }
        }

        public async Task DropUserAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            string username,
            string hostPattern,
            CancellationToken cancellationToken)
        {
            ValidateCredential(credential);
            ValidateUserName(username);
            ValidateHostPattern(hostPattern);
            using (SshDatabaseTunnel tunnel = await OpenTunnelAsync(server, serverPassword, credential, cancellationToken))
            using (MySqlConnection connection = CreateConnection(tunnel.LocalPort, credential))
            {
                await connection.OpenAsync(cancellationToken);
                await ExecuteNonQueryAsync(connection, "DROP USER " + QuoteSqlString(username) + "@" + QuoteSqlString(hostPattern), cancellationToken);
            }
        }

        public async Task<IList<MySqlGrantScope>> ListGrantScopesAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            string username,
            string hostPattern,
            CancellationToken cancellationToken)
        {
            ValidateCredential(credential);
            ValidateUserName(username);
            ValidateHostPattern(hostPattern);
            using (SshDatabaseTunnel tunnel = await OpenTunnelAsync(server, serverPassword, credential, cancellationToken))
            using (MySqlConnection connection = CreateConnection(tunnel.LocalPort, credential))
            {
                await connection.OpenAsync(cancellationToken);
                List<MySqlGrantScope> scopes = new List<MySqlGrantScope>();
                string sql = "SHOW GRANTS FOR " + QuoteSqlString(username) + "@" + QuoteSqlString(hostPattern);
                using (MySqlCommand command = new MySqlCommand(sql, connection))
                using (System.Data.Common.DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        if (reader.IsDBNull(0))
                            continue;
                        MySqlGrantScope parsed = ParseGrant(reader.GetString(0));
                        if (parsed == null)
                            continue;
                        MySqlGrantScope existing = scopes.FirstOrDefault(item =>
                            string.Equals(item.ScopeText, parsed.ScopeText, StringComparison.OrdinalIgnoreCase));
                        if (existing == null)
                            scopes.Add(parsed);
                        else
                            MergeGrant(existing, parsed);
                    }
                }
                return scopes;
            }
        }

        public async Task UpdatePermissionsAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            string username,
            string hostPattern,
            MySqlGrantScope scope,
            CancellationToken cancellationToken)
        {
            ValidateCredential(credential);
            ValidateUserName(username);
            ValidateHostPattern(hostPattern);
            if (scope == null || !scope.IsEditable)
                throw new InvalidOperationException("当前授权范围暂不支持编辑");

            using (SshDatabaseTunnel tunnel = await OpenTunnelAsync(server, serverPassword, credential, cancellationToken))
            using (MySqlConnection connection = CreateConnection(tunnel.LocalPort, credential))
            {
                await connection.OpenAsync(cancellationToken);
                string account = QuoteSqlString(username) + "@" + QuoteSqlString(hostPattern);
                string sqlScope = BuildGrantScope(scope.DatabaseName);
                await ExecuteNonQueryAsync(connection, "REVOKE ALL PRIVILEGES, GRANT OPTION ON " + sqlScope + " FROM " + account, cancellationToken);
                string grant = BuildGrantSql(username, hostPattern, scope);
                if (!string.IsNullOrWhiteSpace(grant))
                    await ExecuteNonQueryAsync(connection, grant, cancellationToken);
                await ExecuteNonQueryAsync(connection, "FLUSH PRIVILEGES", cancellationToken);
            }
        }

        public async Task ResetUserPasswordAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            string username,
            string hostPattern,
            string newPassword,
            CancellationToken cancellationToken)
        {
            ValidateCredential(credential);
            ValidateUserName(username);
            ValidateHostPattern(hostPattern);
            if (string.IsNullOrEmpty(newPassword))
                throw new InvalidOperationException("新密码不能为空");
            using (SshDatabaseTunnel tunnel = await OpenTunnelAsync(server, serverPassword, credential, cancellationToken))
            using (MySqlConnection connection = CreateConnection(tunnel.LocalPort, credential))
            {
                await connection.OpenAsync(cancellationToken);
                await ExecuteNonQueryAsync(connection,
                    "ALTER USER " + QuoteSqlString(username) + "@" + QuoteSqlString(hostPattern) + " IDENTIFIED BY " + QuoteSqlString(newPassword),
                    cancellationToken);
                await ExecuteNonQueryAsync(connection, "FLUSH PRIVILEGES", cancellationToken);
            }
        }

        private static async Task<SshDatabaseTunnel> OpenTunnelAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            CancellationToken cancellationToken)
        {
            return await SshDatabaseTunnel.OpenAsync(server, serverPassword, credential.Host, credential.Port, cancellationToken);
        }

        private static MySqlConnection CreateConnection(int localPort, DatabaseCredentialRecord credential)
        {
            MySqlConnectionStringBuilder builder = new MySqlConnectionStringBuilder
            {
                Server = "127.0.0.1",
                Port = (uint)localPort,
                UserID = credential.Username,
                Password = credential.Password,
                Database = credential.DatabaseName ?? "",
                SslMode = MySqlSslMode.None,
                AllowPublicKeyRetrieval = true,
                ConnectionTimeout = 15,
                Pooling = false,
                AllowUserVariables = true
            };
            return new MySqlConnection(builder.ConnectionString);
        }

        private static async Task<string> GetPermissionSummaryAsync(
            MySqlConnection connection,
            string username,
            string hostPattern,
            CancellationToken cancellationToken)
        {
            string sql = "SHOW GRANTS FOR " + QuoteSqlString(username) + "@" + QuoteSqlString(hostPattern);
            using (MySqlCommand command = new MySqlCommand(sql, connection))
            using (System.Data.Common.DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                List<string> grants = new List<string>();
                while (await reader.ReadAsync(cancellationToken))
                    if (!reader.IsDBNull(0))
                        grants.Add(reader.GetString(0));
                return string.Join("；", grants);
            }
        }

        private static async Task ExecuteNonQueryAsync(MySqlConnection connection, string sql, CancellationToken cancellationToken)
        {
            using (MySqlCommand command = new MySqlCommand(sql, connection))
                await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static string BuildGrantSql(MySqlUserRequest request)
        {
            MySqlPermissionSelection p = request.Permissions;
            if (p == null)
                return "";
            if (p.AllPrivileges)
                return "GRANT ALL PRIVILEGES ON *.* TO " + QuoteSqlString(request.Username) + "@" + QuoteSqlString(request.HostPattern) + (p.GrantOption ? " WITH GRANT OPTION" : "");

            List<string> privileges = new List<string>();
            if (p.Select) privileges.Add("SELECT");
            if (p.Insert) privileges.Add("INSERT");
            if (p.Update) privileges.Add("UPDATE");
            if (p.Delete) privileges.Add("DELETE");
            if (p.Create) privileges.Add("CREATE");
            if (p.Alter) privileges.Add("ALTER");
            if (p.Execute) privileges.Add("EXECUTE");
            if (p.CreateDatabase) privileges.Add("CREATE USER");
            if (privileges.Count == 0)
                return "";

            if (string.IsNullOrWhiteSpace(p.DatabaseName))
                throw new InvalidOperationException("请选择授权数据库；如果确需全库授权，请使用“全部权限”并确认风险");
            string database = QuoteIdentifier(p.DatabaseName);
            string sql = "GRANT " + string.Join(", ", privileges) + " ON " + database + ".* TO " +
                QuoteSqlString(request.Username) + "@" + QuoteSqlString(request.HostPattern);
            if (p.GrantOption)
                sql += " WITH GRANT OPTION";
            return sql;
        }

        private static string BuildGrantSql(string username, string hostPattern, MySqlGrantScope scope)
        {
            string account = QuoteSqlString(username) + "@" + QuoteSqlString(hostPattern);
            string sqlScope = BuildGrantScope(scope.DatabaseName);
            if (scope.AllPrivileges)
                return "GRANT ALL PRIVILEGES ON " + sqlScope + " TO " + account + (scope.GrantOption ? " WITH GRANT OPTION" : "");

            List<string> privileges = new List<string>();
            if (scope.Select) privileges.Add("SELECT");
            if (scope.Insert) privileges.Add("INSERT");
            if (scope.Update) privileges.Add("UPDATE");
            if (scope.Delete) privileges.Add("DELETE");
            if (scope.Create) privileges.Add("CREATE");
            if (scope.Alter) privileges.Add("ALTER");
            if (scope.Execute) privileges.Add("EXECUTE");
            if (privileges.Count == 0)
                return "";
            string grant = "GRANT " + string.Join(", ", privileges) + " ON " + sqlScope + " TO " + account;
            return scope.GrantOption ? grant + " WITH GRANT OPTION" : grant;
        }

        private static string BuildGrantScope(string databaseName)
        {
            if (databaseName == "*")
                return "*.*";
            if (string.IsNullOrWhiteSpace(databaseName))
                throw new InvalidOperationException("授权数据库不能为空");
            return QuoteIdentifier(databaseName) + ".*";
        }

        private static MySqlGrantScope ParseGrant(string grant)
        {
            if (string.IsNullOrWhiteSpace(grant))
                return null;
            Match match = Regex.Match(grant, @"^GRANT\s+(?<priv>.+?)\s+ON\s+(?<scope>[^\s]+)\s+TO\s+", RegexOptions.IgnoreCase);
            if (!match.Success)
                return null;
            string scopeText = match.Groups["scope"].Value.Trim();
            string[] parts = scopeText.Split(new[] { '.' }, 2);
            if (parts.Length != 2)
                return null;
            string databaseName = UnquoteIdentifier(parts[0]);
            string objectName = UnquoteIdentifier(parts[1]);
            bool editable = objectName == "*" && (databaseName == "*" || SafeIdentifier.IsMatch(databaseName));
            MySqlGrantScope scope = new MySqlGrantScope
            {
                DatabaseName = databaseName,
                ScopeText = scopeText,
                IsEditable = editable,
                GrantOption = grant.IndexOf("WITH GRANT OPTION", StringComparison.OrdinalIgnoreCase) >= 0
            };
            string privileges = match.Groups["priv"].Value;
            if (privileges.IndexOf("ALL PRIVILEGES", StringComparison.OrdinalIgnoreCase) >= 0)
                scope.AllPrivileges = true;
            scope.Select = HasPrivilege(privileges, "SELECT");
            scope.Insert = HasPrivilege(privileges, "INSERT");
            scope.Update = HasPrivilege(privileges, "UPDATE");
            scope.Delete = HasPrivilege(privileges, "DELETE");
            scope.Create = HasPrivilege(privileges, "CREATE");
            scope.Alter = HasPrivilege(privileges, "ALTER");
            scope.Execute = HasPrivilege(privileges, "EXECUTE");
            return scope;
        }

        private static void MergeGrant(MySqlGrantScope target, MySqlGrantScope source)
        {
            target.AllPrivileges = target.AllPrivileges || source.AllPrivileges;
            target.Select = target.Select || source.Select;
            target.Insert = target.Insert || source.Insert;
            target.Update = target.Update || source.Update;
            target.Delete = target.Delete || source.Delete;
            target.Create = target.Create || source.Create;
            target.Alter = target.Alter || source.Alter;
            target.Execute = target.Execute || source.Execute;
            target.GrantOption = target.GrantOption || source.GrantOption;
        }

        private static bool HasPrivilege(string value, string privilege)
        {
            return Regex.IsMatch(value, @"(^|[,\s])" + Regex.Escape(privilege) + @"([,\s]|$)", RegexOptions.IgnoreCase);
        }

        private static string UnquoteIdentifier(string value)
        {
            string result = (value ?? "").Trim();
            if (result.StartsWith("`") && result.EndsWith("`") && result.Length >= 2)
                result = result.Substring(1, result.Length - 2).Replace("``", "`");
            return result;
        }

        private static void ValidateCredential(DatabaseCredentialRecord credential)
        {
            if (credential == null)
                throw new InvalidOperationException("数据库凭据为空");
            ValidateUserName(credential.Username);
            if (string.IsNullOrWhiteSpace(credential.Host))
                credential.Host = "127.0.0.1";
            if (credential.Port < 1 || credential.Port > 65535)
                throw new InvalidOperationException("数据库端口无效");
        }

        private static void ValidateUserRequest(MySqlUserRequest request)
        {
            if (request == null)
                throw new InvalidOperationException("用户信息为空");
            ValidateUserName(request.Username);
            ValidateHostPattern(request.HostPattern);
            if (string.IsNullOrEmpty(request.Password))
                throw new InvalidOperationException("用户密码不能为空");
        }

        private static void ValidateUserName(string username)
        {
            if (string.IsNullOrWhiteSpace(username) || !SafeIdentifier.IsMatch(username))
                throw new InvalidOperationException("用户名只能包含字母、数字、下划线、点、短横线或美元符号");
        }

        private static void ValidateHostPattern(string hostPattern)
        {
            if (string.IsNullOrWhiteSpace(hostPattern) || !SafeHost.IsMatch(hostPattern))
                throw new InvalidOperationException("来源主机格式无效");
        }

        private static string QuoteIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "*")
                return "*";
            if (!SafeIdentifier.IsMatch(value))
                throw new InvalidOperationException("数据库名包含不支持的字符");
            return "`" + value.Replace("`", "``") + "`";
        }

        private static string QuoteSqlString(string value)
        {
            return "'" + (value ?? "").Replace("\\", "\\\\").Replace("'", "''") + "'";
        }
    }
}
