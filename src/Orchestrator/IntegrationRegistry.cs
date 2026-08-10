using System.Collections.Concurrent;

namespace IntegrationHub.Orchestrator;

/// <summary>
/// In-memory manifest registry, reconciled from <c>integrations/*.yaml</c> at startup.
/// </summary>
/// <remarks>
/// ponytail: in-memory only for MVP-0. Phase 1 task 1.2 backs this with Postgres and
/// adds the <c>source</c> (file | api) column the hot-load path needs; nothing else
/// about this surface should have to change.
/// </remarks>
public sealed class IntegrationRegistry
{
    private readonly ConcurrentDictionary<string, IntegrationManifest> _byId = new(StringComparer.Ordinal);

    public IReadOnlyCollection<IntegrationManifest> All => _byId.Values.ToList();

    public IntegrationManifest? Find(string id) => _byId.GetValueOrDefault(id);

    public void Upsert(IntegrationManifest manifest) => _byId[manifest.Metadata.Id] = manifest;

    /// <summary>
    /// Loads every manifest in a directory. One bad file fails startup — a half-loaded
    /// registry is harder to debug than a refusal to boot.
    /// </summary>
    public int LoadDirectory(string directory, ILogger logger)
    {
        if (!Directory.Exists(directory))
        {
            logger.LogWarning("integrations directory {Directory} not found; registry is empty", directory);
            return 0;
        }

        var files = Directory
            .EnumerateFiles(directory, "*.yaml", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        foreach (var file in files)
        {
            IntegrationManifest manifest;
            try
            {
                manifest = ManifestLoader.Parse(File.ReadAllText(file));
            }
            catch (ManifestException ex)
            {
                throw new ManifestException($"{Path.GetFileName(file)}: {ex.Message}");
            }

            var existing = Find(manifest.Metadata.Id);
            if (existing is not null)
            {
                throw new ManifestException(
                    $"{Path.GetFileName(file)}: duplicate integration id '{manifest.Metadata.Id}'");
            }

            Upsert(manifest);
            logger.LogInformation(
                "loaded integration {Id} ({Resources} resources) from {File}",
                manifest.Metadata.Id, manifest.Spec.Resources.Count, Path.GetFileName(file));
        }

        return files.Count;
    }
}
