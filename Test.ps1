param(
    [ValidatePattern("^[A-Fa-f0-9]{40}$")]
    [string]$CertificateThumbprint = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )
    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

$testOutputName = "VaultLoop-test.exe"
$testExecutable = Join-Path $PSScriptRoot "publish\$testOutputName"

try {
    $buildParameters = @{ OutputName = $testOutputName }
    if ($CertificateThumbprint) {
        $buildParameters.CertificateThumbprint = $CertificateThumbprint
    }
    & (Join-Path $PSScriptRoot "Build.ps1") @buildParameters | Out-Host

    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($testExecutable))
    Assert-True ($assembly.GetName().Version.ToString() -eq "1.2.0.0") `
        "assembly version must be 1.2.0.0"
    Assert-True ($assembly.ImageRuntimeVersion -eq "v4.0.30319") `
        "release must use the canonical .NET Framework runtime"
    Assert-True ($assembly.GetManifestResourceNames() -contains "ReplayGlitchLogo.png") `
        "embedded logo must be present"

    $staticFlags = [Reflection.BindingFlags]"Static,NonPublic"
    $gameType = $assembly.GetType("ReplayGlitchGTA.GameProcessService", $true)
    $supportedName = $gameType.GetMethod("IsSupportedProcessName", $staticFlags)
    Assert-True ([bool]$supportedName.Invoke($null, @("GTA5"))) "GTA5 must be supported"
    Assert-True ([bool]$supportedName.Invoke($null, @("GTA5_Enhanced"))) `
        "GTA5_Enhanced must be supported"
    Assert-True (-not [bool]$supportedName.Invoke($null, @("NVIDIA Share"))) `
        "unrelated processes must be rejected"
    $foregroundCheck = $gameType.GetMethod("IsCurrentForegroundWindow", $staticFlags)
    Assert-True (-not [bool]$foregroundCheck.Invoke($null, @([IntPtr]::Zero))) `
        "an empty foreground handle must never arm the shortcut"

    $firewallType = $assembly.GetType("ReplayGlitchGTA.FirewallService", $true)
    $addressCheck = $firewallType.GetMethod("TargetsOnlyRemoteAddress", $staticFlags)
    foreach ($validAddress in @(
        "192.81.241.171",
        "192.81.241.171/32",
        "192.81.241.171/255.255.255.255"
    )) {
        Assert-True ([bool]$addressCheck.Invoke($null, @($validAddress))) `
            "valid firewall address form rejected: $validAddress"
    }
    Assert-True (-not [bool]$addressCheck.Invoke($null, @("192.81.241.171,8.8.8.8"))) `
        "multi-address firewall rule must be rejected"
    $ownershipCheck = $firewallType.GetMethod("IsOwnedManagedRule", $staticFlags)
    $candidateRule = New-Object -ComObject HNetCfg.FWRule
    try {
        $candidateRule.Enabled = $true
        $candidateRule.Direction = 2
        $candidateRule.Action = 0
        $candidateRule.Protocol = 256
        $candidateRule.Profiles = [int]::MaxValue
        $candidateRule.LocalAddresses = "*"
        $candidateRule.RemoteAddresses = "192.81.241.171"
        $candidateRule.InterfaceTypes = "All"
        $candidateRule.EdgeTraversal = $false
        $candidateRule.ApplicationName = ""
        $candidateRule.ServiceName = ""
        $candidateRule.Description = ""
        $candidateRule.Grouping = ""
        Assert-True ([bool]$ownershipCheck.Invoke(
            $null, @($candidateRule, "123456"))) `
            "the exact historical script rule must be recoverable"
        $candidateRule.Description = "Third-party rule"
        Assert-True (-not [bool]$ownershipCheck.Invoke(
            $null, @($candidateRule, "123456"))) `
            "a third-party rule with the legacy name must not be removed"
        $candidateRule.Description = "VaultLoop managed rule v2"
        $candidateRule.Grouping = "VaultLoop"
        Assert-True ([bool]$ownershipCheck.Invoke(
            $null, @($candidateRule, "VaultLoop - No Save"))) `
            "the marked VaultLoop rule must remain recoverable"
    }
    finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($candidateRule)
    }

    $shortcutType = $assembly.GetType("ReplayGlitchGTA.ShortcutDialog", $true)
    $validShortcut = $shortcutType.GetMethod("IsValidShortcut", $staticFlags)
    $keysType = [Windows.Forms.Keys]
    $safeModifiers = $keysType::Control -bor $keysType::Shift
    Assert-True ([bool]$validShortcut.Invoke($null, @($safeModifiers, $keysType::F8))) `
        "default shortcut must be valid"
    Assert-True (-not [bool]$validShortcut.Invoke($null, @($keysType::Alt, $keysType::F4))) `
        "Alt+F4 must remain reserved"
    $shortcutSettingsType = $assembly.GetType("ReplayGlitchGTA.ShortcutSettings", $true)
    $formatShortcut = $shortcutSettingsType.GetMethod("Format", $staticFlags)
    Assert-True (
        $formatShortcut.Invoke($null, @($keysType::Alt, $keysType::D8)) -eq "ALT+8") `
        "numeric shortcut names must be user-friendly"

    $programType = $assembly.GetType("ReplayGlitchGTA.Program", $true)
    $runtimeCheck = $programType.GetMethod("HasSupportedRuntime", $staticFlags)
    Assert-True ([bool]$runtimeCheck.Invoke($null, @())) `
        ".NET Framework 4.8 runtime check must pass on the build machine"

    $instanceFlags = [Reflection.BindingFlags]"Instance,NonPublic"
    $mainFormType = $assembly.GetType("ReplayGlitchGTA.MainForm", $true)
    $preview = $mainFormType.GetConstructors($instanceFlags)[0].Invoke(
        @($null, $true, $false, $false))
    try {
        Assert-True ($preview.Text -eq "VaultLoop") "window title must be VaultLoop"
        Assert-True ($preview.ClientSize.Width -ge 780) "main form width is unexpectedly small"
    }
    finally {
        $preview.Dispose()
    }

    $guideType = $assembly.GetType("ReplayGlitchGTA.GuideDialog", $true)
    $guide = $guideType.GetConstructors($instanceFlags)[0].Invoke(@($false))
    try {
        Assert-True ($guide.AutoScaleMode -eq [Windows.Forms.AutoScaleMode]::Dpi) `
            "guide dialog must use DPI scaling"
        $stepPanels = $guideType.GetField("_steps", $instanceFlags).GetValue($guide)
        $checkedSteps = @(
            $stepPanels | Where-Object {
                ($_.AccessibilityObject.State -band [Windows.Forms.AccessibleStates]::Checked) -ne 0
            }
        )
        Assert-True ($checkedSteps.Count -eq 1) `
            "exactly one guide step must be exposed as selected"
    }
    finally {
        $guide.Dispose()
    }

    $toastType = $assembly.GetType("ReplayGlitchGTA.StatusToastForm", $true)
    $toast = $toastType.GetConstructors($instanceFlags)[0].Invoke(
        @("NO-SAVE ERROR", "Test detail", [Drawing.Color]::Yellow))
    try {
        Assert-True ($toast.AccessibleRole -eq [Windows.Forms.AccessibleRole]::Alert) `
            "error toast must expose an alert role"
        Assert-True ($toast.ClientSize.Height -gt 88) `
            "error toast must reserve multiline detail space"
    }
    finally {
        $toast.Dispose()
    }

    $firewall = [Activator]::CreateInstance($firewallType, $true)
    $state = $firewallType.GetMethod("GetState", [Reflection.BindingFlags]"Instance,NonPublic")
    $currentState = $state.Invoke($firewall, @()).ToString()
    Assert-True ($currentState -in @("Inactive", "Active", "Invalid")) `
        "unexpected read-only firewall state"

    $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot "app.manifest") -Raw
    $null = [xml]$manifest
    Assert-True ($manifest.Contains("PerMonitorV2")) "manifest must declare PerMonitorV2"
    Assert-True ($manifest.Contains("requireAdministrator")) `
        "manifest must request firewall elevation"
    Assert-True ($manifest.Contains("{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}")) `
        "manifest must declare Windows 10/11 compatibility"

    $signatureStatus = (Get-AuthenticodeSignature -LiteralPath $testExecutable).Status
    if ($CertificateThumbprint) {
        Assert-True ($signatureStatus -eq [Management.Automation.SignatureStatus]::Valid) `
            "signed test build must have a valid Authenticode signature"
    }

    [PSCustomObject]@{
        Result = "PASS"
        FirewallState = $currentState
        Signature = $signatureStatus
        SHA256 = (Get-FileHash -LiteralPath $testExecutable -Algorithm SHA256).Hash
    }
}
finally {
    if ([IO.File]::Exists($testExecutable)) {
        [IO.File]::Delete($testExecutable)
    }
}
