using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RDPManager
{
    public sealed class RedisConnectionTestResult
    {
        public string Version { get; set; }
        public string UserName { get; set; }
        public bool AclSupported { get; set; }
        public int DatabaseIndex { get; set; }
    }

    public sealed class RedisAclUserInfo
    {
        public string Username { get; set; }
        public bool Enabled { get; set; }
        public bool NoPassword { get; set; }
        public string KeyPatterns { get; set; }
        public string CommandRules { get; set; }
        public string RawRule { get; set; }
    }

    public sealed class RedisAclSelection
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public string KeyPattern { get; set; }
        public bool Read { get; set; }
        public bool Write { get; set; }
        public bool Connection { get; set; }
        public bool Transaction { get; set; }
        public bool PubSub { get; set; }
        public bool Scripting { get; set; }
        public bool Admin { get; set; }
        public bool AllCommands { get; set; }
    }

    public sealed class RedisDatabaseService
    {
        private static readonly Regex SafeUserName = new Regex(@"^[A-Za-z0-9_.-]{1,64}$", RegexOptions.Compiled);

        public async Task<RedisConnectionTestResult> TestConnectionAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            CancellationToken cancellationToken)
        {
            ValidateCredential(credential);
            using (SshDatabaseTunnel tunnel = await SshDatabaseTunnel.OpenAsync(
                server, serverPassword, credential.Host, credential.Port, cancellationToken))
            using (RedisRespConnection connection = await OpenAuthenticatedAsync(tunnel.LocalPort, credential, cancellationToken))
            {
                string pong = Convert.ToString(await connection.CommandAsync(cancellationToken, "PING"));
                if (!string.Equals(pong, "PONG", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Redis 没有返回 PONG");

                string info = Convert.ToString(await connection.CommandAsync(cancellationToken, "INFO", "server"));
                string version = ReadInfoValue(info, "redis_version");
                string whoAmI = credential.Username;
                bool aclSupported = true;
                try
                {
                    whoAmI = Convert.ToString(await connection.CommandAsync(cancellationToken, "ACL", "WHOAMI"));
                }
                catch (RedisCommandException ex) when (IsUnknownCommand(ex.Message))
                {
                    aclSupported = false;
                    whoAmI = "default";
                }
                return new RedisConnectionTestResult
                {
                    Version = version,
                    UserName = string.IsNullOrWhiteSpace(whoAmI) ? "default" : whoAmI,
                    AclSupported = aclSupported,
                    DatabaseIndex = ParseDatabaseIndex(credential.DatabaseName)
                };
            }
        }

        public async Task<IList<RedisAclUserInfo>> ListUsersAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            CancellationToken cancellationToken)
        {
            ValidateCredential(credential);
            using (SshDatabaseTunnel tunnel = await SshDatabaseTunnel.OpenAsync(
                server, serverPassword, credential.Host, credential.Port, cancellationToken))
            using (RedisRespConnection connection = await OpenAuthenticatedAsync(tunnel.LocalPort, credential, cancellationToken))
            {
                await EnsureAclSupportedAsync(connection, cancellationToken);
                object response = await connection.CommandAsync(cancellationToken, "ACL", "LIST");
                List<object> rows = response as List<object> ?? new List<object>();
                return rows.Select(row => ParseAclRule(Convert.ToString(row))).Where(item => item != null).ToList();
            }
        }

        public async Task CreateUserAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            RedisAclSelection selection,
            CancellationToken cancellationToken)
        {
            ValidateCredential(credential);
            ValidateSelection(selection, true);
            using (SshDatabaseTunnel tunnel = await SshDatabaseTunnel.OpenAsync(
                server, serverPassword, credential.Host, credential.Port, cancellationToken))
            using (RedisRespConnection connection = await OpenAuthenticatedAsync(tunnel.LocalPort, credential, cancellationToken))
            {
                await EnsureAclSupportedAsync(connection, cancellationToken);
                bool created = false;
                try
                {
                    List<string> args = new List<string> { "ACL", "SETUSER", selection.Username, "reset", "on", ">" + selection.Password };
                    args.AddRange(BuildPermissionTokens(selection));
                    await connection.CommandAsync(cancellationToken, args.ToArray());
                    created = true;
                    await PersistAclAsync(connection, cancellationToken);
                    await VerifyUserLoginAsync(tunnel.LocalPort, credential, selection.Username, selection.Password, cancellationToken);
                }
                catch
                {
                    if (created)
                    {
                        try
                        {
                            await connection.CommandAsync(CancellationToken.None, "ACL", "DELUSER", selection.Username);
                            await PersistAclAsync(connection, CancellationToken.None);
                        }
                        catch { }
                    }
                    throw;
                }
            }
        }

        public async Task UpdatePermissionsAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            RedisAclSelection selection,
            CancellationToken cancellationToken)
        {
            ValidateCredential(credential);
            ValidateSelection(selection, false);
            using (SshDatabaseTunnel tunnel = await SshDatabaseTunnel.OpenAsync(
                server, serverPassword, credential.Host, credential.Port, cancellationToken))
            using (RedisRespConnection connection = await OpenAuthenticatedAsync(tunnel.LocalPort, credential, cancellationToken))
            {
                await EnsureAclSupportedAsync(connection, cancellationToken);
                string original = await FindUserRuleAsync(connection, selection.Username, cancellationToken);
                if (string.IsNullOrWhiteSpace(original))
                    throw new InvalidOperationException("Redis ACL 用户不存在");
                try
                {
                    List<string> args = new List<string> { "ACL", "SETUSER", selection.Username, "on" };
                    args.AddRange(BuildPermissionTokens(selection));
                    await connection.CommandAsync(cancellationToken, args.ToArray());
                    await PersistAclAsync(connection, cancellationToken);
                    string updated = await FindUserRuleAsync(connection, selection.Username, cancellationToken);
                    if (string.IsNullOrWhiteSpace(updated))
                        throw new InvalidOperationException("修改后无法重新读取 Redis ACL 用户");
                }
                catch
                {
                    try
                    {
                        await RestoreUserRuleAsync(connection, original, CancellationToken.None);
                        await PersistAclAsync(connection, CancellationToken.None);
                    }
                    catch { }
                    throw;
                }
            }
        }

        public async Task ResetPasswordAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            string username,
            string newPassword,
            string oldPassword,
            CancellationToken cancellationToken)
        {
            ValidateCredential(credential);
            ValidateUserName(username);
            if (string.IsNullOrEmpty(newPassword))
                throw new InvalidOperationException("新密码不能为空");
            using (SshDatabaseTunnel tunnel = await SshDatabaseTunnel.OpenAsync(
                server, serverPassword, credential.Host, credential.Port, cancellationToken))
            using (RedisRespConnection connection = await OpenAuthenticatedAsync(tunnel.LocalPort, credential, cancellationToken))
            {
                await EnsureAclSupportedAsync(connection, cancellationToken);
                bool changed = false;
                try
                {
                    await connection.CommandAsync(cancellationToken, "ACL", "SETUSER", username, "resetpass", ">" + newPassword);
                    changed = true;
                    await PersistAclAsync(connection, cancellationToken);
                    await VerifyUserLoginAsync(tunnel.LocalPort, credential, username, newPassword, cancellationToken);
                }
                catch
                {
                    if (changed && !string.IsNullOrEmpty(oldPassword))
                    {
                        try
                        {
                            await connection.CommandAsync(CancellationToken.None, "ACL", "SETUSER", username, "resetpass", ">" + oldPassword);
                            await PersistAclAsync(connection, CancellationToken.None);
                        }
                        catch { }
                    }
                    throw;
                }
            }
        }

        public async Task DeleteUserAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            string username,
            CancellationToken cancellationToken)
        {
            ValidateCredential(credential);
            ValidateUserName(username);
            if (string.Equals(username, "default", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("不能删除 Redis default 用户");
            using (SshDatabaseTunnel tunnel = await SshDatabaseTunnel.OpenAsync(
                server, serverPassword, credential.Host, credential.Port, cancellationToken))
            using (RedisRespConnection connection = await OpenAuthenticatedAsync(tunnel.LocalPort, credential, cancellationToken))
            {
                await EnsureAclSupportedAsync(connection, cancellationToken);
                object removed = await connection.CommandAsync(cancellationToken, "ACL", "DELUSER", username);
                if (Convert.ToInt64(removed) != 1)
                    throw new InvalidOperationException("Redis ACL 用户不存在或未删除");
                await PersistAclAsync(connection, cancellationToken);
            }
        }

        private static async Task<RedisRespConnection> OpenAuthenticatedAsync(
            int localPort,
            DatabaseCredentialRecord credential,
            CancellationToken cancellationToken)
        {
            RedisRespConnection connection = await RedisRespConnection.ConnectAsync("127.0.0.1", localPort, cancellationToken);
            try
            {
                string username = string.IsNullOrWhiteSpace(credential.Username) ? "default" : credential.Username;
                if (string.Equals(username, "default", StringComparison.OrdinalIgnoreCase))
                    await connection.CommandAsync(cancellationToken, "AUTH", credential.Password ?? "");
                else
                    await connection.CommandAsync(cancellationToken, "AUTH", username, credential.Password ?? "");
                int database = ParseDatabaseIndex(credential.DatabaseName);
                if (database != 0)
                    await connection.CommandAsync(cancellationToken, "SELECT", database.ToString());
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        private static async Task VerifyUserLoginAsync(
            int localPort,
            DatabaseCredentialRecord adminCredential,
            string username,
            string password,
            CancellationToken cancellationToken)
        {
            DatabaseCredentialRecord userCredential = new DatabaseCredentialRecord
            {
                Host = adminCredential.Host,
                Port = adminCredential.Port,
                Username = username,
                Password = password,
                DatabaseName = adminCredential.DatabaseName
            };
            using (RedisRespConnection verify = await OpenAuthenticatedAsync(localPort, userCredential, cancellationToken))
            {
                string pong = Convert.ToString(await verify.CommandAsync(cancellationToken, "PING"));
                if (!string.Equals(pong, "PONG", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("新 Redis ACL 用户没有通过登录验证");
            }
        }

        private static async Task EnsureAclSupportedAsync(RedisRespConnection connection, CancellationToken cancellationToken)
        {
            try { await connection.CommandAsync(cancellationToken, "ACL", "WHOAMI"); }
            catch (RedisCommandException ex) when (IsUnknownCommand(ex.Message))
            {
                throw new InvalidOperationException("当前 Redis 版本不支持 ACL，需要 Redis 6.0 或更高版本");
            }
        }

        private static async Task PersistAclAsync(RedisRespConnection connection, CancellationToken cancellationToken)
        {
            try
            {
                await connection.CommandAsync(cancellationToken, "ACL", "SAVE");
                return;
            }
            catch (RedisCommandException)
            {
            }
            try
            {
                await connection.CommandAsync(cancellationToken, "CONFIG", "REWRITE");
            }
            catch (RedisCommandException ex)
            {
                throw new InvalidOperationException("Redis ACL 已在内存中修改，但无法写入配置文件：" + ex.Message);
            }
        }

        private static IEnumerable<string> BuildPermissionTokens(RedisAclSelection selection)
        {
            string pattern = NormalizeKeyPattern(selection.KeyPattern);
            List<string> tokens = new List<string> { "resetkeys", "~" + pattern, "resetchannels", "&*", "-@all", "+ping" };
            if (selection.AllCommands)
                tokens.Add("+@all");
            else
            {
                if (selection.Read) tokens.Add("+@read");
                if (selection.Write) tokens.Add("+@write");
                if (selection.Connection) tokens.Add("+@connection");
                if (selection.Transaction) tokens.Add("+@transaction");
                if (selection.PubSub) tokens.Add("+@pubsub");
                if (selection.Scripting) tokens.Add("+@scripting");
                if (selection.Admin) tokens.Add("+@admin");
            }
            return tokens;
        }

        private static RedisAclUserInfo ParseAclRule(string rule)
        {
            if (string.IsNullOrWhiteSpace(rule))
                return null;
            string[] tokens = rule.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2 || !string.Equals(tokens[0], "user", StringComparison.OrdinalIgnoreCase))
                return null;
            return new RedisAclUserInfo
            {
                Username = tokens[1],
                Enabled = tokens.Any(token => string.Equals(token, "on", StringComparison.OrdinalIgnoreCase)),
                NoPassword = tokens.Any(token => string.Equals(token, "nopass", StringComparison.OrdinalIgnoreCase)),
                KeyPatterns = string.Join(" ", tokens.Where(token => token.StartsWith("~", StringComparison.Ordinal))),
                CommandRules = string.Join(" ", tokens.Where(token => token.StartsWith("+", StringComparison.Ordinal) || token.StartsWith("-", StringComparison.Ordinal))),
                RawRule = rule
            };
        }

        private static async Task<string> FindUserRuleAsync(RedisRespConnection connection, string username, CancellationToken cancellationToken)
        {
            object response = await connection.CommandAsync(cancellationToken, "ACL", "LIST");
            List<object> rows = response as List<object> ?? new List<object>();
            return rows.Select(row => Convert.ToString(row)).FirstOrDefault(rule =>
                rule.StartsWith("user " + username + " ", StringComparison.Ordinal));
        }

        private static async Task RestoreUserRuleAsync(RedisRespConnection connection, string rule, CancellationToken cancellationToken)
        {
            string[] tokens = rule.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3 || tokens[0] != "user")
                throw new InvalidOperationException("无法识别原 Redis ACL 规则");
            List<string> args = new List<string> { "ACL", "SETUSER", tokens[1], "reset" };
            args.AddRange(tokens.Skip(2));
            await connection.CommandAsync(cancellationToken, args.ToArray());
        }

        private static void ValidateCredential(DatabaseCredentialRecord credential)
        {
            if (credential == null || credential.Port < 1 || credential.Port > 65535 || string.IsNullOrEmpty(credential.Password))
                throw new InvalidOperationException("Redis 凭据不完整");
            if (string.IsNullOrWhiteSpace(credential.Host)) credential.Host = "127.0.0.1";
            if (string.IsNullOrWhiteSpace(credential.Username)) credential.Username = "default";
            ValidateUserName(credential.Username);
        }

        private static void ValidateSelection(RedisAclSelection selection, bool requirePassword)
        {
            if (selection == null)
                throw new InvalidOperationException("Redis ACL 用户信息为空");
            ValidateUserName(selection.Username);
            NormalizeKeyPattern(selection.KeyPattern);
            if (requirePassword && string.IsNullOrEmpty(selection.Password))
                throw new InvalidOperationException("Redis ACL 用户密码不能为空");
        }

        private static void ValidateUserName(string username)
        {
            if (string.IsNullOrWhiteSpace(username) || !SafeUserName.IsMatch(username))
                throw new InvalidOperationException("Redis ACL 用户名只能包含字母、数字、点、下划线和短横线");
        }

        private static string NormalizeKeyPattern(string pattern)
        {
            string value = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim();
            if (value.StartsWith("~", StringComparison.Ordinal)) value = value.Substring(1);
            if (value.Length > 256 || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
                throw new InvalidOperationException("Redis Key 范围不能包含空格或控制字符");
            return value;
        }

        private static int ParseDatabaseIndex(string value)
        {
            int index;
            if (string.IsNullOrWhiteSpace(value)) return 0;
            if (!int.TryParse(value, out index) || index < 0 || index > 1024)
                throw new InvalidOperationException("Redis 数据库编号无效");
            return index;
        }

        private static string ReadInfoValue(string info, string key)
        {
            foreach (string line in (info ?? "").Split('\n'))
            {
                string value = line.Trim();
                if (value.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                    return value.Substring(key.Length + 1).Trim();
            }
            return "未知";
        }

        private static bool IsUnknownCommand(string message)
        {
            return (message ?? "").IndexOf("unknown command", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    internal sealed class RedisRespConnection : IDisposable
    {
        private readonly TcpClient client;
        private readonly NetworkStream stream;

        private RedisRespConnection(TcpClient client)
        {
            this.client = client;
            stream = client.GetStream();
        }

        public static async Task<RedisRespConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            TcpClient client = new TcpClient();
            try
            {
                using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await client.ConnectAsync(host, port, timeout.Token);
                }
                return new RedisRespConnection(client);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public async Task<object> CommandAsync(CancellationToken cancellationToken, params string[] args)
        {
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                byte[] payload = EncodeCommand(args);
                await stream.WriteAsync(payload.AsMemory(0, payload.Length), timeout.Token);
                await stream.FlushAsync(timeout.Token);
                return await ReadObjectAsync(timeout.Token);
            }
        }

        private async Task<object> ReadObjectAsync(CancellationToken cancellationToken)
        {
            int prefix = await ReadByteAsync(cancellationToken);
            switch (prefix)
            {
                case '+': return await ReadLineAsync(cancellationToken);
                case '-': throw new RedisCommandException(await ReadLineAsync(cancellationToken));
                case ':': return long.Parse(await ReadLineAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
                case '$':
                    int length = int.Parse(await ReadLineAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
                    if (length < 0) return null;
                    byte[] data = await ReadExactAsync(length, cancellationToken);
                    await ExpectCrlfAsync(cancellationToken);
                    return Encoding.UTF8.GetString(data);
                case '*':
                    int count = int.Parse(await ReadLineAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
                    if (count < 0) return null;
                    List<object> items = new List<object>(count);
                    for (int index = 0; index < count; index++)
                        items.Add(await ReadObjectAsync(cancellationToken));
                    return items;
                default:
                    throw new InvalidOperationException("Redis 返回了无法识别的 RESP 数据");
            }
        }

        private async Task<int> ReadByteAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[1];
            int read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
            if (read != 1) throw new EndOfStreamException("Redis 连接意外关闭");
            return buffer[0];
        }

        private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
        {
            using (MemoryStream output = new MemoryStream())
            {
                int previous = -1;
                while (true)
                {
                    int current = await ReadByteAsync(cancellationToken);
                    if (previous == '\r' && current == '\n')
                    {
                        byte[] data = output.ToArray();
                        return Encoding.UTF8.GetString(data, 0, Math.Max(0, data.Length - 1));
                    }
                    output.WriteByte((byte)current);
                    previous = current;
                    if (output.Length > 16 * 1024 * 1024)
                        throw new InvalidOperationException("Redis 返回行过长");
                }
            }
        }

        private async Task<byte[]> ReadExactAsync(int length, CancellationToken cancellationToken)
        {
            byte[] data = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = await stream.ReadAsync(data.AsMemory(offset, length - offset), cancellationToken);
                if (read <= 0) throw new EndOfStreamException("Redis 连接意外关闭");
                offset += read;
            }
            return data;
        }

        private async Task ExpectCrlfAsync(CancellationToken cancellationToken)
        {
            int cr = await ReadByteAsync(cancellationToken);
            int lf = await ReadByteAsync(cancellationToken);
            if (cr != '\r' || lf != '\n') throw new InvalidOperationException("Redis RESP 数据格式错误");
        }

        private static byte[] EncodeCommand(IEnumerable<string> args)
        {
            List<string> values = args == null ? new List<string>() : args.ToList();
            using (MemoryStream output = new MemoryStream())
            {
                WriteAscii(output, "*" + values.Count + "\r\n");
                foreach (string value in values)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
                    WriteAscii(output, "$" + bytes.Length + "\r\n");
                    output.Write(bytes, 0, bytes.Length);
                    WriteAscii(output, "\r\n");
                }
                return output.ToArray();
            }
        }

        private static void WriteAscii(Stream stream, string value)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
        }

        public void Dispose()
        {
            stream.Dispose();
            client.Dispose();
        }
    }

    internal sealed class RedisCommandException : Exception
    {
        public RedisCommandException(string message) : base(message) { }
    }
}
