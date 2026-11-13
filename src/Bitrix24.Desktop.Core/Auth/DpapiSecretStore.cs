using System.Security.Cryptography;
using System.Text.Json;

namespace Bitrix24.Desktop.Core.Auth;

public sealed class DpapiSecretStore
{
    private readonly string _path;

    public DpapiSecretStore(string path) => _path = path;

    public StoredSession? Load()
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            var protectedBytes = File.ReadAllBytes(_path);
            var json = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredSession>(json);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or IOException)
        {
            FileLog.Write(exception);
            return null;
        }
    }

    public void Save(StoredSession session)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(session);
        var protectedBytes = ProtectedData.Protect(json, optionalEntropy: null, DataProtectionScope.CurrentUser);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = _path + ".tmp";
        File.WriteAllBytes(temporary, protectedBytes);
        File.Move(temporary, _path, overwrite: true);
    }
}
