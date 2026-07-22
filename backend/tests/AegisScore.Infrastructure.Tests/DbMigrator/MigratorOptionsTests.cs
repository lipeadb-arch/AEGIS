using AegisScore.DbMigrator;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.DbMigrator;

/// <summary>
/// [AEGIS-AUD-052] Contrato de linha de comando do migrator.
///
/// O caso mais importante aqui é a RECUSA de <c>--connection</c>: aceitar connection string por
/// argumento deixaria a senha no histórico do shell e visível na lista de processos do host. É uma
/// porta que precisa continuar fechada mesmo que alguém a considere conveniente no futuro.
/// </summary>
public sealed class MigratorOptionsTests
{
    [Fact]
    public void SemArgumentos_UsaOFluxoPadrao()
    {
        var options = MigratorOptions.Parse([]);

        options.Error.Should().BeNull();
        options.VerifyOnly.Should().BeFalse();
        options.SkipSeed.Should().BeFalse("o padrão é migrate → seed → verify");
        options.LockTimeoutSeconds.Should().Be(MigratorOptions.DefaultLockTimeoutSeconds);
    }

    [Fact]
    public void VerifyOnly_EhReconhecido()
    {
        MigratorOptions.Parse(["--verify-only"]).VerifyOnly.Should().BeTrue();
    }

    [Fact]
    public void SkipSeed_EhReconhecido()
    {
        MigratorOptions.Parse(["--skip-seed"]).SkipSeed.Should().BeTrue();
    }

    [Theory]
    [InlineData("--lock-timeout-seconds", "90")]
    [InlineData("--lock-timeout-seconds=90", null)]
    public void LockTimeout_AceitaAsDuasFormas(string first, string? second)
    {
        var args = second is null ? new[] { first } : new[] { first, second };

        MigratorOptions.Parse(args).LockTimeoutSeconds.Should().Be(90);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    public void LockTimeoutInvalido_EhRejeitado(string valor)
    {
        var options = MigratorOptions.Parse(["--lock-timeout-seconds", valor]);

        options.Error.Should().NotBeNull();
    }

    [Fact]
    public void LockTimeoutSemValor_EhRejeitado()
    {
        MigratorOptions.Parse(["--lock-timeout-seconds"]).Error.Should().NotBeNull();
    }

    [Fact]
    public void Environment_EhReconhecido()
    {
        MigratorOptions.Parse(["--environment", "Staging"]).Environment.Should().Be("Staging");
    }

    [Fact]
    public void Help_EhReconhecido()
    {
        MigratorOptions.Parse(["--help"]).ShowHelp.Should().BeTrue();
    }

    [Theory]
    [InlineData("--connection")]
    [InlineData("--connection-string")]
    [InlineData("-c")]
    public void ConnectionString_PorArgumento_EhSempreRecusada(string flag)
    {
        var options = MigratorOptions.Parse([flag, "Host=x;Password=segredo"]);

        options.Error.Should().NotBeNull();
        options.Error.Should().Contain("não é aceito");
        options.Error.Should().NotContain("segredo",
            "o valor recusado pode JÁ conter a senha — a mensagem de erro não pode ecoá-lo");
    }

    [Fact]
    public void ArgumentoDesconhecido_EhRejeitado()
    {
        MigratorOptions.Parse(["--force"]).Error.Should().Contain("desconhecido");
    }

    [Fact]
    public void VerifyOnlyComSkipSeed_EhRejeitadoPorRedundancia()
    {
        MigratorOptions.Parse(["--verify-only", "--skip-seed"]).Error.Should().NotBeNull();
    }

    [Fact]
    public void HelpText_NaoSugereConnectionStringPorArgumento()
    {
        MigratorOptions.HelpText.Should().NotContain("--connection");
        MigratorOptions.HelpText.Should().Contain("user-secrets");
    }

    [Fact]
    public void CodigosDeSaida_SaoDistintos()
    {
        int[] codes =
        [
            MigratorExitCode.Success,
            MigratorExitCode.InvalidConfiguration,
            MigratorExitCode.MigrationFailure,
            MigratorExitCode.SeedFailure,
            MigratorExitCode.VerificationFailure,
            MigratorExitCode.DatabaseUnreachable,
            MigratorExitCode.LockNotAcquired,
        ];

        codes.Should().OnlyHaveUniqueItems("um job de deploy decide o próximo passo por estes códigos");
        MigratorExitCode.Success.Should().Be(0);
    }

    [Fact]
    public void ChaveDoAdvisoryLock_EhEstavel()
    {
        // Se estes valores mudarem num refactor, duas execuções passariam a usar chaves diferentes e
        // rodariam simultaneamente sem que ninguém percebesse. O teste existe para travar isso.
        PostgresAdvisoryLock.ClassId.Should().Be(1095059273);
        PostgresAdvisoryLock.ObjectId.Should().Be(52);
    }
}
