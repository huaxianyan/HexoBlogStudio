# 打一个可直接双击的 exe 到 dist/
# 框架依赖发布：只带自己的代码，运行时用本机已装的 .NET 10 桌面运行时（约 2 MB）
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet publish -c Release -o dist --no-self-contained -p:PublishSingleFile=true -p:DebugType=none
Write-Output "产物：dist/HexoBlogStudio.exe（需要 .NET 10 桌面运行时）"
