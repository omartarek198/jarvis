using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jarvis.Core;

namespace Jarvis.Kernel.Services;

/// <summary>
/// DPAPI-encrypted secret store (CurrentUser scope) at data/secrets.bin.
/// Values never appear in logs, audit events, or speech. Import path: drop
/// data/setup-secrets.json (flat string map) — it is absorbed and DELETED at startup.
/// </summary>
public class SecretsService(ILogger<SecretsService> log)
{
    private static string StorePath => Path.Combine(JarvisPaths.Data, "secrets.bin");
    private static string SetupPath => Path.Combine(JarvisPaths.Data, "setup-secrets.json");
    private readonly Lock _lock = new();
    private Dictionary<string, string>? _cache;

    public string? Get(string key)
    {
        lock (_lock) return Load().GetValueOrDefault(key);
    }

    public bool Has(string key) => !string.IsNullOrEmpty(Get(key));

    public void Set(string key, string value)
    {
        lock (_lock)
        {
            var map = Load();
            map[key] = value;
            Save(map);
        }
    }

    public IReadOnlyCollection<string> Keys { get { lock (_lock) return [.. Load().Keys]; } }

    /// <summary>Absorb data/setup-secrets.json into the encrypted store, then delete it.</summary>
    public int ImportSetupFileIfPresent()
    {
        lock (_lock)
        {
            if (!File.Exists(SetupPath)) return 0;
            try
            {
                var json = File.ReadAllText(SetupPath);
                var incoming = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
                var map = Load();
                var imported = 0;
                foreach (var (k, v) in incoming)
                    if (!string.IsNullOrWhiteSpace(v)) { map[k.Trim()] = v.Trim(); imported++; }
                Save(map);
                File.Delete(SetupPath);
                log.LogInformation("[secrets] imported {Count} secret(s) from setup file; file deleted", imported);
                return imported;
            }
            catch (Exception ex)
            {
                log.LogError("[secrets] setup import failed: {Msg}", ex.Message);
                return 0;
            }
        }
    }

    private Dictionary<string, string> Load()
    {
        if (_cache is not null) return _cache;
        try
        {
            if (File.Exists(StorePath))
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(StorePath), null, DataProtectionScope.CurrentUser);
                _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(plain)) ?? [];
                return _cache;
            }
        }
        catch (Exception ex) { log.LogError("[secrets] store unreadable: {Msg}", ex.Message); }
        _cache = [];
        return _cache;
    }

    private void Save(Dictionary<string, string> map)
    {
        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(map));
        File.WriteAllBytes(StorePath, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
        _cache = map;
    }
}
