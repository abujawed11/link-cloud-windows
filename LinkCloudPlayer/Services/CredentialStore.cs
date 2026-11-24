using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LinkCloudPlayer.Services;

public class SavedSession
{
    public string ServerUrl { get; set; } = "";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

// Persists the server URL + login to disk, encrypted with DPAPI (CurrentUser
// scope) so only this Windows account can decrypt it - no separate secret
// management needed for a single-user app.
public static class CredentialStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LinkCloudPlayer",
        "session.dat"
    );

    public static void Save(SavedSession session)
    {
        var json = JsonSerializer.Serialize(session);
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllBytes(FilePath, encrypted);
    }

    public static SavedSession? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var encrypted = File.ReadAllBytes(FilePath);
            var plainBytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<SavedSession>(Encoding.UTF8.GetString(plainBytes));
        }
        catch
        {
            // Corrupted file, or encrypted under a different Windows account/profile.
            return null;
        }
    }

    public static void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}
