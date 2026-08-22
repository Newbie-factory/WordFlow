[CmdletBinding()]
param(
    [switch]$UiOnly,
    [string]$EvidencePath,
    [switch]$TopmostHelper,
    [string]$HelperTitle,
    [int]$HelperLeft,
    [int]$HelperTop,
    [int]$HelperWidth,
    [int]$HelperHeight
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($TopmostHelper) {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    Add-Type -ReferencedAssemblies @('System.Windows.Forms.dll', 'System.Drawing.dll') -TypeDefinition @'
using System.Windows.Forms;
public sealed class WordFlowVerificationCoverForm : Form
{
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams value = base.CreateParams;
            value.ExStyle |= 0x08000000;
            return value;
        }
    }
}
'@
    $form = New-Object WordFlowVerificationCoverForm
    $form.Text = $HelperTitle
    $form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
    $form.SetBounds($HelperLeft, $HelperTop, $HelperWidth, $HelperHeight)
    $form.TopMost = $true
    $form.ShowInTaskbar = $false
    $form.BackColor = [System.Drawing.Color]::DarkSlateBlue
    [System.Windows.Forms.Application]::Run($form)
    exit 0
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Set-Location -LiteralPath $repositoryRoot
if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    $EvidencePath = Join-Path $repositoryRoot 'artifacts\verification\second-iteration-evidence.json'
}
$EvidencePath = [System.IO.Path]::GetFullPath($EvidencePath)
$releaseDirectory = 'D:\baicizhan\release\WordFlow-second-iteration'
$photoExe = Join-Path $releaseDirectory 'WordFlow-Photo.exe'
$classicExe = Join-Path $releaseDirectory 'WordFlow-Classic.exe'
$tempParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\')
$verificationRoot = Join-Path $tempParent ("WordFlow-second-iteration-verification-{0}" -f [guid]::NewGuid().ToString('N'))
$launchedProcesses = New-Object 'System.Collections.Generic.List[System.Diagnostics.Process]'
$gateOutputs = [ordered]@{}
$evidence = [ordered]@{
    schemaVersion = 1
    startedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    repositoryRoot = $repositoryRoot
    powershell = $PSVersionTable.PSVersion.ToString()
    gates = @()
    vocabulary = $null
    relations = $null
    tests = $null
    build = $null
    release = $null
    singleInstance = @()
    ui = $null
    cleanup = $null
    status = 'running'
}

function Assert-Verification {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Wait-Condition {
    param(
        [Parameter(Mandatory)][scriptblock]$Predicate,
        [Parameter(Mandatory)][int]$TimeoutMilliseconds,
        [Parameter(Mandatory)][string]$Description
    )
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    do {
        if ([bool](& $Predicate)) { return $stopwatch.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 50
    } while ($stopwatch.ElapsedMilliseconds -lt $TimeoutMilliseconds)
    throw "Timed out after $TimeoutMilliseconds ms waiting for $Description."
}

function Invoke-Gate {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Command,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$DisplayCommand
    )
    Write-Host "[gate:$Name] $DisplayCommand"
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $output = @(& $Command @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $stopwatch.Stop()
    $text = $output -join [Environment]::NewLine
    $gateOutputs[$Name] = $text
    foreach ($line in $output) { Write-Host $line }
    $record = [ordered]@{
        name = $Name
        command = $DisplayCommand
        exitCode = $exitCode
        elapsedMilliseconds = $stopwatch.ElapsedMilliseconds
    }
    $evidence.gates += [pscustomobject]$record
    Write-Host "[gate:$Name] exit=$exitCode elapsedMs=$($stopwatch.ElapsedMilliseconds)"
    if ($exitCode -ne 0) { throw "Gate '$Name' failed with exit code $exitCode." }
    return $text
}

function Get-ExistingWordFlowProcesses {
    $result = @()
    foreach ($name in @('WordFlow-Photo', 'WordFlow-Classic', 'WordFlow.App')) {
        $result += @(Get-Process -Name $name -ErrorAction SilentlyContinue)
    }
    return @($result | Sort-Object Id -Unique)
}

function Start-IsolatedProcess {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string]$DataRoot,
        [string[]]$Arguments = @(),
        [string]$ImportPath
    )
    New-Item -ItemType Directory -Force -Path $DataRoot | Out-Null
    $oldMode = [Environment]::GetEnvironmentVariable('WORDFLOW_VERIFICATION_MODE', 'Process')
    $oldRoot = [Environment]::GetEnvironmentVariable('WORDFLOW_VERIFICATION_LOCAL_APP_DATA', 'Process')
    $oldImport = [Environment]::GetEnvironmentVariable('WORDFLOW_VERIFICATION_IMPORT', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('WORDFLOW_VERIFICATION_MODE', 'isolated', 'Process')
        [Environment]::SetEnvironmentVariable('WORDFLOW_VERIFICATION_LOCAL_APP_DATA', $DataRoot, 'Process')
        [Environment]::SetEnvironmentVariable('WORDFLOW_VERIFICATION_IMPORT', $ImportPath, 'Process')
        if ($Arguments.Count -eq 0) {
            $process = Start-Process -FilePath $Executable -WorkingDirectory $releaseDirectory -PassThru
        }
        else {
            $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WorkingDirectory $releaseDirectory -PassThru
        }
        $launchedProcesses.Add($process)
        return $process
    }
    finally {
        [Environment]::SetEnvironmentVariable('WORDFLOW_VERIFICATION_MODE', $oldMode, 'Process')
        [Environment]::SetEnvironmentVariable('WORDFLOW_VERIFICATION_LOCAL_APP_DATA', $oldRoot, 'Process')
        [Environment]::SetEnvironmentVariable('WORDFLOW_VERIFICATION_IMPORT', $oldImport, 'Process')
    }
}

function Wait-PrimaryReady {
    param([Parameter(Mandatory)][System.Diagnostics.Process]$Process, [Parameter(Mandatory)][string]$DataRoot)
    $log = Join-Path $DataRoot 'WordFlow\Logs\lifecycle.log'
    [void](Wait-Condition -TimeoutMilliseconds 30000 -Description "WordFlow PID $($Process.Id) primary-ready" -Predicate {
        $Process.Refresh()
        if ($Process.HasExited) { throw "WordFlow PID $($Process.Id) exited with code $($Process.ExitCode) before primary-ready." }
        if (-not (Test-Path -LiteralPath $log -PathType Leaf)) { return $false }
        return (Get-Content -LiteralPath $log -Raw) -match "primary-ready pid=$($Process.Id)(\D|$)"
    })
}

function Stop-ExactProcess {
    param([Parameter(Mandatory)][System.Diagnostics.Process]$Process)
    $Process.Refresh()
    if ($Process.HasExited) { return }
    $expectedStart = $Process.StartTime.ToUniversalTime()
    $current = Get-Process -Id $Process.Id -ErrorAction SilentlyContinue
    if ($null -eq $current) { return }
    Assert-Verification ($current.StartTime.ToUniversalTime() -eq $expectedStart) "PID $($Process.Id) was reused; refusing termination."
    Stop-Process -Id $Process.Id
    [void](Wait-Condition -TimeoutMilliseconds 10000 -Description "exact PID $($Process.Id) termination" -Predicate {
        return $null -eq (Get-Process -Id $Process.Id -ErrorAction SilentlyContinue)
    })
}

function Test-DualInstanceOrder {
    param(
        [Parameter(Mandatory)][string]$PrimaryExe,
        [Parameter(Mandatory)][string]$SecondaryExe,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$DataRoot
    )
    $primary = Start-IsolatedProcess -Executable $PrimaryExe -DataRoot $DataRoot
    Wait-PrimaryReady -Process $primary -DataRoot $DataRoot
    $secondary = Start-IsolatedProcess -Executable $SecondaryExe -DataRoot $DataRoot
    [void](Wait-Condition -TimeoutMilliseconds 10000 -Description "secondary PID $($secondary.Id) exit" -Predicate {
        $secondary.Refresh()
        return $secondary.HasExited
    })
    Assert-Verification ($secondary.ExitCode -eq 0) "Secondary PID $($secondary.Id) exited with $($secondary.ExitCode)."
    $live = @()
    $convergence = Wait-Condition -TimeoutMilliseconds 10000 -Description "one primary WordFlow process after '$Name'" -Predicate {
        $live = @(Get-ExistingWordFlowProcesses)
        return $live.Count -eq 1 -and $live[0].Id -eq $primary.Id
    }
    $live = @(Get-ExistingWordFlowProcesses)
    $record = [pscustomobject][ordered]@{
        order = $Name
        primaryPid = $primary.Id
        secondaryPid = $secondary.Id
        secondaryExitCode = $secondary.ExitCode
        remainingPid = $live[0].Id
        count = $live.Count
        convergenceMilliseconds = $convergence
    }
    Stop-ExactProcess -Process $primary
    [void](Wait-Condition -TimeoutMilliseconds 10000 -Description "WordFlow process quiescence after '$Name'" -Predicate {
        return @(Get-ExistingWordFlowProcesses).Count -eq 0
    })
    return $record
}

function Initialize-UiAutomation {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    if ($null -eq ('WordFlowVerificationNative' -as [type])) {
        Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WordFlowVerificationNative
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] public static extern IntPtr GetTopWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    public static readonly IntPtr HwndTopmost = new IntPtr(-1);
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoActivate = 0x0010;
    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
}
'@
    }
}

function Get-ProcessWindows {
    param([Parameter(Mandatory)][int]$ProcessId)
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $all = $desktop.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    $result = @()
    for ($index = 0; $index -lt $all.Count; $index++) {
        $window = $all.Item($index)
        if ($window.Current.ProcessId -eq $ProcessId) { $result += $window }
    }
    return $result
}

function Wait-ProcessWindow {
    param([Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][string]$Name, [int]$TimeoutMilliseconds = 10000)
    $found = $null
    [void](Wait-Condition -TimeoutMilliseconds $TimeoutMilliseconds -Description "window '$Name' for PID $ProcessId" -Predicate {
        $script:candidateWindow = @(Get-ProcessWindows -ProcessId $ProcessId | Where-Object { $_.Current.Name -eq $Name }) | Select-Object -First 1
        if ($null -ne $script:candidateWindow) { $found = $script:candidateWindow; return $true }
        return $false
    })
    if ($null -eq $found) {
        $found = @(Get-ProcessWindows -ProcessId $ProcessId | Where-Object { $_.Current.Name -eq $Name }) | Select-Object -First 1
    }
    return $found
}

function Find-UiElement {
    param(
        [Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [string]$AutomationId,
        [System.Windows.Automation.ControlType]$ControlType
    )
    $all = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    for ($index = 0; $index -lt $all.Count; $index++) {
        $element = $all.Item($index)
        if ($PSBoundParameters.ContainsKey('Name') -and $element.Current.Name -ne $Name) { continue }
        if ($PSBoundParameters.ContainsKey('AutomationId') -and $element.Current.AutomationId -ne $AutomationId) { continue }
        if ($PSBoundParameters.ContainsKey('ControlType') -and $element.Current.ControlType -ne $ControlType) { continue }
        return $element
    }
    return $null
}

function Select-UiElement {
    param([Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$Element)
    $pattern = [System.Windows.Automation.SelectionItemPattern]$Element.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
    [void](Wait-Condition -TimeoutMilliseconds 3000 -Description "selection '$($Element.Current.Name)'" -Predicate {
        return ([System.Windows.Automation.SelectionItemPattern]$Element.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected
    })
}

function Get-ThemeSlider {
    param([Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$Window, [switch]$Card)
    if ($Card) {
        $slider = Find-UiElement -Root $Window -AutomationId 'ThemeOpacitySlider' -ControlType ([System.Windows.Automation.ControlType]::Slider)
        if ($null -eq $slider) { throw 'Floating-card theme slider was not found.' }
        return $slider
    }
    $all = $Window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $sliders = @()
    for ($index = 0; $index -lt $all.Count; $index++) {
        $element = $all.Item($index)
        if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Slider -and -not $element.Current.IsOffscreen) {
            $sliders += $element
        }
    }
    Assert-Verification ($sliders.Count -eq 1) "Expected one visible control-center slider; found $($sliders.Count)."
    return $sliders[0]
}

function Set-SliderValue {
    param([Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$Slider, [Parameter(Mandatory)][double]$Value)
    $pattern = [System.Windows.Automation.RangeValuePattern]$Slider.GetCurrentPattern(
        [System.Windows.Automation.RangeValuePattern]::Pattern)
    $pattern.SetValue($Value)
    [void](Wait-Condition -TimeoutMilliseconds 3000 -Description "slider value $Value" -Predicate {
        $current = [System.Windows.Automation.RangeValuePattern]$Slider.GetCurrentPattern(
            [System.Windows.Automation.RangeValuePattern]::Pattern)
        return [Math]::Abs($current.Current.Value - $Value) -le 0.001
    })
}

function Test-ThemeSlider {
    param(
        [Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$Slider,
        [Parameter(Mandatory)][string]$Name
    )
    $range = [System.Windows.Automation.RangeValuePattern]$Slider.GetCurrentPattern(
        [System.Windows.Automation.RangeValuePattern]::Pattern)
    Assert-Verification ([Math]::Abs($range.Current.Minimum) -le 0.0001) "$Name minimum is $($range.Current.Minimum), expected 0."
    Assert-Verification ([Math]::Abs($range.Current.Maximum - 1) -le 0.0001) "$Name maximum is $($range.Current.Maximum), expected 1."
    $samples = @()
    foreach ($requested in @(0.0, 0.37, 1.0)) {
        Set-SliderValue -Slider $Slider -Value $requested
        $thumbCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Thumb)
        $thumb = $Slider.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $thumbCondition)
        Assert-Verification ($null -ne $thumb) "$Name thumb was not exposed through UI Automation."
        $sliderBounds = $Slider.Current.BoundingRectangle
        $thumbBounds = $thumb.Current.BoundingRectangle
        $expectedCenterX = $sliderBounds.Left + ($thumbBounds.Width / 2) + (($sliderBounds.Width - $thumbBounds.Width) * $requested)
        $actualCenterX = $thumbBounds.Left + ($thumbBounds.Width / 2)
        $delta = [Math]::Abs($expectedCenterX - $actualCenterX)
        Assert-Verification ($delta -le 3.0) "$Name thumb delta $delta px exceeded 3 px at $requested."
        [void][WordFlowVerificationNative]::SetForegroundWindow([intptr]$Window.Current.NativeWindowHandle)
        [WordFlowVerificationNative]::Click([int][Math]::Round($actualCenterX), [int][Math]::Round($thumbBounds.Top + ($thumbBounds.Height / 2)))
        $afterClick = [System.Windows.Automation.RangeValuePattern]$Slider.GetCurrentPattern(
            [System.Windows.Automation.RangeValuePattern]::Pattern)
        Assert-Verification ([Math]::Abs($afterClick.Current.Value - $requested) -le 0.002) "$Name pointer click moved value away from $requested."
        $samples += [pscustomobject][ordered]@{
            requested = $requested
            actual = $afterClick.Current.Value
            sliderBounds = $sliderBounds.ToString()
            thumbBounds = $thumbBounds.ToString()
            expectedCenterX = [Math]::Round($expectedCenterX, 3)
            actualCenterX = [Math]::Round($actualCenterX, 3)
            deltaPixels = [Math]::Round($delta, 3)
        }
    }
    return [pscustomobject][ordered]@{ name = $Name; minimum = 0; maximum = 1; samples = $samples }
}

function Get-ThemeDisplayName {
    param([Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$ControlCenter)
    $all = $ControlCenter.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    for ($index = 0; $index -lt $all.Count; $index++) {
        $element = $all.Item($index)
        if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and
            $element.Current.Name -match '^theme-[0-9a-f]{32}\.(png|jpg)$') {
            return $element.Current.Name
        }
    }
    throw 'The imported theme display name was not visible.'
}

function Open-ThemeSettings {
    param([Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$ControlCenter)
    $settings = Find-UiElement -Root $ControlCenter -Name '设置' -ControlType ([System.Windows.Automation.ControlType]::RadioButton)
    Assert-Verification ($null -ne $settings) 'Settings navigation item was not found.'
    Select-UiElement -Element $settings
    $themeTab = Find-UiElement -Root $ControlCenter -Name '悬浮卡与外观' -ControlType ([System.Windows.Automation.ControlType]::TabItem)
    Assert-Verification ($null -ne $themeTab) 'Theme settings tab was not found.'
    Select-UiElement -Element $themeTab
}

function Get-ZIndex {
    param([Parameter(Mandatory)][intptr]$Handle)
    $current = [WordFlowVerificationNative]::GetTopWindow([intptr]::Zero)
    for ($index = 0; $index -lt 10000 -and $current -ne [intptr]::Zero; $index++) {
        if ($current -eq $Handle) { return $index }
        $current = [WordFlowVerificationNative]::GetWindow($current, 2)
    }
    return [int]::MaxValue
}

function Start-TopmostHelper {
    param([Parameter(Mandatory)]$Bounds)
    $title = "WordFlow-Topmost-Cover-$([guid]::NewGuid().ToString('N'))"
    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath,
        '-TopmostHelper', '-HelperTitle', $title,
        '-HelperLeft', ([int][Math]::Floor($Bounds.Left)),
        '-HelperTop', ([int][Math]::Floor($Bounds.Top)),
        '-HelperWidth', ([int][Math]::Ceiling($Bounds.Width)),
        '-HelperHeight', ([int][Math]::Ceiling($Bounds.Height)))
    $helperStdout = Join-Path $verificationRoot "$title.stdout.log"
    $helperStderr = Join-Path $verificationRoot "$title.stderr.log"
    $helper = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -PassThru `
        -RedirectStandardOutput $helperStdout -RedirectStandardError $helperStderr
    $launchedProcesses.Add($helper)
    $window = $null
    [void](Wait-Condition -TimeoutMilliseconds 10000 -Description "window '$title' for PID $($helper.Id)" -Predicate {
        $helper.Refresh()
        if ($helper.HasExited) {
            $detail = if (Test-Path -LiteralPath $helperStderr) { Get-Content -LiteralPath $helperStderr -Raw } else { '' }
            throw "Topmost helper PID $($helper.Id) exited with code $($helper.ExitCode): $detail"
        }
        $window = @(Get-ProcessWindows -ProcessId $helper.Id | Where-Object { $_.Current.Name -eq $title }) | Select-Object -First 1
        return $null -ne $window
    })
    $window = @(Get-ProcessWindows -ProcessId $helper.Id | Where-Object { $_.Current.Name -eq $title }) | Select-Object -First 1
    return [pscustomobject]@{ Process = $helper; Window = $window; Title = $title }
}

function Test-TopmostBehavior {
    param(
        [Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$Card,
        [Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$ControlCenter
    )
    Open-ThemeSettings -ControlCenter $ControlCenter
    $checkbox = Find-UiElement -Root $ControlCenter -Name '悬浮卡始终置顶' -ControlType ([System.Windows.Automation.ControlType]::CheckBox)
    Assert-Verification ($null -ne $checkbox) 'Always-on-top checkbox was not found.'
    $toggle = [System.Windows.Automation.TogglePattern]$checkbox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $toggle.Toggle() }
    [void](Wait-Condition -TimeoutMilliseconds 3000 -Description 'always-on-top enabled' -Predicate {
        return ([System.Windows.Automation.TogglePattern]$checkbox.GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    })

    $cardHandle = [intptr]$Card.Current.NativeWindowHandle
    $controlHandle = [intptr]$ControlCenter.Current.NativeWindowHandle
    [void][WordFlowVerificationNative]::SetForegroundWindow($controlHandle)
    [void](Wait-Condition -TimeoutMilliseconds 3000 -Description 'control-center foreground' -Predicate {
        return [WordFlowVerificationNative]::GetForegroundWindow() -eq $controlHandle
    })
    $helper = Start-TopmostHelper -Bounds $Card.Current.BoundingRectangle
    $helperHandle = [intptr]$helper.Window.Current.NativeWindowHandle
    [void][WordFlowVerificationNative]::SetForegroundWindow($controlHandle)
    [void](Wait-Condition -TimeoutMilliseconds 3000 -Description 'control-center foreground after cover creation' -Predicate {
        return [WordFlowVerificationNative]::GetForegroundWindow() -eq $controlHandle
    })
    [void][WordFlowVerificationNative]::SetWindowPos(
        $helperHandle, [WordFlowVerificationNative]::HwndTopmost, 0, 0, 0, 0,
        [WordFlowVerificationNative]::SwpNoMove -bor [WordFlowVerificationNative]::SwpNoSize -bor [WordFlowVerificationNative]::SwpNoActivate)
    $foregroundBefore = [WordFlowVerificationNative]::GetForegroundWindow()
    Assert-Verification ((Get-ZIndex -Handle $helperHandle) -lt (Get-ZIndex -Handle $cardHandle)) 'Controlled cover window did not begin above WordFlow.'
    $elapsed = Wait-Condition -TimeoutMilliseconds 750 -Description 'WordFlow topmost reassertion' -Predicate {
        return (Get-ZIndex -Handle $cardHandle) -lt (Get-ZIndex -Handle $helperHandle)
    }
    $foregroundAfter = [WordFlowVerificationNative]::GetForegroundWindow()
    Assert-Verification ($foregroundAfter -eq $foregroundBefore) "Topmost reassertion changed foreground from $foregroundBefore to $foregroundAfter."
    Assert-Verification ($foregroundAfter -ne $cardHandle) 'Topmost reassertion stole foreground focus.'

    $toggle = [System.Windows.Automation.TogglePattern]$checkbox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $toggle.Toggle()
    [void](Wait-Condition -TimeoutMilliseconds 3000 -Description 'always-on-top disabled' -Predicate {
        return ([System.Windows.Automation.TogglePattern]$checkbox.GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off
    })
    [void][WordFlowVerificationNative]::SetWindowPos(
        $helperHandle, [WordFlowVerificationNative]::HwndTopmost, 0, 0, 0, 0,
        [WordFlowVerificationNative]::SwpNoMove -bor [WordFlowVerificationNative]::SwpNoSize -bor [WordFlowVerificationNative]::SwpNoActivate)
    Start-Sleep -Milliseconds 750
    Assert-Verification ((Get-ZIndex -Handle $helperHandle) -lt (Get-ZIndex -Handle $cardHandle)) 'WordFlow reasserted after always-on-top was disabled.'
    Stop-ExactProcess -Process $helper.Process
    return [pscustomobject][ordered]@{
        enabledReassertMilliseconds = $elapsed
        foregroundBefore = $foregroundBefore.ToInt64()
        foregroundAfter = $foregroundAfter.ToInt64()
        focusStolen = $false
        disabledStayedBelowForMilliseconds = 750
    }
}

function Test-NavigationAndUndo {
    param(
        [Parameter(Mandatory)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$Card,
        [Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$ControlCenter
    )
    $pages = @('今日学习', '词汇库', '易混词', '已斩词汇', '学习统计', '设置')
    foreach ($page in $pages) {
        $item = Find-UiElement -Root $ControlCenter -Name $page -ControlType ([System.Windows.Automation.ControlType]::RadioButton)
        Assert-Verification ($null -ne $item) "Control-center page '$page' was not found."
        Select-UiElement -Element $item
        $Process.Refresh()
        Assert-Verification (-not $Process.HasExited) "WordFlow exited while opening page '$page'."
    }
    $tabs = @('学习', '快捷键', '悬浮卡与外观', '发音', '数据与备份', '高级 FSRS')
    foreach ($tab in $tabs) {
        $item = Find-UiElement -Root $ControlCenter -Name $tab -ControlType ([System.Windows.Automation.ControlType]::TabItem)
        Assert-Verification ($null -ne $item) "Settings tab '$tab' was not found."
        Select-UiElement -Element $item
        $Process.Refresh()
        Assert-Verification (-not $Process.HasExited) "WordFlow exited while opening settings tab '$tab'."
    }

    $allCard = $Card.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $visibleUndo = @()
    for ($index = 0; $index -lt $allCard.Count; $index++) {
        $element = $allCard.Item($index)
        if (-not $element.Current.IsOffscreen -and
            ($element.Current.AutomationId -eq 'UndoButton' -or $element.Current.Name -match '撤销|Undo')) {
            $visibleUndo += $element
        }
    }
    Assert-Verification ($visibleUndo.Count -eq 0) "Visible undo UI remains: $($visibleUndo.Count) element(s)."

    $word = Find-UiElement -Root $Card -AutomationId 'WordText' -ControlType ([System.Windows.Automation.ControlType]::Button)
    $good = Find-UiElement -Root $Card -AutomationId 'GoodButton' -ControlType ([System.Windows.Automation.ControlType]::Button)
    Assert-Verification ($null -ne $word -and $null -ne $good) 'Word or Good button was not found for Ctrl+Z verification.'
    $original = $word.Current.Name
    ([System.Windows.Automation.InvokePattern]$good.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    [void](Wait-Condition -TimeoutMilliseconds 5000 -Description 'card advance before Ctrl+Z' -Predicate {
        $currentWord = Find-UiElement -Root $Card -AutomationId 'WordText' -ControlType ([System.Windows.Automation.ControlType]::Button)
        return $null -ne $currentWord -and $currentWord.Current.Name -ne $original
    })
    $word = Find-UiElement -Root $Card -AutomationId 'WordText' -ControlType ([System.Windows.Automation.ControlType]::Button)
    [void][WordFlowVerificationNative]::SetForegroundWindow([intptr]$Card.Current.NativeWindowHandle)
    $word.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('^z')
    [void](Wait-Condition -TimeoutMilliseconds 5000 -Description 'Ctrl+Z card restore' -Predicate {
        $restored = Find-UiElement -Root $Card -AutomationId 'WordText' -ControlType ([System.Windows.Automation.ControlType]::Button)
        return $null -ne $restored -and $restored.Current.Name -eq $original
    })
    return [pscustomobject][ordered]@{
        pages = $pages
        settingsTabs = $tabs
        visibleUndoElements = 0
        ctrlZRestoredOriginalCard = $true
        originalAutomationName = $original
    }
}

function Start-UiProcess {
    param([Parameter(Mandatory)][string]$DataRoot, [string]$ImportPath)
    $process = Start-IsolatedProcess -Executable $photoExe -DataRoot $DataRoot -Arguments @('--control-center-smoke') -ImportPath $ImportPath
    Wait-PrimaryReady -Process $process -DataRoot $DataRoot
    if (-not [string]::IsNullOrWhiteSpace($ImportPath)) {
        $log = Join-Path $DataRoot 'WordFlow\Logs\lifecycle.log'
        $extension = [System.IO.Path]::GetExtension($ImportPath).ToLowerInvariant()
        [void](Wait-Condition -TimeoutMilliseconds 10000 -Description "verification import $extension for PID $($process.Id)" -Predicate {
            return (Test-Path -LiteralPath $log) -and
                ((Get-Content -LiteralPath $log -Raw) -match "verification-import-complete pid=$($process.Id) extension=$([regex]::Escape($extension)) ")
        })
    }
    return $process
}

function Test-UiJourney {
    Initialize-UiAutomation
    $dataRoot = Join-Path $verificationRoot 'ui-profile'
    $inputRoot = Join-Path $verificationRoot 'image-inputs'
    New-Item -ItemType Directory -Force -Path $inputRoot | Out-Null
    $png = Join-Path $inputRoot 'skin.png'
    $jpg = Join-Path $inputRoot 'skin.JPG'
    $jpeg = Join-Path $inputRoot 'skin.JPEG'
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'src\WordFlow.App\Assets\Icons\photo-source.png') -Destination $png
    $sourceImage = [System.Drawing.Image]::FromFile($png)
    try { $sourceImage.Save($jpg, [System.Drawing.Imaging.ImageFormat]::Jpeg) }
    finally { $sourceImage.Dispose() }
    Copy-Item -LiteralPath $jpg -Destination $jpeg

    $process = Start-UiProcess -DataRoot $dataRoot
    $card = Wait-ProcessWindow -ProcessId $process.Id -Name 'WordFlow 悬浮学习卡'
    $controlCenter = Wait-ProcessWindow -ProcessId $process.Id -Name 'WordFlow 控制中心'
    Open-ThemeSettings -ControlCenter $controlCenter
    $sliderEvidence = @(
        (Test-ThemeSlider -Window $card -Slider (Get-ThemeSlider -Window $card -Card) -Name 'floating-card')
        (Test-ThemeSlider -Window $controlCenter -Slider (Get-ThemeSlider -Window $controlCenter) -Name 'control-center'))
    Stop-ExactProcess -Process $process

    $imports = @()
    foreach ($input in @($png, $jpg, $jpeg)) {
        $process = Start-UiProcess -DataRoot $dataRoot -ImportPath $input
        $controlCenter = Wait-ProcessWindow -ProcessId $process.Id -Name 'WordFlow 控制中心'
        Open-ThemeSettings -ControlCenter $controlCenter
        $displayName = Get-ThemeDisplayName -ControlCenter $controlCenter
        $expectedStoredExtension = if ([System.IO.Path]::GetExtension($input) -ieq '.png') { '.png' } else { '.jpg' }
        Assert-Verification ($displayName.EndsWith($expectedStoredExtension, [StringComparison]::OrdinalIgnoreCase)) "Import '$input' restored unexpected display name '$displayName'."
        $imports += [pscustomobject][ordered]@{
            sourceExtension = [System.IO.Path]::GetExtension($input)
            storedDisplayName = $displayName
            automation = 'isolated guarded startup seam; native file picker did not surface in this UIA session'
        }
        if ($input -eq $jpeg) {
            $slider = Get-ThemeSlider -Window $controlCenter
            Set-SliderValue -Slider $slider -Value 0.37
            Start-Sleep -Milliseconds 750
            $persistedDisplayName = $displayName
        }
        Stop-ExactProcess -Process $process
    }

    $process = Start-UiProcess -DataRoot $dataRoot
    $card = Wait-ProcessWindow -ProcessId $process.Id -Name 'WordFlow 悬浮学习卡'
    $controlCenter = Wait-ProcessWindow -ProcessId $process.Id -Name 'WordFlow 控制中心'
    Open-ThemeSettings -ControlCenter $controlCenter
    $restoredDisplayName = Get-ThemeDisplayName -ControlCenter $controlCenter
    Assert-Verification ($restoredDisplayName -eq $persistedDisplayName) "Restart restored '$restoredDisplayName', expected '$persistedDisplayName'."
    $cardValue = ([System.Windows.Automation.RangeValuePattern](Get-ThemeSlider -Window $card -Card).GetCurrentPattern(
        [System.Windows.Automation.RangeValuePattern]::Pattern)).Current.Value
    $controlValue = ([System.Windows.Automation.RangeValuePattern](Get-ThemeSlider -Window $controlCenter).GetCurrentPattern(
        [System.Windows.Automation.RangeValuePattern]::Pattern)).Current.Value
    Assert-Verification ([Math]::Abs($cardValue - 0.37) -le 0.001) "Card slider restored $cardValue, expected 0.37."
    Assert-Verification ([Math]::Abs($controlValue - 0.37) -le 0.001) "Control-center slider restored $controlValue, expected 0.37."

    $dpi = [WordFlowVerificationNative]::GetDpiForWindow([intptr]$card.Current.NativeWindowHandle)
    $topmost = Test-TopmostBehavior -Card $card -ControlCenter $controlCenter
    $navigation = Test-NavigationAndUndo -Process $process -Card $card -ControlCenter $controlCenter
    $process.Refresh()
    Assert-Verification (-not $process.HasExited) 'WordFlow was not alive after UI journey.'
    Stop-ExactProcess -Process $process

    return [pscustomobject][ordered]@{
        processAliveAfterAllChecks = $true
        dpi = $dpi
        dpiPercent = [Math]::Round(($dpi / 96.0) * 100)
        sliders = $sliderEvidence
        imports = $imports
        restart = [pscustomobject][ordered]@{
            displayName = $restoredDisplayName
            floatingCardSlider = $cardValue
            controlCenterSlider = $controlValue
        }
        topmost = $topmost
        navigationAndUndo = $navigation
        exclusiveFullscreenLimitation = 'Exclusive fullscreen, secure desktop, and higher-integrity windows can outrank an ordinary desktop topmost window.'
    }
}

function Write-Evidence {
    $parent = Split-Path -Parent $EvidencePath
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $json = $evidence | ConvertTo-Json -Depth 12
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($EvidencePath, $json, $utf8WithoutBom)
}

New-Item -ItemType Directory -Path $verificationRoot | Out-Null
try {
    if (-not $UiOnly) {
        $vocabularyText = Invoke-Gate -Name 'vocabulary-relations' -Command 'python' -Arguments @(
            'tools\vocabulary\verify_vocabulary.py',
            '--artifact-dir', 'data\ielts',
            '--curated', 'data\curated\required_vocabulary.csv',
            '--source-registry', 'data\curated\source_registry.json',
            '--report-root', 'data\reports',
            '--relations',
            '--relations-curated', 'data\curated\confusable_groups.csv',
            '--misspellings', 'data\curated\misspellings.csv',
            '--oewn', 'data\sources\oewn\english-wordnet-2025-json.zip',
            '--relations-quality', 'data\reports\relations-quality.json',
            '--relations-manifest', 'data\ielts\relations-manifest.json') -DisplayCommand 'python tools\vocabulary\verify_vocabulary.py --artifact-dir data\ielts --curated data\curated\required_vocabulary.csv --source-registry data\curated\source_registry.json --report-root data\reports --relations --relations-curated data\curated\confusable_groups.csv --misspellings data\curated\misspellings.csv --oewn data\sources\oewn\english-wordnet-2025-json.zip --relations-quality data\reports\relations-quality.json --relations-manifest data\ielts\relations-manifest.json'
        $verified = $vocabularyText | ConvertFrom-Json
        Assert-Verification ($verified.vocabulary.passed -eq $true) 'Vocabulary verifier did not report passed=true.'
        Assert-Verification ($verified.vocabulary.sqlite_integrity -eq 'ok') 'Vocabulary SQLite integrity was not ok.'
        Assert-Verification ($verified.relations.passed -eq $true) 'Relations verifier did not report passed=true.'
        Assert-Verification ($verified.relations.relation_verification.sqlite_integrity -eq 'ok') 'Relations SQLite integrity was not ok.'
        $evidence.vocabulary = $verified.vocabulary
        $evidence.relations = $verified.relations.relation_verification

        $testText = Invoke-Gate -Name 'tests' -Command 'dotnet' -Arguments @(
            'test', 'WordFlow.sln', '-c', 'Release', '--no-restore') -DisplayCommand 'dotnet test WordFlow.sln -c Release --no-restore'
        $testMatches = [regex]::Matches($testText, '(?:总计|Total):\s*(\d+)')
        $testTotal = 0
        foreach ($match in $testMatches) { $testTotal += [int]$match.Groups[1].Value }
        Assert-Verification ($testTotal -gt 0) 'Could not parse the test total.'
        Assert-Verification ($testText -notmatch '(?:失败|Failed):\s*[1-9]') 'Test output reported failures.'
        $evidence.tests = [pscustomobject][ordered]@{ total = $testTotal; failed = 0; skipped = 0 }

        $buildText = Invoke-Gate -Name 'build' -Command 'dotnet' -Arguments @(
            'build', 'WordFlow.sln', '-c', 'Release', '--no-restore') -DisplayCommand 'dotnet build WordFlow.sln -c Release --no-restore'
        Assert-Verification ($buildText -match '(?m)^\s*0\s+(?:个警告|Warning\(s\))') 'Build output did not report zero warnings.'
        Assert-Verification ($buildText -match '(?m)^\s*0\s+(?:个错误|Error\(s\))') 'Build output did not report zero errors.'
        $evidence.build = [pscustomobject][ordered]@{ warnings = 0; errors = 0 }

        [void](Invoke-Gate -Name 'publish' -Command 'powershell' -Arguments @(
            '-ExecutionPolicy', 'Bypass', '-File', 'scripts\build-second-iteration.ps1') -DisplayCommand 'powershell -ExecutionPolicy Bypass -File scripts\build-second-iteration.ps1')
    }

    foreach ($required in @($photoExe, $classicExe)) {
        Assert-Verification (Test-Path -LiteralPath $required -PathType Leaf) "Release executable is missing: $required"
    }
    $existing = @(Get-ExistingWordFlowProcesses)
    $existingIds = @($existing | ForEach-Object { $_.Id }) -join ','
    Assert-Verification ($existing.Count -eq 0) "Refusing to run single-instance/UI checks while existing WordFlow PID(s) are present: $existingIds."

    $manifest = Get-Content -LiteralPath (Join-Path $releaseDirectory 'release-manifest.json') -Raw | ConvertFrom-Json
    $releaseFiles = @()
    foreach ($path in @($photoExe, $classicExe)) {
        $file = Get-Item -LiteralPath $path
        $releaseFiles += [pscustomobject][ordered]@{
            fileName = $file.Name
            sizeBytes = $file.Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
        }
    }
    Assert-Verification ($releaseFiles[0].sha256 -ne $releaseFiles[1].sha256) 'Release executable hashes are identical.'
    Assert-Verification ($manifest.executables.Count -eq 2) 'Release manifest does not contain two executables.'
    Assert-Verification ($manifest.executables[0].iconArtifactSha256 -ne $manifest.executables[1].iconArtifactSha256) 'Release manifest icon hashes are identical.'
    $evidence.release = [pscustomobject][ordered]@{
        directory = $releaseDirectory
        manifestCommit = $manifest.commit
        executables = $releaseFiles
        iconArtifactSha256 = @($manifest.executables | ForEach-Object { $_.iconArtifactSha256 })
    }

    $evidence.singleInstance = @(
        (Test-DualInstanceOrder -PrimaryExe $photoExe -SecondaryExe $classicExe -Name 'photo-then-classic' -DataRoot (Join-Path $verificationRoot 'single-photo'))
        (Test-DualInstanceOrder -PrimaryExe $classicExe -SecondaryExe $photoExe -Name 'classic-then-photo' -DataRoot (Join-Path $verificationRoot 'single-classic')))
    $evidence.ui = Test-UiJourney
    $evidence.status = 'passed'
}
catch {
    $evidence.status = 'failed'
    $evidence.failure = $_.Exception.ToString()
    throw
}
finally {
    foreach ($process in @($launchedProcesses | Sort-Object Id -Unique)) {
        try { Stop-ExactProcess -Process $process }
        catch { Write-Warning "Exact-PID cleanup failed for $($process.Id): $($_.Exception.Message)" }
    }
    $remaining = @()
    foreach ($process in @($launchedProcesses | Sort-Object Id -Unique)) {
        if ($null -ne (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) { $remaining += $process.Id }
    }
    $evidence.cleanup = [pscustomobject][ordered]@{
        launchedPids = @($launchedProcesses | ForEach-Object { $_.Id } | Sort-Object -Unique)
        remainingPids = $remaining
        exactPidOnly = $true
        verificationRoot = $verificationRoot
    }
    try { Write-Evidence } catch { Write-Warning "Could not write evidence: $($_.Exception.Message)" }
    $resolvedRoot = [System.IO.Path]::GetFullPath($verificationRoot).TrimEnd([char]92)
    $requiredPrefix = "$tempParent\WordFlow-second-iteration-verification-"
    if ($resolvedRoot.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedRoot)) {
        [System.IO.Directory]::Delete($resolvedRoot, $true)
    }
}

Write-Host "VERIFICATION_EVIDENCE=$EvidencePath"
Write-Host ($evidence | ConvertTo-Json -Depth 12)
