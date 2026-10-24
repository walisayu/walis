using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Tests;

public class BuildMatrixTests
{
    [Fact]
    public void ExpandsIntoAllCombinations()
    {
        var combinations = BuildMatrix.Expand("""{"NODE":["20","22"],"OS":["mac","linux"]}""");

        Assert.Equal(4, combinations.Count);
        Assert.Contains(combinations, item => item.Label == "NODE=20, OS=mac");
        Assert.Contains(combinations, item => item.Label == "NODE=22, OS=linux");
    }

    [Fact]
    public void ReturnsEmptyListWhenUnset()
    {
        Assert.Empty(BuildMatrix.Expand(null));
        Assert.Empty(BuildMatrix.Expand(""));
        Assert.Empty(BuildMatrix.Expand("不是 JSON"));
        Assert.Empty(BuildMatrix.Expand("""{"NODE":[]}"""));
    }

    [Fact]
    public void CombinationCountIsCapped()
    {
        // Four axes with five values each is 625 combinations, which is rarely intended.
        var many = BuildMatrix.Expand(
            """{"A":["1","2","3","4","5"],"B":["1","2","3","4","5"],"C":["1","2","3","4","5"],"D":["1","2","3","4","5"]}""");

        Assert.Equal(50, many.Count);
    }

    [Fact]
    public void ValidationPointsOutProblems()
    {
        Assert.True(BuildMatrix.IsValid(null, out _));
        Assert.True(BuildMatrix.IsValid("""{"NODE":["20"]}""", out _));

        Assert.False(BuildMatrix.IsValid("[1,2]", out var arrayError));
        Assert.Contains("物件", arrayError!);

        Assert.False(BuildMatrix.IsValid("{壞掉", out var jsonError));
        Assert.Contains("JSON", jsonError!);

        Assert.False(BuildMatrix.IsValid("""{"NODE":[]}""", out var emptyError));
        Assert.Contains("至少", emptyError!);
    }

    [Fact]
    public void HugeOrExplosiveMatrixIsRejectedBeforeSaving()
    {
        var tooManyCombinations =
            "{\"A\":[\"1\",\"2\",\"3\",\"4\",\"5\",\"6\",\"7\",\"8\"]," +
            "\"B\":[\"1\",\"2\",\"3\",\"4\",\"5\",\"6\",\"7\",\"8\"]}";
        Assert.False(BuildMatrix.IsValid(tooManyCombinations, out var combinationsError));
        Assert.Contains("50", combinationsError!);

        var longValue = "{\"AXIS\":[\"" + new string('x', BuildMatrix.MaximumValueLength + 1) + "\"]}";
        Assert.False(BuildMatrix.IsValid(longValue, out var valueError));
        Assert.Contains(BuildMatrix.MaximumValueLength.ToString(), valueError!);

        var hugeJson = "{\"AXIS\":[\"" + new string('x', BuildMatrix.MaximumJsonLength) + "\"]}";
        Assert.False(BuildMatrix.IsValid(hugeJson, out var sizeError));
        Assert.Contains(BuildMatrix.MaximumJsonLength.ToString("N0"), sizeError!);

        Assert.False(BuildMatrix.IsValid("{\"AXIS\":[123]}", out _));
        Assert.False(BuildMatrix.IsValid("{\"bad-axis\":[\"x\"]}", out _));
    }
}

public class BuildMatrixApiTests(WalisApiFactory factory) : IClassFixture<WalisApiFactory>
{
    [Fact]
    public async Task SingleTriggerCreatesBuildForEachCombination()
    {
        var jobs = factory.Services.GetRequiredService<IJobRepository>();

        var job = await jobs.CreateJobAsync(new JobDraft(
            $"矩陣任務 {Guid.NewGuid():N}", null, null, null, "main", null, null,
            "echo $NODE-$OS", 30, null, TriggerMode.Manual, null, true, false,
            ScriptModes.Source, null, null, """{"NODE":["20","22"],"OS":["mac"]}"""));

        var client = factory.CreateClient();
        var response = await client.PostAsync($"/api/jobs/{job.Id}/builds", null);

        response.EnsureSuccessStatusCode();

        var builds = await jobs.ListBuildsAsync(new BuildQuery(null, null, job.Id, null, 20));

        Assert.Equal(2, builds.Count);
        Assert.Contains(builds, build => build.MatrixLabel == "NODE=20, OS=mac");
        Assert.Contains(builds, build => build.MatrixLabel == "NODE=22, OS=mac");
    }

    [Fact]
    public async Task MalformedMatrixBlocksSaving()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/jobs", new
        {
            name = $"壞矩陣 {Guid.NewGuid():N}",
            command = "echo hi",
            timeoutMinutes = 30,
            triggerKind = "manual",
            matrixJson = "[1,2]"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("matrixJson", await response.Content.ReadAsStringAsync());
    }
}