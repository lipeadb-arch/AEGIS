using System;
using System.Threading;
using System.Threading.Tasks;

namespace AegisScore.Application.Identity.Adm;

/// <summary>
/// [AEGIS-ADM-01] Porta de persistência e reconciliação do ADM de identidade.
///
/// Separação deliberada entre PREPARAR e SALVAR: a aquisição e o snapshot agregado da Evidence Fabric
/// precisam entrar no MESMO <c>SaveChanges</c>. Se a aquisição falhasse depois de o snapshot ter sido
/// gravado, existiria uma avaliação aparentemente sustentada por um registro que não existe — exatamente a
/// inconsistência que este contrato foi desenhado para tornar impossível.
///
/// A LEITURA é feita de volta do banco, e não do objeto que entrou. É isso que transforma "a avaliação
/// consome a aquisição persistida" de promessa em propriedade verificável: se a gravação não aconteceu, não
/// há o que ler, e não há avaliação.
/// </summary>
public interface IIdentityAcquisitionStore
{
    /// <summary>
    /// Abre a SEÇÃO CRÍTICA de gravação desta origem dentro da transação corrente, e deve ser chamada ANTES
    /// de qualquer leitura que informe uma decisão de recência.
    ///
    /// Por que uma trava, e não apenas a atomicidade do <c>SaveChanges</c>: decidir "esta coleta é mais nova
    /// que o estado atual?" é um LER → DECIDIR → GRAVAR. Duas transações podem ler a MESMA versão antiga,
    /// ambas se julgarem mais recentes e gravar em ordem inversa — sem deadlock e sem violar unicidade, ou
    /// seja, sem nada que denuncie o erro. A atomicidade garante que cada uma escreveu por inteiro; não
    /// garante que a última a escrever era a mais recente.
    ///
    /// Escopo e ORDEM definidos (sempre nesta sequência, para que dois escritores nunca se cruzem):
    ///   1. o DIRETÓRIO (tenant + namespace) — onde vivem as entidades canônicas e os vínculos;
    ///   2. o CONECTOR (tenant + configuração) — onde vivem o snapshot agregado e a saúde da integração.
    ///
    /// Fora de uma transação a trava não teria efeito (o autocommit a liberaria em seguida), e por isso a
    /// ausência de transação é recusada em vez de silenciosamente ignorada.
    /// </summary>
    Task LockOriginAsync(IdentityAcquisitionOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Reconcilia a aquisição no contexto de persistência SEM salvar: registra (ou reaproveita, no retry da
    /// MESMA aquisição) o registro da coleta, resolve as entidades canônicas pelos vínculos de origem, cria os
    /// vínculos que faltam, grava as observações e os estados por conjunto, e atualiza a projeção de estado
    /// atual respeitando a ordenação determinística.
    ///
    /// Quem chama é responsável pelo <c>SaveChanges</c> — e por refazer a chamada quando uma corrida de
    /// unicidade a rejeitar. Reprocessar a MESMA aquisição com o MESMO conteúdo é idempotente: não duplica
    /// observação nem vínculo e não reescreve nada. Reapresentá-la com conteúdo DIFERENTE é recusado com
    /// <see cref="IdentityAcquisitionConflictException"/>, antes de qualquer escrita.
    /// </summary>
    Task PrepareAsync(IdentityAcquisitionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Lê a aquisição PERSISTIDA (tenant-safe, sem tracking) com seus conjuntos, completude e objetos
    /// observados. <c>null</c> quando ela não existe neste tenant — inclusive quando existe em OUTRO, caso em
    /// que a resposta correta é indistinguível de "não existe".
    /// </summary>
    Task<IdentityAcquisitionRecord?> ReadAsync(Guid acquisitionId, CancellationToken ct = default);
}
