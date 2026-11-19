param(
    [string]$InnoCompiler,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
if (-not $InnoCompiler) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $InnoCompiler = $command.Source }
}
if (-not $InnoCompiler) {
    $roots = @(${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA 'Programs'))
    foreach ($root in ($roots | Where-Object { $_ })) {
        $InnoCompiler = @('Inno Setup 7', 'Inno Setup 6') |
            ForEach-Object { Join-Path $root "$_\ISCC.exe" } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
        if ($InnoCompiler) { break }
    }
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
    throw 'Install Inno Setup 6 or 7, or pass the path to ISCC.exe with -InnoCompiler.'
}

& (Join-Path $PSScriptRoot 'Build.ps1')
$program = Join-Path $PSScriptRoot 'RepoSaveGuard.exe'
$version = [Reflection.AssemblyName]::GetAssemblyName($program).Version.ToString(3)
$destination = New-Item -ItemType Directory -Path $OutputDirectory -Force

& $InnoCompiler "/DAppVersion=$version" "/O$($destination.FullName)" (Join-Path $PSScriptRoot 'RepoSaveGuard.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
