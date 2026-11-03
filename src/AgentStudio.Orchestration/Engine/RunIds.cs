using System.Globalization;
using System.Security.Cryptography;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Orchestration.Engine;

/// <summary>Allocates run ids of the form <c>yyyyMMdd-xxxxxx</c> (date of the run and six random hex digits), as git refs and folders expect.</summary>
internal static class RunIds
{
    public static RunId New(TimeProvider time, Func<RunId, bool> isTaken)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(isTaken);
        var day = time.GetUtcNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        for (var i = 0; i < 64; i++)
        {
            var suffix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
            var id = new RunId($"{day}-{suffix}");
            if (!isTaken(id))
            {
                return id;
            }
        }

        throw new InvalidOperationException("Could not allocate a free run id.");
    }
}
