namespace AegisScore.Infrastructure.Scoring;

/// <summary>
/// Parâmetros de auditoria do Aegis Score, ligados à seção "Scoring" do appsettings.
/// </summary>
public sealed class ScoringOptions
{
    public const string SectionName = "Scoring";

    /// <summary>
    /// Idade máxima, em horas, de um sinal de telemetria para que ele ainda PROVE um controle.
    /// Passado esse prazo o sinal é tratado como ausente e o controle volta a ser ponto cego.
    ///
    /// 72h por padrão: cobre um fim de semana inteiro sem alarme falso, mas não deixa um conector morto
    /// passar por cobertura ativa na segunda-feira. Num painel de postura, o pior erro é sumir com o
    /// problema da tela justamente quando ele começa.
    ///
    /// ⚠️ Zero ou negativo DESLIGA a checagem (nunca "tudo obsoleto"): uma configuração errada não pode
    /// transformar o painel inteiro em ponto cego — fail-safe na direção de não gritar sem motivo.
    /// </summary>
    public int DefaultSignalFreshnessHours { get; set; } = 72;

    /// <summary>Janela já convertida; <see cref="Timeout.InfiniteTimeSpan"/> quando desligada.</summary>
    public TimeSpan FreshnessWindow => DefaultSignalFreshnessHours > 0
        ? TimeSpan.FromHours(DefaultSignalFreshnessHours)
        : Timeout.InfiniteTimeSpan;
}
