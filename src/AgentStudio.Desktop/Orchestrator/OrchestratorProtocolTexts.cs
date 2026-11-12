using AgentStudio.Application.Security;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;

namespace AgentStudio.Desktop.Orchestrator;

/// <summary>
/// The texts of the orchestrator that are not for the person but for the plan, the agents and the decision log: the title a new
/// node starts with (it reaches the hash of the plan, the prompt of the executor and the message of the commit), the severities
/// and the label of a run in the request of a follow-up run, and the reasons of the denials the log records. They are fixed in
/// Spanish whatever the language of the interface (docs/17 D-48, F14-4), so nothing here reads <c>Texts</c> or the resources. A
/// test checks both.
/// </summary>
public static class OrchestratorProtocolTexts
{
    /// <summary>The title of the task an integration fix adds.</summary>
    public const string IntegrationTitle = "Integración";

    /// <summary>The title a new node of this kind starts with.</summary>
    public static string NewNodeTitle(WorkTaskKind kind) => kind switch
    {
        WorkTaskKind.AgentWork => "Trabajo de agente nuevo",
        WorkTaskKind.Condition => "Condición nueva",
        WorkTaskKind.ToolRun => "Herramienta nueva",
        WorkTaskKind.Review => "Revisión nueva",
        WorkTaskKind.Debate => "Debate nuevo",
        WorkTaskKind.HumanApproval => "Aprobación humana nueva",
        WorkTaskKind.Integration => "Integración nueva",
        _ => kind.ToString(),
    };

    /// <summary>The severity of a finding as the request of a follow-up run names it.</summary>
    public static string Severity(Severity severity) => severity switch
    {
        Domain.Judging.Severity.Low => "Baja",
        Domain.Judging.Severity.Medium => "Media",
        Domain.Judging.Severity.High => "Alta",
        Domain.Judging.Severity.Critical => "Crítica",
        _ => severity.ToString(),
    };

    /// <summary>How «Crear tareas correctivas» names the verdict of the review of a run.</summary>
    public static string RunVerdictLabel(RunId run) => $"Veredicto de la revisión de la ejecución {run}";

    /// <summary>The reason of the denial of a yes whose confirmation was not asked because another one was pending.</summary>
    public const string ConfirmationPending =
        "Ya hay una confirmación pendiente: termina primero la de Windows Hello o la del bloqueo de Windows que está abierta. Esta aprobación se deniega; vuelve a pedirla después.";

    /// <summary>What to do when Windows does not lock either, as the denial in the log says it: set up Windows Hello, and where.</summary>
    public const string SetUpHello =
        "Para aprobarlo, configura Windows Hello: abre Configuración, entra en Cuentas y luego en Opciones de inicio de sesión, y elige el PIN, la huella o la cara.";

    /// <summary>The reason of the denial of a yes when Windows could not be locked to confirm the person.</summary>
    public static string LockNotAsked(string problem) =>
        $"No se pudo bloquear Windows para confirmar que eres tú ({problem}): se deniega. {SetUpHello}";

    /// <summary>The reason of the denial of a yes the verified lock of Windows did not confirm.</summary>
    public static string LockNotConfirmed(WindowsLockResult result)
    {
        var text = $"El bloqueo de Windows no confirmó que eres tú ({result}: {LockWhy(result)}): se deniega.";
        return result == WindowsLockResult.LockFailed ? text + " " + SetUpHello : text;
    }

    private static string LockWhy(WindowsLockResult result) => result switch
    {
        WindowsLockResult.UnlockedAfterRequest => "lo desbloqueaste después de la petición",
        WindowsLockResult.LockFailed => "Windows no se bloqueó",
        WindowsLockResult.NotVerified => "el estado de la sesión no confirma que lo desbloqueaste después de la petición",
        WindowsLockResult.TimedOut => "nadie lo desbloqueó a tiempo",
        _ => "Windows dio un resultado que Agent Studio no conoce",
    };
}
