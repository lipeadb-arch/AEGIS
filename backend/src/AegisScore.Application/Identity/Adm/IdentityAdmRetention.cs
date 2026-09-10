using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Identity.Adm;

// ============================================================================
//  [AEGIS-ADM-02] Retenção operacional e consolidação mensal do ADM de identidade
// ============================================================================
// Duas janelas com propósitos diferentes, e é a diferença que justifica existirem duas:
//
//   • DETALHE OPERACIONAL (90 dias) — os objetos observados. É o que cresce sem limite (uma coleta por
//     avaliação, cada uma com a população inteira) e é o que menos gente relê depois de um trimestre.
//   • CONSOLIDAÇÃO MENSAL (12 meses) — a evolução da origem. Precisa sobreviver ao expurgo do detalhe, e por
//     isso guarda a sua própria cópia agregada em vez de apontar para a evidência que expira.
//
// O que NENHUMA das duas alcança: `KnightAffectedObjects`, comprovações de remediação, fotografias publicadas
// e seus hashes. Aquilo é a prova congelada do produto, tem ciclo de vida próprio e não é tocado aqui.

/// <summary>
/// A POLÍTICA de retenção — os números, as fronteiras de tempo e a aritmética dos meses, num lugar só.
///
/// Está separada do serviço de propósito: os limites precisam ser os MESMOS na consolidação, na remoção, na
/// leitura da API e nos testes. Recalculá-los em cada ponto é como duas listas de feriados — elas concordam
/// até o dia em que discordam, e a divergência aparece como dado sumido.
/// </summary>
public static class IdentityAdmRetentionPolicy
{
    /// <summary>Versão da REGRA de consolidação. Muda quando a semântica do mês muda, não quando a origem muda.</summary>
    public const string ConsolidationVersion = "aegis-adm-identity-monthly-v1";

    /// <summary>Dias de DETALHE operacional (as observações), contados por <c>AcquiredAt</c>.</summary>
    public const int DetailRetentionDays = 90;

    /// <summary>
    /// Meses de consolidação: o corrente (provisório) e os 11 anteriores. Doze é a janela que permite comparar
    /// um mês com o mesmo mês do ano anterior sem guardar dois anos de nada.
    /// </summary>
    public const int RetainedMonths = 12;

    /// <summary>Primeiro dia do mês de calendário UTC a que um instante pertence.</summary>
    public static DateOnly MonthOf(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return new DateOnly(utc.Year, utc.Month, 1);
    }

    /// <summary>Início (inclusivo) do mês, em UTC — a fronteira usada em toda comparação temporal.</summary>
    public static DateTimeOffset StartOf(DateOnly month) =>
        new(month.Year, month.Month, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Início (EXCLUSIVO) do mês seguinte. Comparar por <c>&lt; fim</c> evita o erro de um dia/um tique.</summary>
    public static DateTimeOffset EndOf(DateOnly month) => StartOf(month.AddMonths(1));

    /// <summary>Mês mais ANTIGO ainda retido, dado o instante de referência.</summary>
    public static DateOnly OldestRetainedMonth(DateTimeOffset now) =>
        MonthOf(now).AddMonths(-(RetainedMonths - 1));

    /// <summary>
    /// Instante EXATO em que os 90 dias se completam, contado a partir de <paramref name="now"/>. É a
    /// fronteira da elegibilidade e o limite declarado no relatório: só é candidata a aquisição cujo instante
    /// de aquisição é ESTRITAMENTE ANTERIOR a ele — o detalhe vive 90 dias completos, nem um minuto a menos.
    ///
    /// A comparação é de INSTANTE, e não de dia. Arredondar para o dia acrescentaria até 24 horas à janela, e
    /// um prazo declarado como "90 dias" que na prática varia entre 90 e 91 é um prazo que ninguém confere. A
    /// varredura consegue ser exata porque filtra e ordena pela chave derivada
    /// <c>IdentityAcquisition.AcquiredAtUtc</c>, que é comparável e ordenável em todos os provedores em que
    /// este código roda.
    /// </summary>
    public static DateTimeOffset DetailCutoff(DateTimeOffset now) =>
        now.ToUniversalTime().AddDays(-DetailRetentionDays);

    /// <summary>O mesmo corte na forma da chave de varredura — o valor efetivamente comparado no banco.</summary>
    public static DateTime DetailCutoffKey(DateTimeOffset now) => DetailCutoff(now).UtcDateTime;

    /// <summary>Os meses retidos, do mais ANTIGO ao corrente — a ordem em que a série é lida.</summary>
    public static IReadOnlyList<DateOnly> RetainedWindow(DateTimeOffset now)
    {
        var oldest = OldestRetainedMonth(now);
        var months = new List<DateOnly>(RetainedMonths);
        for (var i = 0; i < RetainedMonths; i++) months.Add(oldest.AddMonths(i));
        return months;
    }
}

/// <summary>
/// Recusa de reapresentar uma aquisição cujo DETALHE já expirou por retenção.
///
/// Aceitar o replay repovoaria silenciosamente observações que foram deliberadamente removidas — e o registro
/// passaria a exibir um detalhe "íntegro" que na verdade foi reconstruído depois, possivelmente por uma coleta
/// diferente da que a avaliação citou. A recusa acontece ANTES de qualquer escrita.
/// </summary>
public sealed class IdentityAcquisitionDetailRetiredException : InvalidOperationException
{
    public IdentityAcquisitionDetailRetiredException(Guid acquisitionId, DateTimeOffset retiredAt)
        : base($"A aquisição de identidade {acquisitionId} teve o detalhe expirado por retenção em "
               + $"{retiredAt.ToUniversalTime():O} e não pode ser reapresentada: repovoar as observações "
               + "removidas alteraria uma evidência que uma avaliação já pode citar. Uma coleta nova é uma "
               + "aquisição NOVA, com identificador próprio.")
    {
        AcquisitionId = acquisitionId;
        RetiredAt = retiredAt;
    }

    public Guid AcquisitionId { get; }
    public DateTimeOffset RetiredAt { get; }
}

/// <summary>
/// Recusa de CRIAR uma aquisição que a retenção já varreu — o contrato que impede o repovoamento silencioso
/// de uma coleta removida por INTEIRO.
///
/// A recusa por <see cref="IdentityAcquisitionDetailRetiredException"/> só alcança a aquisição cuja LINHA
/// ainda existe. Quando nada mais a referenciava e ela saiu inteira, uma reapresentação do mesmo
/// identificador cairia no caminho de criação e recriaria, sem avisar, uma evidência que o produto tinha
/// deliberadamente removido — sob o mesmo identificador que o histórico e as avaliações citam.
///
/// A memória que impede isso é a FRONTEIRA gravada na consolidação do mês
/// (<c>IdentityMonthlyRollup.RetentionSweptThroughAt</c>), e não uma lista de identificadores removidos: uma
/// lista dessas cresceria para sempre, que é o oposto do propósito de uma retenção.
///
/// LIMITE declarado: passados os 12 meses, a consolidação do mês também expira, e com ela a fronteira. Uma
/// coleta tão antiga voltaria a ser aceita — e sairia na varredura seguinte, porque continua vencida.
/// </summary>
public sealed class IdentityAcquisitionRetentionSweptException : InvalidOperationException
{
    public IdentityAcquisitionRetentionSweptException(
        Guid acquisitionId, DateTimeOffset acquiredAt, DateOnly month, DateTimeOffset sweptThrough)
        : base($"A aquisição de identidade {acquisitionId}, de {acquiredAt.ToUniversalTime():O}, não pode ser "
               + $"registrada: a retenção já varreu {month:yyyy-MM} até {sweptThrough.ToUniversalTime():O}. "
               + "Aceitá-la repovoaria em silêncio uma evidência que o produto removeu por vencimento, sob um "
               + "identificador que o histórico e as avaliações já podem citar. Uma coleta nova é uma "
               + "aquisição NOVA, com instante e identificador próprios.")
    {
        AcquisitionId = acquisitionId;
        AcquiredAt = acquiredAt;
        Month = month;
        SweptThrough = sweptThrough;
    }

    public Guid AcquisitionId { get; }
    public DateTimeOffset AcquiredAt { get; }
    public DateOnly Month { get; }
    public DateTimeOffset SweptThrough { get; }
}

/// <summary>
/// O que a fixação de uma aquisição citada conseguiu garantir. São duas perguntas DIFERENTES, e é por isso
/// que a resposta não é um booleano: "a linha ainda existe?" e "o detalhe observado continua disponível?".
///
/// Uma avaliação pode legitimamente citar uma aquisição cujo detalhe expirou — o cabeçalho, os fatos e a
/// completude por conjunto continuam lá, e os objetos afetados daquele veredito estão congelados em
/// <c>KnightAffectedObjects</c>. O que ela não pode é apresentar esse estado como detalhe íntegro.
/// </summary>
public sealed record IdentityAcquisitionPin(bool Exists, DateTimeOffset? DetailRetiredAt)
{
    /// <summary>A linha existe E o detalhe observado continua disponível.</summary>
    public bool DetailAvailable => Exists && DetailRetiredAt is null;

    /// <summary>A aquisição não está mais lá — citá-la produziria um veredito apontando para o vazio.</summary>
    public static readonly IdentityAcquisitionPin Missing = new(false, null);
}

/// <summary>
/// Identifica UMA origem de identidade para a manutenção — e serve de CURSOR de retomada. A ordem é
/// determinística <c>(tenant, provedor, namespace)</c> justamente para que "continue de onde parou" seja uma
/// posição, e não uma esperança.
/// </summary>
public sealed record IdentityAdmDirectoryKey(Guid TenantId, KnightSourceType Provider, string DirectoryNamespace);

/// <summary>
/// Onde a varredura parou. Não é só a origem: é a origem DENTRO de uma fase.
///
/// A manutenção percorre duas populações em sequência — primeiro as origens que ainda têm aquisições, depois
/// as ÓRFÃS, que só existem como CONSOLIDAÇÃO (todas as aquisições delas já saíram, e as linhas mensais
/// precisam expirar mesmo assim, senão sobra crescimento silencioso exatamente onde a retenção deveria agir).
///
/// Duas fases, e não uma união das duas, por um motivo concreto: a ordenação e o filtro do cursor precisam
/// ser resolvidos pelo MESMO banco, na mesma consulta. Comparar chaves de duas consultas em memória exigiria
/// que a ordem do .NET coincidisse com a do banco — e não coincide: <c>Guid.CompareTo</c> compara o primeiro
/// campo COM SINAL, o PostgreSQL compara <c>uuid</c> byte a byte sem sinal, e o SQLite compara o BLOB na
/// ordem interna do .NET. Três ordens diferentes; uma origem seria pulada para sempre no primeiro desacordo.
/// </summary>
/// <param name="OrphanRollups">
/// FALSE = ainda percorrendo as origens com aquisições. TRUE = já na segunda fase, das origens que só têm
/// consolidação. A fase é parte da posição: sem ela, "continue depois desta origem" seria ambíguo.
/// </param>
public sealed record IdentityAdmCursor(IdentityAdmDirectoryKey Directory, bool OrphanRollups = false);

/// <summary>
/// O que uma passada de manutenção deve fazer. Todos os limites são explícitos porque a manutenção roda em
/// fundo: uma operação sem teto é a que derruba o banco às 3h.
/// </summary>
/// <param name="Now">
/// Instante de referência. <c>null</c> = o relógio injetado. Existe para que os limites de 90 dias e de 12
/// meses sejam TESTÁVEIS sem esperar noventa dias.
/// </param>
/// <param name="Simulate">
/// SIMULAÇÃO: apura candidatos, protegidos e motivos e NÃO escreve nada — nem consolidação, nem remoção.
/// </param>
/// <param name="Consolidate">Executa a consolidação mensal.</param>
/// <param name="Remove">
/// Executa a REMOÇÃO. Nasce desligada na configuração: expurgo automático precisa ser uma decisão declarada,
/// nunca um efeito colateral de subir a aplicação.
/// </param>
/// <param name="MaxDirectories">Teto de origens por passada — o lote. A passada seguinte retoma pelo cursor.</param>
/// <param name="MaxAcquisitionsPerDirectory">
/// Teto de aquisições examinadas por origem numa passada. Impede que uma origem com anos de acúmulo monopolize
/// o ciclo, e faz o expurgo convergir em várias passadas em vez de uma transação gigante.
/// </param>
/// <param name="ResumeAfter">Cursor: processa as origens ESTRITAMENTE posteriores a esta. <c>null</c> = do início.</param>
public sealed record IdentityAdmMaintenanceRequest(
    DateTimeOffset? Now = null,
    bool Simulate = false,
    bool Consolidate = true,
    bool Remove = false,
    int MaxDirectories = 25,
    int MaxAcquisitionsPerDirectory = 200,
    IdentityAdmCursor? ResumeAfter = null);

/// <summary>Por que uma aquisição vencida NÃO foi removida — o motivo é a parte que interessa a quem audita.</summary>
public sealed record IdentityAdmProtectedAcquisition(
    Guid AcquisitionId, DateTimeOffset AcquiredAt, string Reason);

/// <summary>O que a manutenção fez (ou faria, em simulação) em UMA origem.</summary>
/// <param name="DetailExpired">Aquisições cujo DETALHE foi removido, com cabeçalho e fatos preservados.</param>
/// <param name="AcquisitionsRemoved">Aquisições removidas por INTEIRO (nada no produto as referenciava).</param>
/// <param name="ObservationsRemoved">Observações efetivamente removidas — a medida do que o expurgo liberou.</param>
/// <param name="Protected">
/// As EXCEÇÕES examinadas NESTA passada: evidência mantida além dos 90 dias porque uma avaliação ainda depende
/// dela, cada uma com o motivo. Elas existem para deixar claro que 90 dias é o prazo do DETALHE OPERACIONAL,
/// não o prazo máximo de toda evidência do produto.
/// </param>
/// <param name="ProtectedRetained">
/// TOTAL de aquisições desta origem mais antigas que o corte de 90 dias e ainda retidas por referência —
/// inclusive as que já foram processadas em passadas anteriores e por isso não reaparecem em
/// <paramref name="Protected"/>. É o número que responde "quanta evidência vencida continua aqui, e por quê".
/// </param>
/// <param name="Failure">
/// Motivo pelo qual esta origem NÃO foi mantida nesta passada (<c>null</c> = correu bem). Uma origem com
/// problema não pode paralisar as demais nem, muito menos, ter evidência removida pela metade: consolidação e
/// retenção correm na MESMA transação, e o que falha volta atrás por inteiro.
/// </param>
public sealed record IdentityAdmDirectoryReport(
    IdentityAdmDirectoryKey Directory,
    int MonthsConsolidated,
    int RollupsRemoved,
    int AcquisitionsExamined,
    int DetailExpired,
    int AcquisitionsRemoved,
    int ObservationsRemoved,
    int ProtectedRetained,
    IReadOnlyList<IdentityAdmProtectedAcquisition> Protected,
    string? Failure = null);

/// <summary>
/// O resultado de uma passada. <see cref="Completed"/> <c>false</c> significa que o LOTE acabou antes das
/// origens — e <see cref="NextCursor"/> diz por onde continuar. Não é erro: é a manutenção respeitando o teto.
/// </summary>
/// <param name="DetailCutoff">
/// Instante EXATO em que os 90 dias se completam, e fronteira efetiva da elegibilidade: só é candidata a
/// aquisição adquirida ANTES dele. Sem arredondamento — o prazo declarado é o prazo aplicado.
/// </param>
public sealed record IdentityAdmMaintenanceReport(
    DateTimeOffset ReferenceInstant,
    DateTimeOffset DetailCutoff,
    DateOnly OldestRetainedMonth,
    bool Simulated,
    bool RemovalRequested,
    bool Completed,
    IdentityAdmCursor? NextCursor,
    IReadOnlyList<IdentityAdmDirectoryReport> Directories)
{
    /// <summary>Origens que FALHARAM nesta passada — nenhuma escrita ocorreu para elas.</summary>
    public IReadOnlyList<IdentityAdmDirectoryReport> Failed =>
        Directories.Where(d => d.Failure is not null).ToList();
}

/// <summary>
/// Manutenção do ADM de identidade: consolida os meses e aplica a retenção — nesta ordem, e nunca a segunda
/// sem a primeira ter sido CONFIRMADA no banco. Remover o que a consolidação ainda precisava ler destruiria o
/// histórico para economizar espaço.
///
/// Roda em lotes, é cancelável e retomável. Não conhece HTTP, não conhece tenant ambiente (varre os tenants
/// explicitamente, como os workers) e NUNCA é chamada por uma rota de leitura.
/// </summary>
public interface IIdentityAdmMaintenanceService
{
    /// <summary>
    /// Executa uma passada. <see cref="IdentityAdmMaintenanceRequest.Remove"/> SEM
    /// <see cref="IdentityAdmMaintenanceRequest.Consolidate"/> é RECUSADO: remover evidência numa passada que
    /// não consolidou nada é apagar a matéria-prima de um histórico que ninguém gravou.
    /// </summary>
    Task<IdentityAdmMaintenanceReport> RunAsync(
        IdentityAdmMaintenanceRequest request, CancellationToken ct = default);
}
