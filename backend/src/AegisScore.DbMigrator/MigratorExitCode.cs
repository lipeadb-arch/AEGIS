namespace AegisScore.DbMigrator;

/// <summary>
/// [AEGIS-AUD-052] Códigos de saída do migrator. Distintos e estáveis: são o contrato que um job de
/// implantação (GitHub Actions, init container, Kubernetes Job) usa para decidir se prossegue.
/// Nunca há sucesso parcial — qualquer etapa que falhe interrompe as seguintes.
/// </summary>
public static class MigratorExitCode
{
    /// <summary>Todas as etapas concluídas e verificação final aprovada.</summary>
    public const int Success = 0;

    /// <summary>Argumento desconhecido, valor inválido ou connection string ausente.</summary>
    public const int InvalidConfiguration = 1;

    /// <summary>Falha ao aplicar migrations de algum dos contextos.</summary>
    public const int MigrationFailure = 2;

    /// <summary>Falha ao semear catálogo ou regras.</summary>
    public const int SeedFailure = 3;

    /// <summary>Migrations e seed concluíram, mas a verificação final reprovou.</summary>
    public const int VerificationFailure = 4;

    /// <summary>Banco inacessível (rede, credencial, servidor fora).</summary>
    public const int DatabaseUnreachable = 5;

    /// <summary>Advisory lock não adquirido dentro do timeout — outra execução está em andamento.</summary>
    public const int LockNotAcquired = 6;
}
