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
      demo       Build demo/PadClaimDemo.csproj in Debug, then drive Godot headless: --import --quit (a pass red on
                 the cold-cache line alone runs again, up to three) and --build-solutions --quit.
                 $env:GODOT_PATH overrides the console executable.
      smoke      The demo build, then Godot headless on res://Smoke/ClaimSmoke.tscn, which injects button presses and
                 exits non-zero unless the taps, the A-A near-miss and the two-button claim all come out as expected.
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
    [ValidateSet('', 'help', 'build', 'test', 'format', 'demo', 'smoke')]
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
$script:DemoProject = 'demo'
$script:GodotConsole = if ($env:GODOT_PATH) { $env:GODOT_PATH } else { 'F:\Godot\Godot_console.exe' }
$script:ImportSeconds = 300
$script:BuildSolutionsSeconds = 180
$script:SmokeSeconds = 600
$script:SmokeScene = 'res://Smoke/ClaimSmoke.tscn'
# How many times a cold import may run. A pass can end on the same pre-import line as the one before it, because the
# importer writes its cache as it goes, so the import gets three tries at a cache it is still filling.
$script:ImportPasses = 3
# What a cold import complains about before it has imported anything: the cache the pass is only now writing. The demo
# has no imported art, so this is the whole list; anything else an import reports is a real failure.
$script:ImportMarkers = @('.godot/imported/')

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
  demo                                Debug build of demo/PadClaimDemo.csproj, then Godot --headless --import --quit
                                      (a cold pass red on the import-cache line alone is retried, up to three) and
                                      --build-solutions --quit; GODOT_PATH overrides the console executable
  smoke                               Debug build of demo/PadClaimDemo.csproj, then Godot --headless on
                                      res://Smoke/ClaimSmoke.tscn; exits non-zero unless the taps, the A-A
                                      near-miss and the two-button claim come out as expected
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
    # so each assembly's 8 is ignored; the minimum of one test is checked per test assembly (with one test project that is
    # the whole run), which exits 9 when the filter matched nothing.
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

# --- demo ---

# Whether a red import failed at nothing but the lines $script:ImportMarkers names, the block a cold cache always
# prints. A log holding no ERROR: line at all passes the test, since nothing in it goes unexplained.
function Test-ImportRedOnMarker {
    [OutputType([bool])]
    param([string]$Log)
    if (-not (Test-Path -LiteralPath $Log)) {
        return $false
    }
    foreach ($line in @(Get-Content -LiteralPath $Log)) {
        # The gate prefixes every line it writes with an ISO timestamp and a stream tag, so the payload is what the
        # markers are read from rather than the line as it stands.
        $text = (([string]$line) -replace '^\d{4}-\d\d-\d\dT\S+ (?:gat|out|err) ', '').Trim()
        if (-not $text.StartsWith('ERROR:')) {
            continue
        }
        $explained = $false
        foreach ($marker in $script:ImportMarkers) {
            if ($text.Contains($marker)) {
                $explained = $true
                break
            }
        }
        if (-not $explained) {
            return $false
        }
    }
    return $true
}

# The demo project's Debug build on its own, so a run that follows opens with a compiling assembly: a Godot import or
# solutions pass against source that no longer compiles reports on the last good one instead.
function Invoke-DemoBuild {
    Invoke-Gate -Title 'dotnet build demo/PadClaimDemo.csproj -c Debug -warnaserror' `
        -Log (Join-Path $script:Tmp 'demo-dotnet-build.log') -Seconds $script:BuildSeconds -Slot heavy `
        -Gate @('dotnet', 'build', "$($script:DemoProject)/PadClaimDemo.csproj", '-c', 'Debug', '-warnaserror')
}

# The import, before --build-solutions: with nothing under .godot/imported the editor cannot load the project and the
# solutions pass can die before it builds. The first pass can end red on the cold-cache line alone, so such a pass runs
# again, up to three passes; a gate kill or any other red stops here.
function Invoke-DemoImport {
    $import = @($script:GodotConsole, '--headless', '--path', $script:DemoProject, '--import', '--quit')
    for ($pass = 1; $pass -le $script:ImportPasses; $pass++) {
        $name = if ($pass -eq 1) { 'demo-import' } else { "demo-import-$pass" }
        $log = Join-Path $script:Tmp "$name.log"
        Write-Section "Godot --import --quit (pass $pass of $($script:ImportPasses))"
        & $script:GateScript -Log $log -TimeoutSeconds $script:ImportSeconds -Slot light -- @import
        $imported = $LASTEXITCODE
        if ($imported -eq 0) {
            break
        }
        if ($imported -eq 124) {
            throw "Godot --import was killed by the gate: $(Get-GateKill -Log $log)"
        }
        if (-not (Test-ImportRedOnMarker -Log $log)) {
            throw "Godot --import failed (exit $imported) on lines the cold-cache markers do not explain; read $log"
        }
        if ($pass -eq $script:ImportPasses) {
            throw "Godot --import still failed after $($script:ImportPasses) passes (exit $imported); read $log"
        }
        Write-Host "the import ended red on the cold-cache line alone; running pass $($pass + 1)" -ForegroundColor Yellow
    }
}

# Godot_console.exe is a job-object wrapper that returns only when every process in its job has exited, and a build Godot
# runs on a tree with no live build servers births MSBuild's worker nodes and the compiler server inside that job, where
# they idle for up to fifteen minutes after Godot itself has quit. With node reuse and shared compilation off for the
# launch, the job empties when Godot does.
function Invoke-DemoSolutionBuild {
    $nodeReuse = $env:MSBUILDDISABLENODEREUSE
    $shared = $env:UseSharedCompilation
    $env:MSBUILDDISABLENODEREUSE = '1'
    $env:UseSharedCompilation = 'false'
    try {
        Invoke-Gate -Title 'Godot --build-solutions --quit' -Log (Join-Path $script:Tmp 'demo-build.log') `
            -Seconds $script:BuildSolutionsSeconds -Slot heavy `
            -Gate @($script:GodotConsole, '--headless', '--path', $script:DemoProject, '--build-solutions', '--quit')
    }
    finally {
        $env:MSBUILDDISABLENODEREUSE = $nodeReuse
        $env:UseSharedCompilation = $shared
    }
}

# Before anything is built, so a missing engine names itself instead of surfacing three red imports later: a console
# that is not there writes no ERROR: line at all, which the cold-cache test reads as nothing to explain.
function Assert-GodotConsole {
    if (-not (Test-Path -LiteralPath $script:GodotConsole)) {
        throw "Godot console not found at $($script:GodotConsole); set GODOT_PATH to Godot_console.exe"
    }
}

# The demo project through the fresh-clone order: the Debug build, the import, then the solutions pass.
function Invoke-Demo {
    if ($script:Rest.Count -gt 0) {
        throw "demo takes no arguments, not: $($script:Rest -join ' ')"
    }
    Assert-GodotConsole
    Invoke-DemoBuild
    Invoke-DemoImport
    Invoke-DemoSolutionBuild
}

# --- smoke ---

# The headless claim smoke: the demo build, then the smoke scene, which injects button presses into the running engine
# and exits 0 only when the taps, the A-A near-miss and the two-button claim all come out as expected. A non-zero exit is
# the scene's own verdict, so it is reported with the log, whose `smoke:` lines say which scenario failed and what the
# claimer recorded.
function Invoke-Smoke {
    if ($script:Rest.Count -gt 0) {
        throw "smoke takes no arguments, not: $($script:Rest -join ' ')"
    }
    Assert-GodotConsole
    Invoke-DemoBuild
    Invoke-Gate -Title 'Godot --headless res://Smoke/ClaimSmoke.tscn' -Log (Join-Path $script:Tmp 'smoke.log') `
        -Seconds $script:SmokeSeconds -Slot light `
        -Gate @($script:GodotConsole, '--headless', '--path', $script:DemoProject, $script:SmokeScene)
}

# --- dispatch ---

switch ($Command) {
    '' { Invoke-Help }
    'help' { Invoke-Help }
    'build' { Invoke-Build }
    'test' { Invoke-Test }
    'format' { Invoke-Format }
    'demo' { Invoke-Demo }
    'smoke' { Invoke-Smoke }
}
