using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace RDPManager
{
    public sealed class MongoConnectionTestResult
    {
        public string ServerVersion { get; set; }
        public string UserName { get; set; }
        public string AuthenticationDatabase { get; set; }
    }

    public sealed class MongoUserInfo
    {
        public string UserName { get; set; }
        public string AuthenticationDatabase { get; set; }
        public string Roles { get; set; }
        public bool IsSaved { get; set; }
    }

    public sealed class MongoUserRequest
    {
        public string UserName { get; set; }
        public string Password { get; set; }
        public string AuthenticationDatabase { get; set; }
        public string DatabaseName { get; set; }
        public List<string> Roles { get; set; } = new List<string>();
    }

    public sealed class MongoDatabaseService
    {
        public async Task<MongoConnectionTestResult> TestConnectionAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            CancellationToken cancellationToken)
        {
            ValidateCredential(credential);
            using (SshDatabaseTunnel tunnel = await SshDatabaseTunnel.OpenAsync(server, serverPassword, credential.Host, credential.Port, cancellationToken))
            {
                MongoClient client = CreateClient(tunnel.LocalPort, credential);
                IMongoDatabase admin = client.GetDatabase(string.IsNullOrWhiteSpace(credential.AuthenticationDatabase) ? "admin" : credential.AuthenticationDatabase);
                BsonDocument buildInfo = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1), cancellationToken: cancellationToken);
                string version = buildInfo.GetValue("version", "未知").AsString;
                BsonDocument connectionStatus = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("connectionStatus", 1), cancellationToken: cancellationToken);
                string authenticatedUser = credential.Username;
                BsonValue authInfo;
                if (connectionStatus.TryGetValue("authInfo", out authInfo) && authInfo.IsBsonDocument)
                {
                    BsonValue authenticatedUsers;
                    if (authInfo.AsBsonDocument.TryGetValue("authenticatedUsers", out authenticatedUsers) && authenticatedUsers.IsBsonArray && authenticatedUsers.AsBsonArray.Count > 0)
                        authenticatedUser = authenticatedUsers.AsBsonArray[0].AsBsonDocument.GetValue("user", credential.Username).AsString;
                }
                return new MongoConnectionTestResult
                {
                    ServerVersion = version,
                    UserName = authenticatedUser,
                    AuthenticationDatabase = string.IsNullOrWhiteSpace(credential.AuthenticationDatabase) ? "admin" : credential.AuthenticationDatabase
                };
            }
        }

        public async Task<IList<MongoUserInfo>> ListUsersAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            CancellationToken cancellationToken)
        {
            ValidateCredential(credential);
            using (SshDatabaseTunnel tunnel = await SshDatabaseTunnel.OpenAsync(server, serverPassword, credential.Host, credential.Port, cancellationToken))
            {
                MongoClient client = CreateClient(tunnel.LocalPort, credential);
                IMongoDatabase admin = client.GetDatabase("admin");
                BsonDocument result = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("usersInfo", 1), cancellationToken: cancellationToken);
                BsonArray users = result.GetValue("users", new BsonArray()).AsBsonArray;
                List<MongoUserInfo> output = new List<MongoUserInfo>();
                foreach (BsonValue value in users)
                {
                    BsonDocument user = value.AsBsonDocument;
                    string username = user.GetValue("user", "").AsString;
                    string authDb = user.GetValue("db", "admin").AsString;
                    BsonArray roles = user.GetValue("roles", new BsonArray()).AsBsonArray;
                    output.Add(new MongoUserInfo
                    {
                        UserName = username,
                        AuthenticationDatabase = authDb,
                        Roles = string.Join("、", roles.Select(role =>
                        {
                            BsonDocument document = role.AsBsonDocument;
                            return document.GetValue("role", "").AsString + "@" + document.GetValue("db", "").AsString;
                        }))
                    });
                }
                return output;
            }
        }

        public async Task<IList<string>> ListDatabasesAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            CancellationToken cancellationToken)
        {
            ValidateCredential(credential);
            using (SshDatabaseTunnel tunnel = await SshDatabaseTunnel.OpenAsync(server, serverPassword, credential.Host, credential.Port, cancellationToken))
            {
                MongoClient client = CreateClient(tunnel.LocalPort, credential);
                IMongoDatabase admin = client.GetDatabase("admin");
                BsonDocument result = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("listDatabases", 1), cancellationToken: cancellationToken);
                BsonArray databases = result.GetValue("databases", new BsonArray()).AsBsonArray;
                return databases.Select(value => value.AsBsonDocument.GetValue("name", "").AsString)
                    .Where(name => !string.Equals(name, "admin", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(name, "local", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(name, "config", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        public async Task CreateUserAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            MongoUserRequest request,
            CancellationToken cancellationToken)
        {
            ValidateCredential(credential);
            ValidateRequest(request);
            using (SshDatabaseTunnel tunnel = await SshDatabaseTunnel.OpenAsync(server, serverPassword, credential.Host, credential.Port, cancellationToken))
            {
                MongoClient client = CreateClient(tunnel.LocalPort, credential);
                IMongoDatabase database = client.GetDatabase(request.AuthenticationDatabase);
                List<BsonDocument> roles = request.Roles.Select(role =>
                {
                    string[] parts = role.Split(new[] { '@' }, 2);
                    return new BsonDocument { { "role", parts[0] }, { "db", parts.Length == 2 ? parts[1] : request.DatabaseName } };
                }).ToList();
                BsonDocument command = new BsonDocument
                {
                    { "createUser", request.UserName },
                    { "pwd", request.Password },
                    { "roles", new BsonArray(roles) }
                };
                await database.RunCommandAsync<BsonDocument>(command, cancellationToken: cancellationToken);
            }
        }

        public async Task UpdateRolesAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            MongoUserRequest request,
            CancellationToken cancellationToken)
        {
            ValidateCredential(credential);
            ValidateRequest(request, false);
            using (SshDatabaseTunnel tunnel = await SshDatabaseTunnel.OpenAsync(server, serverPassword, credential.Host, credential.Port, cancellationToken))
            {
                MongoClient client = CreateClient(tunnel.LocalPort, credential);
                IMongoDatabase database = client.GetDatabase(request.AuthenticationDatabase);
                List<BsonDocument> roles = request.Roles.Select(role =>
                {
                    string[] parts = role.Split(new[] { '@' }, 2);
                    return new BsonDocument { { "role", parts[0] }, { "db", parts.Length == 2 ? parts[1] : request.DatabaseName } };
                }).ToList();
                await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "updateUser", request.UserName }, { "roles", new BsonArray(roles) } }, cancellationToken: cancellationToken);
            }
        }

        public async Task ResetPasswordAsync(
            Server server,
            string serverPassword,
            DatabaseCredentialRecord credential,
            string username,
            string authenticationDatabase,
            string newPassword,
            CancellationToken cancellationToken)
        {
            ValidateCredential(credential);
            ValidateName(username);
            if (string.IsNullOrEmpty(newPassword)) throw new InvalidOperationException("新密码不能为空");
            using (SshDatabaseTunnel tunnel = await SshDatabaseTunnel.OpenAsync(server, serverPassword, credential.Host, credential.Port, cancellationToken))
            {
                MongoClient client = CreateClient(tunnel.LocalPort, credential);
                await client.GetDatabase(authenticationDatabase).RunCommandAsync<BsonDocument>(new BsonDocument { { "updateUser", username }, { "pwd", newPassword } }, cancellationToken: cancellationToken);
                DatabaseCredentialRecord verify = new DatabaseCredentialRecord { Host = credential.Host, Port = credential.Port, Username = username, Password = newPassword, AuthenticationDatabase = authenticationDatabase };
                MongoClient verifyClient = CreateClient(tunnel.LocalPort, verify);
                await verifyClient.GetDatabase(authenticationDatabase).RunCommandAsync<BsonDocument>(new BsonDocument("connectionStatus", 1), cancellationToken: cancellationToken);
            }
        }

        public async Task DeleteUserAsync(Server server, string serverPassword, DatabaseCredentialRecord credential, string username, string authenticationDatabase, CancellationToken cancellationToken)
        {
            ValidateCredential(credential);
            ValidateName(username);
            if (string.Equals(username, credential.Username, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("不能删除当前管理账号");
            using (SshDatabaseTunnel tunnel = await SshDatabaseTunnel.OpenAsync(server, serverPassword, credential.Host, credential.Port, cancellationToken))
            {
                MongoClient client = CreateClient(tunnel.LocalPort, credential);
                await client.GetDatabase(authenticationDatabase).RunCommandAsync<BsonDocument>(new BsonDocument { { "dropUser", username } }, cancellationToken: cancellationToken);
            }
        }

        private static MongoClient CreateClient(int localPort, DatabaseCredentialRecord credential)
        {
            string authDb = string.IsNullOrWhiteSpace(credential.AuthenticationDatabase) ? "admin" : credential.AuthenticationDatabase;
            MongoClientSettings settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:" + localPort + "/" + Uri.EscapeDataString(authDb));
            settings.Credential = MongoCredential.CreateCredential(authDb, credential.Username, credential.Password);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(15);
            settings.ConnectTimeout = TimeSpan.FromSeconds(10);
            return new MongoClient(settings);
        }

        private static void ValidateCredential(DatabaseCredentialRecord credential)
        {
            if (credential == null || credential.Port < 1 || credential.Port > 65535 || string.IsNullOrWhiteSpace(credential.Username) || string.IsNullOrEmpty(credential.Password))
                throw new InvalidOperationException("MongoDB 凭据不完整");
            if (string.IsNullOrWhiteSpace(credential.Host)) credential.Host = "127.0.0.1";
            ValidateName(credential.Username);
        }
        private static void ValidateRequest(MongoUserRequest request, bool requirePassword = true)
        {
            if (request == null) throw new InvalidOperationException("MongoDB 用户信息为空");
            ValidateName(request.UserName);
            if (string.IsNullOrWhiteSpace(request.AuthenticationDatabase)) request.AuthenticationDatabase = "admin";
            if (string.IsNullOrWhiteSpace(request.DatabaseName)) request.DatabaseName = request.AuthenticationDatabase;
            ValidateName(request.AuthenticationDatabase); ValidateName(request.DatabaseName);
            if (requirePassword && string.IsNullOrEmpty(request.Password)) throw new InvalidOperationException("MongoDB 用户密码不能为空");
        }
        private static void ValidateName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.'))) throw new InvalidOperationException("MongoDB 名称格式无效");
        }
    }
}
