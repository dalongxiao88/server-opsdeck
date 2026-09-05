using System;
using System.Security.Cryptography;

namespace RDPManager
{
    public static class PasswordSecurity
    {
        private const string Prefix = "pbkdf2-sha256";
        private const int Iterations = 120000;
        private const int SaltLength = 16;
        private const int HashLength = 32;

        public static bool IsHash(string value)
        {
            return !string.IsNullOrEmpty(value) && value.StartsWith(Prefix + "$", StringComparison.Ordinal);
        }

        public static string Hash(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, Iterations, HashAlgorithmName.SHA256, HashLength);
            return string.Join("$", Prefix, Iterations.ToString(), Convert.ToBase64String(salt), Convert.ToBase64String(hash));
        }

        public static bool Verify(string password, string storedHash)
        {
            if (!IsHash(storedHash))
                return false;

            try
            {
                string[] parts = storedHash.Split('$');
                int iterations = int.Parse(parts[1]);
                byte[] salt = Convert.FromBase64String(parts[2]);
                byte[] expected = Convert.FromBase64String(parts[3]);
                byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch
            {
                return false;
            }
        }
    }
}
