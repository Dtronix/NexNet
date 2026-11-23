using System.Text.Json.Serialization;

namespace NexNet.Inspector.App.Settings;

/// <summary>
/// Source-generated System.Text.Json metadata for <c>settings.json</c>: no reflection, so it works under Native AOT.
/// Enums are written as names so a hand-edited file stays readable; comments and trailing commas are tolerated.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
