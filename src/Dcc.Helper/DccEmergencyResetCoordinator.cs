namespace NvUv.Dcc.Helper;

internal sealed record DccEmergencyResetOperation(string Name, Func<Task> ExecuteAsync);

internal static class DccEmergencyResetCoordinator
{
    public static async Task RunAllAsync(
        IReadOnlyList<DccEmergencyResetOperation> operations,
        Action<string, Exception>? failureObserver = null)
    {
        var failures = new List<Exception>();
        foreach (DccEmergencyResetOperation operation in operations)
        {
            try
            {
                await operation.ExecuteAsync();
            }
            catch (Exception ex)
            {
                failureObserver?.Invoke(operation.Name, ex);
                failures.Add(new InvalidOperationException(
                    $"The independent DCC emergency-reset stage '{operation.Name}' failed.",
                    ex));
            }
        }

        if (failures.Count != 0)
        {
            throw new AggregateException(
                "DCC attempted every independent emergency-reset stage, but one or more stages failed.",
                failures);
        }
    }
}


