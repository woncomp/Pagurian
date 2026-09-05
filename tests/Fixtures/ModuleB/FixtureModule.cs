using Pagurian.Fixtures.PrivateDependency;
using Pagurian.Sdk;

namespace Pagurian.Fixtures.ModuleB;

[PagurianModule(DisplayName = "Isolation Fixture B")]
public sealed class FixtureModule : PagurianModule
{
    public string DependencyVersion => Pagurian.Fixtures.PrivateDependency.DependencyVersion.Value;
}

[Shell(DisplayName = "Isolation Fixture B")]
public sealed class FixtureShell : Shell;
