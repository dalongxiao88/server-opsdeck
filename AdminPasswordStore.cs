using System;
using System.IO;

namespace RDPManager
{
    public static class AdminPasswordStore
    {
        public const string DefaultPassword = "admin";

        public static string LoadHash(string legacyFilePath, out bool firstRun)
        {
            firstRun = false;
            string stored;
            if (CredentialStore.TryRead(CredentialStore.AdminTarget, out stored) && PasswordSecurity.IsHash(stored))
            {
                DeleteLegacyFile(legacyFilePath);
                return stored;
            }

            if (File.Exists(legacyFilePath))
            {
                string legacy = File.ReadAllText(legacyFilePath).Trim();
                if (!string.IsNullOrEmpty(legacy))
                {
                    string hash = PasswordSecurity.IsHash(legacy) ? legacy : PasswordSecurity.Hash(legacy);
                    CredentialStore.Write(CredentialStore.AdminTarget, "local-admin", hash);
                    DeleteLegacyFile(legacyFilePath);
                    return hash;
                }
            }

            firstRun = true;
            return null;
        }

        public static string SetPassword(string password, string legacyFilePath)
        {
            string hash = PasswordSecurity.Hash(password ?? "");
            CredentialStore.Write(CredentialStore.AdminTarget, "local-admin", hash);
            DeleteLegacyFile(legacyFilePath);
            return hash;
        }

        public static string SetPasswordFromHash(string hash, string legacyFilePath)
        {
            if (!PasswordSecurity.IsHash(hash))
                throw new InvalidOperationException("管理密码哈希无效");
            CredentialStore.Write(CredentialStore.AdminTarget, "local-admin", hash);
            DeleteLegacyFile(legacyFilePath);
            return hash;
        }

        public static string SetDefault(string legacyFilePath)
        {
            return SetPassword(DefaultPassword, legacyFilePath);
        }

        private static void DeleteLegacyFile(string filePath)
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }
}
