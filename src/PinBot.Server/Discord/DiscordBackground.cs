namespace PinBot.Server.Discord;

/// <summary>Registers the slash commands at startup, then runs the publisher, member sync and weekly scheduler loops.</summary>
public sealed class DiscordBackground(DiscordOptions discord, IDiscordApi api, LeaderboardPublisher publisher, InfoPublisher info, MemberSync members,
    WeeklyScheduler weekly, ILogger<DiscordBackground> log) : BackgroundService
{
    /// <summary>The least time between attempts after a failure (database restarting, Discord down): never a busy loop.</summary>
    public static readonly TimeSpan FailurePause = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!discord.Enabled || !discord.RunBackground) return;
        if (discord.ChannelId is null) log.LogWarning("Discord: PinBot__Discord__ChannelId is not set; leaderboards will not be posted");

        await Task.WhenAll(
            RegisterCommandsAsync(stop),
            Loop("leaderboard", async ct => { await publisher.RunOnceAsync(ct); await info.RunOnceAsync(ct); await publisher.WaitAsync(TimeSpan.FromSeconds(30), ct); }, TimeSpan.Zero, stop),
            Loop("members", members.RunOnceAsync, TimeSpan.FromMinutes(10), stop),
            Loop("weekly", async ct => await weekly.RunOnceAsync(ct), TimeSpan.FromMinutes(1), stop));
    }

    /// <summary>
    /// Sends the command definitions until Discord has them: the server may start before the network (a NAS reboot), and
    /// a release that changes a command must not wait for the next restart.
    /// </summary>
    async Task RegisterCommandsAsync(CancellationToken stop)
    {
        for (var wait = TimeSpan.FromSeconds(15); !stop.IsCancellationRequested; wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, TimeSpan.FromMinutes(10).Ticks)))
        {
            try
            {
                await api.RegisterGuildCommandsAsync(discord.ApplicationId!, discord.GuildId!, Interactions.CommandDefinitions(), stop);
                log.LogInformation("Discord: /pinbot commands registered");
                return;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is DiscordRestException or HttpRequestException or TaskCanceledException)
            {
                log.LogError("Discord: registering commands failed: {Message}; trying again in {Wait}", ex.Message, wait);
            }
            try { await Task.Delay(wait, stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    async Task Loop(string name, Func<CancellationToken, Task> work, TimeSpan every, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var pause = every;
            try { await work(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log.LogError(ex, "Discord background task '{Name}' failed; retrying", name);
                pause = PauseAfter(every, failed: true);
            }
            try { if (pause > TimeSpan.Zero) await Task.Delay(pause, stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>How long a loop waits before its next pass: its own interval, and at least <see cref="FailurePause"/> after a failure.</summary>
    internal static TimeSpan PauseAfter(TimeSpan every, bool failed) => failed && every < FailurePause ? FailurePause : every;
}
