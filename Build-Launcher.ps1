#requires -Version 5.1
<#
.SYNOPSIS
    Builds Discord Quest Launcher using Windows PowerShell 5.1 (.NET CodeDOM).
    Run from "Windows PowerShell" (powershell.exe), NOT pwsh.
#>

if ($PSVersionTable.PSEdition -ne "Desktop") {
    Write-Error "Run this in Windows PowerShell 5.1 (powershell.exe), not PowerShell Core (pwsh)."
    exit 1
}

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$csFile    = Join-Path $scriptDir "GameLauncher.cs"
$outputExe = Join-Path $scriptDir "GameLauncher.exe"
$stagedExe = Join-Path $scriptDir (".GameLauncher." + [Guid]::NewGuid().ToString("N") + ".exe")
$iconFile  = Join-Path $scriptDir "assets\AppIcon.ico"

if (-not (Test-Path $csFile)) { Write-Error "Source file '$csFile' not found!"; exit 1 }
if (-not (Test-Path $iconFile)) { Write-Error "App icon '$iconFile' not found!"; exit 1 }

$src = Get-Content -Raw -Path $csFile -Encoding UTF8
$m = [regex]::Match($src, 'BuildId\s*=\s*"([^"]+)"')
Write-Host "[*] Source build id: $($m.Groups[1].Value)" -ForegroundColor Cyan

$refs = @("System.dll","System.Core.dll","System.Windows.Forms.dll","System.Drawing.dll","System.Web.Extensions.dll")
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $parameters = New-Object System.CodeDom.Compiler.CompilerParameters
    $parameters.GenerateExecutable = $true
    $parameters.OutputAssembly = $stagedExe
    $parameters.CompilerOptions = '/target:winexe /win32icon:"' + $iconFile + '"'
    $parameters.ReferencedAssemblies.AddRange($refs)
    [void]$parameters.EmbeddedResources.Add($iconFile)
    $result = $provider.CompileAssemblyFromSource($parameters, $src)
    if ($result.Errors.HasErrors) {
        $errors = ($result.Errors | Where-Object { -not $_.IsWarning } | ForEach-Object { $_.ToString() }) -join "`n"
        throw $errors
    }
    if (-not (Test-Path -LiteralPath $stagedExe)) { throw "Output binary was not created." }

    # Only stop running copies after compilation succeeds. Scope launcher detection to this checkout.
    $dummyDir = [IO.Path]::GetFullPath((Join-Path $env:TEMP "DiscordDummyTools")) + [IO.Path]::DirectorySeparatorChar
    Get-Process | Where-Object {
        $_.Path -and (
            [string]::Equals($_.Path, $outputExe, [StringComparison]::OrdinalIgnoreCase) -or
            $_.Path.StartsWith($dummyDir, [StringComparison]::OrdinalIgnoreCase)
        )
    } | ForEach-Object {
        Write-Host "[!] Closing $($_.Name) (PID $($_.Id))..." -ForegroundColor Yellow
        Stop-Process -Id $_.Id -Force -ErrorAction Stop
        [void]$_.WaitForExit(1500)
    }

    # A failed replacement keeps the previous executable intact.
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try {
            if (Test-Path -LiteralPath $outputExe) {
                [IO.File]::Replace($stagedExe, $outputExe, [System.Management.Automation.Language.NullString]::Value, $true)
            } else {
                [IO.File]::Move($stagedExe, $outputExe)
            }
            break
        } catch {
            # Antivirus scans can temporarily hold a freshly compiled executable.
            if ($attempt -eq 19 -or $_.Exception.InnerException -isnot [IO.IOException]) { throw }
            Start-Sleep -Milliseconds 250
        }
    }
} catch {
    Write-Error "BUILD FAILED (the previous executable is preserved):`n$_"
    exit 1
} finally {
    $provider.Dispose()
    if (Test-Path -LiteralPath $stagedExe) { Remove-Item -LiteralPath $stagedExe -Force }
}

if (Test-Path $outputExe) {
    $fi = Get-Item $outputExe
    Write-Host "[+] Built $($fi.FullName)" -ForegroundColor Green
    Write-Host "[+] Size: $($fi.Length) bytes, written $($fi.LastWriteTime)" -ForegroundColor Green
} else {
    Write-Error "Output binary was not created."; exit 1
}
