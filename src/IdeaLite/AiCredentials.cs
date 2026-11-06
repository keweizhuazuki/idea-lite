using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using IdeaLite.Core;

namespace IdeaLite;

/// <summary>
/// 在当前 Windows 用户的凭据库中分别保存服务商 Key，不写入工作区配置。
/// </summary>
public sealed class AiCredentials(string targetPrefix = "IdeaLite/AI/")
{
    public string Read(AiProvider provider)
    {
        if (!CredRead(targetPrefix + provider, 1, 0, out var pointer))
        {
            int code = Marshal.GetLastWin32Error();
            if (code == 1168) return "";
            throw new InvalidOperationException("无法读取 Windows 凭据库中的 API Key。");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return credential.BlobSize == 0 ? "" : Marshal.PtrToStringUni(credential.Blob, (int)credential.BlobSize / 2) ?? "";
        }
        finally { CredFree(pointer); }
    }

    public void Save(AiProvider provider, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (!CredDelete(targetPrefix + provider, 1, 0) && Marshal.GetLastWin32Error() != 1168)
                throw new InvalidOperationException("无法删除 Windows 凭据库中的 API Key。");
            return;
        }
        if (apiKey.Length > 1200 || apiKey.Any(char.IsControl)) throw new InvalidOperationException("API Key 格式无效，请检查后重试。");
        var data = Encoding.Unicode.GetBytes(apiKey.Trim());
        var blob = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, blob, data.Length);
            var credential = new Credential { Type = 1, TargetName = targetPrefix + provider, BlobSize = (uint)data.Length, Blob = blob, Persist = 2, UserName = "api-key" };
            if (!CredWrite(ref credential, 0)) throw new InvalidOperationException("无法安全保存 API Key，请检查 Windows 凭据库。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data); Marshal.Copy(data, 0, blob, data.Length); Marshal.FreeHGlobal(blob);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
