using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace RDPManager
{
    public static class PlainServerStorage
    {
        public static List<Server> Load(string filePath, out string adminPasswordHash)
        {
            adminPasswordHash = null;
            XmlSerializer serializer = new XmlSerializer(typeof(PlainStoreFile));
            using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                PlainStoreFile file = (PlainStoreFile)serializer.Deserialize(stream);
                adminPasswordHash = file == null ? null : file.AdminPasswordHash;
                List<Server> servers = new List<Server>();
                foreach (PlainStoredServer record in file == null || file.Servers == null ? new List<PlainStoredServer>() : file.Servers)
                    servers.Add(ToServer(record));
                return servers;
            }
        }

        public static void Save(string filePath, IEnumerable<Server> servers, string adminPasswordHash)
        {
            PlainStoreFile file = new PlainStoreFile
            {
                AdminPasswordHash = adminPasswordHash,
                Servers = new List<PlainStoredServer>()
            };
            foreach (Server server in servers)
            {
                server.EnsureDefaults();
                file.Servers.Add(FromServer(server));
            }

            string temporaryPath = filePath + ".tmp-" + Guid.NewGuid().ToString("N");
            XmlSerializer serializer = new XmlSerializer(typeof(PlainStoreFile));
            try
            {
                using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    serializer.Serialize(stream, file);
                ReplaceAtomically(temporaryPath, filePath);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        private static void ReplaceAtomically(string temporaryPath, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                try
                {
                    File.Replace(temporaryPath, destinationPath, null, true);
                    return;
                }
                catch (PlatformNotSupportedException) { }
                catch (IOException) { }
            }
            File.Move(temporaryPath, destinationPath, true);
        }

        private static PlainStoredServer FromServer(Server server)
        {
            return new PlainStoredServer
            {
                Name = server.Name,
                IP = server.IP,
                Port = server.Port,
                Username = server.Username,
                Password = server.Password,
                Remark = server.Remark,
                Group = server.Group,
                Provider = server.Provider,
                ProviderUrl = server.ProviderUrl,
                PurchaseDate = server.PurchaseDate,
                ExpireDate = server.ExpireDate,
                Type = server.Type,
                CredentialId = server.CredentialId,
                ManagementType = server.ManagementType,
                ManagementPort = server.ManagementPort,
                SshCredentialMode = server.SshCredentialMode,
                SshPrivateKeyPath = server.SshPrivateKeyPath,
                ServicePorts = server.ServicePorts,
                DatabaseCredentials = server.DatabaseCredentials
            };
        }

        private static Server ToServer(PlainStoredServer record)
        {
            Server server = new Server
            {
                Name = record.Name,
                IP = record.IP,
                Port = record.Port,
                Username = record.Username,
                Password = record.Password,
                Remark = record.Remark,
                Group = record.Group,
                Provider = record.Provider,
                ProviderUrl = record.ProviderUrl,
                PurchaseDate = record.PurchaseDate,
                ExpireDate = record.ExpireDate,
                Type = record.Type,
                CredentialId = record.CredentialId,
                ManagementType = record.ManagementType,
                ManagementPort = record.ManagementPort,
                SshCredentialMode = record.SshCredentialMode,
                SshPrivateKeyPath = record.SshPrivateKeyPath,
                ServicePorts = record.ServicePorts,
                DatabaseCredentials = record.DatabaseCredentials
            };
            server.EnsureDefaults();
            return server;
        }

        [XmlRoot("XiaoBaiServerStore")]
        public sealed class PlainStoreFile
        {
            public string AdminPasswordHash { get; set; }
            [XmlArray("Servers")]
            [XmlArrayItem("Server")]
            public List<PlainStoredServer> Servers { get; set; } = new List<PlainStoredServer>();
        }

        public sealed class PlainStoredServer
        {
            public string Name { get; set; }
            public string IP { get; set; }
            public string Port { get; set; }
            public string Username { get; set; }
            public string Password { get; set; }
            public string Remark { get; set; }
            public string Group { get; set; }
            public string Provider { get; set; }
            public string ProviderUrl { get; set; }
            public DateTime PurchaseDate { get; set; }
            public DateTime ExpireDate { get; set; }
            public ServerType Type { get; set; }
            public string CredentialId { get; set; }
            public RemoteManagementType ManagementType { get; set; }
            public string ManagementPort { get; set; }
            public SshCredentialMode SshCredentialMode { get; set; }
            public string SshPrivateKeyPath { get; set; }
            public List<ServicePortRecord> ServicePorts { get; set; }
            public List<DatabaseCredentialRecord> DatabaseCredentials { get; set; }
        }
    }
}
