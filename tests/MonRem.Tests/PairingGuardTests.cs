using MonRem.Host.Core.Security;
using MonRem.Host.Core.Session;

namespace MonRem.Tests;

public class PairingGuardTests
{
    [Fact]
    public void Locks_after_five_failures_and_doubles()
    {
        var time = new ManualTimeProvider();
        var guard = new PairingGuard(time);

        for (var i = 0; i < 4; i++) guard.RecordFailure();
        Assert.Equal(TimeSpan.Zero, guard.RetryAfter);

        guard.RecordFailure();
        Assert.Equal(TimeSpan.FromSeconds(60), guard.RetryAfter);

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(TimeSpan.Zero, guard.RetryAfter);

        for (var i = 0; i < 5; i++) guard.RecordFailure();
        Assert.Equal(TimeSpan.FromSeconds(120), guard.RetryAfter);

        guard.RecordSuccess();
        Assert.Equal(TimeSpan.Zero, guard.RetryAfter);
    }

    [Fact]
    public void Only_one_pairing_at_a_time()
    {
        var guard = new PairingGuard(new ManualTimeProvider());
        object a = new(), b = new();
        Assert.True(guard.TryBegin(a));
        Assert.True(guard.TryBegin(a));  // o mesmo dono pode repetir
        Assert.False(guard.TryBegin(b));
        guard.End(b);                    // quem não é dono não libera
        Assert.False(guard.TryBegin(b));
        guard.End(a);
        Assert.True(guard.TryBegin(b));
    }

    [Fact]
    public void Lock_is_capped_at_one_hour()
    {
        var time = new ManualTimeProvider();
        var guard = new PairingGuard(time);
        for (var round = 0; round < 12; round++)
        {
            for (var i = 0; i < 5; i++) guard.RecordFailure();
            Assert.True(guard.RetryAfter <= TimeSpan.FromHours(1));
            time.Advance(TimeSpan.FromHours(2));
        }
    }
}

public class SessionRegistryTests
{
    [Fact]
    public void Resume_token_is_single_use_bound_to_client_and_expires()
    {
        var time = new ManualTimeProvider();
        var reg = new SessionRegistry(time);

        var (session, token) = reg.Open("c1");
        reg.MarkDisconnected("c1", session);
        Assert.False(reg.TryResume("other", token)); // outro cliente não usa nem queima o token
        Assert.True(reg.TryResume("c1", token));
        Assert.False(reg.TryResume("c1", token));    // uso único

        var (session3, token3) = reg.Open("c1");
        reg.MarkDisconnected("c1", session3);
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.False(reg.TryResume("c1", token3));
    }
}
