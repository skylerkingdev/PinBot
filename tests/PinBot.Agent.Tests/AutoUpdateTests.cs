using PinBot.Agent.Sync;
using PinBot.Contracts;

namespace PinBot.Agent.Tests;

public class AutoUpdateTests
{
    static readonly byte[] Exe = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();

    [Fact]
    public void A_release_verifies_only_with_its_own_key_and_only_for_its_exact_bytes()
    {
        var (priv, pub) = UpdateSigning.NewKey();
        var (_, otherPub) = UpdateSigning.NewKey();
        var m = UpdateSigning.Sign(priv, 20261013120000, Exe);

        Assert.True(UpdateSigning.Verify(m, pub));
        Assert.True(UpdateSigning.Matches(m, Exe));
        Assert.False(UpdateSigning.Verify(m, otherPub));                         // another key
        Assert.False(UpdateSigning.Verify(m with { Version = m.Version + 1 }, pub)); // version changed after signing
        Assert.False(UpdateSigning.Verify(m with { Sha256 = new string('0', 64) }, pub)); // different exe claimed
        var tampered = (byte[])Exe.Clone();
        tampered[100] ^= 1;
        Assert.False(UpdateSigning.Matches(m, tampered));                         // download altered
    }

    [Fact]
    public void Only_newer_correctly_signed_releases_are_installed()
    {
        var (priv, pub) = UpdateSigning.NewKey();
        var (otherPriv, _) = UpdateSigning.NewKey();
        const long current = 20261013120000;
        Assert.Null(AutoUpdate.Refusal(UpdateSigning.Sign(priv, current + 1, Exe), pub, current));
        Assert.Equal("not newer than this build", AutoUpdate.Refusal(UpdateSigning.Sign(priv, current, Exe), pub, current));
        Assert.Equal("not newer than this build", AutoUpdate.Refusal(UpdateSigning.Sign(priv, current - 1, Exe), pub, current)); // no downgrade
        Assert.Contains("release key", AutoUpdate.Refusal(UpdateSigning.Sign(otherPriv, current + 1, Exe), pub, current));
    }

    [Fact]
    public void Development_builds_do_not_update_themselves() => Assert.False(AutoUpdate.Enabled);
}
