$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $root 'FatmaVision.exe'
$icon = "/win32icon:" + (Join-Path $root 'FatmaVision.ico')
& $csc /nologo /target:winexe /platform:x64 /optimize+ /unsafe $icon /out:$out `
    (Join-Path $root 'src\Program.cs') `
    (Join-Path $root 'src\MfInterop.cs') `
    (Join-Path $root 'src\AnnexBClient.cs') `
    (Join-Path $root 'src\MfDecoder.cs') `
    (Join-Path $root 'src\MainForm.cs') `
    (Join-Path $root 'src\DeviceSetup.cs') `
    (Join-Path $root 'src\DeviceWizard.cs') `
    (Join-Path $root 'src\MkvMux.cs')
if ($LASTEXITCODE -ne 0) { throw "compile failed rc=$LASTEXITCODE" }
"built: $out"
