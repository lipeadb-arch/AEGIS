using AegisScore.Infrastructure.Documents;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Documents;

/// <summary>
/// [AEGIS-AUD-050] Batimento de lease. Determinístico via <see cref="FakeTimeProvider"/> — sem sleeps reais:
/// avançamos o relógio virtual e sincronizamos pela conclusão da renovação (um <see cref="TaskCompletionSource"/>),
/// com um timeout curto apenas como rede de segurança contra travamento do teste.
/// </summary>
public sealed class LeaseHeartbeatTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Renova_Periodicamente_EnquantoNaoDescartado()
    {
        var clock = new FakeTimeProvider();
        var count = 0;
        var firstRenewal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var leaseCts = new CancellationTokenSource();

        await using var hb = LeaseHeartbeat.Start(
            _ => { if (Interlocked.Increment(ref count) == 1) firstRenewal.TrySetResult(); return Task.FromResult(true); },
            Interval, clock, leaseCts, NullLogger.Instance);

        clock.Advance(Interval);                       // dispara a 1ª renovação
        await firstRenewal.Task.WaitAsync(Safety);     // sincroniza (não é um sleep de domínio)

        count.Should().BeGreaterThanOrEqualTo(1);
        leaseCts.IsCancellationRequested.Should().BeFalse("renovação bem-sucedida não cancela o trabalho");
    }

    [Fact]
    public async Task LeasePerdido_CancelaOProcessamento()
    {
        var clock = new FakeTimeProvider();
        using var leaseCts = new CancellationTokenSource();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        leaseCts.Token.Register(() => cancelled.TrySetResult());

        await using var hb = LeaseHeartbeat.Start(
            _ => Task.FromResult(false),   // renovação indica lease PERDIDO
            Interval, clock, leaseCts, NullLogger.Instance);

        clock.Advance(Interval);
        await cancelled.Task.WaitAsync(Safety);

        leaseCts.IsCancellationRequested.Should().BeTrue("perder o lease deve cancelar o processamento");
    }

    [Fact]
    public async Task FalhaTransitoriaNaRenovacao_NaoCancela_TentaDeNovo()
    {
        var clock = new FakeTimeProvider();
        using var leaseCts = new CancellationTokenSource();
        var attempts = 0;
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var hb = LeaseHeartbeat.Start(
            _ =>
            {
                var n = Interlocked.Increment(ref attempts);
                if (n == 1) throw new InvalidOperationException("banco momentaneamente indisponível");
                secondAttempt.TrySetResult();
                return Task.FromResult(true);
            },
            Interval, clock, leaseCts, NullLogger.Instance);

        clock.Advance(Interval);   // 1ª renovação lança (transitória)
        clock.Advance(Interval);   // 2ª renovação tenta de novo e sucede
        await secondAttempt.Task.WaitAsync(Safety);

        leaseCts.IsCancellationRequested.Should().BeFalse("uma falha transitória de renovação não derruba o trabalho");
    }
}
