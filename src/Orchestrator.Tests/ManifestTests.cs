using Microsoft.Extensions.Logging.Abstractions;

namespace IntegrationHub.Orchestrator.Tests;

internal static class Yaml
{
    /// <summary>
    /// A valid manifest, built line by line. YAML is indentation-sensitive and raw
    /// string literals quietly re-indent, so the columns are explicit here.
    /// </summary>
    public static string Manifest(
        IEnumerable<string>? authLines = null,
        IEnumerable<string>? extraLines = null)
    {
        var lines = new List<string>
        {
            "apiVersion: integrationhub.dev/v1alpha1",
            "kind: Integration",
            "metadata:",
            "  id: demo",
            "  displayName: Demo",
            "spec:",
            "  protocol: rest",
            "  baseUrl: https://api.example.test",
            "  auth:",
        };
        lines.AddRange((authLines ?? ["type: none"]).Select(l => "    " + l));
        lines.AddRange([
            "  resources:",
            "    - name: thing",
            "      method: GET",
            "      path: /thing",
            "      emit: single",
            "      transform: \"{ id: to_string(id) }\"",
        ]);
        if (extraLines is not null)
        {
            lines.AddRange(extraLines);
        }
        return string.Join("\n", lines) + "\n";
    }
}

public class ManifestParsingTests
{
    [Fact]
    public void A_minimal_manifest_round_trips()
    {
        var m = ManifestLoader.Parse(Yaml.Manifest());

        Assert.Equal("demo", m.Metadata.Id);
        Assert.Equal("https://api.example.test", m.Spec.BaseUrl);
        Assert.Equal(5000, m.Spec.Defaults.TimeoutMs);   // default applied, not in the YAML
        Assert.Single(m.Spec.Resources);
        Assert.Equal("single", m.Spec.Resources[0].Emit);
    }

    [Theory]
    [InlineData("integrationhub.dev/v1")]
    [InlineData("v1")]
    public void An_unrecognised_apiVersion_is_rejected(string apiVersion)
    {
        var yaml = Yaml.Manifest().Replace("integrationhub.dev/v1alpha1", apiVersion);
        var ex = Assert.Throws<ManifestException>(() => ManifestLoader.Parse(yaml));
        Assert.Contains("apiVersion", ex.Message);
    }

    [Theory]
    [InlineData("\"Has Spaces\"")]
    [InlineData("UPPER")]
    [InlineData("under_score")]
    [InlineData("\"\"")]
    public void An_invalid_id_is_rejected(string id)
    {
        var yaml = Yaml.Manifest().Replace("id: demo", $"id: {id}");
        var ex = Assert.Throws<ManifestException>(() => ManifestLoader.Parse(yaml));
        Assert.Contains("metadata.id", ex.Message);
    }

    [Fact]
    public void A_relative_baseUrl_is_rejected()
    {
        var yaml = Yaml.Manifest().Replace("https://api.example.test", "/api");
        var ex = Assert.Throws<ManifestException>(() => ManifestLoader.Parse(yaml));
        Assert.Contains("baseUrl", ex.Message);
    }

    [Fact]
    public void Duplicate_resource_names_are_rejected()
    {
        var yaml = Yaml.Manifest(extraLines: [
            "    - name: thing",
            "      method: GET",
            "      path: /other",
            "      emit: single",
            "      transform: \"{ id: to_string(id) }\"",
        ]);

        var ex = Assert.Throws<ManifestException>(() => ManifestLoader.Parse(yaml));
        Assert.Contains("duplicate resource", ex.Message);
    }

    [Fact]
    public void A_path_placeholder_without_a_matching_path_param_is_rejected()
    {
        // Without this check, substitution silently emits a URL containing braces.
        var yaml = Yaml.Manifest().Replace("path: /thing", "path: /thing/{owner}");
        var ex = Assert.Throws<ManifestException>(() => ManifestLoader.Parse(yaml));
        Assert.Contains("owner", ex.Message);
    }

    [Fact]
    public void A_declared_path_param_satisfies_the_placeholder()
    {
        var yaml = Yaml.Manifest()
            .Replace("path: /thing", "path: /thing/{owner}")
            .Replace("      emit: single", "      params:\n        - { name: owner, in: path, required: true }\n      emit: single");

        var m = ManifestLoader.Parse(yaml);
        Assert.Equal("owner", m.Spec.Resources[0].Params[0].Name);
        Assert.True(m.Spec.Resources[0].Params[0].Required);
    }

    [Fact]
    public void The_reserved_handler_escape_hatch_fails_loudly_rather_than_being_ignored()
    {
        var yaml = Yaml.Manifest(extraLines: ["      handler: mypkg.mod:fn"]);
        var ex = Assert.Throws<ManifestException>(() => ManifestLoader.Parse(yaml));
        Assert.Contains("handler", ex.Message);
    }

    [Fact]
    public void An_unknown_emit_is_rejected()
    {
        var yaml = Yaml.Manifest().Replace("emit: single", "emit: stream");
        var ex = Assert.Throws<ManifestException>(() => ManifestLoader.Parse(yaml));
        Assert.Contains("emit", ex.Message);
    }

    [Fact]
    public void A_resource_without_a_transform_is_rejected()
    {
        var yaml = Yaml.Manifest().Replace("transform: \"{ id: to_string(id) }\"", "transform: \"\"");
        var ex = Assert.Throws<ManifestException>(() => ManifestLoader.Parse(yaml));
        Assert.Contains("transform", ex.Message);
    }

    [Fact]
    public void A_typo_in_a_known_field_is_not_silently_ignored()
    {
        var yaml = Yaml.Manifest().Replace("      method: GET", "      methd: GET");
        Assert.Throws<ManifestException>(() => ManifestLoader.Parse(yaml));
    }
}

/// <summary>SPEC §5: a manifest carries credential names, never credential values.</summary>
public class CredentialSafetyTests
{
    [Fact]
    public void A_credentialRef_that_is_actually_a_token_is_rejected()
    {
        var yaml = Yaml.Manifest([
            "type: bearer",
            "credentialRef: ghp_R2d2C3poNotARealTokenButShapedLikeOne",
        ]);

        var ex = Assert.Throws<ManifestException>(() => ManifestLoader.Parse(yaml));
        Assert.Contains("never be the credential itself", ex.Message);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("value")]
    [InlineData("secret")]
    [InlineData("password")]
    public void An_inline_secret_field_fails_the_load_instead_of_being_ignored(string field)
    {
        var yaml = Yaml.Manifest([
            "type: bearer",
            "credentialRef: github-token",
            $"{field}: hunter2-actual-secret",
        ]);

        // Strict deserialization: an unknown key is a typo or a smuggled secret, and
        // silently dropping either is worse than refusing the manifest.
        Assert.Throws<ManifestException>(() => ManifestLoader.Parse(yaml));
    }

    [Fact]
    public void Bearer_auth_without_a_credentialRef_is_rejected()
    {
        var yaml = Yaml.Manifest(["type: bearer"]);
        var ex = Assert.Throws<ManifestException>(() => ManifestLoader.Parse(yaml));
        Assert.Contains("credentialRef", ex.Message);
    }

    [Fact]
    public void Auth_none_with_a_credentialRef_is_rejected_as_contradictory()
    {
        var yaml = Yaml.Manifest(["type: none", "credentialRef: github-token"]);
        Assert.Throws<ManifestException>(() => ManifestLoader.Parse(yaml));
    }

    [Fact]
    public void HeaderKey_auth_requires_a_header_name()
    {
        var yaml = Yaml.Manifest(["type: headerKey", "credentialRef: some-key"]);
        var ex = Assert.Throws<ManifestException>(() => ManifestLoader.Parse(yaml));
        Assert.Contains("headerName", ex.Message);
    }

    [Fact]
    public void A_valid_credentialRef_is_accepted()
    {
        var m = ManifestLoader.Parse(Yaml.Manifest(["type: bearer", "credentialRef: github-token"]));
        Assert.Equal("github-token", m.Spec.Auth.CredentialRef);
    }
}

/// <summary>The manifests actually committed to this repo must load.</summary>
public class ShippedManifestTests
{
    private static string IntegrationsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "integrations")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "integrations");
    }

    [Theory]
    [InlineData("open-meteo.yaml", "open-meteo", 2)]
    [InlineData("github.yaml", "github", 2)]
    public void Every_shipped_manifest_parses(string file, string expectedId, int resourceCount)
    {
        var m = ManifestLoader.Parse(File.ReadAllText(Path.Combine(IntegrationsDir(), file)));

        Assert.Equal(expectedId, m.Metadata.Id);
        Assert.Equal(resourceCount, m.Spec.Resources.Count);
        Assert.All(m.Spec.Resources, r => Assert.False(string.IsNullOrWhiteSpace(r.Transform)));
    }

    [Fact]
    public void The_whole_integrations_directory_loads_into_the_registry()
    {
        var registry = new IntegrationRegistry();
        var count = registry.LoadDirectory(IntegrationsDir(), NullLogger.Instance);

        Assert.True(count >= 2);
        Assert.NotNull(registry.Find("open-meteo"));
        Assert.NotNull(registry.Find("github"));
        Assert.Null(registry.Find("nope"));
    }
}

public class ParamBindingTests
{
    private static ResourceSpec Resource(params ParamSpec[] parameters) => new()
    {
        Name = "thing",
        Path = "/repos/{owner}/{repo}",
        Params = [.. parameters],
    };

    private static ParamSpec Param(string name, string @in, bool required = false, string? def = null)
        => new() { Name = name, In = @in, Required = required, Default = def };

    [Fact]
    public void A_missing_required_param_is_reported_before_any_network_call()
    {
        var ok = Invoker.TryBindParams(
            Resource(Param("owner", "path", required: true)),
            [], out _, out _, out var problem);

        Assert.False(ok);
        Assert.Contains("owner", problem);
    }

    [Fact]
    public void Defaults_fill_in_for_omitted_optional_params()
    {
        var ok = Invoker.TryBindParams(
            Resource(Param("per_page", "query", def: "10")),
            [], out _, out var query, out _);

        Assert.True(ok);
        Assert.Equal("10", query["per_page"]);
    }

    [Fact]
    public void A_supplied_value_beats_the_default()
    {
        var ok = Invoker.TryBindParams(
            Resource(Param("per_page", "query", def: "10")),
            new Dictionary<string, string> { ["per_page"] = "50" },
            out _, out var query, out _);

        Assert.True(ok);
        Assert.Equal("50", query["per_page"]);
    }

    [Fact]
    public void An_undeclared_param_is_rejected_rather_than_dropped()
    {
        // Dropping it would produce a confidently wrong answer instead of an error.
        var ok = Invoker.TryBindParams(
            Resource(Param("owner", "path", required: true)),
            new Dictionary<string, string> { ["owner"] = "a", ["sneaky"] = "b" },
            out _, out _, out var problem);

        Assert.False(ok);
        Assert.Contains("sneaky", problem);
    }

    [Fact]
    public void Path_params_are_substituted_and_url_escaped()
    {
        Invoker.TryBindParams(
            Resource(Param("owner", "path", required: true), Param("repo", "path", required: true)),
            new Dictionary<string, string> { ["owner"] = "some org", ["repo"] = "a/b" },
            out var pathValues, out _, out _);

        var path = Invoker.SubstitutePath("/repos/{owner}/{repo}", pathValues);

        // A raw '/' in a path segment would silently change which endpoint is called.
        Assert.Equal("/repos/some%20org/a%2Fb", path);
    }
}
