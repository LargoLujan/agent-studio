namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// The lock file that keeps a second instance of the app away from a project: it is held open with no sharing for as
/// long as the project is open, and the system deletes it when the handle closes, also when the process dies, so a
/// crash never leaves a stale lock behind.
/// </summary>
internal static class ProjectLock
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int Attempts = 3;

    public static async Task<IDisposable> AcquireAsync(string lockPath, string projectRoot, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                // A scanner that opens the file for an instant also causes a sharing violation; only a lock that
                // persists means another instance.
                if (attempt == Attempts)
                {
                    throw new ProjectInUseException(
                        $"The project at '{projectRoot}' is open in another instance of Agent Studio (lock file '{lockPath}').", ex);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;
}
