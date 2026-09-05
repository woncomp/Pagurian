using System.Runtime.CompilerServices;

// The host implements the internal infrastructure (IShellHostChannel, Logger
// sink, internal property setters) and needs access to these members.
[assembly: InternalsVisibleTo("Pagurian")]
