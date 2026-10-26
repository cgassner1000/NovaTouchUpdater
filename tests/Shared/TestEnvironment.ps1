# Shared paths only. No application configuration, hosts or credentials are read.
$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$TestSourceRoot = Join-Path $RepositoryRoot 'src\NovaTouchUpdater'
$TestScriptsRoot = Join-Path $TestSourceRoot 'Scripts'
$TestArtifactsRoot = Join-Path $RepositoryRoot 'artifacts\test-results'
[void][IO.Directory]::CreateDirectory($TestArtifactsRoot)
