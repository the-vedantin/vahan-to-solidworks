# Rebuild VahanHardpoints.exe from hp.cs
# Run:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$dir  = "C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS"
$wf   = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Windows.Forms.dll"
$dr   = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Drawing.dll"

# keep the interop assemblies beside the exe so it runs without SW on the PATH
Copy-Item "$dir\SolidWorks.Interop.sldworks.dll" $here -Force
Copy-Item "$dir\SolidWorks.Interop.swconst.dll"  $here -Force

& $csc /nologo /platform:x64 /target:winexe /out:"$here\VahanHardpoints.exe" `
    /reference:"$dir\SolidWorks.Interop.sldworks.dll" `
    /reference:"$dir\SolidWorks.Interop.swconst.dll" `
    /reference:"$wf" `
    /reference:"$dr" `
    "$here\hp.cs"

if ($LASTEXITCODE -eq 0) { "Built VahanHardpoints.exe" } else { throw "compile failed ($LASTEXITCODE)" }
