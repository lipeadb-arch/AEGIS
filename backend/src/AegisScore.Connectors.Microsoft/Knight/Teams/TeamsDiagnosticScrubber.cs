using System.Collections.Generic;
using AegisScore.Connectors.Microsoft.Knight.PowerShell;

namespace AegisScore.Connectors.Microsoft.Knight.Teams;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-02] Sanitização do diagnóstico do adaptador do Microsoft Teams.
///
/// [AEGIS-KNIGHT-COVERAGE-03] A IMPLEMENTAÇÃO mudou de lugar: ela agora é
/// <see cref="PowerShellDiagnosticScrubber"/>, compartilhada com o adaptador do Exchange Online. A regra de
/// redação é idêntica para qualquer processo externo, e duas cópias significariam corrigir o mesmo defeito em
/// dois arquivos. Este tipo permanece como o nome pelo qual o adaptador do Teams e os seus testes a conhecem —
/// o comportamento é, literalmente, o mesmo objeto.
/// </summary>
internal static class TeamsDiagnosticScrubber
{
    internal const string Marker = PowerShellDiagnosticScrubber.Marker;

    internal static string Scrub(string? text, IEnumerable<string?>? knownSecrets = null) =>
        PowerShellDiagnosticScrubber.Scrub(text, knownSecrets);

    internal static string? ScrubIdentifier(string? value) =>
        PowerShellDiagnosticScrubber.ScrubIdentifier(value);
}
