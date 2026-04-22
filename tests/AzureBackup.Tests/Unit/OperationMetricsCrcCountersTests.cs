using AzureBackup.Core;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for X2 CRC counter exposure on <see cref="OperationMetrics"/>.
/// The actual increment paths in <c>AzureBlobService</c> are exercised
/// against the live Azure SDK (covered by integration tests); these unit
/// tests validate the metrics record shape so a regression that drops
/// the field from JSON serialization shows up locally.
/// </summary>
public class OperationMetricsCrcCountersTests
{
    [Fact]
    public void DefaultsAreZero()
    {
        var op = new OperationMetrics();
        Assert.Equal(0, op.CrcFailCount);
        Assert.Equal(0, op.CrcRetryCount);
    }

    [Fact]
    public void Setters_RoundTrip()
    {
        var op = new OperationMetrics
        {
            CrcFailCount = 3,
            CrcRetryCount = 7
        };
        Assert.Equal(3, op.CrcFailCount);
        Assert.Equal(7, op.CrcRetryCount);
    }

    [Fact]
    public void JsonSerialization_IncludesCrcFields_WhenNonZero()
    {
        // ThroughputMetrics serializes with DefaultIgnoreCondition.WhenWritingDefault,
        // so zero values are intentionally omitted to keep daily JSONL files
        // small. Non-zero values must appear.
        var op = new OperationMetrics
        {
            Operation = "backup",
            Files = 1,
            CrcFailCount = 2,
            CrcRetryCount = 5
        };
        var json = System.Text.Json.JsonSerializer.Serialize(op, new System.Text.Json.JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
        });
        Assert.Contains("crc_fail_count", json);
        Assert.Contains("crc_retry_count", json);
    }

    [Fact]
    public void JsonSerialization_OmitsCrcFields_WhenZero()
    {
        // Zero values stay out of JSON to avoid bloating the daily metrics
        // file when CRC behaviour is healthy. A grep "crc_fail_count" gives
        // an immediate triage signal of "any op had a CRC fail today".
        var op = new OperationMetrics
        {
            Operation = "backup",
            Files = 1,
            CrcFailCount = 0,
            CrcRetryCount = 0
        };
        var json = System.Text.Json.JsonSerializer.Serialize(op, new System.Text.Json.JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
        });
        Assert.DoesNotContain("crc_fail_count", json);
        Assert.DoesNotContain("crc_retry_count", json);
    }
}
