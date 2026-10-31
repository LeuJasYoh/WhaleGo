using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Model;
using WhaleGo.Executor.Infrastructure.Config;

namespace WhaleGo.Executor.Infrastructure.Secrets;

/// <summary>Plaintext token entry stored in secrets.json (current-user ACL, never in config.json/logs).</summary>
public sealed record SecretToken(string Name, string Token, string CreatedAt);

public sealed record SecretsDocument(List<SecretToken> Tokens);

/// <summary>
/// wg_ token lifecycle: 32-byte CSPRNG generation, SHA-256 verification (constant time), plaintext only in secrets.json.
/// </summary>
public sealed class TokenService : ITokenService
{
    public const string TokenPrefix = "wg_";
    private const string DefaultTokenName = "astrbot";

    private readonly object _lock = new();
    private readonly ConfigService _config;
    private readonly ExecutorPaths _paths;
    private SecretsDocument _secrets = new([]);

    public TokenService(ConfigService config, ExecutorPaths paths)
    {
        _config = config;
        _paths = paths;
        _secrets = LoadSecrets();
    }

    public string SecretsPath => _paths.SecretsPath;

    /// <summary>True when bootstrap generated a token because config/secrets were missing or inconsistent.</summary>
    public bool TokenWasCreated { get; private set; }

    public string? CreatedTokenName { get; private set; }

    public string Generate() => TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    public string Rotate() => Rotate(FirstConfiguredName());

    public string Rotate(string name)
    {
        var tokenName = string.IsNullOrWhiteSpace(name) ? DefaultTokenName : name.Trim();
        var token = Generate();
        var createdAt = DateTimeOffset.Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        lock (_lock)
        {
            _config.SetTokens([new TokenConfigEntry(tokenName, HashToken(token), createdAt)]);
            WriteSecrets([new SecretToken(tokenName, token, createdAt)]);
        }

        return token;
    }

    public bool Verify(string presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        // HashToken() returns 64 hex chars; decode to the same 32 raw bytes as the configured SHA-256.
        byte[] presentedHash;
        try
        {
            presentedHash = Convert.FromHexString(HashToken(presented));
        }
        catch (FormatException)
        {
            return false;
        }

        foreach (var token in _config.Config.AuthTokens)
        {
            if (token.Sha256.Length != 64)
            {
                continue;
            }

            byte[] expected;
            try
            {
                expected = Convert.FromHexString(token.Sha256);
            }
            catch (FormatException)
            {
                continue;
            }

            if (expected.Length == presentedHash.Length && CryptographicOperations.FixedTimeEquals(presentedHash, expected))
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<TokenDescriptor> GetTokens()
    {
        lock (_lock)
        {
            var secrets = _secrets.Tokens.ToList();
            return _config.Config.AuthTokens.Select(entry =>
            {
                var secret = secrets.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, entry.Name, StringComparison.Ordinal) &&
                    string.Equals(HashToken(candidate.Token), entry.Sha256, StringComparison.Ordinal));
                var plaintext = secret?.Token;
                return new TokenDescriptor(
                    entry.Name,
                    plaintext is null ? "••••••••" : Mask(plaintext),
                    entry.CreatedAt ?? string.Empty,
                    plaintext is not null);
            }).ToList();
        }
    }

    /// <summary>Plaintext for the local-only "copy plugin config" action; null when secrets are unreadable.</summary>
    public string? TryGetPlaintextForCopy()
    {
        lock (_lock)
        {
            foreach (var entry in _config.Config.AuthTokens)
            {
                var secret = _secrets.Tokens.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, entry.Name, StringComparison.Ordinal) &&
                    string.Equals(HashToken(candidate.Token), entry.Sha256, StringComparison.Ordinal));
                if (secret is not null)
                {
                    return secret.Token;
                }
            }
            return null;
        }
    }

    /// <summary>First-run / rotation bootstrap: guarantee at least one usable token matching a config hash.</summary>
    public void Bootstrap(string? forcedToken = null)
    {
        lock (_lock)
        {
            var configured = _config.Config.AuthTokens;
            if (!string.IsNullOrEmpty(forcedToken))
            {
                var name = configured.Count > 0 ? configured[0].Name : DefaultTokenName;
                ApplyBootstrapToken(name, forcedToken);
                return;
            }

            if (configured.Count > 0 && SecretsMatchConfig(configured))
            {
                RestrictAcl();
                return;
            }

            var bootstrapName = configured.Count > 0 ? configured[0].Name : DefaultTokenName;
            ApplyBootstrapToken(bootstrapName, Generate());
            TokenWasCreated = true;
            CreatedTokenName = bootstrapName;
        }
    }

    private void ApplyBootstrapToken(string name, string token)
    {
        var createdAt = DateTimeOffset.Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        _config.SetTokens([new TokenConfigEntry(name, HashToken(token), createdAt)]);
        WriteSecrets([new SecretToken(name, token, createdAt)]);
    }

    private bool SecretsMatchConfig(IReadOnlyList<TokenConfigEntry> configured)
    {
        if (_secrets.Tokens.Count == 0)
        {
            return false;
        }

        return configured.Any(entry => _secrets.Tokens.Any(secret =>
            string.Equals(secret.Name, entry.Name, StringComparison.Ordinal) &&
            string.Equals(HashToken(secret.Token), entry.Sha256, StringComparison.Ordinal)));
    }

    private string FirstConfiguredName()
    {
        var configured = _config.Config.AuthTokens;
        return configured.Count > 0 && !string.IsNullOrWhiteSpace(configured[0].Name) ? configured[0].Name : DefaultTokenName;
    }

    private SecretsDocument LoadSecrets()
    {
        try
        {
            if (!File.Exists(_paths.SecretsPath))
            {
                return new SecretsDocument([]);
            }

            var document = JsonSerializer.Deserialize<SecretsDocument>(File.ReadAllText(_paths.SecretsPath), ConfigJson.Options);
            return document ?? new SecretsDocument([]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SecretsDocument([]);
        }
    }

    private void WriteSecrets(IReadOnlyList<SecretToken> tokens)
    {
        var directory = Path.GetDirectoryName(_paths.SecretsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(new SecretsDocument(tokens.ToList()), ConfigJson.Options) + Environment.NewLine;
        var temp = _paths.SecretsPath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _paths.SecretsPath, overwrite: true);
        _secrets = new SecretsDocument(tokens.ToList());
        RestrictAcl();
    }

    /// <summary>Best-effort current-user-only ACL (icacls with the current user SID; failures are non-fatal).</summary>
    public void RestrictAcl()
    {
        try
        {
            if (!File.Exists(_paths.SecretsPath))
            {
                return;
            }

            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrEmpty(sid))
            {
                return;
            }

            using var process = Process.Start(new ProcessStartInfo("icacls.exe", $"\"{_paths.SecretsPath}\" /inheritance:r /grant:r *{sid}:F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process?.WaitForExit(3000);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            // Non-fatal: secrets stay protected by the default user profile ACL.
        }
    }

    public static string HashToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Mask(string token)
    {
        if (token.Length <= 12)
        {
            return token[..Math.Min(5, token.Length)] + "••••••••";
        }

        return token[..5] + "••••••••" + token[^4..];
    }
}