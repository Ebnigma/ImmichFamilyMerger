namespace ImmichFamilyMerger;

internal static class InstanceLock
{
    public static FileStream Acquire(string statePath)
    {
        try
        {
            return new FileStream(statePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Another Immich Family Merger process is already using this state path. Run exactly one replica per journal.",
                exception);
        }
    }
}
