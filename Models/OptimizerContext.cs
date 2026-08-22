using System.Text.Json.Serialization;
using OptiscalerClient.Models;

namespace OptiscalerClient.Models
{
    /// <summary>
    /// Source generator for JSON serialization to support high-performance trimming.
    /// This allows the compiler to remove unused reflection code, significantly reducing binary size.
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(AppConfiguration))]
    [JsonSerializable(typeof(NetworkConfig))]
    [JsonSerializable(typeof(ScanSourcesConfig))]
    [JsonSerializable(typeof(ComponentVersions))]
    [JsonSerializable(typeof(InstallationManifest))]
    [JsonSerializable(typeof(ManifestFileRecord))]
    [JsonSerializable(typeof(KeyFileSnapshot))]
    [JsonSerializable(typeof(List<ManifestFileRecord>))]
    [JsonSerializable(typeof(List<KeyFileSnapshot>))]
    [JsonSerializable(typeof(List<Game>))]
    [JsonSerializable(typeof(Game))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(OptiScalerReleaseEntry))]
    [JsonSerializable(typeof(OptiScalerReleasesCache))]
    [JsonSerializable(typeof(List<OptiScalerReleaseEntry>))]
    [JsonSerializable(typeof(ExtrasReleaseEntry))]
    [JsonSerializable(typeof(ExtrasReleasesCache))]
    [JsonSerializable(typeof(List<ExtrasReleaseEntry>))]
    [JsonSerializable(typeof(OptiPatcherReleaseEntry))]
    [JsonSerializable(typeof(OptiPatcherReleasesCache))]
    [JsonSerializable(typeof(List<OptiPatcherReleaseEntry>))]
    [JsonSerializable(typeof(FakenvapiReleaseEntry))]
    [JsonSerializable(typeof(FakenvapiReleasesCache))]
    [JsonSerializable(typeof(List<FakenvapiReleaseEntry>))]
    [JsonSerializable(typeof(CompatibilityListEntry))]
    [JsonSerializable(typeof(CompatibilityListCache))]
    [JsonSerializable(typeof(List<CompatibilityListEntry>))]
    [JsonSerializable(typeof(GameWikiDetails))]
    [JsonSerializable(typeof(GameWikiDetailsCacheEntry))]
    [JsonSerializable(typeof(GameWikiDetailsCache))]
    [JsonSerializable(typeof(Dictionary<string, GameWikiDetailsCacheEntry>))]
    [JsonSerializable(typeof(OptiScalerProfile))]
    [JsonSerializable(typeof(List<OptiScalerProfile>))]
    [JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    internal partial class OptimizerContext : JsonSerializerContext
    {
    }
}
