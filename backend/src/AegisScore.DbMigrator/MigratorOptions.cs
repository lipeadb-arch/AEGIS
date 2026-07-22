using System.Globalization;

namespace AegisScore.DbMigrator;

/// <summary>
/// [AEGIS-AUD-052] Argumentos aceitos pelo migrator.
///
/// ⚠️ Não existe <c>--connection</c>, e a ausência é deliberada: uma connection string em argv fica
/// registrada no histórico do shell e visível na lista de processos de qualquer usuário do host. A
/// configuração vem sempre de <c>ConnectionStrings:AegisScore</c>, resolvida por appsettings, variável
/// de ambiente, <c>dotnet user-secrets</c> (Development) ou secret manager. Passar <c>--connection</c>
/// é rejeitado com mensagem explicando o motivo — falhar é melhor do que aceitar em silêncio.
/// </summary>
public sealed record MigratorOptions
{
    public const int DefaultLockTimeoutSeconds = 60;

    /// <summary>Apenas verifica o estado do banco; não migra nem semeia.</summary>
    public bool VerifyOnly { get; init; }

    /// <summary>Aplica migrations e verifica, sem semear catálogo e regras.</summary>
    public bool SkipSeed { get; init; }

    /// <summary>Tempo máximo de espera pelo advisory lock.</summary>
    public int LockTimeoutSeconds { get; init; } = DefaultLockTimeoutSeconds;

    /// <summary>Ambiente para resolução de <c>appsettings.{Environment}.json</c> e user-secrets.</summary>
    public string? Environment { get; init; }

    public bool ShowHelp { get; init; }

    /// <summary>Preenchido quando o parsing falha; o chamador sai com <see cref="MigratorExitCode.InvalidConfiguration"/>.</summary>
    public string? Error { get; init; }

    public static MigratorOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var verifyOnly = false;
        var skipSeed = false;
        var lockTimeout = DefaultLockTimeoutSeconds;
        string? environment = null;

        for (var i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            var (name, inlineValue) = Split(raw);

            switch (name)
            {
                case "--help" or "-h" or "-?":
                    return new MigratorOptions { ShowHelp = true };

                case "--verify-only":
                    verifyOnly = true;
                    break;

                case "--skip-seed":
                    skipSeed = true;
                    break;

                case "--lock-timeout-seconds":
                {
                    if (!TryTakeValue(args, ref i, inlineValue, out var value))
                        return Fail("--lock-timeout-seconds exige um valor em segundos.");

                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                        || seconds <= 0)
                        return Fail($"Valor inválido para --lock-timeout-seconds: '{value}'. Use um inteiro positivo.");

                    lockTimeout = seconds;
                    break;
                }

                case "--environment" or "-e":
                {
                    if (!TryTakeValue(args, ref i, inlineValue, out var value))
                        return Fail("--environment exige um valor (ex.: Development, Staging, Production).");

                    environment = value;
                    break;
                }

                // Recusa explícita: o valor pode JÁ conter a senha, então nem o ecoamos de volta.
                case "--connection" or "--connection-string" or "-c":
                    return Fail(
                        "--connection não é aceito. Uma connection string passada por argumento fica no " +
                        "histórico do shell e na lista de processos do host. Configure " +
                        "ConnectionStrings:AegisScore por variável de ambiente, user-secrets ou secret manager.");

                default:
                    return Fail($"Argumento desconhecido: '{raw}'. Use --help para ver as opções.");
            }
        }

        if (verifyOnly && skipSeed)
            return Fail("--verify-only e --skip-seed são redundantes juntos: --verify-only já não semeia.");

        return new MigratorOptions
        {
            VerifyOnly = verifyOnly,
            SkipSeed = skipSeed,
            LockTimeoutSeconds = lockTimeout,
            Environment = environment,
        };
    }

    private static MigratorOptions Fail(string error) => new() { Error = error };

    private static (string Name, string? Value) Split(string arg)
    {
        var eq = arg.IndexOf('=');
        return eq < 0 ? (arg, null) : (arg[..eq], arg[(eq + 1)..]);
    }

    private static bool TryTakeValue(string[] args, ref int i, string? inlineValue, out string value)
    {
        if (!string.IsNullOrEmpty(inlineValue))
        {
            value = inlineValue;
            return true;
        }

        if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
        {
            value = args[++i];
            return true;
        }

        value = "";
        return false;
    }

    public static string HelpText =>
        """
        AegisScore.DbMigrator — prepara o banco antes de subir a API (AEGIS-AUD-052).

        Comportamento padrão: migrate -> seed -> verify, sob advisory lock exclusivo.

        Uso:
          AegisScore.DbMigrator [opções]

        Opções:
          --verify-only                 Apenas verifica o estado do banco. Não migra nem semeia.
          --skip-seed                   Aplica migrations e verifica, sem semear catálogo e regras.
          --lock-timeout-seconds <n>    Espera máxima pelo advisory lock (padrão: 60).
          --environment, -e <nome>      Ambiente de configuração (padrão: DOTNET_ENVIRONMENT ou Production).
          --help, -h                    Mostra esta ajuda.

        Configuração:
          A connection string vem de ConnectionStrings:AegisScore, resolvida por appsettings.json,
          variáveis de ambiente, user-secrets (Development) ou secret manager.
          NÃO é aceita por linha de comando — isso a exporia no histórico e na lista de processos.

        Códigos de saída:
          0 sucesso | 1 configuração inválida | 2 falha de migration | 3 falha de seed
          4 verificação reprovada | 5 banco inacessível | 6 lock não adquirido
        """;
}
