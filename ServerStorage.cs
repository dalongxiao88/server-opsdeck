using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace RDPManager
{
    public static class ServerStorage
    {
        public static List<Server> Load(string filePath, out bool migratedSecrets)
        {
            migratedSecrets = false;
            List<Server> servers = new List<Server>();
            if (!File.Exists(filePath))
                return servers;

            XmlSerializer serializer = new XmlSerializer(typeof(LegacyServerFile));
            using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                LegacyServerFile file = (LegacyServerFile)serializer.Deserialize(stream);
                List<StoredServer> records = file == null ? new List<StoredServer>() : file.Servers;
                foreach (StoredServer record in records ?? new List<StoredServer>())
                {
                    Server server = new Server
                    {
                        Name = record.Name,
                        IP = record.IP,
                        Port = record.Port,
                        Username = record.Username,
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
                        ServicePorts = record.ServicePorts,
                        DatabaseCredentials = record.DatabaseCredentials
                    };
                    server.EnsureDefaults();

                    if (!string.IsNullOrEmpty(record.Password))
                    {
                        CredentialStore.Write(CredentialStore.GetServerTarget(server.CredentialId), server.Username, record.Password);
                        migratedSecrets = true;
                    }
                    servers.Add(server);
                }
            }
            return servers;
        }

        public static void Save(string filePath, IEnumerable<Server> servers)
        {
            List<StoredServer> records = new List<StoredServer>();
            foreach (Server server in servers)
            {
                server.EnsureDefaults();
                records.Add(new StoredServer
                {
                    Name = server.Name,
                    IP = server.IP,
                    Port = server.Port,
                    Username = server.Username,
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
                    ServicePorts = server.ServicePorts
                });
            }

            XmlSerializer serializer = new XmlSerializer(typeof(List<Server>));
            using (FileStream stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                serializer.Serialize(stream, ConvertToServers(records));
        }

        private static List<Server> ConvertToServers(List<StoredServer> records)
        {
            List<Server> servers = new List<Server>();
            foreach (StoredServer record in records)
            {
                Server server = new Server
                {
                    Name = record.Name,
                    IP = record.IP,
                    Port = record.Port,
                    Username = record.Username,
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
                    ServicePorts = record.ServicePorts,
                    DatabaseCredentials = record.DatabaseCredentials
                };
                server.EnsureDefaults();
                servers.Add(server);
            }
            return servers;
        }

        [XmlRoot("ArrayOfServer")]
        public class LegacyServerFile
        {
            [XmlElement("Server")]
            public List<StoredServer> Servers { get; set; } = new List<StoredServer>();
        }

        [Serializable]
        public class StoredServer
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
            public List<ServicePortRecord> ServicePorts { get; set; }
            public List<DatabaseCredentialRecord> DatabaseCredentials { get; set; }
        }
    }
}
