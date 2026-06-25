using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace SecureCloudBackup.Tests;

public class LoggingContractTests
{
    [Fact]
    public void ServiceLog_Methods_AreConditionalOnDiagnosticLog()
    {
        var serviceTypes = new[]
        {
            typeof(SecureCloudBackup.Core.Services.AzureBlobService),
            typeof(SecureCloudBackup.Core.Services.BackupOrchestrator),
            typeof(SecureCloudBackup.Core.Services.ChunkIndexService),
            typeof(SecureCloudBackup.Core.Services.EncryptionService),
            typeof(SecureCloudBackup.Core.Services.FileWatcherService),
            typeof(SecureCloudBackup.Core.Services.LocalDatabaseService),
            typeof(SecureCloudBackup.Core.Services.RestoreService),
        };

        var failures = new List<string>();
        foreach (var t in serviceTypes)
        {
            var log = t.GetMethod("Log",
                BindingFlags.Instance | BindingFlags.NonPublic,
                new[] { typeof(string) });
            if (log == null) { failures.Add(t.Name + ": no private Log(string) method"); continue; }
            var attr = log.GetCustomAttribute<ConditionalAttribute>();
            if (attr == null || attr.ConditionString != "DIAGNOSTICLOG")
                failures.Add(t.Name + ".Log not [Conditional(DIAGNOSTICLOG)]");
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }
}
