using System;
using System.Collections.Generic;

namespace RDPManager
{
    public enum StorageMode
    {
        None,
        PlainXml,
        EncryptedVault,
        Conflict
    }

    public sealed class StartupSession
    {
        public StorageMode Mode { get; set; }
        public List<Server> Servers { get; set; } = new List<Server>();
        public string AdminPasswordHash { get; set; }
        public byte[] VaultKey { get; set; }
        public byte[] VaultSalt { get; set; }
    }

    public static class StorageModePaths
    {
        public const string PlainFileName = "servers.xml";
        public const string VaultFileName = "servers.vault";

        public static string GetPlainPath(string baseDirectory)
        {
            return System.IO.Path.Combine(baseDirectory, PlainFileName);
        }

        public static string GetVaultPath(string baseDirectory)
        {
            return System.IO.Path.Combine(baseDirectory, VaultFileName);
        }

        public static StorageMode Detect(string baseDirectory)
        {
            bool plain = System.IO.File.Exists(GetPlainPath(baseDirectory));
            bool vault = System.IO.File.Exists(GetVaultPath(baseDirectory));
            if (plain && vault)
                return StorageMode.Conflict;
            if (vault)
                return StorageMode.EncryptedVault;
            if (plain)
                return StorageMode.PlainXml;
            return StorageMode.None;
        }
    }
}
