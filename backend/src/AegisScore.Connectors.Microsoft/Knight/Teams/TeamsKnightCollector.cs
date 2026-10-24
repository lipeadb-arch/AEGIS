using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Domain;
using AegisScore.Connectors.Microsoft.Knight.PowerShell;
using AegisScore.Connectors.Microsoft.Knight.Services;

namespace AegisScore.Connectors.Microsoft.Knight.Teams;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-02] Coletor REAL do Microsoft Teams (somente leitura) para o AEGIS KNIGHT.
///
/// MÉTODO DE COLETA — a decisão e o porquê, para não ser redescoberta:
///   • O que os controles de Teams precisam ler (configuração do cliente, federação do locatário, políticas de
///     reunião, de mensagens e de permissão de aplicativos, e as atribuições dessas políticas a grupos) NÃO é
///     exposto pela versão estável (v1.0) do Microsoft Graph. O caminho oficial de leitura é o módulo
///     Microsoft Teams PowerShell, com AUTENTICAÇÃO DE APLICATIVO — documentada por certificado OU por tokens
///     de acesso. Aqui é por TOKENS: as client credentials do conector Microsoft já configurado bastam, e
///     nenhum certificado precisa ser emitido, distribuído ou rotacionado pelo cliente.
///   • As APIs de Gerenciamento de Configuração de Locatário (TCM) do Microsoft Graph cobrem estes mesmos
///     recursos do Teams, mas exigem PROVISIONAR e AUTORIZAR um service principal do fornecedor dentro do
///     locatário do cliente, e a execução de um snapshot exige uma permissão de ESCRITA
///     (ConfigurationMonitoring.ReadWrite.All). Alterar o locatário do cliente e pedir permissão de escrita
///     para ler está fora do escopo deste pacote — a opção fica registrada como pendência de decisão.
///
/// O que este coletor NÃO faz: não cria, altera nem remove nada no locatário; não cria monitores; não enumera
/// usuário a usuário (a atribuição direta de política por conta fica declarada como limitação, ver
/// <see cref="TeamsPolicyReach"/>); e não produz fato de identidade — a avaliação do Entra ID continua sendo
/// de outra fonte e não é tocada por uma sincronização do Teams.
///
/// Permissão e papel exigidos pelos comandos EFETIVAMENTE usados (todos de verbo Get):
///   • permissão de aplicativo do Microsoft Graph: <c>Organization.Read.All</c> — a documentação do módulo a
///     exige para o conjunto de comandos; nenhuma outra permissão da documentação do módulo é pedida aqui,
///     porque nenhum comando fora dos <c>*-Cs*</c> de leitura é executado;
///   • papel de diretório atribuído à APLICAÇÃO: <b>Leitor do Teams</b> (o menor papel documentado que lê tudo
///     no centro de administração do Teams). Leitor Global também serve; Administrador do Teams é excessivo e
///     concede escrita.
/// </summary>
public sealed class TeamsKnightCollector : IKnightCollector
{
    private const string Label = "Microsoft Teams";

    private readonly ITeamsTokenClient _tokens;
    private readonly ITeamsAdminReader _reader;
    private readonly ILogger<TeamsKnightCollector>? _log;
    private readonly TimeProvider _time;

    public TeamsKnightCollector(
        ITeamsTokenClient tokens,
        ITeamsAdminReader reader,
        ILogger<TeamsKnightCollector>? log = null,
        TimeProvider? time = null)
    {
        _tokens = tokens;
        _reader = reader;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public KnightSourceType Source => KnightSourceType.MicrosoftTeams;

    /// <summary>Capacidades deste coletor, na ordem em que o adaptador as executa.</summary>
    internal static IReadOnlyList<KnightCapability> Capabilities { get; } = new[]
    {
        KnightCapability.TeamsClientConfiguration,
        KnightCapability.TeamsFederationConfiguration,
        KnightCapability.TeamsMeetingPolicies,
        KnightCapability.TeamsMessagingPolicies,
        KnightCapability.TeamsAppPermissionPolicies,
        KnightCapability.TeamsPolicyAssignments,
    };

    public async Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Configuration is not KnightTeamsConfiguration cfg)
            return KnightCollectionResult.NotConfigured(Source, Label);

        var correlation = KnightRunCorrelation.New();
        TeamsAdminCredentials credentials;
        try
        {
            credentials = await _tokens.AcquireAsync(cfg, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not EntraGraphException && !KnightFailureClassifier.IsCallerCancellation(ex, ct))
        {
            // [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Tempo esgotado, circuito aberto ou transporte ao pedir os tokens: classificado.
            var failure = KnightFailureClassifier.Classify(ex, Label, "token", null, correlation);
            var detail = failure.Reason + " Nenhuma leitura foi tentada.";
            return Failure(StateFor(failure.Outcome), detail, failure.Outcome, detail, diagnostic: failure.Diagnostic);
        }
        catch (EntraGraphException ex)
        {
            var tokenState = ex.Kind switch
            {
                EntraGraphErrorKind.AuthFailure => KnightSourceState.AuthenticationFailure,
                EntraGraphErrorKind.Throttled => KnightSourceState.Throttled,
                _ => KnightSourceState.Unavailable,
            };
            _log?.LogWarning(
                "Falha ao obter os tokens de aplicativo do Microsoft Teams: Kind={Kind}, HttpStatus={HttpStatus}.",
                ex.Kind, ex.HttpStatusCode);
            return Failure(tokenState, "Falha ao obter os tokens de aplicativo exigidos pela administração do Microsoft Teams.",
                OutcomeFor(tokenState), "A aplicação não obteve os tokens exigidos; nenhuma leitura foi tentada.",
                diagnostic: KnightFailureClassifier.FromHttp(ex, Label, "token", null, correlation).Diagnostic);
        }

        TeamsAdminOutput output;
        try
        {
            output = await _reader.ReadAsync(credentials, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TeamsAdminTransportException ex)
        {
            // [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Só o que IMPEDIU o processo de começar chega aqui: aí, sim, está demonstrado
            // que nenhuma leitura foi tentada. O que acontece depois vem no relatório do processo, com estágio e código de saída.
            _log?.LogWarning(ex, "O adaptador de coleta do Microsoft Teams não pôde ser iniciado (correlação {CorrelationId}).", correlation);
            const string reason =
                "O adaptador de coleta do Microsoft Teams não pôde ser iniciado neste ambiente (o runtime do PowerShell não "
                + "executou). Nenhuma leitura foi tentada — e nenhum controle de Teams foi avaliado a partir de coleta vazia.";
            return Failure(KnightSourceState.Unavailable, reason, KnightCapabilityOutcome.Unavailable, reason,
                diagnostic: new KnightCapabilityDiagnostic("process-start", KnightFailureKind.RuntimeUnavailable, CorrelationId: correlation));
        }

        var process = output.Process;
        if (output.ConnectionErrorCategory is null && PowerShellRunInterpretation.FailedBeforeReads(process))
        {
            var before = PowerShellRunInterpretation.BeforeReads(Label, process!, correlation);
            return Failure(StateFor(before.Outcome), before.Detail, before.Outcome, before.Detail, output.Runtime, before.Diagnostic);
        }

        if (!output.Connected && PowerShellRunInterpretation.FailedBeforeConnect(process))
        {
            // O script nem tentou conectar (pedido ilegível, módulo que não carregou): a categoria devolvida não é de conexão.
            var before = PowerShellRunInterpretation.BeforeConnect(Label, process!, output.ConnectionErrorId, correlation);
            return Failure(StateFor(before.Outcome), before.Detail, before.Outcome, before.Detail, output.Runtime, before.Diagnostic);
        }

        if (!output.Connected)
        {
            var outcome = ParseOutcome(output.ConnectionErrorCategory) ?? KnightCapabilityOutcome.AuthenticationFailure;
            // A razão é MONTADA pelo AEGIS a partir da categoria — o texto vindo da fonte não entra aqui. Antes,
            // o diagnóstico bruto da conexão era concatenado e seguia para o ADM, para a API e para o relatório;
            // esse texto é escrito por um módulo de terceiro e pode repetir cabeçalho de autorização ou token.
            // O identificador técnico (já sanitizado) fica só no log do operador.
            var reason = ConnectionReason(outcome);
            _log?.LogWarning(
                "Conexão do adaptador do Microsoft Teams recusada: Categoria={Categoria}, Diagnostico={Diagnostico}, Correlacao={CorrelationId}.",
                output.ConnectionErrorCategory ?? "n/a", output.ConnectionErrorId ?? "n/a", correlation);
            return Failure(StateFor(outcome), reason, outcome, reason, output.Runtime,
                new KnightCapabilityDiagnostic("connect", KnightFailureKind.Connection, process?.DurationMs,
                    Code: output.ConnectionErrorId, CorrelationId: correlation));
        }

        var now = _time.GetUtcNow();
        var caps = new List<KnightCapabilityStatus>();
        var docs = new List<KnightConfigurationDocument>();
        var byCapability = output.Reads.ToDictionary(r => r.Capability, StringComparer.OrdinalIgnoreCase);

        foreach (var capability in Capabilities)
        {
            if (!byCapability.TryGetValue(capability.ToString(), out var read))
            {
                if (process is { Clean: false })
                {
                    var missing = PowerShellRunInterpretation.Missing(capability, Label, process, correlation);
                    caps.Add(new KnightCapabilityStatus(capability, missing.Outcome, missing.Detail, missing.Diagnostic));
                    continue;
                }
                caps.Add(new KnightCapabilityStatus(capability, KnightCapabilityOutcome.NotAttempted,
                    "O adaptador não registrou esta leitura nesta execução."));
                continue;
            }

            if (!read.Ok)
            {
                var outcome = ParseOutcome(read.ErrorCategory) ?? KnightCapabilityOutcome.Error;
                caps.Add(new KnightCapabilityStatus(capability, outcome, Describe(outcome, read),
                    new KnightCapabilityDiagnostic("read " + capability, KnightFailureKind.Command, read.DurationMs,
                        Code: read.ErrorId, CorrelationId: correlation)));
                continue;
            }

            try
            {
                docs.AddRange(Translate(capability, read.Items));
                caps.Add(new KnightCapabilityStatus(capability, KnightCapabilityOutcome.Collected));
            }
            catch (Exception ex)
            {
                // Resposta com forma inesperada NUNCA vira coleção vazia: vira capacidade não coletada, com
                // motivo, e os controles que dependem dela ficam não avaliados.
                _log?.LogWarning(ex, "Resposta do Microsoft Teams ilegível na capacidade {Capability}.", capability);
                caps.Add(new KnightCapabilityStatus(capability, KnightCapabilityOutcome.Error,
                    "A resposta desta leitura não pôde ser interpretada pelo contrato desta versão."));
            }
        }

        var state = DeriveState(caps);
        return PowerShellRunInterpretation.Qualify(new KnightCollectionResult(
            Source, state, Label, KnightFactSet.Empty, caps, now, DescribeState(state, output.Runtime),
            TenantConfiguration: new KnightTenantConfiguration(docs, caps)), process, Label);
    }

    // ---- Tradução: resposta do comando → documento tipado do ADM --------------------------------------

    private static IEnumerable<KnightConfigurationDocument> Translate(KnightCapability capability, JsonElement items)
    {
        if (items.ValueKind != JsonValueKind.Array) yield break;

        var index = 0;
        switch (capability)
        {
            case KnightCapability.TeamsClientConfiguration:
                foreach (var e in items.EnumerateArray())
                    yield return KnightTenantConfiguration.Document(
                        TeamsClientConfiguration.ExternalId, "Configuração do cliente do Teams",
                        new TeamsClientConfiguration(
                            Bool(e, "allowEmailIntoChannel"), Bool(e, "allowDropBox"), Bool(e, "allowBox"),
                            Bool(e, "allowGoogleDrive"), Bool(e, "allowShareFile"), Bool(e, "allowEgnyte"),
                            Bool(e, "allowGuestUser")));
                break;

            case KnightCapability.TeamsFederationConfiguration:
                foreach (var e in items.EnumerateArray())
                    yield return KnightTenantConfiguration.Document(
                        TeamsFederationConfiguration.ExternalId, "Configuração de federação do Teams",
                        new TeamsFederationConfiguration(
                            Bool(e, "allowFederatedUsers"), Text(e, "allowedDomainsKind"),
                            Strings(e, "allowedDomains"), Strings(e, "blockedDomains"),
                            Bool(e, "blockAllSubdomains"), Bool(e, "allowTeamsConsumer"),
                            Bool(e, "allowTeamsConsumerInbound"), Text(e, "externalAccessWithTrialTenants"),
                            Strings(e, "allowedTrialTenantDomains"),
                            Bool(e, "restrictTeamsConsumerToExternalUserProfiles")));
                break;

            case KnightCapability.TeamsMeetingPolicies:
                index = 0;
                foreach (var e in items.EnumerateArray())
                {
                    var identity = Identity(e);
                    yield return KnightTenantConfiguration.Document(
                        PolicyDocumentId(TeamsMeetingPolicyConfiguration.PolicyType, identity, index++),
                        "Política de reunião — " + DisplayName(identity),
                        new TeamsMeetingPolicyConfiguration(
                            identity,
                            Bool(e, "allowAnonymousUsersToJoinMeeting"), Bool(e, "allowAnonymousUsersToStartMeeting"),
                            Text(e, "autoAdmittedUsers"), Bool(e, "allowPSTNUsersToBypassLobby"),
                            Text(e, "meetingChatEnabledType"), Text(e, "designatedPresenterRoleMode"),
                            Bool(e, "allowExternalParticipantGiveRequestControl"),
                            Bool(e, "allowExternalNonTrustedMeetingChat"), Bool(e, "allowCloudRecording")));
                }
                break;

            case KnightCapability.TeamsMessagingPolicies:
                index = 0;
                foreach (var e in items.EnumerateArray())
                {
                    var identity = Identity(e);
                    yield return KnightTenantConfiguration.Document(
                        PolicyDocumentId(TeamsMessagingPolicyConfiguration.PolicyType, identity, index++),
                        "Política de mensagens — " + DisplayName(identity),
                        new TeamsMessagingPolicyConfiguration(identity, Bool(e, "allowSecurityEndUserReporting")));
                }
                break;

            case KnightCapability.TeamsAppPermissionPolicies:
                index = 0;
                foreach (var e in items.EnumerateArray())
                {
                    var identity = Identity(e);
                    yield return KnightTenantConfiguration.Document(
                        PolicyDocumentId(TeamsAppPermissionPolicyConfiguration.PolicyType, identity, index++),
                        "Política de permissão de aplicativos — " + DisplayName(identity),
                        new TeamsAppPermissionPolicyConfiguration(
                            identity,
                            Text(e, "defaultCatalogAppsType"), Int(e, "defaultCatalogAppsCount"),
                            Text(e, "globalCatalogAppsType"), Int(e, "globalCatalogAppsCount"),
                            Text(e, "privateCatalogAppsType"), Int(e, "privateCatalogAppsCount")));
                }
                break;

            case KnightCapability.TeamsPolicyAssignments:
                var seq = 0;
                foreach (var e in items.EnumerateArray())
                {
                    var policyType = Text(e, "policyType");
                    var groupId = Text(e, "groupId");
                    if (policyType is null || groupId is null) continue;
                    yield return KnightTenantConfiguration.Document(
                        $"{policyType}:{Text(e, "policyName") ?? "?"}:{groupId}:{seq++}",
                        "Atribuição de política a grupo",
                        new TeamsPolicyAssignment(policyType, Text(e, "policyName"), groupId, Int(e, "rank") is var r && r > 0 ? r : null));
                }
                break;
        }
    }

    /// <summary>
    /// Identidade da política como a FONTE a devolveu — sem substituto. O caminho anterior devolvia "Global"
    /// quando a fonte não identificava a política, e com isso inventava a instância que sempre se aplica: bastava
    /// um registro sem identidade para o ambiente inteiro ser aprovado, e dois registros sem identidade viravam
    /// um só documento. Identificação ausente é DADO INSUFICIENTE, e é assim que a avaliação passa a tratá-la.
    /// </summary>
    private static string? Identity(JsonElement e) => Text(e, "identity");

    /// <summary>
    /// Identificador do documento no ADM. Com identidade, é o tipo mais a identidade — estável entre coletas.
    /// SEM identidade, entra a posição na resposta: dois registros não identificados continuam sendo dois
    /// documentos distintos, em vez de um sobrescrever o outro.
    /// </summary>
    private static string PolicyDocumentId(string policyType, string? identity, int index) =>
        TeamsPolicyIdentities.IsIdentified(identity)
            ? policyType + ":" + identity!.Trim()
            : policyType + ":#" + index.ToString(CultureInfo.InvariantCulture);

    private static string DisplayName(string? identity) =>
        TeamsPolicyIdentities.Name(identity) ?? "sem identificação na coleta";

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && v.GetString() is { Length: > 0 } s
            ? s
            : null;

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : 0;

    private static IReadOnlyList<string> Strings(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return arr.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    // ---- Estado ----------------------------------------------------------------------------------------

    /// <summary>
    /// Falha ANTES de qualquer leitura: todas as capacidades recebem o mesmo desfecho, com o motivo. É isso que
    /// impede uma coleta frustrada de virar "nada encontrado" — os controles ficam não avaliados.
    /// </summary>
    private KnightCollectionResult Failure(
        KnightSourceState state, string detail, KnightCapabilityOutcome outcome, string reason,
        TeamsAdminRuntime? runtime = null, KnightCapabilityDiagnostic? diagnostic = null)
    {
        var caps = Capabilities.Select(c => new KnightCapabilityStatus(c, outcome, reason, diagnostic)).ToList();
        return new KnightCollectionResult(
            Source, state, Label, KnightFactSet.Empty, caps, _time.GetUtcNow(),
            detail + (runtime?.Module is { Length: > 0 } m ? $" (módulo {m})" : ""),
            TenantConfiguration: new KnightTenantConfiguration(Array.Empty<KnightConfigurationDocument>(), caps));
    }

    private static KnightCapabilityOutcome? ParseOutcome(string? category) =>
        Enum.TryParse<KnightCapabilityOutcome>(category, ignoreCase: true, out var parsed) ? parsed : null;

    private static KnightCapabilityOutcome OutcomeFor(KnightSourceState state) => state switch
    {
        KnightSourceState.AuthenticationFailure => KnightCapabilityOutcome.AuthenticationFailure,
        KnightSourceState.Throttled => KnightCapabilityOutcome.Throttled,
        KnightSourceState.InsufficientPermission => KnightCapabilityOutcome.InsufficientPermission,
        _ => KnightCapabilityOutcome.Unavailable,
    };

    private static KnightSourceState StateFor(KnightCapabilityOutcome outcome) => outcome switch
    {
        KnightCapabilityOutcome.AuthenticationFailure => KnightSourceState.AuthenticationFailure,
        KnightCapabilityOutcome.InsufficientPermission => KnightSourceState.InsufficientPermission,
        KnightCapabilityOutcome.Throttled => KnightSourceState.Throttled,
        KnightCapabilityOutcome.Error => KnightSourceState.Error,
        _ => KnightSourceState.Unavailable,
    };

    /// <summary>
    /// Mensagem da falha de CONEXÃO, montada pela CATEGORIA. Nunca reproduz texto da fonte: o que o módulo
    /// escreve num erro é texto de terceiro e pode carregar cabeçalho de autorização ou token.
    /// </summary>
    private static string ConnectionReason(KnightCapabilityOutcome outcome) => outcome switch
    {
        KnightCapabilityOutcome.InsufficientPermission =>
            "A conexão de aplicativo com a administração do Microsoft Teams foi recusada por permissão ou papel insuficiente. "
            + "Confira se a aplicação tem o papel Leitor do Teams (ou Leitor Global) atribuído.",
        KnightCapabilityOutcome.AuthenticationFailure =>
            "A conexão de aplicativo com a administração do Microsoft Teams falhou na autenticação. Confira o segredo da "
            + "aplicação, o consentimento do administrador e se os dois tokens exigidos foram emitidos para os recursos corretos.",
        KnightCapabilityOutcome.Throttled =>
            "A administração do Microsoft Teams aplicou limite de taxa ao estabelecer a conexão. Nenhuma leitura foi tentada; "
            + "a coleta pode ser repetida mais tarde.",
        KnightCapabilityOutcome.LimitedByLicense =>
            "A conexão de aplicativo com a administração do Microsoft Teams foi recusada por licença do locatário.",
        KnightCapabilityOutcome.Unavailable =>
            "A administração do Microsoft Teams não respondeu ao estabelecer a conexão. Nenhuma leitura foi tentada.",
        _ =>
            "A conexão de aplicativo com a administração do Microsoft Teams não foi estabelecida. Nenhuma leitura foi tentada.",
    };

    private static string Describe(KnightCapabilityOutcome outcome, TeamsAdminRead read)
    {
        var head = outcome switch
        {
            KnightCapabilityOutcome.InsufficientPermission =>
                "Permissão ou papel insuficiente para esta leitura. Confira se a aplicação tem o papel Leitor do Teams (ou Leitor Global) atribuído.",
            KnightCapabilityOutcome.AuthenticationFailure => "Falha de autenticação da aplicação nesta leitura.",
            KnightCapabilityOutcome.Throttled => "Limite de taxa da administração do Microsoft Teams nesta leitura.",
            KnightCapabilityOutcome.LimitedByLicense => "O locatário não tem a licença que este recurso exige.",
            KnightCapabilityOutcome.Unavailable =>
                "A leitura não pôde ser concluída: o comando não está disponível nesta versão do módulo, ou o serviço não respondeu.",
            _ => "Erro inesperado nesta leitura.",
        };
        return $"{head} (comando: {read.Command})";
    }

    /// <summary>
    /// Estado da coleta a partir do desfecho de TODAS as capacidades esperadas — inclusive as que o adaptador
    /// nem registrou.
    ///
    /// A versão anterior descartava as capacidades NÃO TENTADAS antes de decidir, e com isso uma leitura que o
    /// adaptador simplesmente OMITIU produzia "coleta concluída": o conjunto restante era todo Collected. Uma
    /// leitura esperada que não aparece na resposta não é leitura concluída sem registros — é leitura ausente.
    /// Agora: qualquer omissão impede Completed; havendo alguma leitura concluída, o estado é PARCIAL; não
    /// havendo nenhuma, o estado vem do motivo das que falharam, e a omissão pura é indisponibilidade.
    /// </summary>
    private static KnightSourceState DeriveState(IReadOnlyList<KnightCapabilityStatus> all)
    {
        if (all.Count == 0) return KnightSourceState.Unavailable;

        var collected = all.Count(c => c.Outcome == KnightCapabilityOutcome.Collected);
        if (collected == all.Count) return KnightSourceState.Completed;
        if (collected > 0) return KnightSourceState.PartialCollection;

        // Nenhuma leitura concluída: o estado é o motivo, e só quando ele for o MESMO em todas as capacidades.
        var failed = all.Where(c => c.Outcome != KnightCapabilityOutcome.NotAttempted).ToList();
        if (failed.Count == all.Count)
        {
            if (failed.All(c => c.Outcome == KnightCapabilityOutcome.InsufficientPermission)) return KnightSourceState.InsufficientPermission;
            if (failed.All(c => c.Outcome == KnightCapabilityOutcome.Throttled)) return KnightSourceState.Throttled;
            if (failed.All(c => c.Outcome == KnightCapabilityOutcome.AuthenticationFailure)) return KnightSourceState.AuthenticationFailure;
            if (failed.All(c => c.Outcome == KnightCapabilityOutcome.Error)) return KnightSourceState.Error;
        }

        return KnightSourceState.Unavailable;
    }

    private static string DescribeState(KnightSourceState state, TeamsAdminRuntime runtime)
    {
        var suffix = runtime.Module is { Length: > 0 } m ? $" (módulo Teams PowerShell {m})" : "";
        return state switch
        {
            KnightSourceState.Completed => "Coleta do Microsoft Teams concluída" + suffix + ".",
            KnightSourceState.PartialCollection => "Coleta parcial do Microsoft Teams — parte das leituras faltou" + suffix + ".",
            KnightSourceState.InsufficientPermission => "Permissões ou papel insuficientes para a coleta do Microsoft Teams" + suffix + ".",
            KnightSourceState.AuthenticationFailure => "Falha de autenticação da aplicação na administração do Microsoft Teams" + suffix + ".",
            KnightSourceState.Throttled => "Limite de taxa da administração do Microsoft Teams durante a coleta" + suffix + ".",
            KnightSourceState.Unavailable => "Administração do Microsoft Teams indisponível durante a coleta" + suffix + ".",
            KnightSourceState.Error => "Erro inesperado durante a coleta do Microsoft Teams" + suffix + ".",
            _ => "Coleta do Microsoft Teams" + suffix + ".",
        };
    }
}
