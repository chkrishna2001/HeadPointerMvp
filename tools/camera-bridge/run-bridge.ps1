<#
Runs the native camera bridge (HeadPointerMvp.CameraBridge.exe) directly for manual testing,
optionally with a preview window. Build the solution first (or from Visual Studio) so the exe
exists under src\HeadPointerMvp\bin\<Configuration>\<TFM>\CameraBridge\.
#>
param(
    [int]$Camera = 0,
    [int]$Port = 11573,
    [switch]$InvertYaw,
    [switch]$ShowPreview,
    [string]$Configuration = "Debug"
)

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe = Get-ChildItem -Path (Join-Path $repoRoot "src\HeadPointerMvp\bin\$Configuration") -Filter "HeadPointerMvp.CameraBridge.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1

if (-not $exe) {
    Write-Error "Could not find HeadPointerMvp.CameraBridge.exe under src\HeadPointerMvp\bin\$Configuration. Build the solution first."
    exit 1
}

$argsList = @("--port", $Port, "--camera", $Camera)
if ($InvertYaw) { $argsList += "--invert-yaw" }
if ($ShowPreview) { $argsList += "--show-preview" }

& $exe.FullName @argsList
