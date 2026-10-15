#requires -Version 5.1
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Run with Windows PowerShell 5.1 (powershell.exe).' }
$repository = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $env:TEMP ('launcher-tests-' + [Guid]::NewGuid().ToString('N'))
$pipePrefix = 'launcher-tests-' + [Guid]::NewGuid().ToString('N') + '-'
[void](New-Item -ItemType Directory -Path $testRoot)
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $source = Get-Content -LiteralPath (Join-Path $repository 'GameLauncher.cs') -Raw -Encoding UTF8
    $parameters = New-Object System.CodeDom.Compiler.CompilerParameters
    $parameters.GenerateInMemory = $true
    $parameters.ReferencedAssemblies.AddRange(@('System.dll','System.Core.dll','System.Windows.Forms.dll','System.Drawing.dll','System.Web.Extensions.dll'))
    $compiled = $provider.CompileAssemblyFromSource($parameters, $source)
    if ($compiled.Errors.Count) { throw (($compiled.Errors | ForEach-Object { $_.ToString() }) -join "`n") }
    Write-Host 'PASS: production source compiles without warnings'

    # Compile the same application into a test host, redirecting only filesystem paths and pipe names.
    # Helpers forward --dummy to the actual entry point, exercising the real process lifecycle.
    $sandboxLiteral = '@"' + $testRoot.Replace('"','""') + '"'
    $source = $source.Replace('Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)', $sandboxLiteral)
    $source = $source.Replace('AppDomain.CurrentDomain.BaseDirectory', $sandboxLiteral)
    $source = $source.Replace('Path.GetTempPath()', $sandboxLiteral)
    $source = $source.Replace('discord-ipc-', $pipePrefix)
    $probe = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'LauncherRegressionTests.cs') -Raw -Encoding UTF8
    $parameters.GenerateInMemory = $false
    $parameters.GenerateExecutable = $true
    $parameters.OutputAssembly = Join-Path $testRoot 'LauncherTests.exe'
    $parameters.CompilerOptions = '/target:exe /main:LauncherRegressionTests'
    [void]$parameters.EmbeddedResources.Add((Join-Path $repository 'assets\AppIcon.ico'))
    $compiled = $provider.CompileAssemblyFromSource($parameters, [string[]]@($source, $probe))
    if ($compiled.Errors.Count) { throw (($compiled.Errors | ForEach-Object { $_.ToString() }) -join "`n") }
    & $parameters.OutputAssembly $testRoot $pipePrefix
    if ($LASTEXITCODE -ne 0) { throw 'Application regression tests failed.' }

    # Exercise the build script in a separate checkout. Its helper-process scope uses a private TEMP.
    $buildRoot = Join-Path $testRoot 'build'
    [void](New-Item -ItemType Directory -Path (Join-Path $buildRoot 'assets'))
    Copy-Item -LiteralPath (Join-Path $repository 'Build-Launcher.ps1') -Destination $buildRoot
    Copy-Item -LiteralPath (Join-Path $repository 'assets\AppIcon.ico') -Destination (Join-Path $buildRoot 'assets\AppIcon.ico')
    $exePath = Join-Path $buildRoot 'GameLauncher.exe'
    [IO.File]::WriteAllText($exePath, 'previous working executable')
    $originalHash = (Get-FileHash -LiteralPath $exePath).Hash
    [IO.File]::WriteAllText((Join-Path $buildRoot 'GameLauncher.cs'), 'intentionally invalid source')
    $buildDriver = Join-Path $buildRoot 'Invoke-Build.ps1'
    @'
$env:TEMP = Join-Path $PSScriptRoot 'private-temp'
$env:TMP = $env:TEMP
[void](New-Item -ItemType Directory -Path $env:TEMP -Force)
& (Join-Path $PSScriptRoot 'Build-Launcher.ps1')
exit $LASTEXITCODE
'@ | Set-Content -LiteralPath $buildDriver -Encoding UTF8
    function Invoke-IsolatedBuild {
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = 'powershell.exe'
        $start.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $buildDriver + '"'
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $process = [Diagnostics.Process]::Start($start)
        try {
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            $process.WaitForExit()
            return @{ Code = $process.ExitCode; Output = $stdout.Result + $stderr.Result }
        } finally { $process.Dispose() }
    }
    $build = Invoke-IsolatedBuild
    if ($build.Code -eq 0 -or (Get-FileHash -LiteralPath $exePath).Hash -ne $originalHash) {
        throw ('Failed compilation check: code=' + $build.Code + '; preserved=' + ((Get-FileHash -LiteralPath $exePath).Hash -eq $originalHash) + "`n" + $build.Output)
    }
    Write-Host 'PASS: failed build preserves the previous executable'
    [IO.File]::WriteAllText((Join-Path $buildRoot 'GameLauncher.cs'), 'public static class TestProgram { public static void Main() {} }')
    $build = Invoke-IsolatedBuild
    if ($build.Code -ne 0) { throw $build.Output }
    $validHash = (Get-FileHash -LiteralPath $exePath).Hash
    if ($validHash -eq $originalHash) { throw 'Successful build did not replace the executable.' }
    Write-Host 'PASS: successful build replaces the executable'
    $lock = [IO.File]::Open($exePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $build = Invoke-IsolatedBuild
        if ($build.Code -eq 0) { throw 'Replacing a locked executable unexpectedly succeeded.' }
    } finally { $lock.Dispose() }
    if ((Get-FileHash -LiteralPath $exePath).Hash -ne $validHash) { throw 'Failed replacement damaged the working executable.' }
    if (Get-ChildItem -LiteralPath $buildRoot -Filter '.GameLauncher.*.exe') { throw 'Staged build files leaked.' }
    Write-Host 'PASS: failed replacement preserves the executable and cleans staging files'
} finally {
    $provider.Dispose()
    # The generated sandbox is the only recursive cleanup target.
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    if (-not $resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup target.' }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
