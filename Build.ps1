param(
    [ValidatePattern("^[A-Za-z0-9._-]+\.exe$")]
    [string]$OutputName = "VaultLoop.exe",

    [ValidatePattern("^[A-Fa-f0-9]{40}$")]
    [string]$CertificateThumbprint = "",

    [ValidatePattern("^https://")]
    [string]$TimestampUrl = "https://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "ReplayGlitchGTA.csproj"
$publishDirectory = Join-Path $PSScriptRoot "publish"
$buildId = [Guid]::NewGuid().ToString("N")
$stageDirectory = Join-Path $PSScriptRoot "obj\VaultLoopBuild\$buildId"
$stagedExecutable = Join-Path $stageDirectory "VaultLoop.exe"
$destination = Join-Path $publishDirectory $OutputName
$pendingDestination = Join-Path $publishDirectory ".$OutputName.$buildId.pending.exe"
$backupDestination = Join-Path $stageDirectory "$OutputName.backup"
$windowsDirectory = Split-Path -Parent ([Environment]::SystemDirectory)
$frameworkPath = Join-Path $windowsDirectory "Microsoft.NET\Framework64\v4.0.30319"

if (-not (Test-Path -LiteralPath (Join-Path $frameworkPath "mscorlib.dll") -PathType Leaf)) {
    throw ".NET Framework 4.x reference assemblies were not found at $frameworkPath."
}

New-Item -ItemType Directory -Force -Path $stageDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null

try {
    & dotnet build $project `
        --configuration Release `
        --nologo `
        --output $stageDirectory `
        -p:TreatWarningsAsErrors=true `
        -p:FrameworkPathOverride=$frameworkPath `
        -p:AutomaticallyUseReferenceAssemblyPackages=false
    if ($LASTEXITCODE -ne 0) {
        throw "Compilation failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $stagedExecutable -PathType Leaf)) {
        throw "The canonical build did not produce VaultLoop.exe."
    }
    $unexpectedFiles = @(
        Get-ChildItem -LiteralPath $stageDirectory -File |
            Where-Object { $_.Name -ne "VaultLoop.exe" }
    )
    if ($unexpectedFiles.Count -ne 0) {
        throw "The single-file build produced unexpected files: $($unexpectedFiles.Name -join ', ')"
    }

    Copy-Item -LiteralPath $stagedExecutable -Destination $pendingDestination

    if ($CertificateThumbprint) {
        $certificate = Get-Item `
            -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint" `
            -ErrorAction Stop
        $signature = Set-AuthenticodeSignature `
            -FilePath $pendingDestination `
            -Certificate $certificate `
            -TimestampServer $TimestampUrl `
            -HashAlgorithm SHA256
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
            throw "Authenticode signing failed: $($signature.StatusMessage)"
        }
    }

    if ([IO.File]::Exists($destination)) {
        [IO.File]::Replace($pendingDestination, $destination, $backupDestination)
    }
    else {
        [IO.File]::Move($pendingDestination, $destination)
    }

    $result = Get-Item -LiteralPath $destination
    $signatureStatus = (Get-AuthenticodeSignature -LiteralPath $destination).Status
    [PSCustomObject]@{
        FullName = $result.FullName
        Length = $result.Length
        Version = $result.VersionInfo.FileVersion
        Signature = $signatureStatus
        SHA256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    }
}
finally {
    if ([IO.File]::Exists($pendingDestination)) {
        [IO.File]::Delete($pendingDestination)
    }
    if ([IO.Directory]::Exists($stageDirectory)) {
        [IO.Directory]::Delete($stageDirectory, $true)
    }
}
