param([Parameter(Mandatory)][string]$PublishDir)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $PublishDir 'licenses'
New-Item -ItemType Directory -Path $out -Force | Out-Null
function Copy-License([string]$Source, [string]$Name) {
    if (-not (Test-Path $Source -PathType Leaf)) { throw "Required license missing: $Source" }
    Copy-Item -LiteralPath $Source -Destination (Join-Path $out $Name)
}
Copy-License "$root\worker\external\qpdf\LICENSE.txt" 'qpdf-LICENSE.txt'
Copy-License "$root\worker\external\qpdf\NOTICE.md" 'qpdf-NOTICE.md'
Copy-License "$root\worker\external\xmp\LICENSE" 'Adobe-XMP-LICENSE.txt'
Copy-License "$root\worker\external\expat\expat\COPYING" 'Expat-LICENSE.txt'
Copy-License "$root\worker\third_party\nlohmann\LICENSE.MIT" 'nlohmann-json-LICENSE.txt'
$nuget = ((dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', '').Trim()
Copy-License "$nuget\communitytoolkit.mvvm\8.4.0\License.md" 'CommunityToolkit-Mvvm-LICENSE.md'
Copy-License "$nuget\communitytoolkit.mvvm\8.4.0\ThirdPartyNotices.txt" 'CommunityToolkit-Mvvm-NOTICES.txt'
$publishedDeps = Get-Content (Join-Path $PublishDir 'PdfMetaStudio.deps.json') -Raw | ConvertFrom-Json
$runtimeVersions = @()
foreach ($pack in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
    $library = $publishedDeps.libraries.PSObject.Properties.Name | Where-Object { $_ -like "runtimepack.$pack/*" } | Select-Object -First 1
    if (-not $library) { throw "Published runtime identity missing: $pack" }
    $runtimeVersion = ($library -split '/')[1]
    $runtimeVersions += "$pack $runtimeVersion"
    $packageDir = Join-Path $nuget "$pack\$runtimeVersion"
    if (-not (Test-Path $packageDir)) {
        # The SDK may supply a runtime pack without placing it in the NuGet global cache.
        # Fetch the exact published package to collect its original license, not a guessed SDK version.
        $packageDir = Join-Path $root "build\license-packages\$pack\$runtimeVersion"
        New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
        $archive = Join-Path $root "build\license-packages\$pack-$runtimeVersion.zip"
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$pack/$runtimeVersion/$pack.$runtimeVersion.nupkg" -OutFile $archive
        Expand-Archive -LiteralPath $archive -DestinationPath $packageDir -Force
    }
    $license = Get-ChildItem $packageDir -File | Where-Object Name -Match '^LICENSE(\.TXT)?$' | Select-Object -First 1
    if (-not $license) { throw "Runtime license missing: $pack $runtimeVersion" }
    Copy-License $license.FullName "$pack-LICENSE.txt"
    $notices = Get-ChildItem $packageDir -File | Where-Object Name -Match '^THIRD-PARTY-NOTICES\.TXT$' | Select-Object -First 1
    if ($notices) { Copy-License $notices.FullName "$pack-NOTICES.txt" }
    elseif ($pack -eq 'microsoft.windowsdesktop.app.runtime.win-x64') {
        "$pack $runtimeVersion publishes its MIT license and no additional notice file in the runtime package." | Set-Content (Join-Path $out "$pack-NOTICES.txt") -Encoding utf8
    } else { throw "Runtime notices missing: $pack $runtimeVersion" }
}
$native = Join-Path $root 'build\worker\vcpkg_installed\x64-windows-static\share'
foreach ($port in @('zlib', 'libjpeg-turbo')) { Copy-License "$native\$port\copyright" "$port-LICENSE.txt" }
Copy-License "$root\scripts\deps.lock" 'native-versions.lock'
Copy-License "$root\worker\vcpkg.json" 'vcpkg-baseline.json'
Copy-License "$root\Directory.Packages.props" 'managed-versions.props'
$commit = git -C $root rev-parse HEAD
@("PDF Meta Studio third-party license bundle", "Source commit: $commit", "Published runtime packages: $($runtimeVersions -join '; ')", "CommunityToolkit.Mvvm 8.4.0", "nlohmann/json 3.11.3", "qpdf 12.4.2; Adobe XMP Toolkit 2025.03; Expat 2.9.0", "zlib and libjpeg-turbo: versions fixed by the enclosed vcpkg baseline", "The full licenses and notices are included in this directory.") | Set-Content (Join-Path $out 'MANIFEST.txt') -Encoding utf8
