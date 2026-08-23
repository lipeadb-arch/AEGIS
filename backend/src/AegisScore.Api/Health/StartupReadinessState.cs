namespace AegisScore.Api.Health;

/// <summary>
/// [AEGIS-MVP-OPS-01] Autoridade em memória de que a validação estrutural COMPLETA do arranque
/// (<see cref="AegisScore.Infrastructure.Persistence.SchemaReadinessGuard.EnsureReadyAsync"/>) foi
/// APROVADA — antes de <c>app.Run()</c>. É o que o readiness recorrente consulta para NÃO reexecutar,
/// a cada probe do orquestrador, a verificação cara do pacote (catálogo/metodologia/regras/proveniência).
///
/// Latch MONOTÔNICO: começa "não pronto" e transita UMA vez, em sentido único, para "pronto". Não há
/// método público para voltar a "não pronto" — a integridade do pacote é responsabilidade do
/// AegisScore.DbMigrator ANTES da API; este estado não é um monitor contínuo de adulteração.
///
/// Sem informação sensível, sem persistência em banco, sem cache distribuído, sem tabela/migration, sem
/// hosted service, sem timer/worker. Thread-safe pelo campo <c>volatile</c> (leitura/escrita de um bool
/// é atômica; a transição é idempotente — marcar "pronto" repetidamente é seguro). Registrado como
/// singleton no DI.
/// </summary>
public sealed class StartupReadinessState
{
    private volatile bool _isReady;

    /// <summary>True somente depois que o guard completo do arranque foi aprovado por <see cref="MarkReady"/>.</summary>
    public bool IsReady => _isReady;

    /// <summary>
    /// Marca o arranque como aprovado. Chamado UMA vez pelo <c>Program.cs</c>, apenas DEPOIS de
    /// <c>SchemaReadinessGuard.EnsureReadyAsync</c> retornar com sucesso. Idempotente e sem volta.
    /// </summary>
    public void MarkReady() => _isReady = true;
}
