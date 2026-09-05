using Pagurian.Fixtures.PrivateDependency;
using Pagurian.Sdk;

namespace Pagurian.Fixtures.ModuleA;

[PagurianModule(DisplayName = "Isolation Fixture A")]
public sealed class FixtureModule : PagurianModule
{
    public string DependencyVersion => Pagurian.Fixtures.PrivateDependency.DependencyVersion.Value;
}

[Shell(DisplayName = "Isolation Fixture A")]
public sealed class FixtureShell : Shell;
