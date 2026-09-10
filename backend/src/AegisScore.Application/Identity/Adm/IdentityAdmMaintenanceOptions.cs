namespace AegisScore.Application.Identity.Adm;

/// <summary>
/// [AEGIS-ADM-02] Configuração da manutenção do ADM de identidade.
///
/// Os padrões foram escolhidos com uma assimetria deliberada: <b>consolidar é seguro e liga por padrão</b>
/// (escreve linhas novas, não destrói nada, e sem ele o histórico simplesmente não existe); <b>remover é
/// destrutivo e nasce desligado</b>. Um expurgo que começa a rodar porque alguém subiu a aplicação é
/// exatamente o tipo de efeito colateral que ninguém autoriza e todo mundo descobre tarde.
/// </summary>
public sealed class IdentityAdmMaintenanceOptions
{
    public const string SectionName = "IdentityAdm:Maintenance";

    /// <summary>Liga o ciclo de manutenção em fundo. Desligar aqui para o motor inteiro.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Executa a consolidação mensal. Ligado: é escrita ADITIVA de agregados, e é o que sustenta a leitura do
    /// histórico — sem ele a API responderia "não consolidado" para sempre.
    /// </summary>
    public bool ConsolidationEnabled { get; set; } = true;

    /// <summary>
    /// Executa a REMOÇÃO (detalhe vencido, aquisições sem referência e consolidações fora da janela).
    /// DESLIGADO por padrão, e é a decisão central destas opções: exigir configuração explícita.
    /// </summary>
    public bool RemovalEnabled { get; set; }

    /// <summary>Horas entre ciclos. Duas por dia bastam para uma janela medida em meses.</summary>
    public int PeriodHours { get; set; } = 12;

    /// <summary>Espera antes do PRIMEIRO ciclo, para não disputar o boot com a migração e o aquecimento.</summary>
    public int InitialDelaySeconds { get; set; } = 120;

    /// <summary>Origens por passada — o lote. O que sobrar é retomado pelo cursor na passada seguinte.</summary>
    public int MaxDirectoriesPerCycle { get; set; } = 25;

    /// <summary>Aquisições examinadas por origem numa passada. Impede uma origem acumulada de monopolizar o ciclo.</summary>
    public int MaxAcquisitionsPerDirectory { get; set; } = 200;

    /// <summary>Valida os limites; devolve <c>false</c> e uma mensagem clara quando algum valor é inválido.</summary>
    public bool TryValidate(out string? error)
    {
        if (PeriodHours <= 0) { error = "IdentityAdm:Maintenance:PeriodHours precisa ser maior que zero."; return false; }
        if (InitialDelaySeconds < 0) { error = "IdentityAdm:Maintenance:InitialDelaySeconds não pode ser negativo."; return false; }
        if (MaxDirectoriesPerCycle <= 0) { error = "IdentityAdm:Maintenance:MaxDirectoriesPerCycle precisa ser maior que zero."; return false; }
        if (MaxAcquisitionsPerDirectory <= 0) { error = "IdentityAdm:Maintenance:MaxAcquisitionsPerDirectory precisa ser maior que zero."; return false; }

        // A retenção só é segura sobre um mês que a MESMA passada consolidou e confirmou. Ligar a remoção com a
        // consolidação desligada não é uma configuração "mais agressiva": é a única combinação que apaga
        // evidência sem deixar histórico no lugar dela.
        if (RemovalEnabled && !ConsolidationEnabled)
        {
            error = "IdentityAdm:Maintenance:RemovalEnabled=true exige ConsolidationEnabled=true — a retenção "
                    + "remove apenas o que a consolidação daquela passada já incorporou ao histórico.";
            return false;
        }

        error = null;
        return true;
    }
}
