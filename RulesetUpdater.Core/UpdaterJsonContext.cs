using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RulesetUpdater;

// Explicit metadata keeps persisted update state and GitHub responses working
// when unused framework code is trimmed from the standalone helper.
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RulesetCatalog))]
[JsonSerializable(typeof(Release))]
[JsonSerializable(typeof(Release[]))]
[JsonSerializable(typeof(ClientSession))]
[JsonSerializable(typeof(StagedUpdate))]
[JsonSerializable(typeof(InstallJournal))]
[JsonSerializable(typeof(SelfUpdatePlan))]
[JsonSerializable(typeof(UpdateStatus))]
internal partial class UpdaterJsonContext : JsonSerializerContext
{
    public static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)(Default.GetTypeInfo(typeof(T))
        ?? throw new NotSupportedException($"No updater JSON contract for {typeof(T)}."));
}
