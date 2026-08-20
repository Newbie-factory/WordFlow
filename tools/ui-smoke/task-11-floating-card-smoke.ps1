param(
    [string]$Configuration = "Release",
    [int]$WorkAreaHeightPx = 0
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$candidates = @(
    (Join-Path $repositoryRoot "src\WordFlow.App\bin\x64\$Configuration\net8.0-windows10.0.19041.0\WordFlow.App.exe"),
    (Join-Path $repositoryRoot "src\WordFlow.App\bin\$Configuration\net8.0-windows10.0.19041.0\WordFlow.App.exe")
)
$executable = $candidates | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ } |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $executable) { throw "Build the $Configuration WordFlow.App executable before running the UI smoke." }

$suffix = if ($WorkAreaHeightPx -gt 0) { "-constrained" } else { "" }
$screenshot = Join-Path $repositoryRoot ".superpowers\sdd\task-11-visual$suffix.png"
$evidence = Join-Path $repositoryRoot ".superpowers\sdd\task-11-ui-smoke$suffix.json"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WordFlowSmokeNative {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint attachTo, bool value);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
}
'@
function U([string]$Value) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value)) }
$n = @{
    Window = U 'V29yZEZsb3cg5oKs5rWu5a2m5Lmg5Y2h'
    WordExact = U '5pKt5pS+5b2T5YmN5Y2V6K+NIG1ldGljdWxvdXMg56a757q/5Y+R6Z+z'
    Phonetic = U '5b2T5YmN5Y2V6K+N6Z+z5qCHIC9tyZnLiHTJqmtqyZlsyZlzLw=='
    PhoneticValue = U 'L23JmcuIdMmqa2rJmWzJmXMv'
    Chinese = U '5b2T5YmN5Y2V6K+N5Lit5paH6YeK5LmJIOS4gOS4neS4jeiLn+eahO+8m+aegeWFtuS7lOe7hueahA=='
    ChineseValue = U '5LiA5Lid5LiN6Iuf55qE77yb5p6B5YW25LuU57uG55qE'
    WordPrefix = U '5pKt5pS+5b2T5YmN5Y2V6K+NIG1ldGljdWxvdXM='
    DetailsPrefix = U '5omT5byA5b2T5YmN5Y2V6K+NIG1ldGljdWxvdXM='
    Again = U '5LiN6K6k6K+G'; Hard = U '5qih57OK'; Good = U '6K6k6K+G'; Slash = U '5pap6K+N'
    Synonyms = U '5bGV5byA5oiW5pS26LW36L+R5LmJ6L6o5p6Q'; Confusables = U '5bGV5byA5oiW5pS26LW35b2i6L+R5piT5re3'; Undo = U '5pKk6ZSA'
    SpeechStatus = U '5bey5pKt5pS+IG1ldGljdWxvdXMg55qE56a757q/5Y+R6Z+z'
    Results = U '5YWz57O76K+N5a6M5pW057uT5p6c'
    Search = U '5pCc57Si5YWo6YOo5YWz57O76K+N'
    Group = U 'YSDCtyBjYXJlZnVsIGFuZCBwcmVjaXNl'
    RowPrefix = U 'cHJlY2lzZe+8m+mfs+aghyAvc2FtcGxlLTEv77yb5Lit5paHIOe7j+i/h+aguOmqjOeahOS4reaWh+mHiuS5iSAx'
    Contrast = U '6L6o5p6Q'; Collocation = U '5pCt6YWN'
    RowSpeak = U '5pKt5pS+IHByZWNpc2Ug56a757q/5Y+R6Z+z'; RowDetails = U '5omT5byAIHByZWNpc2Ug5a6M5pW06K+N5p2h'
    RowSpeechStatus = U '5bey5pKt5pS+IHByZWNpc2Ug55qE56a757q/5Y+R6Z+z'
    RowAdd = U '5bCGIHByZWNpc2Ug5Yqg5YWl5a2m5Lmg'
    RowAddStatus = U '5bey5bCGIHByZWNpc2Ug5Yqg5YWl5a2m5Lmg'
    SearchMatch = U 'c2NydXB1bG91c++8m+mfs+aghyAvc2FtcGxlLTMv'
    Paused = U '5a2m5Lmg5bey5pqC5YGc'
}

function Wait-ForElement {
    param([System.Windows.Automation.AutomationElement]$Root, [System.Windows.Automation.Condition]$Condition,
        [string]$Description = "automation element", [int]$TimeoutMilliseconds = 15000)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $Condition)
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Description."
}

function Pid-Condition([int]$ProcessIdentifier) {
    [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessIdentifier)
}
function Name-Condition([string]$Name) {
    [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
}
function Find-PidName([System.Windows.Automation.AutomationElement]$Root, [int]$ProcessIdentifier, [string]$Name) {
    $condition = [System.Windows.Automation.AndCondition]::new((Pid-Condition $ProcessIdentifier), (Name-Condition $Name))
    Wait-ForElement $Root $condition "PID $ProcessIdentifier automation name '$Name'"
}
function Find-PidNameType([System.Windows.Automation.AutomationElement]$Root, [int]$ProcessIdentifier, [string]$Name,
        [System.Windows.Automation.ControlType]$ControlType) {
    $condition = [System.Windows.Automation.AndCondition]::new((Pid-Condition $ProcessIdentifier), (Name-Condition $Name),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType))
    Wait-ForElement $Root $condition "PID $ProcessIdentifier $ControlType '$Name'"
}
function Find-PidNamePrefix([System.Windows.Automation.AutomationElement]$Root, [int]$ProcessIdentifier, [string]$Prefix) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $elements = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Pid-Condition $ProcessIdentifier))
        foreach ($element in $elements) { if ($element.Current.Name.StartsWith($Prefix, [StringComparison]::Ordinal)) { return $element } }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for PID $ProcessIdentifier automation name prefix '$Prefix'."
}
function Invoke-Element([System.Windows.Automation.AutomationElement]$Element) {
    ([System.Windows.Automation.InvokePattern]$Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
function Assert-FocusPrefix([string]$Prefix) {
    Start-Sleep -Milliseconds 100
    $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
    if ($null -eq $focused -or $focused.Current.ProcessId -ne $process.Id) {
        $focused = $desktop.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.AndCondition]::new((Pid-Condition $process.Id),
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::HasKeyboardFocusProperty, $true)))
    }
    $candidate = $focused
    for ($depth = 0; $null -ne $candidate -and $depth -lt 5; $depth++) {
        if ($candidate.Current.Name.StartsWith($Prefix, [StringComparison]::Ordinal)) { return $candidate.Current.Name }
        $candidate = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($candidate)
    }
    if ($null -eq $focused -or -not $focused.Current.Name.StartsWith($Prefix, [StringComparison]::Ordinal)) {
        throw "Expected Tab focus '$Prefix', got '$($focused.Current.Name)'."
    }
    return $focused.Current.Name
}

$process = $null
try {
    $arguments = @("--ui-smoke")
    if ($WorkAreaHeightPx -gt 0) { $arguments += "--ui-smoke-work-area-height=$WorkAreaHeightPx" }
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -PassThru
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $windowCondition = [System.Windows.Automation.AndCondition]::new(
        (Pid-Condition $process.Id),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window),
        (Name-Condition $n.Window))
    $window = Wait-ForElement $desktop $windowCondition "exact PID-owned WordFlow window"
    $nativeHandle = [IntPtr]$window.Current.NativeWindowHandle
    $null = [WordFlowSmokeNative]::ShowWindow($nativeHandle, 5)
    $foregroundHandle = [WordFlowSmokeNative]::GetForegroundWindow()
    [uint32]$ignoredProcess = 0
    $foregroundThread = [WordFlowSmokeNative]::GetWindowThreadProcessId($foregroundHandle, [ref]$ignoredProcess)
    $targetThread = [WordFlowSmokeNative]::GetWindowThreadProcessId($nativeHandle, [ref]$ignoredProcess)
    $currentThread = [WordFlowSmokeNative]::GetCurrentThreadId()
    $null = [WordFlowSmokeNative]::AttachThreadInput($currentThread, $foregroundThread, $true)
    $null = [WordFlowSmokeNative]::AttachThreadInput($currentThread, $targetThread, $true)
    $null = [WordFlowSmokeNative]::BringWindowToTop($nativeHandle)
    $null = [WordFlowSmokeNative]::SetForegroundWindow($nativeHandle)
    $null = [WordFlowSmokeNative]::SetFocus($nativeHandle)
    $null = [WordFlowSmokeNative]::AttachThreadInput($currentThread, $targetThread, $false)
    $null = [WordFlowSmokeNative]::AttachThreadInput($currentThread, $foregroundThread, $false)
    $window.SetFocus()
    Start-Sleep -Milliseconds 200

    $word = Find-PidNamePrefix $desktop $process.Id $n.WordPrefix
    $phonetic = Find-PidName $desktop $process.Id $n.Phonetic
    $chinese = Find-PidName $desktop $process.Id $n.Chinese
    if ($phonetic.Current.HelpText -ne $n.PhoneticValue -or $chinese.Current.HelpText -ne $n.ChineseValue) { throw "UIA phonetic/Chinese values are incomplete." }
    $wordBefore = $word.Current.Name
    $mainBefore = $window.Current.BoundingRectangle

    $null = [WordFlowSmokeNative]::PostMessage($nativeHandle, 0x806F, [IntPtr]3, [IntPtr]0)
    $pauseDeadline = [DateTime]::UtcNow.AddSeconds(5)
    $good = Find-PidNamePrefix $desktop $process.Id $n.Good
    while ($good.Current.IsEnabled -and [DateTime]::UtcNow -lt $pauseDeadline) { Start-Sleep -Milliseconds 50 }
    if ($good.Current.IsEnabled) { throw "Pause did not disable learning commands." }
    $null = Find-PidName $desktop $process.Id $n.Paused
    $null = [WordFlowSmokeNative]::PostMessage($nativeHandle, 0x806F, [IntPtr]4, [IntPtr]0)
    $resumeDeadline = [DateTime]::UtcNow.AddSeconds(5)
    while (-not $good.Current.IsEnabled -and [DateTime]::UtcNow -lt $resumeDeadline) { Start-Sleep -Milliseconds 50 }
    if (-not $good.Current.IsEnabled) { throw "Resume did not restore learning commands." }

    $tabPrefixes = @($n.WordPrefix, $n.DetailsPrefix, $n.Again, $n.Hard, $n.Good, $n.Slash, $n.Synonyms, $n.Confusables)
    $word.SetFocus()
    Start-Sleep -Milliseconds 200
    $tabNames = @((Assert-FocusPrefix $tabPrefixes[0]))
    for ($index = 1; $index -lt $tabPrefixes.Count; $index++) {
        $null = [WordFlowSmokeNative]::SendMessage($nativeHandle, 0x0100, [IntPtr]9, [IntPtr]0)
        $null = [WordFlowSmokeNative]::SendMessage($nativeHandle, 0x0101, [IntPtr]9, [IntPtr]0)
        $tabNames += Assert-FocusPrefix $tabPrefixes[$index]
    }
    Invoke-Element (Find-PidNamePrefix $desktop $process.Id $n.Good)
    $undoElement = Find-PidNamePrefix $desktop $process.Id $n.Undo
    $enableDeadline = [DateTime]::UtcNow.AddSeconds(5)
    while (-not $undoElement.Current.IsEnabled -and [DateTime]::UtcNow -lt $enableDeadline) { Start-Sleep -Milliseconds 100 }
    if (-not $undoElement.Current.IsEnabled) { throw "Undo did not enable after the committed rating." }
    (Find-PidName $desktop $process.Id $n.Confusables).SetFocus()
    $null = Assert-FocusPrefix $n.Confusables
    $null = [WordFlowSmokeNative]::SendMessage($nativeHandle, 0x0100, [IntPtr]9, [IntPtr]0)
    $null = [WordFlowSmokeNative]::SendMessage($nativeHandle, 0x0101, [IntPtr]9, [IntPtr]0)
    $tabNames += Assert-FocusPrefix $n.Undo

    $word = Find-PidNamePrefix $desktop $process.Id $n.WordPrefix
    Invoke-Element $word
    $null = Find-PidName $desktop $process.Id $n.SpeechStatus

    Invoke-Element (Find-PidName $desktop $process.Id $n.Synonyms)
    $synonymResults = Find-PidName $desktop $process.Id $n.Results
    $search = Find-PidName $desktop $process.Id $n.Search
    if (-not $search.Current.HasKeyboardFocus) { throw "Opening a relation popup did not focus its search box." }
    $valuePattern = [System.Windows.Automation.ValuePattern]$search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $valuePattern.SetValue("scrupulous")
    $null = Find-PidNamePrefix $desktop $process.Id $n.SearchMatch
    $valuePattern.SetValue("")
    $search.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait("{TAB}")
    $popupTabResults = Assert-FocusPrefix $n.Results
    [System.Windows.Forms.SendKeys]::SendWait("{TAB}")
    $popupTabGroup = Assert-FocusPrefix $n.Group
    [System.Windows.Forms.SendKeys]::SendWait("{TAB}")
    $popupTabRow = Assert-FocusPrefix $n.RowPrefix
    [System.Windows.Forms.SendKeys]::SendWait("{TAB}")
    $popupTabAction = Assert-FocusPrefix $n.RowSpeak
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Start-Sleep -Milliseconds 200
    $openAfterEscape = $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.AndCondition]::new((Pid-Condition $process.Id), (Name-Condition $n.Results))) |
        Where-Object { -not $_.Current.IsOffscreen }
    if ($openAfterEscape.Count -ne 0) { throw "Escape did not close the focused relation popup." }
    $null = Assert-FocusPrefix $n.Synonyms
    Invoke-Element (Find-PidName $desktop $process.Id $n.Synonyms)
    $synonymResults = Find-PidName $desktop $process.Id $n.Results
    $row = Find-PidNamePrefix $desktop $process.Id $n.RowPrefix
    if (-not $row.Current.Name.Contains($n.Contrast) -or -not $row.Current.Name.Contains($n.Collocation)) { throw "Relation row lacks contextual evidence." }
    $rowSpeak = Find-PidName $desktop $process.Id $n.RowSpeak
    $null = Find-PidName $desktop $process.Id $n.RowDetails
    Invoke-Element $rowSpeak
    $null = Find-PidName $desktop $process.Id $n.RowSpeechStatus

    $rowBounds = $row.Current.BoundingRectangle
    $null = [WordFlowSmokeNative]::SetCursorPos([int]($rowBounds.Left + $rowBounds.Width / 2), [int]($rowBounds.Top + $rowBounds.Height / 2))
    [WordFlowSmokeNative]::mouse_event(8, 0, 0, 0, [UIntPtr]::Zero)
    [WordFlowSmokeNative]::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero)
    $contextDetails = Find-PidNameType $desktop $process.Id $n.RowDetails ([System.Windows.Automation.ControlType]::MenuItem)
    $null = Find-PidNameType $desktop $process.Id $n.RowAdd ([System.Windows.Automation.ControlType]::MenuItem)
    Invoke-Element $contextDetails
    $null = Find-PidName $desktop $process.Id (U '5bey5omT5byAIHByZWNpc2Ug55qE5a6M5pW06K+N5p2h')
    $null = [WordFlowSmokeNative]::SetCursorPos([int]($rowBounds.Left + $rowBounds.Width / 2), [int]($rowBounds.Top + $rowBounds.Height / 2))
    [WordFlowSmokeNative]::mouse_event(8, 0, 0, 0, [UIntPtr]::Zero)
    [WordFlowSmokeNative]::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero)
    Invoke-Element (Find-PidNameType $desktop $process.Id $n.RowAdd ([System.Windows.Automation.ControlType]::MenuItem))
    $null = Find-PidName $desktop $process.Id $n.RowAddStatus

    $null = [WordFlowSmokeNative]::PostMessage($nativeHandle, 0x806F, [IntPtr]1, [IntPtr]0)
    $hideDeadline = [DateTime]::UtcNow.AddSeconds(5)
    while (-not $window.Current.IsOffscreen -and [DateTime]::UtcNow -lt $hideDeadline) { Start-Sleep -Milliseconds 50 }
    if (-not $window.Current.IsOffscreen) { throw "Hide seam did not hide the card." }
    $null = [WordFlowSmokeNative]::PostMessage($nativeHandle, 0x806F, [IntPtr]2, [IntPtr]0)
    $showDeadline = [DateTime]::UtcNow.AddSeconds(5)
    while ($window.Current.IsOffscreen -and [DateTime]::UtcNow -lt $showDeadline) { Start-Sleep -Milliseconds 50 }
    if ($window.Current.IsOffscreen) { throw "Restore seam did not show the card." }
    $visibleAfterRestore = $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.AndCondition]::new((Pid-Condition $process.Id), (Name-Condition $n.Results))) |
        Where-Object { -not $_.Current.IsOffscreen }
    if ($visibleAfterRestore.Count -ne 0) { throw "Tray-style hide/restore reopened a relation popup." }

    Invoke-Element (Find-PidName $desktop $process.Id $n.Confusables)
    Start-Sleep -Milliseconds 300
    $visibleResults = $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.AndCondition]::new((Pid-Condition $process.Id), (Name-Condition $n.Results))) |
        Where-Object { -not $_.Current.IsOffscreen }
    if ($visibleResults.Count -ne 1) { throw "Exactly one relation drawer must be visible." }
    $resultsBounds = $visibleResults[0].Current.BoundingRectangle
    $visibleRows = $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.AndCondition]::new((Pid-Condition $process.Id),
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) |
        Where-Object { -not $_.Current.IsOffscreen -and ([string]$_.Current.Name).Contains('/sample-') }
    $completeRows = @($visibleRows | Where-Object {
        $_.Current.BoundingRectangle.Top -ge $resultsBounds.Top - 8 -and $_.Current.BoundingRectangle.Bottom -le $resultsBounds.Bottom + 8 })
    $clippedRows = @($visibleRows | Where-Object {
        $_.Current.BoundingRectangle.Top -lt $resultsBounds.Top - 8 -or $_.Current.BoundingRectangle.Bottom -gt $resultsBounds.Bottom + 8 })
    if ($clippedRows.Count -ne 0) { throw "Relation viewport exposed a clipped row fragment." }
    if ($WorkAreaHeightPx -eq 0 -and $completeRows.Count -ne 5) { throw "Normal drawer must expose exactly five complete rows; got $($completeRows.Count)." }
    if ($WorkAreaHeightPx -gt 0 -and $completeRows.Count -lt 1) { throw "Constrained drawer must expose at least one complete compact row." }
    $scroll = [System.Windows.Automation.ScrollPattern]$visibleResults[0].GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    if (-not $scroll.Current.VerticallyScrollable) { throw "More-than-five results must scroll internally." }
    $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 100)
    Start-Sleep -Milliseconds 200

    $mainAfter = $window.Current.BoundingRectangle
    if ([Math]::Abs($mainBefore.Top - $mainAfter.Top) -gt 1 -or [Math]::Abs($mainBefore.Left - $mainAfter.Left) -gt 1) { throw "Opening the drawer moved the stable word block window." }
    $word = Find-PidNamePrefix $desktop $process.Id $n.WordPrefix
    if ($wordBefore -ne $word.Current.Name) { throw "Opening and scrolling drawers changed the current word." }
    $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 0)
    Start-Sleep -Milliseconds 200

    $drawerBounds = $visibleResults[0].Current.BoundingRectangle
    $left = [int][Math]::Floor([Math]::Min($mainAfter.Left, $drawerBounds.Left - 30))
    $top = [int][Math]::Floor([Math]::Min($mainAfter.Top, $drawerBounds.Top - 100))
    $right = [int][Math]::Ceiling([Math]::Max($mainAfter.Right, $drawerBounds.Right + 30))
    $bottom = [int][Math]::Ceiling([Math]::Max($mainAfter.Bottom, $drawerBounds.Bottom + 30))
    $screen = [System.Windows.Forms.Screen]::FromHandle($process.MainWindowHandle).WorkingArea
    $left = [Math]::Max($left, $screen.Left); $top = [Math]::Max($top, $screen.Top)
    $right = [Math]::Min($right, $screen.Right); $bottom = [Math]::Min($bottom, $screen.Bottom)
    if ($WorkAreaHeightPx -gt 0 -and ($bottom - $top) -gt $WorkAreaHeightPx + 2) { throw "Constrained work-area UI height $($bottom-$top) exceeds simulated $WorkAreaHeightPx." }
    $nativeSurfaces = $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, (Pid-Condition $process.Id)) |
        Where-Object { $_.Current.NativeWindowHandle -ne 0 -and -not $_.Current.IsOffscreen -and $_.Current.NativeWindowHandle -ne $nativeHandle.ToInt32() -and
            $_.Current.BoundingRectangle.Left -le ($drawerBounds.Left + $drawerBounds.Width / 2) -and $_.Current.BoundingRectangle.Right -ge ($drawerBounds.Left + $drawerBounds.Width / 2) -and
            $_.Current.BoundingRectangle.Top -le ($drawerBounds.Top + $drawerBounds.Height / 2) -and $_.Current.BoundingRectangle.Bottom -ge ($drawerBounds.Top + $drawerBounds.Height / 2) } |
        Sort-Object { $_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height }
    $popupSurface = $nativeSurfaces | Select-Object -First 1
    if ($null -eq $popupSurface) { throw "Could not resolve the exact PID-owned popup HWND for capture." }
    $popupBounds = $popupSurface.Current.BoundingRectangle
    $null = [WordFlowSmokeNative]::PostMessage($nativeHandle, 0x806F, [IntPtr]5, [IntPtr]0)
    Start-Sleep -Milliseconds 150
    if (([WordFlowSmokeNative]::GetWindowLong($nativeHandle, -20) -band 8) -ne 0 -or
        ([WordFlowSmokeNative]::GetWindowLong([IntPtr]$popupSurface.Current.NativeWindowHandle, -20) -band 8) -ne 0) {
        throw "Fullscreen suppression seam did not demote both main and popup HWNDs."
    }
    $null = [WordFlowSmokeNative]::PostMessage($nativeHandle, 0x806F, [IntPtr]6, [IntPtr]0)
    Start-Sleep -Milliseconds 150
    if (([WordFlowSmokeNative]::GetWindowLong($nativeHandle, -20) -band 8) -eq 0 -or
        ([WordFlowSmokeNative]::GetWindowLong([IntPtr]$popupSurface.Current.NativeWindowHandle, -20) -band 8) -eq 0) {
        throw "Fullscreen restoration seam did not restore both main and popup HWNDs."
    }
    $left = [int][Math]::Floor([Math]::Min($mainAfter.Left, $popupBounds.Left))
    $top = [int][Math]::Floor([Math]::Min($mainAfter.Top, $popupBounds.Top))
    $right = [int][Math]::Ceiling([Math]::Max($mainAfter.Right, $popupBounds.Right))
    $bottom = [int][Math]::Ceiling([Math]::Max($mainAfter.Bottom, $popupBounds.Bottom))
    $bitmap = [System.Drawing.Bitmap]::new($right - $left, $bottom - $top)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::FromArgb(243, 246, 249))
            foreach ($surface in @(
                @{ Handle = $nativeHandle; Bounds = $mainAfter },
                @{ Handle = [IntPtr]$popupSurface.Current.NativeWindowHandle; Bounds = $popupBounds })) {
                $bounds = $surface.Bounds
                $part = [System.Drawing.Bitmap]::new([int][Math]::Ceiling($bounds.Width), [int][Math]::Ceiling($bounds.Height))
                try {
                    $partGraphics = [System.Drawing.Graphics]::FromImage($part)
                    try {
                        $dc = $partGraphics.GetHdc()
                        try { if (-not [WordFlowSmokeNative]::PrintWindow($surface.Handle, $dc, 2)) { throw "PrintWindow failed for PID-owned surface." } }
                        finally { $partGraphics.ReleaseHdc($dc) }
                    } finally { $partGraphics.Dispose() }
                    $graphics.DrawImageUnscaled($part, [int]($bounds.Left - $left), [int]($bounds.Top - $top))
                } finally { $part.Dispose() }
            }
        } finally { $graphics.Dispose() }
        New-Item -ItemType Directory -Force -Path (Split-Path $screenshot) | Out-Null
        $bitmap.Save($screenshot, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }

    $result = [ordered]@{
        pid = $process.Id; executable = $executable; window = $window.Current.Name
        word_before = $wordBefore; word_after = $word.Current.Name
        phonetic_name = $phonetic.Current.Name; chinese_name = $chinese.Current.Name
        tab_order = $tabNames; popup_tab_order = @($search.Current.Name, $popupTabResults, $popupTabGroup, $popupTabRow, $popupTabAction); contextual_row = $row.Current.Name
        one_drawer_visible = $true; internal_scroll = $true; stable_window_bounds = $true
        pause_gated = $true; escape_returned_to_main = $true; hide_restore_closed_popup = $true; popup_topmost_suppressed_and_restored = $true
        complete_visible_rows = $completeRows.Count; clipped_visible_rows = $clippedRows.Count
        work_area_height_limit_px = $WorkAreaHeightPx; screenshot = $screenshot
    }
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $evidence -Encoding utf8
    $result | ConvertTo-Json -Depth 4
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(2500)) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
    }
    if ($null -ne $process) {
        Remove-Item -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) "wordflow-ui-smoke-$($process.Id).json") -Force -ErrorAction SilentlyContinue
        $remaining = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
        if ($remaining) { throw "Exact UI smoke PID $($process.Id) remained after cleanup." }
    }
}
