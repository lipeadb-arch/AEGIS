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
    /// Reconcilia a aquisição no contexto de persistência SEM salvar: registra (ou reaproveita, no retry da
    /// MESMA aquisição) o registro da coleta, resolve as entidades canônicas pelos vínculos de origem, cria os
    /// vínculos que faltam, grava as observações e os estados por conjunto, e atualiza a projeção de estado
    /// atual respeitando a ordenação determinística.
    ///
    /// Quem chama é responsável pelo <c>SaveChanges</c> — e por refazer a chamada quando uma corrida de
    /// unicidade a rejeitar. Reprocessar a MESMA aquisição é idempotente: não duplica observação nem vínculo.
    /// </summary>
    Task PrepareAsync(IdentityAcquisitionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Lê a aquisição PERSISTIDA (tenant-safe, sem tracking) com seus conjuntos, completude e objetos
    /// observados. <c>null</c> quando ela não existe neste tenant — inclusive quando existe em OUTRO, caso em
    /// que a resposta correta é indistinguível de "não existe".
    /// </summary>
    Task<IdentityAcquisitionRecord?> ReadAsync(Guid acquisitionId, CancellationToken ct = default);
}
