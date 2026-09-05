using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace RDPManager
{
    public static class StartupSecurity
    {
        public static bool TryUnlock(string baseDirectory, out StartupSession session)
        {
            session = null;
            try
            {
                StorageMode mode = StorageModePaths.Detect(baseDirectory);
                if (mode == StorageMode.Conflict)
                {
                    DialogResult reset = MessageBox.Show(
                        "同时发现 servers.xml 和 servers.vault，无法安全判断活动存储模式。\n\n如需继续使用，请选择“是”彻底重置软件；两份文件和全部服务器资料都会删除。",
                        "存储文件冲突",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);
                    if (reset != DialogResult.Yes)
                        return false;
                    ResetAllCredentials(baseDirectory);
                    return RunFirstUse(baseDirectory, out session);
                }
                if (mode == StorageMode.EncryptedVault)
                    return UnlockVault(baseDirectory, out session);
                if (mode == StorageMode.PlainXml && IsNewPlainStore(StorageModePaths.GetPlainPath(baseDirectory)))
                    return UnlockPlain(baseDirectory, out session);
                return UnlockLegacyOrFirstUse(baseDirectory, out session);
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动安全检查失败：" + ex.Message, "无法启动", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        public static void ResetAllCredentials(string baseDirectory)
        {
            List<string> ips = new List<string>();
            string plainPath = StorageModePaths.GetPlainPath(baseDirectory);
            if (File.Exists(plainPath))
            {
                try
                {
                    string hash;
                    foreach (Server server in PlainServerStorage.Load(plainPath, out hash))
                        AddIp(ips, server.IP);
                }
                catch { }

                try
                {
                    bool migrated;
                    foreach (Server server in ServerStorage.Load(plainPath, out migrated))
                        AddIp(ips, server.IP);
                }
                catch { }
            }

            CredentialStore.DeleteAllServerCredentials();
            CredentialStore.Delete(CredentialStore.AdminTarget);
            foreach (string ip in ips)
                ClearRdpCredential(ip);
            ClearAllRdpCredentials();

            DeleteIfExists(plainPath);
            DeleteIfExists(StorageModePaths.GetVaultPath(baseDirectory));
            DeleteIfExists(Path.Combine(baseDirectory, "password.dat"));
        }

        private static bool UnlockPlain(string baseDirectory, out StartupSession session)
        {
            session = null;
            string hash;
            List<Server> servers = PlainServerStorage.Load(StorageModePaths.GetPlainPath(baseDirectory), out hash);
            if (!PasswordSecurity.IsHash(hash))
                return UnlockLegacyOrFirstUse(baseDirectory, out session);

            using (StartupPasswordForm unlock = StartupPasswordForm.CreateUnlock(hash))
            {
                if (unlock.ShowDialog() != DialogResult.OK)
                    return false;
                if (unlock.ResetRequested)
                {
                    ResetAllCredentials(baseDirectory);
                    return RunFirstUse(baseDirectory, out session);
                }

                session = new StartupSession
                {
                    Mode = StorageMode.PlainXml,
                    Servers = servers,
                    AdminPasswordHash = hash
                };
                return true;
            }
        }

        private static bool UnlockVault(string baseDirectory, out StartupSession session)
        {
            session = null;
            string vaultPath = StorageModePaths.GetVaultPath(baseDirectory);
            List<Server> loaded = null;
            byte[] key = null;
            byte[] salt = null;
            using (StartupPasswordForm unlock = StartupPasswordForm.CreateUnlock(password =>
            {
                try
                {
                    if (key != null)
                        CryptographicOperations.ZeroMemory(key);
                    loaded = VaultStore.Load(vaultPath, password, out key, out salt);
                    return true;
                }
                catch
                {
                    loaded = null;
                    if (key != null)
                        CryptographicOperations.ZeroMemory(key);
                    key = null;
                    salt = null;
                    return false;
                }
            }))
            {
                if (unlock.ShowDialog() != DialogResult.OK)
                    return false;
                if (unlock.ResetRequested)
                {
                    if (key != null)
                        CryptographicOperations.ZeroMemory(key);
                    ResetAllCredentials(baseDirectory);
                    return RunFirstUse(baseDirectory, out session);
                }

                session = new StartupSession
                {
                    Mode = StorageMode.EncryptedVault,
                    Servers = loaded ?? new List<Server>(),
                    AdminPasswordHash = PasswordSecurity.Hash(unlock.Password),
                    VaultKey = key,
                    VaultSalt = salt
                };
                return true;
            }
        }

        private static bool UnlockLegacyOrFirstUse(string baseDirectory, out StartupSession session)
        {
            session = null;
            string legacyPath = Path.Combine(baseDirectory, "password.dat");
            string storedHash;
            bool firstRun;
            storedHash = AdminPasswordStore.LoadHash(legacyPath, out firstRun);
            bool hasLegacyData = File.Exists(StorageModePaths.GetPlainPath(baseDirectory));

            if (firstRun)
                return RunFirstUse(baseDirectory, out session);

            using (StartupPasswordForm unlock = StartupPasswordForm.CreateUnlock(storedHash))
            {
                if (unlock.ShowDialog() != DialogResult.OK)
                    return false;
                if (unlock.ResetRequested)
                {
                    ResetAllCredentials(baseDirectory);
                    return RunFirstUse(baseDirectory, out session);
                }

                List<Server> servers = LoadLegacyServers(baseDirectory);
                return ChooseAndCreateStorage(baseDirectory, unlock.Password, servers, out session, hasLegacyData);
            }
        }

        private static bool RunFirstUse(string baseDirectory, out StartupSession session)
        {
            session = null;
            string password;
            using (StartupPasswordForm setup = StartupPasswordForm.CreateFirstRun())
            {
                password = setup.ShowDialog() == DialogResult.OK
                    ? setup.Password
                    : AdminPasswordStore.DefaultPassword;
            }
            return ChooseAndCreateStorage(baseDirectory, password, new List<Server>(), out session, false);
        }

        private static bool ChooseAndCreateStorage(
            string baseDirectory,
            string password,
            List<Server> servers,
            out StartupSession session,
            bool migration)
        {
            session = null;
            StorageMode mode;
            using (StorageModeForm selector = new StorageModeForm(migration))
            {
                if (selector.ShowDialog() != DialogResult.OK)
                    return false;
                mode = selector.SelectedMode;
            }

            string hash = PasswordSecurity.Hash(password);
            if (mode == StorageMode.PlainXml)
            {
                PlainServerStorage.Save(StorageModePaths.GetPlainPath(baseDirectory), servers, hash);
                DeleteLegacyServerCredentials();
                CredentialStore.Delete(CredentialStore.AdminTarget);
                DeleteIfExists(Path.Combine(baseDirectory, "password.dat"));
                session = new StartupSession { Mode = mode, Servers = servers, AdminPasswordHash = hash };
                return true;
            }

            byte[] salt;
            byte[] key = VaultStore.Save(StorageModePaths.GetVaultPath(baseDirectory), servers, password, out salt);
            if (migration)
                DeleteIfExists(StorageModePaths.GetPlainPath(baseDirectory));
            DeleteLegacyServerCredentials();
            CredentialStore.Delete(CredentialStore.AdminTarget);
            DeleteIfExists(Path.Combine(baseDirectory, "password.dat"));
            session = new StartupSession { Mode = mode, Servers = servers, AdminPasswordHash = hash, VaultKey = key, VaultSalt = salt };
            return true;
        }

        private static List<Server> LoadLegacyServers(string baseDirectory)
        {
            string path = StorageModePaths.GetPlainPath(baseDirectory);
            if (!File.Exists(path))
                return new List<Server>();

            bool migrated;
            List<Server> servers = ServerStorage.Load(path, out migrated);
            foreach (Server server in servers)
            {
                string password;
                if (CredentialStore.TryRead(CredentialStore.GetServerTarget(server.CredentialId), out password))
                    server.Password = password;
            }
            return servers;
        }

        private static bool IsNewPlainStore(string path)
        {
            try
            {
                string hash;
                PlainServerStorage.Load(path, out hash);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void DeleteLegacyServerCredentials()
        {
            CredentialStore.DeleteAllServerCredentials();
        }

        private static void AddIp(List<string> ips, string ip)
        {
            if (!string.IsNullOrWhiteSpace(ip) && !ips.Contains(ip, StringComparer.OrdinalIgnoreCase))
                ips.Add(ip);
        }

        private static void ClearAllRdpCredentials()
        {
            ProcessStartInfo list = new ProcessStartInfo("cmdkey.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            list.ArgumentList.Add("/list");
            using (Process process = Process.Start(list))
            {
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);
                MatchCollection matches = Regex.Matches(output ?? "", @"TERMSRV/[^\s\r\n]+", RegexOptions.IgnoreCase);
                foreach (Match match in matches)
                    ClearRdpCredential(match.Value.Substring("TERMSRV/".Length));
            }
        }

        private static void ClearRdpCredential(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
                return;

            ProcessStartInfo start = new ProcessStartInfo("cmdkey.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("/delete:TERMSRV/" + ip);
            using (Process process = Process.Start(start))
                process.WaitForExit(3000);
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
