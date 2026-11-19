$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
& $compiler /nologo /target:winexe /optimize+ /codepage:65001 "/out:$PSScriptRoot\RepoSaveGuard.exe" "/win32icon:$PSScriptRoot\RepoSaveGuard.ico" "/resource:$PSScriptRoot\RepoSaveGuard.ico,RepoSaveGuard.RepoSaveGuard.ico" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll "$PSScriptRoot\RepoSaveGuard.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
Write-Output "Ready: $PSScriptRoot\RepoSaveGuard.exe"
