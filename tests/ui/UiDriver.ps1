# Shared helpers for driving a Debug build of PDF4Free in UI tests. Dot-source this file.
#
# Three channels are used, none of which touches the real mouse, keyboard or any other window:
#   - the debug-only automation pipe (started with --automation) for pointer actions on the page view,
#   - Windows UI Automation for ribbon buttons, dialogs and the system file dialogs,
#   - PrintWindow for screenshots of the application's own window.

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
if (-not ('UiNative' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class UiNative {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, string lParam);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
}
[void][UiNative]::SetProcessDPIAware()

$script:Auto = [System.Windows.Automation.AutomationElement]
$script:Scope = [System.Windows.Automation.TreeScope]
$script:Types = [System.Windows.Automation.ControlType]
$script:RepoRoot = Split-Path (Split-Path $PSScriptRoot)
$script:AppExe = Join-Path $script:RepoRoot 'src\PDF4Free.App\bin\Debug\net10.0-windows10.0.19041.0\PDF4Free.exe'
$script:App = $null

function New-Condition($property, $value) { New-Object System.Windows.Automation.PropertyCondition($property, $value) }

# ---------------------------------------------------------------- application lifetime

function Start-App([string]$Document = '') {
    Get-Process PDF4Free -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); [void]$_.WaitForExit(5000) }
    $arguments = @('--automation')
    if ($Document) { $arguments = @("`"$Document`"", '--automation') }
    $script:App = Start-Process $script:AppExe -ArgumentList $arguments -PassThru
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $script:App.Refresh()
        if ($script:App.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($Document) {
        # Opening is done when the pipe answers with a document.
        while ((Get-Date) -lt $deadline) {
            try { if ((Send-App 'info') -like 'pages=*') { return } } catch { Start-Sleep -Milliseconds 300 }
        }
        throw "PDF4Free did not open $Document"
    }
}

# Closes the main window the way a user would. Fails if the application asks to save (it should have nothing unsaved).
function Stop-App {
    $script:App.Refresh()
    [void][UiNative]::PostMessage($script:App.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if (-not $script:App.WaitForExit(8000)) {
        $script:App.Kill()
        throw 'PDF4Free did not close on its own: it probably asked to save changes that should already have been saved.'
    }
}

# ---------------------------------------------------------------- automation pipe

function Send-App([string]$Command, [switch]$NoWait) {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'PDF4Free.Automation', [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(60000)
        $bytes = [Text.Encoding]::UTF8.GetBytes($Command + "`n")
        $pipe.Write($bytes, 0, $bytes.Length)
        $pipe.Flush()
        if ($NoWait) { Start-Sleep -Milliseconds 250; return }
        $reader = [IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8)
        $reply = $reader.ReadLine()
    }
    finally { $pipe.Dispose() }
    if ($reply -like 'error:*') { throw "PDF4Free answered '$Command' with: $reply" }
    return $reply
}

function Get-AppFields { (Send-App 'fields') | ConvertFrom-Json }

# ---------------------------------------------------------------- UI Automation

function Get-MainWindow {
    $script:App.Refresh()
    $script:Auto::FromHandle($script:App.MainWindowHandle)
}

# All top-level windows of the process plus the dialogs they own.
function Get-AppWindows {
    $all = @()
    foreach ($top in $script:Auto::RootElement.FindAll($script:Scope::Children, (New-Condition $script:Auto::ProcessIdProperty $script:App.Id))) {
        $all += $top
        $all += @($top.FindAll($script:Scope::Descendants, (New-Condition $script:Auto::ControlTypeProperty $script:Types::Window)))
    }
    $all
}

function Wait-Dialog([string]$Title, [int]$TimeoutSeconds = 15) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($window in Get-AppWindows) { if ($window.Current.Name -eq $Title) { return $window } }
        Start-Sleep -Milliseconds 120
    }
    throw "The dialog '$Title' did not appear."
}

function Wait-DialogGone([string]$Title, [int]$TimeoutSeconds = 15) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-AppWindows | Where-Object { $_.Current.Name -eq $Title })) { return }
        Start-Sleep -Milliseconds 120
    }
    throw "The dialog '$Title' did not close."
}

function Set-DialogText($Dialog, [int]$Index, [string]$Text) {
    $edits = $Dialog.FindAll($script:Scope::Descendants, (New-Condition $script:Auto::ControlTypeProperty $script:Types::Edit))
    $edits[$Index].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
}

# Presses a button, toggles a toggle button or selects a tab, found by its accessible name.
function Invoke-Named($Within, [string]$Name) {
    $found = $Within.FindAll($script:Scope::Descendants, (New-Condition $script:Auto::NameProperty $Name))
    foreach ($element in $found) {
        $pattern = $null
        if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return }
        if ($element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { $pattern.Toggle(); return }
        if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select(); return }
    }
    throw "Nothing named '$Name' could be pressed."
}

function Select-RibbonTab([string]$Name) { Invoke-Named (Get-MainWindow) $Name; Start-Sleep -Milliseconds 250 }

# Opens a dropdown and selects the entry that passes the test. The list of a dropdown closes when another program
# takes the focus, so the attempt is repeated a few times before giving up.
function Select-ComboEntry($Combo, [scriptblock]$Test, [string]$What) {
    $expand = $Combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    foreach ($attempt in 1..8) {
        $expand.Expand()
        Start-Sleep -Milliseconds 250
        $entries = $Combo.FindAll($script:Scope::Descendants, (New-Condition $script:Auto::ControlTypeProperty $script:Types::ListItem))
        $entry = $entries | Where-Object { & $Test $_.Current.Name } | Select-Object -First 1
        if ($entry) {
            $entry.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            $expand.Collapse()
            Start-Sleep -Milliseconds 150
            return
        }
        $expand.Collapse()
        Start-Sleep -Milliseconds 500
    }
    throw "The dropdown has no entry $What."
}

# Picks an item in a dropdown of the tool options bar, found by the dropdown's accessible name.
function Select-ComboItem([string]$ComboName, [string]$Item) {
    $condition = New-Object System.Windows.Automation.AndCondition((New-Condition $script:Auto::ControlTypeProperty $script:Types::ComboBox), (New-Condition $script:Auto::NameProperty $ComboName))
    $combo = (Get-MainWindow).FindFirst($script:Scope::Descendants, $condition)
    if (-not $combo) { throw "No dropdown named '$ComboName'." }
    Select-ComboEntry $combo { param($name) $name -ceq $Item }.GetNewClosure() "'$Item' in '$ComboName'"
}

# Picks the entry of a dialog's (first) dropdown whose text ends with the given text.
function Select-DialogChoice($Dialog, [string]$EndsWith) {
    $combo = $Dialog.FindFirst($script:Scope::Descendants, (New-Condition $script:Auto::ControlTypeProperty $script:Types::ComboBox))
    if (-not $combo) { throw 'The dialog has no dropdown.' }
    Select-ComboEntry $combo { param($name) $name.EndsWith($EndsWith) }.GetNewClosure() "ending with '$EndsWith'"
}

function Set-ToggleState([string]$Name, [bool]$On) {
    $element = (Get-MainWindow).FindFirst($script:Scope::Descendants, (New-Condition $script:Auto::NameProperty $Name))
    if (-not $element) { throw "No toggle named '$Name'." }
    $toggle = $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $isOn = $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    if ($isOn -ne $On) { $toggle.Toggle(); Start-Sleep -Milliseconds 150 }
}

function Find-DialogControl($Dialog, [string]$AutomationId, [string]$ClassName) {
    $condition = New-Object System.Windows.Automation.AndCondition((New-Condition $script:Auto::AutomationIdProperty $AutomationId), (New-Condition $script:Auto::ClassNameProperty $ClassName))
    $Dialog.FindFirst($script:Scope::Descendants, $condition)
}

# Uses Home > Save As and the Windows "Save as" dialog to save the active document under a new path.
function Save-DocumentAs([string]$Path) {
    if (Test-Path $Path) { Remove-Item $Path -Force }
    Select-RibbonTab 'Home'
    Invoke-Named (Get-MainWindow) 'Save As'
    $dialog = Wait-Dialog 'Save as'
    Start-Sleep -Milliseconds 600
    # The system file dialog exposes its classic controls only loosely to UI Automation, so they are addressed by
    # their well-known ids and driven with the standard window messages (sent to these two controls only).
    $nameBox = Find-DialogControl $dialog '1001' 'Edit'
    $save = Find-DialogControl $dialog '1' 'Button'
    if (-not $nameBox -or -not $save) { throw 'The file name box or the Save button of the Save dialog was not found.' }
    [void][UiNative]::SendMessage([IntPtr]$nameBox.Current.NativeWindowHandle, 0x000C, [IntPtr]::Zero, $Path)   # WM_SETTEXT
    Start-Sleep -Milliseconds 200
    [void][UiNative]::PostMessage([IntPtr]$save.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) # BM_CLICK
    Wait-DialogGone 'Save as'
    [void](Send-App 'idle')
    $deadline = (Get-Date).AddSeconds(10)
    while (-not (Test-Path $Path) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 150 }
    if (-not (Test-Path $Path)) { throw "Save As did not create $Path" }
}

function Save-Screenshot([string]$Path) {
    $script:App.Refresh()
    $hwnd = $script:App.MainWindowHandle
    $rect = New-Object UiNative+RECT
    [void][UiNative]::GetWindowRect($hwnd, [ref]$rect)
    $bitmap = New-Object System.Drawing.Bitmap(($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    [void][UiNative]::PrintWindow($hwnd, $hdc, 2)
    $graphics.ReleaseHdc($hdc)
    $graphics.Dispose()
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

# ---------------------------------------------------------------- reporting

$script:Failures = 0
$script:Passes = 0

function Assert-That([bool]$Condition, [string]$What) {
    if ($Condition) { $script:Passes++; Write-Host "  ok    $What" }
    else { $script:Failures++; Write-Host "  FAIL  $What" -ForegroundColor Red }
}

function Assert-Equal($Expected, $Actual, [string]$What) {
    if ("$Expected" -ceq "$Actual") { $script:Passes++; Write-Host "  ok    $What" }
    else { $script:Failures++; Write-Host "  FAIL  $What  (expected '$Expected', found '$Actual')" -ForegroundColor Red }
}
