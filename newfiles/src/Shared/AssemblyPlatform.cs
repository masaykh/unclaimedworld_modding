// We keep each assembly's ORIGINAL decompiled Properties/AssemblyInfo.cs verbatim to guarantee
// the identity XNB readers depend on, which means GenerateAssemblyInfo is off - and that also
// suppresses the SDK's auto-generated [SupportedOSPlatform]. Without it every MonoGame call
// site reports CA1416. Re-add it here rather than muting the warning, so the analyzer stays
// useful for Stage 2 (the net8.0 / DesktopGL platform-neutral build), where this file is
// excluded and CA1416 correctly flags Windows-only API use.

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
