using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace IntegrationHub.Orchestrator;

// The manifest is the source of truth (SPEC §3). These are plain mutable classes
// rather than records because YamlDotNet's object factory wants settable properties.

public sealed class IntegrationManifest
{
    public string ApiVersion { get; set; } = "";
    public string Kind { get; set; } = "";
    public ManifestMetadata Metadata { get; set; } = new();
    public IntegrationSpec Spec { get; set; } = new();
}

public sealed class ManifestMetadata
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public sealed class IntegrationSpec
{
    public string Protocol { get; set; } = "rest";
    public string BaseUrl { get; set; } = "";
    public AuthSpec Auth { get; set; } = new();
    public DefaultsSpec Defaults { get; set; } = new();

    // Parsed but unused until Phase 1 task 1.6. Modelled anyway so the shipped
    // manifests round-trip under strict deserialization.
    public ResiliencySpec? Resiliency { get; set; }
    public RateLimitSpec? RateLimit { get; set; }

    public List<ResourceSpec> Resources { get; set; } = [];
}

/// <summary>
/// Only names live here, never values (SPEC §5). Strict deserialization means a
/// stray <c>token:</c> or <c>value:</c> key fails the load rather than being ignored.
/// </summary>
public sealed class AuthSpec
{
    public string Type { get; set; } = "none";
    public string? CredentialRef { get; set; }
    public string? HeaderName { get; set; }
}

public sealed class DefaultsSpec
{
    public Dictionary<string, string> Headers { get; set; } = [];
    public int TimeoutMs { get; set; } = 5000;
}

public sealed class ResiliencySpec
{
    public RetrySpec? Retry { get; set; }
    public CircuitBreakerSpec? CircuitBreaker { get; set; }
}

public sealed class RetrySpec
{
    public int MaxAttempts { get; set; } = 3;
    public string Backoff { get; set; } = "exponential";
    public int BaseDelayMs { get; set; } = 200;
    public bool Jitter { get; set; } = true;
}

public sealed class CircuitBreakerSpec
{
    public double FailureRatio { get; set; }
    public int SamplingSeconds { get; set; }
    public int BreakSeconds { get; set; }
    public int MinThroughput { get; set; }
}

public sealed class RateLimitSpec
{
    public int RequestsPerMinute { get; set; }
}

public sealed class ResourceSpec
{
    public string Name { get; set; } = "";
    public string Method { get; set; } = "GET";
    public string Path { get; set; } = "";
    public List<ParamSpec> Params { get; set; } = [];
    public string Emit { get; set; } = "single";
    public string Transform { get; set; } = "";

    // GraphQL resources carry a query instead of a path — task 1.8.
    public string? Query { get; set; }

    // Reserved escape hatch (SPEC §3.4). Rejected at validation until implemented.
    public string? Handler { get; set; }
}

public sealed class ParamSpec
{
    public string Name { get; set; } = "";
    public string In { get; set; } = "query";   // path | query | variable
    public bool Required { get; set; }
    public string? Default { get; set; }
}

public sealed class ManifestException(string message) : Exception(message);

public static class ManifestLoader
{
    public const string SupportedApiVersion = "integrationhub.dev/v1alpha1";

    private static readonly Regex IdPattern = new("^[a-z0-9-]{1,40}$", RegexOptions.Compiled);
    private static readonly Regex ResourceNamePattern = new("^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex CredentialRefPattern = new("^[a-z0-9-]{1,60}$", RegexOptions.Compiled);
    private static readonly Regex PathParamPattern = new(@"\{([^}]+)\}", RegexOptions.Compiled);

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        // No IgnoreUnmatchedProperties on purpose: an unrecognised key is a typo or a
        // smuggled secret, and silently dropping either is worse than failing the load.
        .Build();

    public static IntegrationManifest Parse(string yaml)
    {
        IntegrationManifest? manifest;
        try
        {
            manifest = Deserializer.Deserialize<IntegrationManifest>(yaml);
        }
        catch (YamlException ex)
        {
            throw new ManifestException($"could not parse manifest: {ex.Message}");
        }

        if (manifest is null)
        {
            throw new ManifestException("manifest is empty");
        }

        Validate(manifest);
        return manifest;
    }

    private static void Validate(IntegrationManifest m)
    {
        if (m.ApiVersion != SupportedApiVersion)
        {
            throw new ManifestException(
                $"unsupported apiVersion '{m.ApiVersion}' — expected '{SupportedApiVersion}'");
        }

        if (m.Kind != "Integration")
        {
            throw new ManifestException($"unsupported kind '{m.Kind}' — expected 'Integration'");
        }

        if (!IdPattern.IsMatch(m.Metadata.Id))
        {
            throw new ManifestException(
                $"metadata.id '{m.Metadata.Id}' must match [a-z0-9-] and be 1-40 characters");
        }

        var id = m.Metadata.Id;

        if (m.Spec.Protocol is not ("rest" or "graphql"))
        {
            throw new ManifestException($"{id}: spec.protocol must be 'rest' or 'graphql'");
        }

        if (!Uri.TryCreate(m.Spec.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ManifestException($"{id}: spec.baseUrl must be an absolute http(s) URL");
        }

        ValidateAuth(id, m.Spec.Auth);

        if (m.Spec.Resources.Count == 0)
        {
            throw new ManifestException($"{id}: spec.resources must declare at least one resource");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in m.Spec.Resources)
        {
            ValidateResource(id, r, m.Spec.Protocol);
            if (!seen.Add(r.Name))
            {
                throw new ManifestException($"{id}: duplicate resource name '{r.Name}'");
            }
        }
    }

    private static void ValidateAuth(string id, AuthSpec auth)
    {
        if (auth.Type is not ("none" or "bearer" or "headerKey" or "queryKey"))
        {
            throw new ManifestException(
                $"{id}: spec.auth.type '{auth.Type}' must be none, bearer, headerKey or queryKey");
        }

        if (auth.Type == "none")
        {
            if (auth.CredentialRef is not null)
            {
                throw new ManifestException($"{id}: auth.type is 'none' but a credentialRef is set");
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(auth.CredentialRef))
        {
            throw new ManifestException($"{id}: auth.type '{auth.Type}' requires a credentialRef");
        }

        // A credentialRef is a NAME. Real tokens are long and mixed-case, so this
        // pattern rejects a secret pasted where its reference belongs (SPEC §5).
        if (!CredentialRefPattern.IsMatch(auth.CredentialRef))
        {
            throw new ManifestException(
                $"{id}: auth.credentialRef must be a lowercase name matching [a-z0-9-]{{1,60}}. " +
                "It names a credential; it must never be the credential itself.");
        }

        if (auth.Type is "headerKey" or "queryKey" && string.IsNullOrWhiteSpace(auth.HeaderName))
        {
            throw new ManifestException($"{id}: auth.type '{auth.Type}' requires a headerName");
        }
    }

    private static void ValidateResource(string id, ResourceSpec r, string protocol)
    {
        // Resource names end up in the invoke URL, so they are constrained here and
        // in schemas/integration.schema.json. The two must agree.
        if (!ResourceNamePattern.IsMatch(r.Name ?? ""))
        {
            throw new ManifestException(
                $"{id}: resource name '{r.Name}' must start with a letter and contain " +
                "only letters, digits and underscores");
        }

        var where = $"{id}.{r.Name}";

        if (r.Handler is not null)
        {
            throw new ManifestException(
                $"{where}: the 'handler' escape hatch is reserved but not implemented (SPEC §3.4)");
        }

        if (r.Emit is not ("single" or "list"))
        {
            throw new ManifestException($"{where}: emit must be 'single' or 'list'");
        }

        if (string.IsNullOrWhiteSpace(r.Transform))
        {
            throw new ManifestException($"{where}: transform is required");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in r.Params)
        {
            if (string.IsNullOrWhiteSpace(p.Name))
            {
                throw new ManifestException($"{where}: every param needs a name");
            }
            if (p.In is not ("path" or "query" or "variable"))
            {
                throw new ManifestException(
                    $"{where}.{p.Name}: 'in' must be path, query or variable");
            }
            if (!names.Add(p.Name))
            {
                throw new ManifestException($"{where}: duplicate param '{p.Name}'");
            }
            // A required param with a default can never actually be required — the
            // default always satisfies it. Almost always a mistake in the manifest.
            if (p.Required && p.Default is not null)
            {
                throw new ManifestException(
                    $"{where}.{p.Name}: a param cannot be both required and have a default");
            }
        }

        if (protocol == "graphql")
        {
            if (string.IsNullOrWhiteSpace(r.Query))
            {
                throw new ManifestException($"{where}: graphql resources require a query");
            }
            return;
        }

        // Every {placeholder} in the path must have a declared path param backing it,
        // otherwise the substitution silently produces a URL containing braces.
        foreach (Match match in PathParamPattern.Matches(r.Path))
        {
            var token = match.Groups[1].Value;
            if (!r.Params.Any(p => p.Name == token && p.In == "path"))
            {
                throw new ManifestException(
                    $"{where}: path references {{{token}}} but no param named '{token}' " +
                    "is declared with 'in: path'");
            }
        }
    }
}
