using System;
using System.Security.Cryptography;
using System.Text;

namespace ServerForge
{
    // These values are identifiers that make the sensitive storage and remote
    // execution paths easy to locate in a static binary. NativeAOT removes IL;
    // this additional layer removes their plaintext representation as well.
    internal static class ProtectedText
    {
        private static readonly byte[] KeyPartA =
        {
            0xCC, 0x0D, 0xE0, 0xD2, 0x38, 0x94, 0x47, 0x2F,
            0x19, 0xB4, 0x31, 0x2E, 0x11, 0x63, 0xC5, 0xC2,
            0xE7, 0xFF, 0xA7, 0x55, 0x9C, 0x4D, 0xAC, 0x38,
            0xA9, 0x2C, 0x12, 0x71, 0x67, 0x7C, 0xC9, 0xA9
        };

        private static readonly byte[] KeyPartB =
        {
            0xA4, 0x77, 0x50, 0x53, 0x3F, 0xDF, 0x1D, 0xB9,
            0x4B, 0x58, 0x57, 0xD8, 0x7D, 0x71, 0x43, 0xA3,
            0x59, 0x95, 0xA7, 0xCD, 0xA7, 0x79, 0x8C, 0x35,
            0xFF, 0xF5, 0xFA, 0xFE, 0xC7, 0x07, 0x85, 0xF3
        };

        internal static string PlainFileName => Reveal("WVMzzP8zVj7lBQr2O/xLuOHxuMBrx/FR7go1cej6M6RJpBmrpdZY");
        internal static string VaultFileName => Reveal("d8BdamUeO1nwUezQGYlODgJ5o0VK2OVcp6Px9A5BKq7ny7RSVKngfXo=");
        internal static string LegacyPasswordFileName => Reveal("LiHux5VZ9VkaV380UL8CDmKft7s2zFlaZhoBbAjShuQjptmM0auqTg==");
        internal static string VaultMagic => Reveal("udTQ61KySKIiflDBjtmHyuKV+auFcyvtbQBQWKg+tqEJQ34ujOM+c5uXpg==");
        internal static string CredentialPrefix => Reveal("dERQzPY0/ZqaVPKvVgXyFQJhyR9bMe5MMyaLv7P2rsBPqi9vwL4HgBx+bd2Uc0osVQ==");
        internal static string CredentialAdmin => Reveal("wBu3peOu3fk1Lbw4VJPVGL1Qr1RW79SnyAMUd/eHGT97VxRHyI64u2I=");
        internal static string CredentialServer => Reveal("v3Q1V0RQup77odoK9f58ONXQmV6LpmOW8MD3CO0P7JN50NY=");
        internal static string CredentialServerFilter => Reveal("RICdgVLLHervpvFMT0dZ9KL4uHYVEm7yMXiGaWRFi4LMThQQ");
        internal static string IconResource => Reveal("eVAKmOR3uQiIzzvkiDPWkpxmicX0lqsopQBhhE9jy7HJC/lX877m9qEguMDSJF+puSx4");
        internal static string XtermResource => Reveal("QkJ+Vm0Q7DzXegvptxPpNt2GkMH4trGBm4RbXCGlsYzuCbAhFS5vtZAGMsf1HRa2GYP/SME0Ty9XPOyV0h9y");
        internal static string FitResource => Reveal("vtP0Lm+cQz2Hti0amgKzXx3FZQhcQZjOL5mwP1wMtlubCiwfC7AJBoZ6PY0ig2gk4GJPJH42Bfd+dl0DXT6E6Yg55e9cDyPekQ==");
        internal static string CssResource => Reveal("7KXsbYHfonSBuDFnjjTPPQgCnuwi08QY7Ouzt4uVauyufTO/v+K4K+6BYZuE7KEDW0QX7XJY/boCnqFhLX8VBQ==");
        internal static string WindowsPayloadMarker => Reveal("2rltwS/O2WlmHFqn3/Uc0URLoMlEka5fXd5IM9f+2EHx6NRUjAYAWPlG0C45RCL7NmSuTo/s");
        internal static string RestartMarker => Reveal("P5d5DwIxyAUNR679Ncy687Xlt3cyL/44pcSxiGiy0lXiz1aOdM4LdcJ4T7XBDRzDniCpXQ==");
        internal static string RdpClientName => Reveal("EPnDKMfT7RmdG+GHsptO+jD3DuNmljRNvwsFRJEy5E0qoklYvm+H");
        internal static string ClipboardTempName => Reveal("T3soZE8j6yQWfBv8yjnnNbAq9svLlwrkAADz2Px2RnnnsvBunA==");

        private static string Reveal(string encoded)
        {
            byte[] packed = Convert.FromBase64String(encoded);
            if (packed.Length <= 28)
                throw new CryptographicException("Protected value is invalid");

            byte[] key = BuildKey();
            byte[] plain = new byte[packed.Length - 28];
            try
            {
                using (AesGcm aes = new AesGcm(key, 16))
                {
                    aes.Decrypt(
                        packed.AsSpan(0, 12),
                        packed.AsSpan(28),
                        packed.AsSpan(12, 16),
                        plain);
                }
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plain);
                CryptographicOperations.ZeroMemory(packed);
            }
        }

        private static byte[] BuildKey()
        {
            byte[] key = new byte[32];
            for (int index = 0; index < key.Length; index++)
            {
                byte mixed = RotateLeft(KeyPartB[31 - index], index % 7);
                key[index] = (byte)(KeyPartA[index] ^ mixed ^ (byte)(index * 29 + 0x53));
            }
            return key;
        }

        private static byte RotateLeft(byte value, int count)
        {
            return count == 0
                ? value
                : (byte)((value << count) | (value >> (8 - count)));
        }
    }
}
