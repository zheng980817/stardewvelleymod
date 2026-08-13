param(
    [string]$GamePath = ""
)

$ErrorActionPreference = "Stop"

$buildArgs = @("build", "SleepInNPCBeds.csproj", "-c", "Release")
if ($GamePath) {
    $buildArgs += "-p:GamePath=$GamePath"
}

& dotnet @buildArgs

Write-Host ""
Write-Host "构建完成。如果游戏在常见安装路径，mod 已自动复制到游戏的 Mods 文件夹；"
Write-Host "也可以在 bin/Release/net6.0/ 下找到发布用的 zip 包。"
