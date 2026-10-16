using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Abstractions;

/// <summary>
/// [AEGIS-AUD-041] Proteção do payload BRUTO da evidência em repouso (Data Protection), com purpose PRÓPRIO
/// (<c>AegisScore.EvidenceSignal.RawPayload.v1</c>), distinto do purpose dos segredos de conector. O bruto
/// nunca é devolvido pela API/tela; o Unprotect existe para o motor de score (Entrega 3), não para saída.
/// </summary>
public interface IEvidenceRawPayloadProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}
