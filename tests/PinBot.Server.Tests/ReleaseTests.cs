extern alias updater;
using System.Text.Json;
using PinBot.Contracts;
using PinBot.Server.Discord;
using updater::PinBot.Updater;

namespace PinBot.Server.Tests;

/// <summary>Server releases: signed descriptions, which release a server picks, and what the info channel is told.</summary>
public class ReleaseTests
{
    static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    static Published Rel(long version, double hoursAgo) =>
        new(version, Now.AddHours(-hoursAgo), $"https://api.github.com/assets/{version}");

    [Fact]
    public void A_server_release_is_only_valid_for_the_images_it_names()
    {
        var (pem, pub) = UpdateSigning.NewKey();
        var r = UpdateSigning.SignServer(pem, 20261020120000, "ghcr.io/x/pinbot-server@sha256:aa", "ghcr.io/x/pinbot-updater@sha256:bb", ["note"]);
        Assert.True(UpdateSigning.VerifyServer(r, pub));
        Assert.Equal(pub, UpdateSigning.PublicKeyOf(pem));
        Assert.False(UpdateSigning.VerifyServer(r with { ServerImage = "ghcr.io/evil/pinbot-server@sha256:aa" }, pub));
        Assert.False(UpdateSigning.VerifyServer(r with { UpdaterImage = "ghcr.io/x/pinbot-updater@sha256:cc" }, pub));
        Assert.False(UpdateSigning.VerifyServer(r with { Version = 20991231000000 }, pub));
        Assert.False(UpdateSigning.VerifyServer(r, UpdateSigning.NewKey().PublicBase64)); // another community's key
        Assert.True(UpdateSigning.VerifyServer(r with { Notes = ["edited"] }, pub));     // notes are only shown on GitHub
    }

    [Fact]
    public void Early_servers_take_a_release_at_once_stable_ones_after_the_delay()
    {
        var releases = new[] { Rel(1, 72), Rel(2, 50), Rel(3, 10) };
        var delay = TimeSpan.FromHours(48);
        Assert.Equal(3, Ring.Choose(releases, early: true, delay, Now, current: 1, [])!.Version);
        Assert.Equal(2, Ring.Choose(releases, early: false, delay, Now, current: 1, [])!.Version);
        Assert.Null(Ring.Choose(releases, early: false, delay, Now, current: 2, []));   // 3 is too new yet
        Assert.Null(Ring.Choose(releases, early: true, delay, Now, current: 3, []));    // nothing newer
        Assert.Null(Ring.Choose([Rel(1, 72)], early: true, delay, Now, current: 2, [])); // never a downgrade
    }

    [Fact]
    public void A_release_deleted_or_refused_is_never_installed()
    {
        var delay = TimeSpan.FromHours(48);
        // deleted on GitHub within 48 h: no longer listed, so stable servers simply never see it
        Assert.Equal(1, Ring.Choose([Rel(1, 72)], early: false, delay, Now, current: 0, [])!.Version);
        var refused = new[] { new Refused(2, "did not start", Now) };
        Assert.Equal(1, Ring.Choose([Rel(1, 72), Rel(2, 60)], early: false, delay, Now, current: 0, refused)!.Version);
        Assert.Null(Ring.Choose([Rel(1, 72), Rel(2, 60)], early: false, delay, Now, current: 1, refused));
    }

    [Fact]
    public void A_new_stable_server_starts_with_the_newest_release_rather_than_nothing()
    {
        Assert.Equal(5, Ring.Choose([Rel(5, 1)], early: false, TimeSpan.FromHours(48), Now, current: 0, [])!.Version);
    }

    [Theory]
    [InlineData("v20261021004511", 20261021004511L)]
    [InlineData("20261021004511", null)]   // no v
    [InlineData("v2026102100451", null)]   // 13 digits
    [InlineData("v1.4", null)]
    [InlineData(null, null)]
    public void Release_numbers_come_from_the_tag(string? tag, long? version) => Assert.Equal(version, Ring.VersionOfTag(tag));

    [Fact]
    public void A_missing_refused_list_counts_as_empty() =>
        Assert.Equal(1, Ring.Choose([Rel(1, 72)], early: false, TimeSpan.FromHours(48), Now, current: 0, refused: null)!.Version);

    [Fact]
    public void A_damaged_state_file_falls_back_to_the_copy_from_before_the_last_save()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pinbot-updater-state", Guid.NewGuid().ToString("N")[..8])).FullName;
        var s = UpdaterState.Load(dir);
        s.Install(20261020000000);
        s.BeginSwap(20261021000000, "sha256:old");   // the .bak now holds the installed release
        File.WriteAllText(Path.Combine(dir, "updater.json"), "{ half a fi"); // power cut mid-write
        var back = UpdaterState.Load(dir);
        Assert.Equal(20261020000000, back.Installed);
        Assert.Null(back.Pending);
        Assert.NotNull(back.Refused);

        File.WriteAllText(Path.Combine(dir, "updater.json"), """{"installed":5,"refused":null,"failures":null}""");
        var nulls = UpdaterState.Load(dir);
        Assert.Empty(nulls.Refused);
        Assert.Equal(1, nulls.CountFailure(9));
    }

    [Fact]
    public void A_release_is_refused_once_and_failures_are_counted_per_release()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pinbot-updater-state", Guid.NewGuid().ToString("N")[..8])).FullName;
        var s = UpdaterState.Load(dir);
        Assert.Equal(1, s.CountFailure(7));
        Assert.Equal(2, s.CountFailure(7));
        s.Refuse(7, "did not start");
        s.Refuse(7, "did not start");
        Assert.Single(UpdaterState.Load(dir).Refused);
        Assert.Empty(UpdaterState.Load(dir).Failures);
    }

    [Fact]
    public async Task The_info_channel_hears_about_updates_rollbacks_and_skipped_releases_once()
    {
        await using var s = await TestServer.StartWithDiscordAsync();
        await using (var conn = await s.Service<Db>().OpenAsync()) await Settings.SetAsync(conn, Settings.InfoChannel, "info1");
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pinbot-release", Guid.NewGuid().ToString("N")[..8])).FullName;
        var info = s.Service<InfoPublisher>();
        info.ReleasePath = Path.Combine(dir, "release.json");
        info.UpdaterStatePath = Path.Combine(dir, "updater.json");
        void Release(long v, params string[] notes) =>
            File.WriteAllText(info.ReleasePath, JsonSerializer.Serialize(new { version = v, notes, url = "https://example.org/r" }));
        List<string> Notices() => s.Discord!.Messages.Values.Where(m => m.Channel == "info1" && m.Message["embeds"] is null)
            .Select(m => m.Message["content"]!.GetValue<string>()).ToList();

        Release(20261020100000);
        await info.RunOnceAsync();
        Assert.Empty(Notices()); // the first release a server runs is not news

        Release(20261021164000, "Guests: `/pinbot guest ABC` lets a friend's scores count", "Now supported: Attack from Mars (afm_11u)");
        await info.RunOnceAsync();
        await info.RunOnceAsync();
        var updated = Assert.Single(Notices());
        Assert.StartsWith("🔄 **PinBot was updated** (release of 21 Oct 2026, 16:40 UTC).\n• Guests:", updated);
        Assert.Contains("• Now supported: Attack from Mars (afm_11u)", updated);
        Assert.Contains("VPins update themselves", updated);

        File.WriteAllText(info.UpdaterStatePath, """{"installed":20261021164000,"refused":[{"version":20261022090000,"reason":"the server did not start with it","atUtc":"2026-10-22T09:10:00Z"}]}""");
        await info.RunOnceAsync();
        await info.RunOnceAsync();
        Assert.Equal(2, Notices().Count);
        Assert.Contains("⏭️ The PinBot release of 22 Oct 2026, 09:00 UTC was **skipped** on this server (the server did not start with it)", Notices()[^1]);

        Release(20261020100000); // back on an older one
        await info.RunOnceAsync();
        Assert.Equal(3, Notices().Count);
        Assert.StartsWith("↩️ **PinBot went back** to its release of 20 Oct 2026, 10:00 UTC", Notices()[^1]);
    }
}
