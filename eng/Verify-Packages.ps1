param(
    [Parameter(Mandatory = $true)]
    [string] $PackagesPath,

    [Parameter(Mandatory = $false)]
    [string] $Version = "0.1.0-preview.1"
)

$ErrorActionPreference = "Stop"
$packages = (Resolve-Path -LiteralPath $PackagesPath).Path
$repository = Split-Path -Parent $PSScriptRoot
$nugetSource = "https://api.nuget.org/v3/index.json"
$ids = @(
    "MailStencil.Core",
    "MailStencil.Scriban",
    "MailStencil.FileSystem",
    "MailStencil.AzureBlob"
)
$expectedDependencies = @{
    "MailStencil.Core" = @("Microsoft.Extensions.Caching.Memory", "Microsoft.Extensions.Logging", "Microsoft.Extensions.Options")
    "MailStencil.Scriban" = @("MailStencil.Core", "Scriban")
    "MailStencil.FileSystem" = @("MailStencil.Core")
    "MailStencil.AzureBlob" = @("Azure.Storage.Blobs", "MailStencil.Core")
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

foreach ($id in $ids) {
    $nupkg = Join-Path $packages "$id.$Version.nupkg"
    $snupkg = Join-Path $packages "$id.$Version.snupkg"
    if (-not (Test-Path -LiteralPath $nupkg -PathType Leaf)) { throw "Missing $nupkg" }
    if (-not (Test-Path -LiteralPath $snupkg -PathType Leaf)) { throw "Missing $snupkg" }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($nupkg)
    try {
        $names = @($archive.Entries | ForEach-Object FullName)
        foreach ($required in @("$id.nuspec", "README.md", "lib/net10.0/$id.dll", "lib/net10.0/$id.xml")) {
            if ($names -notcontains $required) { throw "$id package is missing $required" }
        }

        $unexpected = @($names | Where-Object {
            $_ -match '(^|/)(tests?|samples?|eng|src)/' -or
            $_ -match '(project\.assets\.json|\.csproj$|\.sln$|\.pdb$)'
        })
        if ($unexpected.Count -ne 0) { throw "$id package contains unexpected entries: $($unexpected -join ', ')" }

        $nuspecEntry = $archive.GetEntry("$id.nuspec")
        $reader = [System.IO.StreamReader]::new($nuspecEntry.Open())
        try { [xml] $nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }

        $namespace = [System.Xml.XmlNamespaceManager]::new($nuspec.NameTable)
        $namespace.AddNamespace("n", $nuspec.DocumentElement.NamespaceURI)
        $metadata = $nuspec.SelectSingleNode("/n:package/n:metadata", $namespace)
        if ($metadata.id -ne $id) { throw "$id package declares id '$($metadata.id)'" }
        if ($metadata.version -ne $Version) { throw "$id package declares version '$($metadata.version)'" }
        if (-not $metadata.description -or -not $metadata.authors -or -not $metadata.readme) {
            throw "$id package is missing required descriptive metadata"
        }
        if ($metadata.readme -ne "README.md") { throw "$id package has an unexpected readme path" }

        $dependencies = @($nuspec.SelectNodes("/n:package/n:metadata/n:dependencies/n:group/n:dependency", $namespace))
        $actualIds = @($dependencies | ForEach-Object id | Sort-Object)
        $wantedIds = @($expectedDependencies[$id] | Sort-Object)
        if (($actualIds -join "|") -ne ($wantedIds -join "|")) {
            throw "$id dependencies are '$($actualIds -join ', ')'; expected '$($wantedIds -join ', ')'"
        }
        foreach ($dependency in $dependencies) {
            if ([string]$dependency.version -match '^\[[^,]+\]$') {
                throw "$id uses an unnecessary exact dependency version for $($dependency.id)"
            }
        }

        $textEntries = @($archive.Entries | Where-Object { $_.FullName -match '\.(nuspec|md|xml)$' })
        foreach ($entry in $textEntries) {
            $textReader = [System.IO.StreamReader]::new($entry.Open())
            try { $text = $textReader.ReadToEnd() } finally { $textReader.Dispose() }
            if ($text -match '(?i)SECRET_(CUSTOMER|BODY|PATH|TOKEN)_\d+' -or
                $text -match '(?i)([A-Z]:\\Users\\|/Users/|/home/[^/]+/|/private/var/folders/)') {
                throw "$id contains a secret sentinel or local absolute path in $($entry.FullName)"
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    $symbols = [System.IO.Compression.ZipFile]::OpenRead($snupkg)
    try {
        $symbolNames = @($symbols.Entries | ForEach-Object FullName)
        if ($symbolNames -notcontains "lib/net10.0/$id.pdb") { throw "$id symbol package has no portable PDB" }
    }
    finally {
        $symbols.Dispose()
    }
}

$smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) "mailstencil-consumers-$([Guid]::NewGuid().ToString('N'))"
try {
    New-Item -ItemType Directory -Path $smokeRoot | Out-Null
    foreach ($consumer in @("FileSystem.Consumer", "Azure.Consumer")) {
        $source = Join-Path $repository "eng/package-smoke/$consumer"
        $destination = Join-Path $smokeRoot $consumer
        Copy-Item -LiteralPath $source -Destination $destination -Recurse
        $project = Join-Path $destination "$consumer.csproj"

        & dotnet restore $project "--source=$packages" "--source=$nugetSource" "-p:MailStencilPackageVersion=$Version"
        if ($LASTEXITCODE -ne 0) { throw "$consumer package restore failed" }
        & dotnet run --project $project -c Release --no-restore "-p:MailStencilPackageVersion=$Version"
        if ($LASTEXITCODE -ne 0) { throw "$consumer package smoke test failed" }

        $projectText = Get-Content -LiteralPath $project -Raw
        if ($projectText -match '<ProjectReference') { throw "$consumer unexpectedly uses a ProjectReference" }
    }
}
finally {
    if (Test-Path -LiteralPath $smokeRoot) { Remove-Item -LiteralPath $smokeRoot -Recurse -Force }
}

Write-Host "Verified four nupkg/snupkg pairs and two package-only consumers at $Version."
