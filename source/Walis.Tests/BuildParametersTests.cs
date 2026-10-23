using Walis.Core.Models;

namespace Walis.Tests;

public class BuildParametersTests
{
    private const string Declared = """
                                    [
                                      {"name":"ENV","type":"choice","options":["stg","prod"],"default":"stg"},
                                      {"name":"DRY_RUN","type":"boolean","default":"true"},
                                      {"name":"NOTE","type":"string"},
                                      {"name":"TOKEN","type":"password"}
                                    ]
                                    """;

    [Fact]
    public void DeclarationsMustPassNameTypeAndOptionChecks()
    {
        Assert.True(BuildParameters.IsValid(Declared, out _));
        Assert.True(BuildParameters.IsValid(null, out _));

        Assert.False(BuildParameters.IsValid("""[{"name":"bad name","type":"string"}]""", out var nameError));
        Assert.Contains("名稱", nameError);
        Assert.False(BuildParameters.IsValid("""[{"name":"WALIS_X","type":"string"}]""", out var reservedError));
        Assert.Contains("WALIS_", reservedError);
        Assert.False(BuildParameters.IsValid("""[{"name":"A","type":"string"},{"name":"A","type":"text"}]""",
            out var duplicateError));
        Assert.Contains("重複", duplicateError);
        Assert.False(BuildParameters.IsValid("""[{"name":"A","type":"choice","options":[]}]""", out var choiceError));
        Assert.Contains("選項", choiceError);
        Assert.False(BuildParameters.IsValid("""[{"name":"A","type":"choice","options":["x"],"default":"y"}]""",
            out _));
        Assert.False(BuildParameters.IsValid("""[{"name":"A","type":"magic"}]""", out _));
        Assert.False(BuildParameters.IsValid("not json", out _));
    }

    [Fact]
    public void MissingParametersUseDefaultsAndUnknownNamesOrValuesAreRejected()
    {
        var definitions = BuildParameters.Parse(Declared);

        Assert.True(BuildParameters.TryResolve(definitions, null, out var defaults, out _));
        Assert.Equal("stg", defaults["ENV"]);
        Assert.Equal("true", defaults["DRY_RUN"]);
        Assert.Equal(string.Empty, defaults["NOTE"]);
        Assert.Equal(string.Empty, defaults["TOKEN"]);

        Assert.True(BuildParameters.TryResolve(
            definitions,
            new Dictionary<string, string> { ["ENV"] = "prod", ["DRY_RUN"] = "False", ["TOKEN"] = "s3cret" },
            out var given,
            out _));
        Assert.Equal("prod", given["ENV"]);
        Assert.Equal("false", given["DRY_RUN"]);
        Assert.Equal(["s3cret"], BuildParameters.SecretValues(definitions, given));
        Assert.Equal("********", BuildParameters.Mask(definitions, given)["TOKEN"]);

        Assert.False(BuildParameters.TryResolve(definitions, new Dictionary<string, string> { ["OTHER"] = "x" }, out _,
            out var unknown));
        Assert.Contains("沒有宣告", unknown);
        Assert.False(BuildParameters.TryResolve(definitions, new Dictionary<string, string> { ["ENV"] = "dev" }, out _,
            out var option));
        Assert.Contains("選項", option);
        Assert.False(BuildParameters.TryResolve(definitions, new Dictionary<string, string> { ["DRY_RUN"] = "maybe" },
            out _, out _));
    }

    [Fact]
    public void DownstreamConfigurationAndTriggerTiming()
    {
        Assert.True(DownstreamJobs.IsValid("""[{"jobName":"部署","when":"success"}]""", "建置", out _));
        Assert.False(DownstreamJobs.IsValid("""[{"jobName":"建置","when":"success"}]""", "建置", out var self));
        Assert.Contains("自己", self);
        Assert.False(DownstreamJobs.IsValid("""[{"jobName":"x","when":"sometimes"}]""", null, out _));

        var onSuccess = new DownstreamTarget { JobName = "x", When = DownstreamWhen.Success };
        var onFailure = new DownstreamTarget { JobName = "x", When = DownstreamWhen.Failure };
        var always = new DownstreamTarget { JobName = "x", When = DownstreamWhen.Always };
        Assert.True(DownstreamJobs.ShouldTrigger(onSuccess, BuildStatus.Succeeded));
        Assert.False(DownstreamJobs.ShouldTrigger(onSuccess, BuildStatus.Failed));
        Assert.True(DownstreamJobs.ShouldTrigger(onFailure, BuildStatus.Failed));
        Assert.True(DownstreamJobs.ShouldTrigger(always, BuildStatus.Failed));
    }

    [Fact]
    public void StepRunConditions()
    {
        Assert.True(StepRunCondition.ShouldRun(null, false));
        Assert.False(StepRunCondition.ShouldRun(StepRunCondition.Success, true));
        Assert.True(StepRunCondition.ShouldRun(StepRunCondition.Always, true));
        Assert.True(StepRunCondition.ShouldRun(StepRunCondition.Failure, true));
        Assert.False(StepRunCondition.ShouldRun(StepRunCondition.Failure, false));
        Assert.Equal(StepRunCondition.Success, StepRunCondition.Normalize("bogus"));
    }
}