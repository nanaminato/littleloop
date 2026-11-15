param([string]$Destination = 'data/development-demo')
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$target = [IO.Path]::GetFullPath($Destination, $repo)
if (Test-Path -LiteralPath $target) { throw 'Destination already exists; choose a fresh directory.' }
$project = Join-Path $target 'project'
New-Item -ItemType Directory -Path $project | Out-Null
[IO.File]::WriteAllText((Join-Path $project 'calculator.py'), "def add(a, b):`n    return a - b`n")
[IO.File]::WriteAllText((Join-Path $project 'tests.py'), "import os, sys`nsys.path.insert(0, os.path.dirname(__file__))`nfrom calculator import add`nassert add(2, 3) == 5, '2 + 3 must be 5'`nassert add(-2, 3) == 1, '-2 + 3 must be 1'`nprint('All addition tests passed')`n")
$policy = @{ root = 'project'; writableFiles = @('calculator.py'); readExtensions = @('.py'); commands = @(@{ id = 'unit'; executable = (Get-Command python -CommandType Application | Select-Object -First 1).Source; arguments = @('-I', '-B', 'tests.py'); isTest = $true; protectedFiles = @('tests.py') }) }
$policy | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $target 'policy.json') -Encoding utf8
@{ maxConcurrentExecutions = 1; totalActionBudget = 100 } | ConvertTo-Json | Set-Content (Join-Path $target 'limits.json') -Encoding utf8
Write-Output $target


