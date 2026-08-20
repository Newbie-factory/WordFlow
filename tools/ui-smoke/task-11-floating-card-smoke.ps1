param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$platformExecutable = Join-Path $repositoryRoot "src\WordFlow.App\bin\x64\$Configuration\net8.0-windows10.0.19041.0\WordFlow.App.exe"
$portableExecutable = Join-Path $repositoryRoot "src\WordFlow.App\bin\$Configuration\net8.0-windows10.0.19041.0\WordFlow.App.exe"
$executable = if (Test-Path -LiteralPath $platformExecutable) { $platformExecutable } else { $portableExecutable }
$screenshot = Join-Path $repositoryRoot ".superpowers\sdd\task-11-visual.png"
$evidence = Join-Path $repositoryRoot ".superpowers\sdd\task-11-ui-smoke.json"

if (-not (Test-Path -LiteralPath $executable)) {
    throw "Build the $Configuration WordFlow.App executable before running the UI smoke."
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing.Common
Add-Type -AssemblyName System.Windows.Forms

function Wait-ForElement {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.Condition]$Condition,
        [string]$Description = "automation element",
        [int]$TimeoutMilliseconds = 10000
    )
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $Condition)
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Description."
}

function Find-ByName {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    return Wait-ForElement -Root $Root -Condition $condition -Description "automation name '$Name'"
}

function Invoke-Element {
    param([System.Windows.Automation.AutomationElement]$Element)
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}

$process = $null
try {
    $process = Start-Process -FilePath $executable -ArgumentList "--ui-smoke" -PassThru -WindowStyle Hidden
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $windowCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $process.Id)
    $window = Wait-ForElement -Root $root -Condition $windowCondition -Description "WordFlow process window" -TimeoutMilliseconds 15000

    $wordElement = Find-ByName -Root $window -Name "当前单词"
    $wordBefore = $wordElement.Current.HelpText

    Invoke-Element (Find-ByName -Root $window -Name "展开或收起近义辨析")
    $synonymResults = Find-ByName -Root $window -Name "关系词完整结果"
    if ($synonymResults.Current.IsOffscreen) { throw "Synonym drawer did not become visible." }

    Invoke-Element (Find-ByName -Root $window -Name "展开或收起形近易混")
    Start-Sleep -Milliseconds 300
    $visibleLists = $window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            "关系词完整结果")) | Where-Object { -not $_.Current.IsOffscreen }
    if ($visibleLists.Count -ne 1) { throw "Exactly one relation drawer must be visible." }

    $scroll = [System.Windows.Automation.ScrollPattern]$visibleLists[0].GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    if (-not $scroll.Current.VerticallyScrollable) { throw "More-than-five relation results must scroll internally." }
    $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 100)
    Start-Sleep -Milliseconds 250

    $wordAfter = $wordElement.Current.HelpText
    if ($wordBefore -ne $wordAfter) { throw "Opening and scrolling drawers changed the current word." }

    $bounds = $window.Current.BoundingRectangle
    $screen = [System.Windows.Forms.Screen]::FromHandle($process.MainWindowHandle).Bounds
    $left = [Math]::Max([int][Math]::Floor($bounds.Left), $screen.Left)
    $top = [Math]::Max([int][Math]::Floor($bounds.Top), $screen.Top)
    $right = [Math]::Min([int][Math]::Ceiling($bounds.Right), $screen.Right)
    $bottom = [Math]::Min([int][Math]::Ceiling($bounds.Bottom), $screen.Bottom)
    $bitmap = [System.Drawing.Bitmap]::new($right - $left, $bottom - $top)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($left, $top, 0, 0, $bitmap.Size)
        }
        finally { $graphics.Dispose() }
        New-Item -ItemType Directory -Force -Path (Split-Path $screenshot) | Out-Null
        $bitmap.Save($screenshot, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }

    $result = [ordered]@{
        pid = $process.Id
        window = $window.Current.Name
        word_before = $wordBefore
        word_after = $wordAfter
        one_drawer_visible = $true
        internal_scroll = $true
        screenshot = $screenshot
    }
    $result | ConvertTo-Json | Set-Content -LiteralPath $evidence -Encoding utf8
    $result | ConvertTo-Json
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(2000)) {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit()
        }
    }
    if ($null -ne $process) {
        $placement = Join-Path ([IO.Path]::GetTempPath()) "wordflow-ui-smoke-$($process.Id).json"
        Remove-Item -LiteralPath $placement -Force -ErrorAction SilentlyContinue
    }
}
