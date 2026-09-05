using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace RDPManager
{
    // Windows Credential Manager keeps secrets outside the application folder.
    public static class CredentialStore
    {
        private const uint GenericCredentialType = 1;
        private const uint LocalMachinePersistence = 2;
        private const string Prefix = "XiaoBaiServerManager/";
        public const string AdminTarget = Prefix + "AdminPassword";

        public static string GetServerTarget(string credentialId)
        {
            return Prefix + "Server/" + credentialId;
        }

        public static bool TryRead(string target, out string secret)
        {
            secret = null;
            IntPtr credentialPointer;
            if (!CredRead(target, GenericCredentialType, 0, out credentialPointer))
                return false;

            try
            {
                CREDENTIAL credential = (CREDENTIAL)Marshal.PtrToStructure(credentialPointer, typeof(CREDENTIAL));
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                    return true;

                byte[] bytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                secret = Encoding.UTF8.GetString(bytes);
                return true;
            }
            finally
            {
                CredFree(credentialPointer);
            }
        }

        public static void Write(string target, string username, string secret)
        {
            byte[] secretBytes = Encoding.UTF8.GetBytes(secret ?? "");
            IntPtr targetPointer = IntPtr.Zero;
            IntPtr userPointer = IntPtr.Zero;
            IntPtr blobPointer = IntPtr.Zero;

            try
            {
                targetPointer = Marshal.StringToCoTaskMemUni(target);
                userPointer = Marshal.StringToCoTaskMemUni(username ?? "");
                blobPointer = Marshal.AllocHGlobal(Math.Max(1, secretBytes.Length));
                if (secretBytes.Length > 0)
                    Marshal.Copy(secretBytes, 0, blobPointer, secretBytes.Length);

                CREDENTIAL credential = new CREDENTIAL
                {
                    Type = GenericCredentialType,
                    TargetName = targetPointer,
                    CredentialBlob = blobPointer,
                    CredentialBlobSize = (uint)secretBytes.Length,
                    Persist = LocalMachinePersistence,
                    UserName = userPointer
                };

                if (!CredWrite(ref credential, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法写入 Windows 凭据管理器");
            }
            finally
            {
                if (targetPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(targetPointer);
                if (userPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(userPointer);
                if (blobPointer != IntPtr.Zero) Marshal.FreeHGlobal(blobPointer);
            }
        }

        public static void Delete(string target)
        {
            if (!CredDelete(target, GenericCredentialType, 0))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 1168)
                    throw new Win32Exception(error, "无法删除 Windows 凭据");
            }
        }

        public static void DeleteAllServerCredentials()
        {
            IntPtr credentials;
            uint count;
            if (!CredEnumerate(Prefix + "Server/*", 0, out count, out credentials))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 1168)
                    return;
                throw new Win32Exception(error, "无法枚举服务器凭据");
            }

            try
            {
                for (int index = 0; index < count; index++)
                {
                    IntPtr itemPointer = Marshal.ReadIntPtr(credentials, index * IntPtr.Size);
                    CREDENTIAL item = (CREDENTIAL)Marshal.PtrToStructure(itemPointer, typeof(CREDENTIAL));
                    string target = item.TargetName == IntPtr.Zero ? null : Marshal.PtrToStringUni(item.TargetName);
                    if (!string.IsNullOrWhiteSpace(target))
                        Delete(target);
                }
            }
            finally
            {
                CredFree(credentials);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredEnumerate(string filter, uint flags, out uint count, out IntPtr credentials);

        [DllImport("advapi32.dll")]
        private static extern bool CredFree(IntPtr credential);
    }
}
