namespace Pagurian.Sdk;

// A message delivered to a shell via "Pagurian.exe post {id} <cmd> [args...]".
// Fire-and-forget: the invoking process never waits for a reply and always
// exits 0.
public sealed record ShellMessage(
    // Sub-command routing key. Guaranteed non-empty (the host drops empty
    // commands before dispatch).
    string Command,
    IReadOnlyList<string> Args,
    // The invoking process's stdin, verbatim (typically JSON). The host never
    // parses it; interpretation belongs to the shell.
    string? Payload,
    DateTimeOffset ReceivedAt);
