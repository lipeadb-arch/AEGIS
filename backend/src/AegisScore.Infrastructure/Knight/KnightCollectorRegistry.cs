using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Application.Knight;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.Knight;

/// <summary>
/// Registro/factory de coletores KNIGHT por <see cref="KnightSourceType"/>, montado a partir de TODOS os
/// <see cref="IKnightCollector"/> registrados na DI. Acrescentar uma fonte (Google Workspace, AD, Okta…) é
/// registrar outro coletor — nada aqui muda. Não conhece Graph nem nenhum fornecedor.
/// </summary>
public sealed class KnightCollectorRegistry : IKnightCollectorRegistry
{
    private readonly IReadOnlyDictionary<KnightSourceType, IKnightCollector> _byType;

    public KnightCollectorRegistry(IEnumerable<IKnightCollector> collectors)
    {
        var map = new Dictionary<KnightSourceType, IKnightCollector>();
        foreach (var c in collectors ?? throw new ArgumentNullException(nameof(collectors)))
            map[c.Source] = c;   // última registro prevalece
        _byType = map;
    }

    public bool TryResolve(KnightSourceType source, out IKnightCollector? collector) =>
        _byType.TryGetValue(source, out collector);

    public IKnightCollector Resolve(KnightSourceType source) =>
        _byType.TryGetValue(source, out var c)
            ? c
            : throw new InvalidOperationException($"Nenhum coletor KNIGHT registrado para a fonte {source}.");

    public IReadOnlyList<KnightSourceType> RegisteredSources => _byType.Keys.ToList();
}
