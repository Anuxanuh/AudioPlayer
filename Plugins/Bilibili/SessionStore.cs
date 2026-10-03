using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AudioPlayer.Plugin.Bilibili;

public sealed record SessionCookie(string Name, string Value, string Domain, string Path, bool Secure, double? Expires);

public sealed class SessionStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public string FilePath => Path.Combine(directory, "session.bin");
    public static bool IsBilibiliDomain(string domain) => domain.TrimStart('.').Equals("bilibili.com", StringComparison.OrdinalIgnoreCase) || domain.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<SessionCookie> Load()
    {
        if (!File.Exists(FilePath)) return Array.Empty<SessionCookie>();
        byte[] plaintext = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<SessionCookie[]>(plaintext, Json)?.Where(c => IsBilibiliDomain(c.Domain)).ToArray() ?? []; }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public void Save(IEnumerable<SessionCookie> cookies)
    {
        Directory.CreateDirectory(directory);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(cookies.Where(c => IsBilibiliDomain(c.Domain)), Json);
        try
        {
            byte[] encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            string temporary = FilePath + ".tmp";
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, FilePath, true);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public void Clear() { if (File.Exists(FilePath)) File.Delete(FilePath); }
}
