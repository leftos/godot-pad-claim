# Development

The toolchain, the commands and their ceilings, the hooks and how tests run.

## Toolchain

| Tool | Version | Where |
|---|---|---|
| .NET SDK | `global.json` names 10.0.401 and rolls forward to the latest feature band (`latestFeature`); the test runner is Microsoft.Testing.Platform | `dotnet` |
| CSharpier | 1.3.0, pinned in `.config/dotnet-tools.json` | `dotnet tool restore` once per clone |
| PowerShell | 7.4 or later (`padclaim.ps1` requires it, and the hook scripts and `tools/gate.ps1` run under `pwsh`) | `pwsh` |
| prek | the git hooks in `prek.toml` | `prek install` once per clone |
| gitleaks | the secrets scan the `gitleaks` hook runs on staged changes | `gitleaks` |
| PSScriptAnalyzer | the lint the `psscriptanalyzer` hook runs, under `PSScriptAnalyzerSettings.psd1` | a PowerShell module |

## First clone

From the repo root:

1. `dotnet tool restore` (CSharpier).
2. `prek install -f` (installs the pre-commit and pre-push hooks).
3. `pwsh ./padclaim.ps1 build`, then `pwsh ./padclaim.ps1 test`.

## Commands

Everything runs from the repo root through `padclaim.ps1`; `pwsh ./padclaim.ps1 help` lists every subcommand. Each command that builds, tests or formats goes through `tools/gate.ps1`, the gate: it writes the whole output to a log under `.tmp/`, prints the tail and exits with the command's own status.

The gate kills a run for one of three reasons, each exit 124: it stalled (no output and no CPU for 120 s), it used up its ceiling on a load-adjusted clock (which slows while other work keeps the machine busy), or five times its ceiling passed in wall time. `padclaim.ps1` names which one it was.

| Command | Does | Ceiling |
|---|---|---|
| `build [-Release]` | `dotnet build GodotPadClaim.slnx -warnaserror`, Debug unless `-Release` | 300 s |
| `test [-Filter "*X"]` | `dotnet build -c Release -warnaserror` of the solution, then `dotnet test -c Release --no-build`; `-Filter` becomes `--filter-class` and fails (exit 9) when no test ran, checked per test assembly (with one test project that is the whole run); the rest forwards | build 300 s, then 120 s; 60 s with `-Filter` |
| `format [-Check]` | `dotnet csharpier format .`, then `dotnet format style` and `dotnet format analyzers` at `--severity info`, then `tools/hooks/Test-LineLength.ps1 -All`; `-Check` only verifies and writes nothing | 180 s each, 60 s for the line check |
| `demo` | Debug build of `demo/PadClaimDemo.csproj`, then Godot `--headless --import --quit` (a cold pass red on the import cache alone is retried, up to three) and `--build-solutions --quit`; stops at once when the Godot console is missing (`GODOT_PATH` overrides `F:\Godot\Godot_console.exe`) | build 300 s, import 300 s, build-solutions 180 s |
| `smoke` | the demo's Debug build, then Godot `--headless` on `res://Smoke/ClaimSmoke.tscn`, the claim smoke on an injected clock; exits non-zero unless the taps, the A, A near-miss and the two-button claim come out as expected | 600 s |
| `help` | prints the list | |

MSBuild switches are written in dash form (`-warnaserror`, `-p:Name=Value`): Git Bash rewrites the slash form into a Windows path and MSBuild reports MSB1008.

## Tests

Tests are xunit.v3 on Microsoft.Testing.Platform (`global.json`), and run in Release: `padclaim.ps1 test` builds the solution in Release with warnings as errors, then runs `dotnet test -c Release --no-build`. They are engine-free: the core and its tests reference no Godot assembly, so they need no engine installed.

A filter needs the full class name or a leading wildcard: `-Filter "*ReferenceTests"`. A bare class name runs zero tests, and a filter that runs no test fails the run.

## Hooks

`prek.toml` installs two stages. At pre-commit, in this order:

1. The builtin checks: `trailing-whitespace`, `end-of-file-fixer`, `mixed-line-ending --fix=lf` (every file LF but `.bat` and `.cmd`), `check-merge-conflict`, `detect-private-key` and `check-added-large-files` at 1024 KB.
2. `dotnet-format-style`: `dotnet format style` over the staged C# files (`tools/hooks/dotnet-format-wrapper.ps1`).
3. `csharpier`: `dotnet csharpier format` over the staged C# files (`tools/hooks/csharpier-wrapper.ps1`).
4. `psscriptanalyzer`: PSScriptAnalyzer over the staged PowerShell, under `PSScriptAnalyzerSettings.psd1`.
5. `line-length`: 150 characters for C# and PowerShell (`tools/hooks/Test-LineLength.ps1`).
6. `gitleaks`: `gitleaks git --pre-commit --staged --redact`.

The fixers run and re-stage first and the readers report on the result. The two C# wrappers `git add` what they changed. The hook scripts are pwsh, not bash: prek launched from PowerShell on Windows can resolve a bash hook to the WSL stub, which has no dotnet.

At pre-push, `padclaim-test` (`padclaim.ps1 test`: a warnings-as-errors build and the whole suite) runs unless the push carries Markdown alone.
