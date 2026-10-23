using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Walis.Core.Actions;

namespace Walis.Tests;

public class ActionPackTests
{
    private static string TemplateDirectory()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Walis.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return Path.Combine(directory ?? throw new InvalidOperationException("找不到 repo 根目錄。"), "docs", "action-pack-template");
    }

    private static string WritePack(string name, params (string File, string Json)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"walis-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "actions", "misc"));
        File.WriteAllText(Path.Combine(root, "pack.json"), $$"""{"name":"{{name}}","version":"1.2.3","description":"測試套件"}""");
        foreach (var (file, json) in files)
        {
            File.WriteAllText(Path.Combine(root, "actions", "misc", file), json);
        }

        return root;
    }

    private const string GoodAction = """
        {"id":"acme.echo","name":"Echo","category":"general","parameters":[{"name":"text","label":"文字","type":"text"}],"template":"echo {{text}}"}
        """;

    [Fact]
    public void TemplatePackLoadsWithoutErrors()
    {
        var pack = ActionPackLoader.Load(TemplateDirectory());

        Assert.Empty(pack.Errors);
        Assert.Equal("community", pack.Name);
        Assert.Contains(pack.Definitions, definition => definition.Id == "example.hello");
        Assert.All(pack.Definitions, definition =>
        {
            Assert.False(definition.IsBuiltIn);
            Assert.Equal("pack:community", definition.Source);
        });
    }

    [Fact]
    public void BrokenFilesAreSkippedWithErrorsAndGoodOnesStillLoad()
    {
        var root = WritePack("bad-pack",
            ("acme.echo.json", GoodAction),
            ("wrong-name.json", """{"id":"acme.other","name":"x","template":"true"}"""),
            ("acme.broken.json", "{ not json"),
            ("acme.undeclared.json", """{"id":"acme.undeclared","name":"x","template":"echo {{missing}}"}"""),
            ("git.clone.json", """{"id":"git.clone","name":"撞內建","template":"true"}"""));

        try
        {
            var pack = ActionPackLoader.Load(root);

            Assert.Equal(["acme.echo"], pack.Definitions.Select(definition => definition.Id));
            Assert.Contains(pack.Errors, error => error.Contains("wrong-name.json") && error.Contains("檔名"));
            Assert.Contains(pack.Errors, error => error.Contains("acme.broken.json"));
            Assert.Contains(pack.Errors, error => error.Contains("acme.undeclared.json") && error.Contains("missing"));
            Assert.Contains(pack.Errors, error => error.Contains("git.clone.json") && error.Contains("內建"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void EachSubdirectoryWithPackJsonIsAPackAndOthersAreSkipped()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"walis-packs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(parent, "not-a-pack"));
        var first = WritePack("one", ("acme.echo.json", GoodAction));
        var second = WritePack("two", ("acme.echo.json", GoodAction));
        Directory.Move(first, Path.Combine(parent, "one"));
        Directory.Move(second, Path.Combine(parent, "two"));

        try
        {
            var packs = ActionPackLoader.LoadAll([parent, Path.Combine(parent, "missing")]);

            Assert.Equal(["one", "two"], packs.Select(pack => pack.Name));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public async Task PackActionsAppearInCatalogAndReloadListsErrorsAndCollisions()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"walis-packs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(parent);
        Directory.Move(WritePack("alpha", ("acme.echo.json", GoodAction)), Path.Combine(parent, "alpha"));
        Directory.Move(WritePack("beta", ("acme.echo.json", GoodAction), ("acme.beta.json",
            """{"id":"acme.beta","name":"Beta","template":"true"}""")), Path.Combine(parent, "beta"));

        await using var factory = new WalisApiFactory { ActionPackRoot = parent };
        using var client = factory.CreateClient();

        try
        {
            var catalog = await client.GetFromJsonAsync<JsonElement>("/api/actions/");
            var definitions = catalog.GetProperty("definitions").EnumerateArray().ToList();
            var echo = Assert.Single(definitions, definition => definition.GetProperty("id").GetString() == "acme.echo");
            Assert.Equal("pack:alpha", echo.GetProperty("source").GetString());
            Assert.Contains(definitions, definition => definition.GetProperty("id").GetString() == "acme.beta");

            var reload = await client.PostAsync("/api/actions/packs/reload", null);
            Assert.Equal(HttpStatusCode.OK, reload.StatusCode);
            var packs = (await reload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("packs").EnumerateArray().ToList();
            Assert.Equal(["alpha", "beta"], packs.Select(pack => pack.GetProperty("name").GetString()));
            var beta = packs[1];
            Assert.Equal(1, beta.GetProperty("count").GetInt32());
            Assert.Contains(beta.GetProperty("errors").EnumerateArray(), error => error.GetString()!.Contains("acme.echo"));

            // A pack cannot override a built-in with the same id; a UI-installed action cannot override a pack either.
            var install = await client.PostAsJsonAsync("/api/actions/install", new
            {
                manifest = """{"id":"acme.echo","name":"介面裝的","template":"false"}"""
            });
            Assert.Equal(HttpStatusCode.OK, install.StatusCode);
            catalog = await client.GetFromJsonAsync<JsonElement>("/api/actions/");
            echo = Assert.Single(catalog.GetProperty("definitions").EnumerateArray(),
                definition => definition.GetProperty("id").GetString() == "acme.echo");
            Assert.Equal("Echo", echo.GetProperty("name").GetString());
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public async Task PackEndpointsAreAdminOnly()
    {
        await using var factory = new WalisApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync("/api/actions/packs");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}

public class ActionPackReloadTests
{
    [Fact]
    public async Task ReloadsOnlyWhenDirectoryContentChanged()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"walis-packs-{Guid.NewGuid():N}");
        var pack = Path.Combine(parent, "live");
        Directory.CreateDirectory(Path.Combine(pack, "actions"));
        File.WriteAllText(Path.Combine(pack, "pack.json"), """{"name":"live","version":"1.0.0"}""");

        await using var factory = new WalisApiFactory { ActionPackRoot = parent };
        var provider = factory.Services.GetRequiredService<Walis.Api.Services.ActionPackProvider>();

        try
        {
            Assert.False(provider.ReloadIfChanged());
            Assert.Empty(provider.Definitions);

            File.WriteAllText(Path.Combine(pack, "actions", "acme.new.json"),
                """{"id":"acme.new","name":"新的","template":"true"}""");

            Assert.True(provider.ReloadIfChanged());
            Assert.Single(provider.Definitions, definition => definition.Id == "acme.new");
            Assert.False(provider.ReloadIfChanged());
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }
}
