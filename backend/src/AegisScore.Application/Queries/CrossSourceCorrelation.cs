namespace AegisScore.Application.Queries;

// [AEGIS-CROSS-SOURCE-01] A correlação por CVE entre fontes saiu da operação com a abordagem antiga. Fica só a nota
// congelada no registro de origem dos planos históricos de dispositivo, lida por desserialização.

public sealed record CrossSourceNoteDto(string Code, string Text);
