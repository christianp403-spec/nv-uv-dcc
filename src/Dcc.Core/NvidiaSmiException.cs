namespace NvUv.Dcc.Core;

public sealed class NvidiaSmiException : Exception
{
    public NvidiaSmiException(string message, int? exitCode = null, string? commandOutput = null)
        : base(message)
    {
        ExitCode = exitCode;
        CommandOutput = commandOutput;
    }

    public int? ExitCode { get; }
    public string? CommandOutput { get; }
}
