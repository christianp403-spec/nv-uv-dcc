namespace NvUv.Dcc.Core;

public sealed record DccDriverApplication(
    string ProfileName,
    string Executable,
    string FriendlyName,
    bool IsPredefined)
{
    public DccGameDefinition ToGameDefinition()
    {
        string executable = DccGameCatalog.NormalizeExecutable(Executable);
        string name = string.IsNullOrWhiteSpace(FriendlyName)
            ? string.IsNullOrWhiteSpace(ProfileName)
                ? Path.GetFileNameWithoutExtension(executable)
                : ProfileName.Trim()
            : FriendlyName.Trim();
        return new DccGameDefinition(
            name,
            executable,
            [],
            Ambiguous: false,
            UserAdded: false,
            NvidiaProfileName: ProfileName.Trim());
    }
}
