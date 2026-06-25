namespace SecureCloudBackup.Tests;

/// <summary>
/// Helper class for handling flaky timing-dependent tests.
/// Retries tests up to a specified number of times, passing if any attempt succeeds.
/// </summary>
public static class FlakyTestHelper
{
    /// <summary>
    /// Default number of retry attempts for flaky tests.
    /// </summary>
    public const int DefaultMaxAttempts = 5;

    /// <summary>
    /// Executes a test action with retry logic.
    /// The test passes if any attempt succeeds.
    /// Only fails if all attempts fail.
    /// </summary>
    /// <param name="testAction">The async test action to execute</param>
    /// <param name="maxAttempts">Maximum number of attempts (default: 5)</param>
    /// <param name="delayBetweenAttempts">Optional delay between attempts in milliseconds</param>
    public static async Task RetryAsync(
        Func<Task> testAction, 
        int maxAttempts = DefaultMaxAttempts,
        int delayBetweenAttempts = 100)
    {
        ArgumentNullException.ThrowIfNull(testAction);
        
        Exception? lastException = null;
        
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await testAction();
                // Test passed - exit immediately
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;
                
                // If not the last attempt, wait before retrying
                if (attempt < maxAttempts && delayBetweenAttempts > 0)
                {
                    await Task.Delay(delayBetweenAttempts);
                }
            }
        }
        
        // All attempts failed - throw the last exception with context
        throw new AggregateException(
            $"Test failed after {maxAttempts} attempts. Last error: {lastException?.Message}",
            lastException!);
    }

    /// <summary>
    /// Executes a test action with retry logic, providing the attempt number to the action.
    /// Useful for tests that need to set up fresh state on each attempt.
    /// </summary>
    /// <param name="testAction">The async test action to execute, receives attempt number (1-based)</param>
    /// <param name="maxAttempts">Maximum number of attempts (default: 5)</param>
    /// <param name="delayBetweenAttempts">Optional delay between attempts in milliseconds</param>
    public static async Task RetryWithAttemptAsync(
        Func<int, Task> testAction, 
        int maxAttempts = DefaultMaxAttempts,
        int delayBetweenAttempts = 100)
    {
        ArgumentNullException.ThrowIfNull(testAction);
        
        Exception? lastException = null;
        
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await testAction(attempt);
                // Test passed - exit immediately
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;
                
                // If not the last attempt, wait before retrying
                if (attempt < maxAttempts && delayBetweenAttempts > 0)
                {
                    await Task.Delay(delayBetweenAttempts);
                }
            }
        }
        
        // All attempts failed - throw the last exception with context
        throw new AggregateException(
            $"Test failed after {maxAttempts} attempts. Last error: {lastException?.Message}",
            lastException!);
    }
}
