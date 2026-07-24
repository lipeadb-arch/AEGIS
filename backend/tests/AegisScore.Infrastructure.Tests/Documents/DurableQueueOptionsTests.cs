using AegisScore.Infrastructure.Documents;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Documents;

/// <summary>
/// [AEGIS-AUD-050] Validação das opções da fila durável: valores inválidos/negativos precisam FALHAR de forma
/// clara (as filas os recusam na composição), nunca serem silenciosamente clampados a um default plausível.
/// </summary>
public sealed class DurableQueueOptionsTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        new DocumentAnalysisQueueOptions().TryValidate(out var e1).Should().BeTrue(e1);
        new PolicySyncQueueOptions().TryValidate(out var e2).Should().BeTrue(e2);
    }

    [Theory]
    [InlineData(0, 5, 5, 30)]     // lease zero
    [InlineData(-1, 5, 5, 30)]    // lease negativo
    [InlineData(300, 0, 5, 30)]   // poll zero
    [InlineData(300, 5, 0, 30)]   // maxAttempts zero
    [InlineData(300, 5, 5, -1)]   // backoff negativo
    public void InvalidValues_FailClearly(int lease, int poll, int maxAttempts, int backoff)
    {
        var opt = new DocumentAnalysisQueueOptions
        {
            LeaseSeconds = lease, PollSeconds = poll, MaxAttempts = maxAttempts, RetryBackoffSeconds = backoff,
        };
        opt.TryValidate(out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Heartbeat_NotSmallerThanLease_FailsClearly()
    {
        // Um batimento >= lease deixaria o lease expirar antes de renovar — inválido.
        var opt = new DocumentAnalysisQueueOptions { LeaseSeconds = 30, HeartbeatSeconds = 30 };
        opt.TryValidate(out var error).Should().BeFalse();
        error.Should().Contain("batimento");
    }

    [Fact]
    public void EffectiveHeartbeat_DefaultsToOneThirdOfLease()
    {
        new DocumentAnalysisQueueOptions { LeaseSeconds = 300 }.EffectiveHeartbeatSeconds.Should().Be(100);
        // Explícito vence a derivação.
        new DocumentAnalysisQueueOptions { LeaseSeconds = 300, HeartbeatSeconds = 40 }.EffectiveHeartbeatSeconds.Should().Be(40);
    }

    [Fact]
    public void PolicySync_PeriodicInterval_MustBePositive()
    {
        var opt = new PolicySyncQueueOptions { PeriodicIntervalMinutes = 0 };
        opt.TryValidate(out var error).Should().BeFalse();
        error.Should().Contain("PeriodicIntervalMinutes");
    }
}
