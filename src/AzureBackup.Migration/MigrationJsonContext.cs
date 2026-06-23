using System.Text.Json.Serialization;

namespace AzureBackup.Migration;

/// <summary>
/// Source-generated <c>System.Text.Json</c> context for <see cref="MigrationRequest"/>,
/// so the helper deserializes the stdin request without reflection-based JSON
/// (keeps it trim/AOT-friendly if the migration helper is ever published trimmed).
/// </summary>
[JsonSerializable(typeof(MigrationRequest))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal sealed partial class MigrationJsonContext : JsonSerializerContext;
