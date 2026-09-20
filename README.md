# Pagurian

Pagurian is a WinUI taskbar utility with independently loaded shell modules.

Modules are deployed as self-contained folders and compile against the
versioned `Pagurian.Sdk` package. See
[External modules](docs/External-Modules.md) for the SDK, project, bundle,
dependency-isolation, and installation contract.

## Publishing

Pagurian builds are framework-dependent. Run `build-publish.bat` from the
repository root to create the x64 release in `publish`. Building and running
Pagurian requires these x64 components on the machine:

- .NET 10 Runtime
- Microsoft Visual C++ Redistributable
- Windows App Runtime 2.1.3 or a compatible newer 2.x servicing release

Pagurian does not bundle or install these prerequisites. See Microsoft's
[deployment guide for unpackaged framework-dependent apps](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-unpackaged-apps)
for Windows App Runtime installation options.

