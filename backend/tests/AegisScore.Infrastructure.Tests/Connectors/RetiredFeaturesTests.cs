using AegisScore.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Connectors;

public sealed class RetiredFeaturesTests
{
    [Fact]
    public void LegacyEndpointsReturnGoneInsteadOfTriggeringCollection()
    {
        var result = Assert.IsType<ObjectResult>(new RetiredFeaturesController().Retired());
        Assert.Equal(410, result.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Contains("KNIGHT", problem.Detail!);
        Assert.Contains("NIST", problem.Detail!);
    }
}
