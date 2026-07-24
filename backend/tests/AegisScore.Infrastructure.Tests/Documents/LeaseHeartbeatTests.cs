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

    [Fact]   // FAIL-CLOSED: renovação que LANÇA exceção não comprova posse → trata como lease perdido
    public async Task RenovacaoLancaExcecao_FailClosed_CancelaEEncerra()
    {
        var clock = new FakeTimeProvider();
        using var leaseCts = new CancellationTokenSource();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        leaseCts.Token.Register(() => cancelled.TrySetResult());
        var attempts = 0;

        await using var hb = LeaseHeartbeat.Start(
            _ =>
            {
                Interlocked.Increment(ref attempts);
                throw new InvalidOperationException("banco indisponível na renovação");
            },
            Interval, clock, leaseCts, NullLogger.Instance);

        clock.Advance(Interval);                  // a renovação lança
        await cancelled.Task.WaitAsync(Safety);   // o sinal de processamento é cancelado

        leaseCts.IsCancellationRequested.Should().BeTrue(
            "renovação que falha não comprova posse → lease perdido (fail-closed)");

        // O heartbeat ENCERROU: avançar o relógio não dispara nova renovação — o trabalho não fica esperando.
        clock.Advance(Interval);
        clock.Advance(Interval);
        attempts.Should().Be(1, "após falhar fechado, o heartbeat não tenta renovar de novo");
    }
}
