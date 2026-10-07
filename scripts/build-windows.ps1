# Полная сборка PDF Meta Studio под Windows 11 x64.
# Требуется: Visual Studio 2022 (или Build Tools) с компонентом «Разработка классических приложений на C++»,
# CMake ≥ 3.21, Git, .NET SDK 10.0.1xx, vcpkg (переменная VCPKG_ROOT). Запускать из «Developer PowerShell for VS».
param([string]$Configuration = 'Release', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $env:VCPKG_ROOT) { throw 'Укажите VCPKG_ROOT (каталог vcpkg)' }

& (Join-Path $PSScriptRoot 'fetch-deps.ps1')

$build = Join-Path $root 'build\worker'
cmake -S (Join-Path $root 'worker') -B $build -G 'Visual Studio 17 2022' -A x64 `
    "-DCMAKE_TOOLCHAIN_FILE=$env:VCPKG_ROOT\scripts\buildsystems\vcpkg.cmake" `
    -DVCPKG_TARGET_TRIPLET=x64-windows-static
if ($LASTEXITCODE) { throw 'cmake configure failed' }
# qpdf CLI нужен тестам для создания зашифрованных PDF.
cmake --build $build --config $Configuration --target pdfmeta-worker
if ($LASTEXITCODE) { throw 'worker build failed' }
cmake --build $build --config $Configuration --target test-tools
if ($LASTEXITCODE) { throw 'qpdf CLI build failed' }
$worker = Join-Path $build "$Configuration\pdfmeta-worker.exe"

if (-not $SkipTests) {
    $validation = Join-Path $build 'validation'
    New-Item -ItemType Directory -Path $validation -Force | Out-Null
    $env:QPDF_CLI = (Get-ChildItem (Join-Path $build 'qpdf') -Recurse -Filter qpdf.exe | Select-Object -First 1).FullName
    $env:QPDF_CORPUS = Join-Path $root "worker\external\qpdf\qpdf\qtest\qpdf"
    python (Join-Path $root "worker\tests\run_tests.py") $worker --corpus $env:QPDF_CORPUS --report (Join-Path $validation 'native-worker.json')
    if ($LASTEXITCODE) { throw 'worker tests failed' }
    $env:PDFMETA_WORKER = $worker
    $env:PDFMETA_TEST_WORKER = Join-Path $build "$Configuration\pdfmeta-test-worker.exe"
    dotnet test (Join-Path $root 'tests\PdfMetaStudio.Core.Tests') -c $Configuration --logger 'trx;LogFileName=core-tests.trx' --results-directory $validation
    if ($LASTEXITCODE) { throw 'dotnet test failed' }
}

$out = Join-Path $root 'dist\PdfMetaStudio'
dotnet publish (Join-Path $root 'src\PdfMetaStudio.App') -c $Configuration -r win-x64 --self-contained `
    "-p:WorkerBinDir=$(Split-Path $worker)" -o $out
if ($LASTEXITCODE) { throw 'dotnet publish failed' }
Write-Host "Готово: $out\PdfMetaStudio.exe"

$publishedValidation = Join-Path $out 'validation'
if (Test-Path $publishedValidation) { Remove-Item -LiteralPath $publishedValidation -Recurse -Force }
if (-not $SkipTests) {
    Copy-Item -LiteralPath $validation -Destination $publishedValidation -Recurse
    $commit = git -C $root rev-parse HEAD
    $evidence = if ($env:GITHUB_RUN_ID) { "https://github.com/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID" } else { 'Local build; no CI run attached.' }
    @("Executed native and managed tests for source commit: $commit", "Build host: $([Environment]::OSVersion.VersionString)", "GUI and MSI acceptance execute after packaging; their results are recorded at: $evidence", "Independent corpus reports: https://github.com/qenuternis2/pdf-meta-studio/tree/$commit/docs/validation", 'Real Windows 11, Narrator and physical DPI acceptance remains pending. These reports do not establish those manual results.') | Set-Content (Join-Path $publishedValidation 'README.txt') -Encoding utf8
}

& "$PSScriptRoot\collect-licenses.ps1" -PublishDir $out
& "$PSScriptRoot\build-installer.ps1" -PublishDir $out
