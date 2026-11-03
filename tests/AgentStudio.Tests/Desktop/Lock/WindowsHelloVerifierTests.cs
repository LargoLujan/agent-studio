using AgentStudio.Application.Security;
using AgentStudio.Desktop.Lock;
using Windows.Security.Credentials.UI;

namespace AgentStudio.Tests.Desktop.Lock;

/// <summary>
/// The verifier with Windows Hello (docs/16 D-13, ADR-0037), in what shows nothing: how it reads the answers of WinRT. No
/// test calls Windows Hello, not even to ask whether it is there.
/// </summary>
public sealed class WindowsHelloVerifierTests
{
    public static TheoryData<UserConsentVerifierAvailability> Availabilities => [.. Enum.GetValues<UserConsentVerifierAvailability>()];

    public static TheoryData<UserConsentVerificationResult> Results => [.. Enum.GetValues<UserConsentVerificationResult>()];

    [Theory]
    [MemberData(nameof(Availabilities))]
    public void Every_availability_of_windows_reads_as_the_one_of_the_same_name(UserConsentVerifierAvailability availability) =>
        Assert.Equal(availability.ToString(), UserPresenceMapping.ToAvailability(availability).ToString());

    [Theory]
    [MemberData(nameof(Results))]
    public void Every_result_of_windows_reads_as_the_one_of_the_same_name(UserConsentVerificationResult result) =>
        Assert.Equal(result.ToString(), UserPresenceMapping.ToResult(result).ToString());

    [Fact]
    public void Only_verified_reads_as_a_confirmation()
    {
        var confirmations = Enum.GetValues<UserConsentVerificationResult>().Where(r => UserPresenceMapping.ToResult(r) == UserPresenceResult.Verified);

        Assert.Equal([UserConsentVerificationResult.Verified], confirmations);
    }

    /// <summary>A value a later Windows may add is never taken for one Agent Studio knows: the caller asks Hello, or denies.</summary>
    [Fact]
    public void An_answer_windows_does_not_document_is_never_taken_for_one_it_does()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UserPresenceMapping.ToAvailability((UserConsentVerifierAvailability)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => UserPresenceMapping.ToResult((UserConsentVerificationResult)99));
    }

    [Fact]
    public void Building_the_verifier_asks_nothing_of_windows_nor_reads_the_window()
    {
        var reads = 0;

        // The dispatcher of the shared WPF test thread: no new one, and no window.
        WpfHost.Run(() => new WindowsHelloVerifier(System.Windows.Threading.Dispatcher.CurrentDispatcher, () =>
        {
            reads++;
            return 0;
        }));

        Assert.Equal(0, reads);
    }
}
