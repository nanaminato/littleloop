param([string]$Destination = 'data/repository-demo')
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$target = [IO.Path]::GetFullPath($Destination, $repo)
if (Test-Path -LiteralPath $target) { throw 'Choose a fresh destination.' }
$project = Join-Path $target 'project'
New-Item -ItemType Directory -Path $project | Out-Null
# Copy actual source, never credentials, Git metadata, caches or generated binaries.
Get-ChildItem (Join-Path $repo 'src') -Recurse -File | Where-Object { $_.Extension -in '.cs', '.csproj' -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($repo, $_.FullName)
    $out = Join-Path $project $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $out -Parent) | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $out
}
$verify = Join-Path $project 'acceptance'
New-Item -ItemType Directory -Path $verify | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'RepositoryChecks.cs') (Join-Path $verify 'Program.cs')
'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="../src/LittleLoop.Cli/LittleLoop.Cli.csproj"/></ItemGroup></Project>' | Set-Content (Join-Path $verify 'Acceptance.csproj')
$dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
$writable = @(Get-ChildItem (Join-Path $project 'src/LittleLoop.Cli') -Filter '*.cs' | ForEach-Object { [IO.Path]::GetRelativePath($project, $_.FullName).Replace('\','/') })
$policy = @{ root = 'project'; writableFiles = $writable; readExtensions = @('.cs','.csproj','.md'); maxFileBytes = 262144; maxFiles = 500; timeoutMilliseconds = 120000; maxOutputCharacters = 32768; budget = 48; maxModelCalls = 60; maxFailures = 8;
    commands = @(@{ id = 'cli-acceptance'; executable = $dotnet; arguments = @('run','-p:BuildInParallel=false','--project','acceptance/Acceptance.csproj','--disable-build-servers','-v:q','--','../policy.json',$dotnet); isTest = $true; protectedFiles = @('acceptance/Program.cs','acceptance/Acceptance.csproj') }) }
$policy | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $target 'policy.json') -Encoding utf8
@{ maxConcurrentExecutions = 1; totalActionBudget = 100 } | ConvertTo-Json | Set-Content (Join-Path $target 'limits.json') -Encoding utf8
$model = Get-Content (Join-Path $repo 'examples/models/deepseek.json') -Raw | ConvertFrom-Json
$model.maxObservationCharacters = 6000; $model.maxInputCharacters = 100000
$model | ConvertTo-Json | Set-Content (Join-Path $target 'model.json') -Encoding utf8
Write-Output $target
