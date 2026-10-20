using System.Diagnostics;

namespace PinBot.Updater;

/// <summary>
/// The docker CLI (and its compose plugin) against the host's Docker through the mounted socket. Only the PinBot
/// project's server and updater are ever changed; the database, Tailscale and backups keep running.
/// </summary>
public sealed class Docker(string composeDir)
{
    string _composeDir = composeDir;

    /// <summary>
    /// Compose resolves the project's relative paths (./data/...) against its project directory and hands them to the
    /// host's Docker, which knows only host paths. Inside this container the project is mounted at /deploy, a path the
    /// host does not have. So: find the host folder behind that mount (from Docker itself), make the same path exist in
    /// here as a link to /deploy, and run compose from it; every path compose produces is then a real host path.
    /// </summary>
    public async Task UseHostProjectPathAsync(CancellationToken ct)
    {
        var self = Environment.GetEnvironmentVariable("HOSTNAME");
        if (self is null) return;
        var (exit, source) = await RunAsync(ct, "inspect", "--format",
            "{{range .Mounts}}{{if eq .Destination \"" + _composeDir + "\"}}{{.Source}}{{end}}{{end}}", self);
        source = source.Trim();
        if (exit != 0 || !source.StartsWith('/') || source == _composeDir) return;
        if (!Directory.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateSymbolicLink(source, _composeDir);
        }
        _composeDir = source;
    }

    public string ProjectDir => _composeDir;

    /// <summary>
    /// The image the server service runs (compose's `image:`). A fresh `docker compose up` pulls it once; from then on the
    /// updater re-points this local tag at each verified release, so a restart runs what was verified, never a newer pull.
    /// </summary>
    public static string ServerCurrent => Environment.GetEnvironmentVariable("PINBOT_SERVER_IMAGE") is { Length: > 0 } v
        ? v : "ghcr.io/skylerkingdev/pinbot-server:latest";
    public const string ServerPrevious = "pinbot-server:previous";

    public async Task<(int Exit, string Output)> RunAsync(CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("docker CLI not found");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, ((await stdout) + (await stderr)).Trim());
    }

    public async Task CheckedAsync(CancellationToken ct, params string[] args)
    {
        var (exit, output) = await RunAsync(ct, args);
        if (exit != 0) throw new InvalidOperationException($"docker {string.Join(' ', args)} failed ({exit}): {output}");
    }

    public async Task<bool> ImageExistsAsync(string image, CancellationToken ct) =>
        (await RunAsync(ct, "image", "inspect", image)).Exit == 0;

    string[] Compose(params string[] args) =>
        ["compose", "--project-directory", _composeDir, "-f", Path.Combine(_composeDir, "docker-compose.yml"), .. args];

    /// <summary>Recreates one service of the PinBot project from its current image, leaving the others running.</summary>
    public Task RecreateAsync(string service, CancellationToken ct) =>
        CheckedAsync(ct, Compose("up", "-d", "--no-deps", "--force-recreate", service));

    /// <summary>Like <see cref="RecreateAsync"/>, but returns compose's error instead of throwing (null: it worked).</summary>
    public async Task<string?> TryRecreateAsync(string service, CancellationToken ct)
    {
        var (exit, output) = await RunAsync(ct, Compose("up", "-d", "--no-deps", "--force-recreate", service));
        return exit == 0 ? null : $"docker compose up {service} failed ({exit}): {output}";
    }
}
