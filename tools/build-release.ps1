# 配布物を作る：インストーラー（配布/JapanDrive3D-Setup-<バージョン>.exe）と zip（配布/JapanDrive3D-win-x64.zip）
# GitHub のリリースには両方を置く。
#   ・新しいゲーム（v1.3.0〜）の自動更新はインストーラーを使う
#   ・古いゲーム（v1.1.0 / v1.2.0）の自動更新は zip を使う
# 使い方: powershell -ExecutionPolicy Bypass -File tools/build-release.ps1
# バージョンは JapanDrive3D/JapanDrive3D.csproj の <Version> を使う。
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root "JapanDrive3D\JapanDrive3D.csproj"
$version = ([regex]::Match((Get-Content $proj -Raw -Encoding UTF8), "<Version>([^<]+)</Version>")).Groups[1].Value
if (-not $version) { throw "csproj に <Version> がありません" }

$dist = Join-Path $root "配布"
$app = Join-Path $dist "日本一周ドライブ3D"
$setup = Join-Path $dist "JapanDrive3D-Setup-$version.exe"
$zip = Join-Path $dist "JapanDrive3D-win-x64.zip"
Write-Host "== 日本一周ドライブ3D v$version =="

# 1. 単体 exe（インストーラーと zip に入れる元）
dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $app -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish に失敗しました" }

# 2. zip（古いゲームの自動更新はこの名前を探すので変えないこと）
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $app -DestinationPath $zip

# 3. インストーラー（NSIS）。新しいゲームの自動更新は「JapanDrive3D-Setup-<番号>.exe」という名前を探すので変えないこと
$makensis = @(
    $env:MAKENSIS,
    "${env:ProgramFiles(x86)}\NSIS\makensis.exe",
    "$env:ProgramFiles\NSIS\makensis.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $makensis) {
    # ALLGAME（electron-builder）が持っている NSIS を使う
    $makensis = Get-ChildItem "$env:LOCALAPPDATA\electron-builder\Cache" -Recurse -Filter makensis.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -notmatch '\\Bin$' } | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $makensis) { throw "NSIS（makensis.exe）が見つかりません。NSIS をインストールするか、環境変数 MAKENSIS に場所を設定してください" }
& $makensis /V2 /INPUTCHARSET UTF8 "/DVERSION=$version" "/DSRCDIR=$app" "/DOUTFILE=$setup" (Join-Path $root "installer\JapanDrive3D.nsi")
if ($LASTEXITCODE -ne 0) { throw "インストーラーの作成に失敗しました" }

Write-Host ""
Write-Host "できあがり:"
Get-Item $setup, $zip | ForEach-Object { Write-Host ("  {0}  ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) }
