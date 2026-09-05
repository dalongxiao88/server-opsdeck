using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RDPManager
{
    public static class VaultStore
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("XIAOBAI_VAULT_1");
        private const int SaltSize = 16;
        private const int NonceSize = 12;
        private const int TagSize = 16;
        private const int KeySize = 32;
        private const int KdfIterations = 600000;

        public static byte[] Save(string filePath, IEnumerable<Server> servers, string password)
        {
            byte[] salt;
            return Save(filePath, servers, password, out salt);
        }

        public static byte[] Save(string filePath, IEnumerable<Server> servers, string password, out byte[] salt)
        {
            salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] key = DeriveKey(password, salt);
            try
            {
                SaveEncrypted(filePath, servers, key, salt, nonce);
                return key;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(key);
                throw;
            }
        }

        public static void Save(string filePath, IEnumerable<Server> servers, byte[] key, byte[] salt)
        {
            if (key == null || key.Length != KeySize || salt == null || salt.Length != SaltSize)
                throw new InvalidOperationException("保险库密钥参数无效");
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            SaveEncrypted(filePath, servers, key, salt, nonce);
        }

        private static void SaveEncrypted(string filePath, IEnumerable<Server> servers, byte[] key, byte[] salt, byte[] nonce)
        {
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new List<Server>(servers), new JsonSerializerOptions());
            byte[] cipher = new byte[plain.Length];
            byte[] tag = new byte[TagSize];
            string temporaryPath = filePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (AesGcm aes = new AesGcm(key, TagSize))
                    aes.Encrypt(nonce, plain, cipher, tag, Magic);
                using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, false))
                {
                    writer.Write(Magic);
                    writer.Write(salt);
                    writer.Write(nonce);
                    writer.Write(tag);
                    writer.Write(cipher.Length);
                    writer.Write(cipher);
                    writer.Flush();
                    stream.Flush(true);
                }
                ReplaceAtomically(temporaryPath, filePath);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
                CryptographicOperations.ZeroMemory(cipher);
                CryptographicOperations.ZeroMemory(tag);
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        public static List<Server> Load(string filePath, string password, out byte[] key)
        {
            byte[] salt;
            return Load(filePath, password, out key, out salt);
        }

        public static List<Server> Load(string filePath, string password, out byte[] key, out byte[] salt)
        {
            key = null;
            salt = null;
            byte[] cipher = null;
            byte[] plain = null;
            try
            {
                using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, false))
                {
                    byte[] magic = reader.ReadBytes(Magic.Length);
                    if (!AreEqual(magic, Magic))
                        throw new InvalidOperationException("保险库格式无法识别");
                    salt = reader.ReadBytes(SaltSize);
                    byte[] nonce = reader.ReadBytes(NonceSize);
                    byte[] tag = reader.ReadBytes(TagSize);
                    int length = reader.ReadInt32();
                    if (salt.Length != SaltSize || nonce.Length != NonceSize || tag.Length != TagSize ||
                        length < 0 || length > stream.Length - stream.Position)
                        throw new InvalidOperationException("保险库文件已损坏");

                    cipher = reader.ReadBytes(length);
                    plain = new byte[length];
                    key = DeriveKey(password, salt);
                    using (AesGcm aes = new AesGcm(key, TagSize))
                        aes.Decrypt(nonce, cipher, tag, plain, Magic);
                    List<Server> servers = JsonSerializer.Deserialize<List<Server>>(plain);
                    if (servers == null)
                        throw new InvalidOperationException("保险库内容为空或格式无法识别");
                    foreach (Server server in servers)
                        server.EnsureDefaults();
                    return servers;
                }
            }
            catch (CryptographicException)
            {
                if (key != null)
                    CryptographicOperations.ZeroMemory(key);
                key = null;
                salt = null;
                throw new InvalidOperationException("主密码错误或保险库文件已损坏");
            }
            catch
            {
                if (key != null)
                    CryptographicOperations.ZeroMemory(key);
                key = null;
                salt = null;
                throw;
            }
            finally
            {
                if (cipher != null)
                    CryptographicOperations.ZeroMemory(cipher);
                if (plain != null)
                    CryptographicOperations.ZeroMemory(plain);
            }
        }

        private static byte[] DeriveKey(string password, byte[] salt)
        {
            return Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, KdfIterations, HashAlgorithmName.SHA256, KeySize);
        }

        private static bool AreEqual(byte[] left, byte[] right)
        {
            return left != null && right != null && left.Length == right.Length &&
                CryptographicOperations.FixedTimeEquals(left, right);
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
    }
}
