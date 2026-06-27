using System.Reflection;
using System.Runtime.InteropServices;

// This legacy-style (non-SDK) project does NOT honour the csproj <Version>/<AssemblyVersion>
// MSBuild properties for the built DLL — the assembly version comes from these attributes.
// The release workflow's set-version.ps1 stamps the git tag here (and into the csproj <Version>
// that make package reads for gta5mod.json), so the tag is the single source of truth and the
// menu/log version (derived from this assembly version at runtime) always matches the release.
[assembly: AssemblyTitle("FreemodeIdentity")]
[assembly: AssemblyProduct("FreemodeIdentity")]
[assembly: ComVisible(false)]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
