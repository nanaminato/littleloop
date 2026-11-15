param([string]$Destination = 'data/discovery-demo')
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$target = [IO.Path]::GetFullPath($Destination, $repo)
if (Test-Path -LiteralPath $target) { throw 'Choose a fresh destination.' }
$project = Join-Path $target 'project'
New-Item -ItemType Directory -Path $project | Out-Null
Get-ChildItem (Join-Path $repo 'src') -Recurse -File | Where-Object { $_.Extension -in '.cs', '.csproj' -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | ForEach-Object {
    $out = Join-Path $project ([IO.Path]::GetRelativePath($repo, $_.FullName))
    New-Item -ItemType Directory -Force -Path (Split-Path $out -Parent) | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $out
}
$tests = Join-Path $project 'tests/Project.Checks'
New-Item -ItemType Directory -Path $tests | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'DiscoveryChecks.cs') (Join-Path $tests 'Program.cs')
'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="../../src/LittleLoop.Cli/LittleLoop.Cli.csproj"/></ItemGroup></Project>' | Set-Content (Join-Path $tests 'Project.Checks.csproj')
$model = Get-Content (Join-Path $repo 'examples/models/deepseek.json') -Raw | ConvertFrom-Json
$model.maxObservationCharacters = 6000; $model.maxInputCharacters = 100000
$model | ConvertTo-Json | Set-Content (Join-Path $target 'model.json') -Encoding utf8
# Intentionally no policy, file allowlist, command list, runtime state or scheduled steps.
Write-Output $project
