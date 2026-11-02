#Requires -Version 7.0
<#
.SYNOPSIS
    Godot pad claim dev script: one entry point for the build, the tests and the formatters.

.DESCRIPTION
    Subcommands (`.\padclaim.ps1 help` prints the same list at runtime):

      build      dotnet build GodotPadClaim.slnx with warnings as errors. -Release for Release.
      test       dotnet build GodotPadClaim.slnx -c Release -warnaserror, then dotnet test -c Release --no-build.
                 -Filter "*X" one class (a wildcard on the full class name); a filter that runs no test fails
                 (exit 9). The rest forwards.
      format     dotnet csharpier format ., then dotnet format style and dotnet format analyzers at --severity info,
                 then the 150-character line check (tools/hooks/Test-LineLength.ps1 -All). -Check only verifies.
      help       This list.

    MSBuild switches are written in dash form (-warnaserror, -p:Name=Value) rather than with a slash: Git Bash's path
    conversion rewrites /warnaserror into a Windows path and MSBuild then reports MSB1008, while the dash form reads the
    same from every shell.

    Every gate - build, test, the formatters - runs through tools/gate.ps1 under a ceiling of its own, writing the whole
    output to a log under .tmp and printing its tail. The ceiling runs on a load-adjusted clock, which slows when other
    work keeps the machine busy; the gate also kills a run that stalls (no output and no CPU for 120 s) and one that
    reaches five times its ceiling in wall time. Any of the three kills it with its children and exits 124, and the error
    names which one it was.

.EXAMPLE
    .\padclaim.ps1 build -Release
    .\padclaim.ps1 test -Filter "*ReferenceTests"
    .\padclaim.ps1 format -Check
#>
[CmdletBinding()]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '',
    Justification = 'Interactive dev script; coloured status to the console is the UX.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '',
    Justification = 'Top-level params are read from script scope by the subcommands.')]
param(
    [Parameter(Position = 0)]
    [ValidateSet('', 'help', 'build', 'test', 'format')]
    [string]$Command = '',

    # Everything after the subcommand. Each subcommand takes the options it knows and forwards the rest.
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Rest = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Root = $PSScriptRoot
$script:Tmp = Join-Path $script:Root '.tmp'
# Every build, test and format run this script makes on the caller's behalf goes through tools/gate.ps1: the whole
# output to a log, the tail on the screen, the command's own exit status, and 124 when the gate's watchdog killed it
# and its children - stalled, past the ceiling below on the load-adjusted clock, or at the wall-time backstop of five
# times it. A ceiling is a few times what the command takes on an idle machine today.
$script:GateScript = Join-Path $script:Root 'tools\gate.ps1'
$script:BuildSeconds = 300
$script:FormatSeconds = 180
$script:TestSeconds = 120
$script:FilteredTestSeconds = 60
$script:LineLengthSeconds = 60

Set-Location $script:Root
New-Item -ItemType Directory -Force $script:Tmp | Out-Null

# --- shared helpers ---

function Write-Section {
    param([string]$Title)
    Write-Host ''
    Write-Host "== $Title ==" -ForegroundColor Cyan
}

# Takes the options a subcommand knows out of $Rest: `-Name value` for names in $Values, bare `-Name` for names in
# $Switches. Every other word is left in order, so it can be forwarded untouched or read as a positional.
function Split-Rest {
    param([string[]]$Values = @(), [string[]]$Switches = @())
    $options = @{}
    $left = [System.Collections.Generic.List[string]]::new()
    $words = @($script:Rest)
    $i = 0
    while ($i -lt $words.Count) {
        $word = $words[$i]
        $name = if ($word -match '^-([A-Za-z][A-Za-z0-9]*)$') { $Matches[1] } else { $null }
        if ($name -and $name -in $Switches) {
            $options[$name] = $true
            $i += 1
        }
        elseif ($name -and $name -in $Values -and $i + 1 -lt $words.Count) {
            $options[$name] = $words[$i + 1]
            $i += 2
        }
        else {
            $left.Add($word)
            $i += 1
        }
    }
    return @{ Options = $options; Rest = $left.ToArray() }
}

# The gate's kill line from a log whose gate exited 124, with what that kind of kill means for the next step.
function Get-GateKill {
    param([string]$Log)
    $line = Select-String -Path $Log -Pattern 'gate: (STALLED|TIMED OUT|BACKSTOP)' -ErrorAction SilentlyContinue |
        Select-Object -Last 1
    if (-not $line) { return "no kill line in $Log" }
    $reading = switch ($line.Matches[0].Groups[1].Value) {
        'STALLED' { "It hung: read $Log for where it stopped." }
        'TIMED OUT' {
            "It kept working past its ceiling even allowing for load: a busy loop or a ceiling set too tight; read $Log before raising it."
        }
        default { 'The machine was busy: run it once more alone.' }
    }
    return "$($line.Line) $reading"
}

# Runs one gate under tools/gate.ps1 and stops the script when it fails, so a chain of steps ends at the first red one.
# The command is handed over as an array rather than as words on this line: PowerShell's parser eats a bare -- as its
# own end-of-parameters marker, and a caller's `dotnet test ... -- <runner options>` needs the separator to reach the
# runner.
function Invoke-Gate {
    param(
        [string]$Title,
        [string]$Log,
        [int]$Seconds,
        [Parameter(Mandatory)][ValidateSet('heavy', 'light')][string]$Slot,
        [string[]]$Gate
    )
    Write-Section $Title
    & $script:GateScript -Log $Log -TimeoutSeconds $Seconds -Slot $Slot -- @Gate
    if ($LASTEXITCODE -eq 124) {
        throw "$Title was killed by the gate: $(Get-GateKill -Log $Log)"
    }
    if ($LASTEXITCODE -ne 0) {
        throw "$Title failed (exit $LASTEXITCODE); the whole output is in $Log"
    }
}

# --- help ---

function Invoke-Help {
    Write-Host @'
Godot pad claim dev script

Usage: .\padclaim.ps1 <subcommand> [args]

Build and check
  build [-Release]                    dotnet build GodotPadClaim.slnx -warnaserror; -Release builds Release
  test [-Filter "*X"]                 Release build, then dotnet test -c Release --no-build; -Filter runs the class
                                      and fails when no test ran; the rest forwards
  format [-Check]                     csharpier, then dotnet format style and analyzers (--severity info), then
                                      the 150-character line check; -Check verifies only and writes nothing
  help                                This list

MSBuild switches are written in dash form (-warnaserror, -p:Name=Value): Git Bash rewrites a /switch into a Windows
path, and MSBuild then reports MSB1008.

Every gate - build, test, the formatters - runs through tools/gate.ps1 under a ceiling of its own, writing the whole
output to a log under .tmp and printing its tail. The ceiling runs on a load-adjusted clock, which slows when other
work keeps the machine busy; the gate also kills a run that stalls (no output and no CPU for 120 s) and one that
reaches five times its ceiling in wall time. Any of the three kills it with its children and exits 124, and the error
names which one it was.
'@
}

# --- build / test / format ---

function Invoke-Build {
    $parsed = Split-Rest -Switches 'Release'
    if ($parsed.Rest.Count -gt 0) {
        throw "build takes only -Release, not: $($parsed.Rest -join ' ')"
    }
    $config = if ($parsed.Options['Release']) { 'Release' } else { 'Debug' }
    Invoke-Gate -Title "dotnet build GodotPadClaim.slnx -c $config -warnaserror" `
        -Log (Join-Path $script:Tmp 'build.log') -Seconds $script:BuildSeconds -Slot heavy `
        -Gate @('dotnet', 'build', 'GodotPadClaim.slnx', '-c', $config, '-warnaserror')
}

function Invoke-Test {
    $parsed = Split-Rest -Values 'Filter'
    $log = Join-Path $script:Tmp 'test.log'
    # Tests run in Release: the build goes first under its own gate with warnings as errors, which `dotnet test`'s own
    # build would not carry, and the tests then run with --no-build.
    Invoke-Gate -Title 'dotnet build GodotPadClaim.slnx -c Release -warnaserror' `
        -Log (Join-Path $script:Tmp 'test-build.log') -Seconds $script:BuildSeconds -Slot heavy `
        -Gate @('dotnet', 'build', 'GodotPadClaim.slnx', '-c', 'Release', '-warnaserror')
    # A filtered run reports an assembly with nothing to run as exit 8, which SDK 10 does not forgive for the whole run,
    # so each assembly's 8 is ignored and the run as a whole must still run at least one test, exiting 9 when the filter
    # matched nothing anywhere.
    $filter = if ($parsed.Options['Filter']) {
        @('--filter-class', $parsed.Options['Filter'], '--ignore-exit-code', '8', '--minimum-expected-tests', '1')
    }
    else {
        @()
    }
    $forwarded = @($parsed.Rest)
    $seconds = if ($parsed.Options['Filter']) { $script:FilteredTestSeconds } else { $script:TestSeconds }
    # Over the solution the test assembly starts in parallel after a Release build; a filtered run on --no-build is one
    # class in that assembly and keeps one or two threads busy.
    $slot = if ($parsed.Options['Filter']) { 'light' } else { 'heavy' }
    $test = @('dotnet', 'test', 'GodotPadClaim.slnx', '-c', 'Release', '--no-build') + $filter + $forwarded
    Invoke-Gate -Title ($test -join ' ') -Log $log -Seconds $seconds -Slot $slot -Gate $test
}

function Invoke-Format {
    $parsed = Split-Rest -Switches 'Check'
    if ($parsed.Rest.Count -gt 0) {
        throw "format takes only -Check, not: $($parsed.Rest -join ' ')"
    }
    $check = $parsed.Options['Check']
    $csharpierLog = Join-Path $script:Tmp 'csharpier.log'
    $formatLog = Join-Path $script:Tmp 'format.log'
    $analyzersLog = Join-Path $script:Tmp 'analyzers.log'
    # style and analyzers are named rather than left to a bare `dotnet format`: its whitespace pass undoes what
    # csharpier just wrote, and the two then fight over every file.
    if ($check) {
        Invoke-Gate -Title 'dotnet csharpier check .' -Log $csharpierLog -Seconds $script:FormatSeconds -Slot heavy `
            -Gate @('dotnet', 'csharpier', 'check', '.')
        Invoke-Gate -Title 'dotnet format style GodotPadClaim.slnx --verify-no-changes --severity info' `
            -Log $formatLog -Seconds $script:FormatSeconds -Slot heavy `
            -Gate @('dotnet', 'format', 'style', 'GodotPadClaim.slnx', '--verify-no-changes', '--severity', 'info')
        Invoke-Gate -Title 'dotnet format analyzers GodotPadClaim.slnx --verify-no-changes --severity info' `
            -Log $analyzersLog -Seconds $script:FormatSeconds -Slot heavy `
            -Gate @('dotnet', 'format', 'analyzers', 'GodotPadClaim.slnx', '--verify-no-changes', '--severity', 'info')
    }
    else {
        Invoke-Gate -Title 'dotnet csharpier format .' -Log $csharpierLog -Seconds $script:FormatSeconds -Slot heavy `
            -Gate @('dotnet', 'csharpier', 'format', '.')
        Invoke-Gate -Title 'dotnet format style GodotPadClaim.slnx --severity info' -Log $formatLog `
            -Seconds $script:FormatSeconds -Slot heavy `
            -Gate @('dotnet', 'format', 'style', 'GodotPadClaim.slnx', '--severity', 'info')
        Invoke-Gate -Title 'dotnet format analyzers GodotPadClaim.slnx --severity info' -Log $analyzersLog `
            -Seconds $script:FormatSeconds -Slot heavy `
            -Gate @('dotnet', 'format', 'analyzers', 'GodotPadClaim.slnx', '--severity', 'info')
    }
    # Neither formatter wraps a comment or a string literal, so the 150-character line check runs here too: without it
    # a long line surfaces only at commit. It reads and never writes, so both modes run it last.
    Invoke-Gate -Title 'Test-LineLength.ps1 -All' -Log (Join-Path $script:Tmp 'line-length.log') `
        -Seconds $script:LineLengthSeconds -Slot light `
        -Gate @('pwsh', (Join-Path $script:Root 'tools\hooks\Test-LineLength.ps1'), '-All')
}

# --- dispatch ---

switch ($Command) {
    '' { Invoke-Help }
    'help' { Invoke-Help }
    'build' { Invoke-Build }
    'test' { Invoke-Test }
    'format' { Invoke-Format }
}
