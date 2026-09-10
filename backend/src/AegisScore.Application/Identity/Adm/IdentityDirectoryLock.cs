using System;
using System.Security.Cryptography;
using System.Text;

namespace AegisScore.Application.Identity.Adm;

/// <summary>
/// [AEGIS-ADM-01/02] A CHAVE da seção crítica de um diretório de identidade — num lugar só.
///
/// Três caminhos disputam as mesmas linhas: a COLETA (grava aquisição, vínculos e projeção), a CONSOLIDAÇÃO
/// mensal (lê as aquisições do mês e escolhe a fotografia) e a RETENÇÃO (remove detalhe e cabeçalhos vencidos).
/// Eles só se serializam se pedirem a MESMA trava — e "a mesma" aqui significa o mesmo número de 64 bits.
/// Recalcular a chave em cada componente é o tipo de duplicação que funciona até alguém mudar o prefixo de um
/// lado: as travas continuariam sendo tomadas, sem nunca se encontrarem, e a proteção viraria decoração.
///
/// Derivada por SHA-256, e deliberadamente NÃO pelo hash de string do .NET: aquele é aleatorizado por
/// processo, e duas instâncias da API escolheriam travas diferentes para o mesmo diretório.
/// </summary>
public static class IdentityDirectoryLock
{
    /// <summary>Chave da trava consultiva de <c>(tenant, namespace do diretório)</c>.</summary>
    public static long AdvisoryKey(Guid tenantId, string directoryNamespace)
    {
        var ns = (directoryNamespace ?? "").Trim();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"aegis-adm-identity|{tenantId:D}|{ns}"));
        return BitConverter.ToInt64(bytes, 0);
    }
}
