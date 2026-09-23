using System.Reflection;
using System.Runtime.Versioning;

[assembly: AssemblyTitle("VolumeGuard")]
[assembly: AssemblyDescription("Volume inicial por app, limitador e histórico de exposição sonora em dB")]
[assembly: AssemblyProduct("VolumeGuard")]
[assembly: AssemblyCompany("VolumeGuard")]
[assembly: AssemblyCopyright("Copyright © 2026 VolumeGuard · Licença MIT")]
[assembly: AssemblyVersion(VolumeGuard.BuildInfo.Version + ".0")]
[assembly: AssemblyFileVersion(VolumeGuard.BuildInfo.Version + ".0")]
[assembly: AssemblyInformationalVersion(VolumeGuard.BuildInfo.Version)]
// Sem isto o .NET roda o app no modo de compatibilidade antigo (DPI, etc.)
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
