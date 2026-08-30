using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using AegisScore.Connectors.Google.Cloud;

namespace AegisScore.Connectors.Google.Auth;

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-01] Autoridade COMPARTILHADA de aquisição de access token de service account do Google —
/// o ÚNICO ponto que constrói o <c>ServiceAccountCredential</c> e troca o JWT por token, reusado por TODOS os
/// conectores Google (OS Config / VM Manager, Google SecOps / Chronicle, …). NÃO duplica a lógica: delega a
/// validação do JSON à autoridade única já existente (<see cref="GoogleCloudServiceAccountValidator"/>), que aceita
/// SOMENTE service account oficial (<c>type=service_account</c>, <c>client_email</c>/<c>private_key</c> presentes,
/// <c>token_uri</c> EXATAMENTE o endpoint oficial), bloqueando <c>token_uri</c> arbitrário.
///
/// Constrói o credential DIRETAMENTE dos campos validados (NUNCA <c>GoogleCredential.FromJson</c>, que interpretaria
/// endpoints/credential source do documento do tenant) e NÃO usa domain-wide delegation (sem <c>User</c>/<c>Subject</c>
/// /<c>CreateWithUser</c>). O ENDPOINT de token é a CONSTANTE oficial; o ESCOPO é injetado pelo chamador — cada API
/// tem o seu (<c>cloud-platform</c> para OS Config; <c>chronicle.readonly</c> para o SecOps). NUNCA registra/loga
/// segredo, chave privada, token ou JSON bruto; qualquer falha sobe como <see cref="GoogleCloudApiException"/>
/// SANITIZADA (<see cref="GoogleCloudApiErrorKind.AuthFailure"/>) — mensagem constante, sem valores recebidos.
/// </summary>
internal static class GoogleServiceAccountTokenSource
{
    public static async Task<string> AcquireAsync(
        string serviceAccountJson, IReadOnlyList<string> scopes, CancellationToken ct)
    {
        // Boundary FECHADO: valida o JSON pela autoridade única ANTES de qualquer construção de credencial ou rede.
        // Falha de validação = AuthFailure sanitizada (nunca vaza chave, e-mail, URL ou JSON).
        var cred = GoogleCloudServiceAccountValidator.Validate(serviceAccountJson);

        try
        {
            // SEM User/Subject/CreateWithUser → SEM domain-wide delegation: a service account atua como ela mesma,
            // limitada aos papéis IAM que possui. O endpoint de token é a CONSTANTE oficial (nunca o valor do tenant).
            var initializer = new ServiceAccountCredential.Initializer(
                    cred.ClientEmail, GoogleCloudServiceAccountValidator.OfficialTokenUri)
                {
                    Scopes = scopes,
                }
                .FromPrivateKey(cred.PrivateKey);
            var credential = new ServiceAccountCredential(initializer);

            var token = await ((ITokenAccess)credential).GetAccessTokenForRequestAsync(cancellationToken: ct);
            if (string.IsNullOrEmpty(token))
                throw new GoogleCloudApiException(GoogleCloudApiErrorKind.AuthFailure,
                    "access token vazio da service account do Google");
            return token;
        }
        catch (GoogleCloudApiException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // SANITIZADO: nunca inclui a chave privada, o e-mail, a URL, o JSON da service account nem o detalhe do erro.
            throw new GoogleCloudApiException(GoogleCloudApiErrorKind.AuthFailure,
                "falha ao obter access token da service account do Google");
        }
    }
}
