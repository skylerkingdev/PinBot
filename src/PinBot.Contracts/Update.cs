using System.Security.Cryptography;
using System.Text;

namespace PinBot.Contracts;

/// <summary>
/// Describes the VPin agent a server offers (download/pinbot.json next to download/pinbot.exe). Signed by the
/// maintainer's release key, so a VPin installs only builds that key signed: whoever controls the server or the
/// network can withhold updates but cannot push one.
/// </summary>
/// <param name="Version">release number, yyyyMMddHHmmss of the build (UTC); never installed unless newer</param>
/// <param name="Sha256">hex SHA-256 of pinbot.exe</param>
/// <param name="Signature">base64 ECDSA P-256 / SHA-256 signature over <see cref="UpdateSigning.Payload"/></param>
public sealed record UpdateManifest(long Version, long Size, string Sha256, string Signature);

/// <summary>
/// One release of the server, published with each GitHub Release (server-release.json). Signed by the same key as the
/// VPin agent, so a server's updater installs only images that key vouches for: whoever controls the registry or the
/// network can withhold updates but cannot push one.
/// </summary>
/// <param name="Version">release number, yyyyMMddHHmmss (UTC); the same number as the agent inside the server image</param>
/// <param name="ServerImage">the server image by digest, e.g. ghcr.io/owner/pinbot-server@sha256:…</param>
/// <param name="UpdaterImage">the updater image by digest</param>
/// <param name="Notes">what changed, for people (not signed: the server announces the notes inside its own image)</param>
public sealed record ServerRelease(long Version, string ServerImage, string UpdaterImage, IReadOnlyList<string> Notes, string Signature);

public static class UpdateSigning
{
    /// <summary>What is signed: the version and the exact bytes of the exe.</summary>
    public static byte[] Payload(long version, long size, string sha256) =>
        Encoding.UTF8.GetBytes($"pinbot-agent-update|{version}|{size}|{sha256.ToLowerInvariant()}");

    public static UpdateManifest Sign(string privateKeyPem, long version, byte[] exe)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        var sha = Convert.ToHexString(SHA256.HashData(exe)).ToLowerInvariant();
        var signature = key.SignData(Payload(version, exe.Length, sha), HashAlgorithmName.SHA256);
        return new UpdateManifest(version, exe.Length, sha, Convert.ToBase64String(signature));
    }

    /// <summary>True when the manifest was signed by the private key matching <paramref name="publicKeyBase64"/> (SPKI).</summary>
    public static bool Verify(UpdateManifest m, string publicKeyBase64)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return key.VerifyData(Payload(m.Version, m.Size, m.Sha256), Convert.FromBase64String(m.Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return false; }
    }

    /// <summary>True when <paramref name="exe"/> is exactly the file the manifest describes.</summary>
    public static bool Matches(UpdateManifest m, byte[] exe) =>
        exe.LongLength == m.Size && Convert.ToHexString(SHA256.HashData(exe)).Equals(m.Sha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>What a server release's signature covers: its number and the exact images (by digest) to run.</summary>
    public static byte[] ServerPayload(long version, string serverImage, string updaterImage) =>
        Encoding.UTF8.GetBytes($"pinbot-server-release|{version}|{serverImage}|{updaterImage}");

    public static ServerRelease SignServer(string privateKeyPem, long version, string serverImage, string updaterImage, IReadOnlyList<string> notes)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        var signature = key.SignData(ServerPayload(version, serverImage, updaterImage), HashAlgorithmName.SHA256);
        return new ServerRelease(version, serverImage, updaterImage, notes, Convert.ToBase64String(signature));
    }

    /// <summary>True when the server release was signed by the private key matching <paramref name="publicKeyBase64"/>.</summary>
    public static bool VerifyServer(ServerRelease r, string publicKeyBase64)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return key.VerifyData(ServerPayload(r.Version, r.ServerImage, r.UpdaterImage), Convert.FromBase64String(r.Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return false; }
    }

    /// <summary>The public key (base64 SPKI) of a private key PEM.</summary>
    public static string PublicKeyOf(string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>A new key pair: (private key PEM, public key base64 SPKI).</summary>
    public static (string PrivatePem, string PublicBase64) NewKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }
}
