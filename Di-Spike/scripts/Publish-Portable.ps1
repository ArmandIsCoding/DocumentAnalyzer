param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $workspace ('dist\release-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Output directory already exists. Choose a new path; existing packages are never overwritten.' }
New-Item -ItemType Directory -Path $destination | Out-Null
$portable = Join-Path $destination 'InvoiceExplorer-win-x64'
$source = Join-Path $destination 'InvoiceExplorer-source'
New-Item -ItemType Directory -Path $portable,$source | Out-Null

# Publish separate executables with all native/runtime files. Trimming and single-file bundling
# are deliberately disabled: reflection, Blazor assets, PDFium, Skia, and SQLite stay explicit.
foreach ($project in @('Cli','Web')) {
    & dotnet publish (Join-Path $workspace "src\InvoiceExplorer.$project") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o (Join-Path $portable $project.ToLowerInvariant())
    if ($LASTEXITCODE -ne 0) { throw "Publication failed for $project." }
}

# Explicit allowlists prevent accidentally shipping business data, credentials, local logs, or screenshots.
$rootFiles = @('README.md','appsettings.json','appsettings.local.example.json','invoice-workspace.marker')
foreach ($file in $rootFiles) { Copy-Item -LiteralPath (Join-Path $workspace $file) -Destination $portable }
Copy-Item -LiteralPath (Join-Path $workspace 'docs') -Destination $portable -Recurse
New-Item -ItemType Directory -Path (Join-Path $portable 'invoices') | Out-Null
foreach ($file in @('Run-Console.cmd','Start-Review.cmd')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $portable }

$sourceRootFiles = $rootFiles + @('InvoiceExplorer.slnx','global.json','Directory.Build.props','.gitignore')
foreach ($file in $sourceRootFiles) { Copy-Item -LiteralPath (Join-Path $workspace $file) -Destination $source }
foreach ($folder in @('src','tests','docs','scripts')) {
    Get-ChildItem -LiteralPath (Join-Path $workspace $folder) -File -Recurse | Where-Object {
        $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -ne '.user' -and $_.Name -ne 'appsettings.local.json'
    } | ForEach-Object {
        $relative = $_.FullName.Substring($workspace.Length + 1)
        $target = Join-Path $source $relative
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $target
    }
}
New-Item -ItemType Directory -Path (Join-Path $source 'invoices') | Out-Null

# Keep NuGet metadata and bundled license/notice files alongside redistributed binaries.
$licenses = Join-Path $portable 'licenses'
New-Item -ItemType Directory -Path $licenses | Out-Null
$inventory = @()
foreach ($project in @('Cli','Web')) {
    $assets = Get-Content -LiteralPath (Join-Path $workspace "src\InvoiceExplorer.$project\obj\project.assets.json") -Raw | ConvertFrom-Json
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package' -or $inventory -contains $library.Name) { continue }
        $inventory += $library.Name
        foreach ($packageRoot in $assets.packageFolders.PSObject.Properties.Name) {
            $packagePath = Join-Path $packageRoot $library.Value.path
            if (-not (Test-Path -LiteralPath $packagePath)) { continue }
            $targetRoot = Join-Path $licenses ($library.Name -replace '/', '-')
            Get-ChildItem -LiteralPath $packagePath -File -Recurse | Where-Object { $_.Name -match '(?i)(license|notice|copying|copyright)' -or $_.Extension -eq '.nuspec' } | ForEach-Object {
                $target = Join-Path $targetRoot ($_.FullName.Substring($packagePath.Length).TrimStart('\','/'))
                New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $target
            }
        }
    }
}
$inventory | Sort-Object | Set-Content -LiteralPath (Join-Path $licenses 'PACKAGE-INVENTORY.txt') -Encoding UTF8
# Runtime packs are not ordinary application libraries in project.assets.json. Include their notices too.
foreach ($project in @('Cli','Web')) {
    $runtime = Get-Content -LiteralPath (Join-Path $portable ($project.ToLowerInvariant() + "\InvoiceExplorer.$project.runtimeconfig.json")) -Raw | ConvertFrom-Json
    foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
        $id = ($framework.name + '.Runtime.win-x64').ToLowerInvariant()
        foreach ($packageRoot in $assets.packageFolders.PSObject.Properties.Name) {
            $pack = Join-Path (Join-Path $packageRoot $id) $framework.version
            if (-not (Test-Path -LiteralPath $pack)) { continue }
            $target = Join-Path $licenses ($id + '-' + $framework.version)
            New-Item -ItemType Directory -Path $target -Force | Out-Null
            Get-ChildItem -LiteralPath $pack -File | Where-Object { $_.Name -match '(?i)(license|notice)' -or $_.Extension -eq '.nuspec' } | Copy-Item -Destination $target
        }
    }
}
Copy-Item -LiteralPath $licenses -Destination $source -Recurse

# Reject accidental sensitive or obsolete dependency files before producing a transfer artifact.
foreach ($tree in @($portable,$source)) {
    $unexpected = Get-ChildItem -LiteralPath $tree -File -Recurse | Where-Object { $_.Name -in @('appsettings.local.json','invoice-settings.local.json') -or $_.Extension -in @('.db','.pdf') -or $_.Name -like 'PdfSharp*' }
    if ($unexpected) { throw 'Package audit found an unexpected credential, database, invoice, or obsolete PDF dependency file.' }
}
foreach ($tree in @($portable,$source)) {
    $zip = $tree + '.zip'
    Compress-Archive -LiteralPath $tree -DestinationPath $zip -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    "$hash  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ASCII
    Write-Output "Created: $zip"
}
