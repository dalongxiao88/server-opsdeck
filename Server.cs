using System;
using System.Collections.Generic;
using System.Drawing;
using System.Xml.Serialization;

namespace RDPManager
{
    [Serializable]
    public enum ServerType
    {
        Windows,
        Linux
    }

    [Serializable]
    public enum RemoteManagementType
    {
        Automatic,
        SSH,
        WinRM
    }

    [Serializable]
    public class ServicePortRecord
    {
        public string ServiceType { get; set; }
        public string ServiceName { get; set; }
        public int Port { get; set; }
        public string Protocol { get; set; }
        public string ConfigPath { get; set; }
        public string TargetKey { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    [Serializable]
    public class DatabaseUserRecord
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public string HostPattern { get; set; }
        public string DatabaseName { get; set; }
        public string PermissionSummary { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastVerifiedAt { get; set; }
        public bool IsVerified { get; set; }
    }

    [Serializable]
    public class DatabaseCredentialRecord
    {
        public string Id { get; set; }
        public string DatabaseType { get; set; }
        public string ServiceName { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string AuthenticationDatabase { get; set; }
        public string DatabaseName { get; set; }
        public DateTime LastVerifiedAt { get; set; }
        public bool IsVerified { get; set; }
        public bool IsManagerDeployed { get; set; }
        public string InstalledVersion { get; set; }
        public string InstallPath { get; set; }
        public DateTime DeployedAt { get; set; }
        public List<DatabaseUserRecord> Users { get; set; }

        public DatabaseCredentialRecord()
        {
            Id = Guid.NewGuid().ToString("N");
            Host = "127.0.0.1";
            Users = new List<DatabaseUserRecord>();
        }
    }

    [Serializable]
    public class Server
    {
        public string Name { get; set; }
        public string IP { get; set; }
        public string Port { get; set; }
        public string Username { get; set; }
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

        [XmlIgnore]
        public string Password { get; set; }

        public Server()
        {
            Type = ServerType.Windows;
            Port = "3389";
            Username = "Administrator";
            Group = "未分组";
            Provider = "阿里云";
            CredentialId = Guid.NewGuid().ToString("N");
            ManagementType = RemoteManagementType.Automatic;
            ManagementPort = "22";
            ServicePorts = new List<ServicePortRecord>();
            DatabaseCredentials = new List<DatabaseCredentialRecord>();
        }

        public Server(Server source)
            : this()
        {
            if (source == null)
                return;

            Name = source.Name;
            IP = source.IP;
            Port = source.Port;
            Username = source.Username;
            Remark = source.Remark;
            Group = string.IsNullOrWhiteSpace(source.Group) ? "未分组" : source.Group;
            Provider = source.Provider;
            ProviderUrl = source.ProviderUrl;
            PurchaseDate = source.PurchaseDate;
            ExpireDate = source.ExpireDate;
            Type = source.Type;
            CredentialId = string.IsNullOrWhiteSpace(source.CredentialId) ? CredentialId : source.CredentialId;
            ManagementType = source.ManagementType;
            ManagementPort = source.ManagementPort;
            ServicePorts = source.ServicePorts == null
                ? new List<ServicePortRecord>()
                : source.ServicePorts.ConvertAll(item => new ServicePortRecord
                {
                    ServiceType = item.ServiceType,
                    ServiceName = item.ServiceName,
                    Port = item.Port,
                    Protocol = item.Protocol,
                    ConfigPath = item.ConfigPath,
                    TargetKey = item.TargetKey,
                    UpdatedAt = item.UpdatedAt
                });
            DatabaseCredentials = source.DatabaseCredentials == null
                ? new List<DatabaseCredentialRecord>()
                : source.DatabaseCredentials.ConvertAll(item => new DatabaseCredentialRecord
                {
                    Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
                    DatabaseType = item.DatabaseType,
                    ServiceName = item.ServiceName,
                    Host = item.Host,
                    Port = item.Port,
                    Username = item.Username,
                    Password = item.Password,
                    AuthenticationDatabase = item.AuthenticationDatabase,
                    DatabaseName = item.DatabaseName,
                    LastVerifiedAt = item.LastVerifiedAt,
                    IsVerified = item.IsVerified,
                    IsManagerDeployed = item.IsManagerDeployed,
                    InstalledVersion = item.InstalledVersion,
                    InstallPath = item.InstallPath,
                    DeployedAt = item.DeployedAt,
                    Users = item.Users == null ? new List<DatabaseUserRecord>() : item.Users.ConvertAll(user => new DatabaseUserRecord
                    {
                        Username = user.Username,
                        Password = user.Password,
                        HostPattern = user.HostPattern,
                        DatabaseName = user.DatabaseName,
                        PermissionSummary = user.PermissionSummary,
                        CreatedAt = user.CreatedAt,
                        LastVerifiedAt = user.LastVerifiedAt,
                        IsVerified = user.IsVerified
                    })
                });
            Password = source.Password;
        }

        public void EnsureDefaults()
        {
            if (ManagementType != RemoteManagementType.Automatic &&
                ManagementType != RemoteManagementType.SSH &&
                ManagementType != RemoteManagementType.WinRM)
                ManagementType = RemoteManagementType.Automatic;
            if (string.IsNullOrWhiteSpace(Group))
                Group = "未分组";
            if (string.IsNullOrWhiteSpace(CredentialId))
                CredentialId = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(Port))
                Port = GetDefaultPort();
            if (string.IsNullOrWhiteSpace(Username))
                Username = GetDefaultUsername();
            if (string.IsNullOrWhiteSpace(ManagementPort))
                ManagementPort = Type == ServerType.Linux ? Port : "22";
            if (ServicePorts == null)
                ServicePorts = new List<ServicePortRecord>();
            if (DatabaseCredentials == null)
                DatabaseCredentials = new List<DatabaseCredentialRecord>();
            foreach (DatabaseCredentialRecord credential in DatabaseCredentials)
            {
                if (string.IsNullOrWhiteSpace(credential.Id))
                    credential.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(credential.Host))
                    credential.Host = "127.0.0.1";
                if (credential.Users == null)
                    credential.Users = new List<DatabaseUserRecord>();
            }
        }

        public string GetDefaultPort()
        {
            return Type == ServerType.Windows ? "3389" : "22";
        }

        public string GetDefaultUsername()
        {
            return Type == ServerType.Windows ? "Administrator" : "root";
        }

        public string GetTypeDisplayName()
        {
            return Type == ServerType.Windows ? "Windows / RDP" : "Linux / SSH";
        }

        public string GetMaskedIP()
        {
            if (string.IsNullOrWhiteSpace(IP))
                return "***.***.***.***";

            return "***.***.***.***";
        }

        public string GetExpireInfo()
        {
            if (ExpireDate == DateTime.MinValue)
                return "未设置";

            int days = (int)(ExpireDate.Date - DateTime.Now.Date).TotalDays;
            if (days < 0)
                return "已过期";
            if (days == 0)
                return "今天到期";
            if (days <= 30)
                return days + "天";

            int months = days / 30;
            int remainingDays = days % 30;
            return remainingDays == 0 ? months + "个月" : string.Format("{0}个月{1}天", months, remainingDays);
        }

        public Color GetExpireColor()
        {
            if (ExpireDate == DateTime.MinValue)
                return Color.FromArgb(120, 128, 136);

            int days = (int)(ExpireDate.Date - DateTime.Now.Date).TotalDays;
            if (days < 0)
                return Color.FromArgb(184, 64, 64);
            if (days <= 7)
                return Color.FromArgb(214, 106, 36);
            if (days <= 30)
                return Color.FromArgb(184, 122, 24);
            return Color.FromArgb(42, 139, 92);
        }

        public static string GetProviderUrl(string provider)
        {
            switch (provider)
            {
                case "阿里云": return "https://www.aliyun.com/";
                case "腾讯云": return "https://cloud.tencent.com/";
                case "华为云": return "https://www.huaweicloud.com/";
                case "AWS": return "https://aws.amazon.com/";
                case "Azure": return "https://azure.microsoft.com/";
                case "Google Cloud": return "https://cloud.google.com/";
                case "Vultr": return "https://www.vultr.com/";
                case "DigitalOcean": return "https://www.digitalocean.com/";
                case "Linode": return "https://www.linode.com/";
                case "搬瓦工": return "https://bandwagonhost.com/";
                case "RackNerd": return "https://www.racknerd.com/";
                case "Hostwinds": return "https://www.hostwinds.com/";
                default: return "";
            }
        }
    }
}
