# End-to-end test of form design and filling in the running application.
#
#   1. Design a purchase-order form (header, order lines, totals/approval) on a blank page with the app's own tools.
#   2. Save it with Save As, close the application.
#   3. Open the saved form in a new application instance, check it is blank, fill every kind of control.
#   4. Save the filled form under another name, close the application.
#   5. Open the filled form again and validate every value three ways: what the application shows, what the
#      engine reads from the file, and what a reader would display (text of the flattened form).
#
# Needs a Debug build (it uses the debug-only automation pipe). Run from anywhere:
#   pwsh tests/ui/FormEndToEnd.ps1

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UiDriver.ps1')

$samples = Join-Path $script:RepoRoot 'samples'
New-Item -ItemType Directory -Force $samples | Out-Null
$blankForm = Join-Path $samples 'PurchaseOrder-blank.pdf'
$filledForm = Join-Path $samples 'PurchaseOrder-filled.pdf'
$inspector = Join-Path $script:RepoRoot 'tests\PDF4Free.Tests\bin\Debug\net10.0-windows10.0.19041.0\PDF4Free.Tests.exe'

# ---------------------------------------------------------------- the form, described once

# Static text: x, top, size, bold, text.
$labels = @(
    @(72, 46, 20, $true, 'PURCHASE ORDER'),
    @(72, 74, 10, $false, 'Cedar Trading Co. - Procurement department'),
    @(72, 98, 9, $true, 'ORDER HEADER'),
    @(72, 112, 9, $false, 'PO number'), @(240, 112, 9, $false, 'Order date'), @(370, 112, 9, $false, 'Payment terms'),
    @(72, 152, 9, $false, 'Customer name'), @(310, 152, 9, $false, 'Customer e-mail'),
    @(72, 192, 9, $false, 'Ship to'), @(310, 192, 9, $false, 'Delivery'),
    @(326, 206, 9, $false, 'Standard'), @(406, 206, 9, $false, 'Express'), @(486, 206, 9, $false, 'Overnight'),
    @(310, 228, 9, $false, 'Shipping method'), @(482, 229, 9, $false, 'Taxable'), @(482, 247, 9, $false, 'Partial shipment'),
    @(72, 268, 9, $true, 'ORDER LINES'),
    @(76, 284, 9, $true, '#'), @(92, 284, 9, $true, 'Item code'), @(176, 284, 9, $true, 'Description'), @(350, 284, 9, $true, 'Qty'),
    @(394, 284, 9, $true, 'Unit price'), @(460, 284, 9, $true, 'Line total'), @(527, 284, 9, $true, 'B/O'),
    @(72, 418, 9, $true, 'TOTALS AND APPROVAL'),
    @(72, 432, 9, $false, 'Notes'), @(408, 449, 9, $false, 'Subtotal'), @(408, 471, 9, $false, 'Tax'), @(408, 493, 9, $false, 'Total'),
    @(88, 517, 9, $false, 'Approved'), @(180, 506, 9, $false, 'Authorized by'), @(370, 506, 9, $false, 'Signature'),
    @(180, 542, 9, $false, 'Approved on')
)
for ($row = 1; $row -le 5; $row++) { $labels += , @(78, (302 + ($row - 1) * 22), 9, $false, "$row") }

# Fields: tool, name, x, y, width, height, extra (choices for lists, value for a radio button, format for dates),
# expected type. A date field is a text field that carries a date format.
$fields = @(
    @('FieldText', 'PONumber', 72, 124, 150, 18, $null, 'Text'),
    @('FieldDate', 'OrderDate', 240, 124, 110, 18, 'dd/mm/yyyy', 'Text'),
    @('FieldCombo', 'PaymentTerms', 370, 124, 170, 18, "Net 15`nNet 30`nNet 60`nPrepaid", 'ComboBox'),
    @('FieldText', 'CustomerName', 72, 164, 220, 18, $null, 'Text'),
    @('FieldText', 'CustomerEmail', 310, 164, 230, 18, $null, 'Text'),
    @('FieldText', 'ShipTo', 72, 204, 220, 48, $null, 'Text'),
    @('FieldRadio', 'Delivery', 310, 205, 12, 12, 'Standard', 'Radio'),
    @('FieldRadio', 'Delivery', 390, 205, 12, 12, 'Express', 'Radio'),
    @('FieldRadio', 'Delivery', 470, 205, 12, 12, 'Overnight', 'Radio'),
    @('FieldList', 'ShippingMethod', 310, 240, 140, 18, "Ground`nCourier`nAir freight`nSea freight", 'ListBox'),
    @('FieldCheck', 'Taxable', 466, 228, 12, 12, $null, 'CheckBox'),
    @('FieldCheck', 'PartialShipment', 466, 246, 12, 12, $null, 'CheckBox')
)
for ($row = 1; $row -le 5; $row++) {
    $y = 298 + ($row - 1) * 22
    $fields += , @('FieldText', "Item$row", 92, $y, 80, 18, $null, 'Text')
    $fields += , @('FieldText', "Desc$row", 176, $y, 170, 18, $null, 'Text')
    $fields += , @('FieldText', "Qty$row", 350, $y, 40, 18, $null, 'Text')
    $fields += , @('FieldText', "Price$row", 394, $y, 62, 18, $null, 'Text')
    $fields += , @('FieldText', "Total$row", 460, $y, 62, 18, $null, 'Text')
    $fields += , @('FieldCheck', "Backorder$row", 530, ($y + 3), 12, 12, $null, 'CheckBox')
}
$fields += @(
    @('FieldText', 'Notes', 72, 444, 300, 54, $null, 'Text'),
    @('FieldText', 'Subtotal', 460, 444, 62, 18, $null, 'Text'),
    @('FieldText', 'Tax', 460, 466, 62, 18, $null, 'Text'),
    @('FieldText', 'GrandTotal', 460, 488, 62, 18, $null, 'Text'),
    @('FieldCheck', 'Approved', 72, 516, 12, 12, $null, 'CheckBox'),
    @('FieldText', 'AuthorizedBy', 180, 518, 170, 18, $null, 'Text'),
    @('FieldSignature', 'ApprovalSignature', 370, 518, 170, 40, $null, 'Signature'),
    @('FieldDateTime', 'ApprovedOn', 180, 554, 170, 18, 'd mmm yyyy HH:MM', 'Text')
)

# What gets typed into the text fields ("\n" is a line break), in filling order.
$textValues = [ordered]@{
    PONumber = 'PO-2026-0457'; CustomerName = 'Cedar Trading Co. s.a.l.'
    CustomerEmail = 'orders@cedartrading.example'; ShipTo = 'Hamra Street, Block B\nBeirut 1103, Lebanon'
    Item1 = 'A-1001'; Desc1 = 'Printer paper A4, 500 sheets'; Qty1 = '12'; Price1 = '4.75'; Total1 = '57.00'
    Item2 = 'B-2040'; Desc2 = 'Toner cartridge, black'; Qty2 = '3'; Price2 = '68.90'; Total2 = '206.70'
    Item3 = 'C-0007'; Desc3 = 'USB-C docking station'; Qty3 = '2'; Price3 = '129.00'; Total3 = '258.00'
    Notes = 'Deliver between 9:00 and 14:00.\nCall reception on arrival. شكرا'
    Subtotal = '521.70'; Tax = '57.39'; GrandTotal = '579.09'; AuthorizedBy = 'Layla Haddad'
}
$choiceValues = [ordered]@{ PaymentTerms = 'Net 30'; ShippingMethod = 'Courier' }
# Date fields: the format chosen in the designer, how the value is entered (typed text, or a moment chosen the way
# the calendar and time boxes choose it), and what the field must hold afterwards.
$dateFields = [ordered]@{
    OrderDate  = @{ Format = 'dd/mm/yyyy'; Typed = '2026-11-25'; Stored = '25/11/2026' }
    ApprovedOn = @{ Format = 'd mmm yyyy HH:MM'; Picked = '2026-11-26T14:30'; Stored = '26 Nov 2026 14:30' }
}
$radioChoice = @{ Delivery = 'Express' }
$checkedBoxes = @('Taxable', 'Backorder2', 'Approved')

# ---------------------------------------------------------------- small steps

function Add-Label($label) {
    $x, $top, $size, $bold, $text = $label
    # The tool puts the first line's top 0.6 of the font size above the click.
    $y = $top + $size * 0.6
    [void](Send-App "drag 0 $x $y $x $y")
    [void](Send-App "type $text")
    [void](Send-App 'idle')
}

function Add-Field($field) {
    $tool, $name, $x, $y, $width, $height, $extra, $type = $field
    [void](Send-App "tool $tool")
    Send-App "drag 0 $x $y $($x + $width) $($y + $height)" -NoWait
    $dialog = Wait-Dialog 'New form field'
    Set-DialogText $dialog 0 $name
    if ($tool -like 'FieldDate*') { Select-DialogChoice $dialog "($extra)" }
    elseif ($null -ne $extra) { Set-DialogText $dialog 1 $extra }
    Invoke-Named $dialog 'OK'
    Wait-DialogGone 'New form field'
    [void](Send-App 'idle')
}

function Click-Field($field) {
    $x = $field.x + $field.width / 2
    $y = $field.y + $field.height / 2
    [void](Send-App "drag $($field.page) $x $y $x $y")
}

# Shows the whole page before a screenshot is taken.
function Save-PageScreenshot([string]$name) {
    Select-RibbonTab 'Home'
    Invoke-Named (Get-MainWindow) 'Fit Page'
    Start-Sleep -Milliseconds 1200
    Save-Screenshot (Join-Path $samples $name)
}

function Normalize([string]$text) { ($text -replace "`r`n", "`n").Trim() }

# What a field should hold once the form has been filled.
function Get-Expected($name, $onState) {
    if ($textValues.Contains($name)) { return @{ Value = ($textValues[$name] -replace '\\n', "`n"); Checked = $false } }
    if ($choiceValues.Contains($name)) { return @{ Value = $choiceValues[$name]; Checked = $false } }
    if ($dateFields.Contains($name)) { return @{ Value = $dateFields[$name].Stored; Checked = $false } }
    if ($radioChoice.ContainsKey($name)) { return @{ Value = $radioChoice[$name]; Checked = ($onState -eq $radioChoice[$name]) } }
    if ($checkedBoxes -contains $name) { return @{ Value = 'Yes'; Checked = $true } }
    return @{ Value = ''; Checked = $false }
}

function Read-FormFile([string]$pdf) {
    $json = [IO.Path]::ChangeExtension($pdf, '.dump.json')
    & $inspector dump-form $pdf $json | Out-Null
    $result = Get-Content $json -Raw -Encoding UTF8 | ConvertFrom-Json
    Remove-Item $json
    $result
}

# ---------------------------------------------------------------- 1. design

Write-Host '1. Designing the form on a blank page'
Remove-Item $blankForm, $filledForm -ErrorAction SilentlyContinue
Start-App
Invoke-Named (Get-MainWindow) 'Create ▾'
Start-Sleep -Milliseconds 500
foreach ($window in Get-AppWindows) {
    try { Invoke-Named $window 'Blank document…'; break } catch { }
}
$newDocument = Wait-Dialog 'New blank document'
Invoke-Named $newDocument 'Create'
Wait-DialogGone 'New blank document'
Assert-That ((Send-App 'info') -like 'pages=1 *') 'a blank one-page document was created'
[void](Send-App 'zoom 1')

[void](Send-App 'tool AddText')
foreach ($group in ($labels | Group-Object { "$($_[2])|$($_[3])" })) {
    $size, $bold = $group.Group[0][2], $group.Group[0][3]
    Select-ComboItem 'Size' "$size pt"
    Set-ToggleState 'Bold' $bold
    foreach ($label in $group.Group) { Add-Label $label }
}
Write-Host "   $($labels.Count) labels typed (three sizes, regular and bold, chosen in the options bar)"

foreach ($field in $fields) { Add-Field $field }
[void](Send-App 'tool Select')
[void](Send-App 'idle')
$designed = Get-AppFields
Assert-Equal $fields.Count $designed.Count 'every field drawn with the form tools exists'
Save-PageScreenshot 'form-1-designed.png'

Write-Host '2. Saving the design with Save As and closing the application'
Save-DocumentAs $blankForm
Assert-That ((Send-App 'info') -like '*dirty=False*') 'the document is clean after saving'
Stop-App
Assert-That $true 'the application closed without asking to save'

# ---------------------------------------------------------------- 2. reopen the blank form and check it

Write-Host '3. Opening the saved form in a new application instance'
Start-App $blankForm
[void](Send-App 'zoom 1')
[void](Send-App 'idle')
$reopened = Get-AppFields
Assert-Equal $fields.Count $reopened.Count 'the reopened form has all its fields'
$wrongType = @()
for ($i = 0; $i -lt $fields.Count; $i++) {
    $match = $reopened | Where-Object { $_.name -eq $fields[$i][1] -and $_.type -eq $fields[$i][7] }
    if (-not $match) { $wrongType += $fields[$i][1] }
}
Assert-Equal '' ($wrongType -join ', ') 'every field kept its name and control type'
$wrongFormat = @($reopened | Where-Object { $_.dateFormat -cne $(if ($dateFields.Contains($_.name)) { $dateFields[$_.name].Format } else { '' }) } | ForEach-Object { "$($_.name) '$($_.dateFormat)'" })
Assert-Equal '' ($wrongFormat -join ', ') 'the two date fields kept their formats and no other field is a date'
Assert-Equal 0 @($reopened | Where-Object { $_.isChecked -or ($_.value -ne '' -and $_.value -ne 'Off') }).Count 'every field of the saved design is empty'
$staticText = (Read-FormFile $blankForm).staticText[0]
Assert-That ($staticText -match 'PURCHASE ORDER' -and $staticText -match 'Unit price' -and $staticText -match 'Authorized by') 'the labels are part of the saved page'

# ---------------------------------------------------------------- 3. fill

Write-Host '4. Filling the form'
# A date field refuses text that is not a date, takes a typed date in another notation, and takes a picked moment.
Click-Field ($reopened | Where-Object name -eq 'OrderDate')
[void](Send-App 'type as soon as possible')
[void](Send-App 'idle')
Assert-Equal '' (Get-AppFields | Where-Object name -eq 'OrderDate').value 'a date field refuses text that is not a date'
foreach ($name in $dateFields.Keys) {
    Click-Field ($reopened | Where-Object name -eq $name)
    if ($dateFields[$name].Typed) { [void](Send-App "type $($dateFields[$name].Typed)") } else { [void](Send-App "pick $($dateFields[$name].Picked)") }
    [void](Send-App 'idle')
}
foreach ($name in $textValues.Keys) {
    Click-Field ($reopened | Where-Object name -eq $name)
    [void](Send-App "type $($textValues[$name])")
    [void](Send-App 'idle')
}
foreach ($name in $choiceValues.Keys) {
    Click-Field ($reopened | Where-Object name -eq $name)
    [void](Send-App "type $($choiceValues[$name])")
    [void](Send-App 'idle')
}
foreach ($name in $radioChoice.Keys) {
    # Pick one button, then change to the wanted one: only the last choice may stay selected.
    Click-Field ($reopened | Where-Object { $_.name -eq $name -and $_.onState -eq 'Overnight' })
    [void](Send-App 'idle')
    Click-Field ($reopened | Where-Object { $_.name -eq $name -and $_.onState -eq $radioChoice[$name] })
    [void](Send-App 'idle')
}
foreach ($name in $checkedBoxes) {
    Click-Field ($reopened | Where-Object name -eq $name)
    [void](Send-App 'idle')
}
# Tick a box and untick it again: it must end up empty.
foreach ($pass in 1..2) {
    Click-Field ($reopened | Where-Object name -eq 'Backorder1')
    [void](Send-App 'idle')
}
Save-PageScreenshot 'form-2-filled.png'

Write-Host '5. Saving the filled form under a new name and closing the application'
Save-DocumentAs $filledForm
Stop-App
Assert-That $true 'the application closed without asking to save'

# ---------------------------------------------------------------- 4. reopen and validate

Write-Host '6. Opening the filled form again and validating it'
Start-App $filledForm
[void](Send-App 'zoom 1')
[void](Send-App 'idle')
$shown = Get-AppFields
Save-PageScreenshot 'form-3-reopened.png'
Stop-App
$stored = Read-FormFile $filledForm

Assert-Equal $fields.Count $shown.Count 'the application shows every field of the filled form'
Assert-Equal $fields.Count $stored.fields.Count 'the engine reads every field from the file'
foreach ($source in @(@('application', $shown), @('file', $stored.fields))) {
    $where, $list = $source
    $problems = @()
    foreach ($field in $list) {
        $expected = Get-Expected $field.name $field.onState
        $value = Normalize $field.value
        if ($field.type -eq 'CheckBox' -and -not $expected.Checked) { if ($value -eq 'Off') { $value = '' } }
        if ($value -cne (Normalize $expected.Value)) { $problems += "$($field.name): value '$value' instead of '$($expected.Value)'" }
        if ([bool]$field.isChecked -ne $expected.Checked) { $problems += "$($field.name) [$($field.onState)]: checked=$($field.isChecked) instead of $($expected.Checked)" }
    }
    Assert-Equal '' ($problems -join '; ') "all $($list.Count) field values and tick states are correct in the $where"
}

# What a reader displays: flatten the form and look for the visible values in the page text.
$visible = ($stored.flattenedText[0] -replace '\s+', ' ')
$missing = @()
foreach ($name in $textValues.Keys) {
    foreach ($line in ($textValues[$name] -split '\\n')) {
        $latin = ($line -replace '[^\x20-\x7E]', '').Trim()
        if ($latin -and -not $visible.Contains($latin)) { $missing += "$name ('$latin')" }
    }
}
foreach ($name in $choiceValues.Keys) { if (-not $visible.Contains($choiceValues[$name])) { $missing += $name } }
foreach ($name in $dateFields.Keys) { if (-not $visible.Contains($dateFields[$name].Stored)) { $missing += $name } }
Assert-Equal '' ($missing -join ', ') 'every typed and chosen value is drawn on the page (field appearances were written)'

$design = Read-FormFile $blankForm
Assert-Equal 0 @($design.fields | Where-Object { $_.isChecked -or ($_.value -ne '' -and $_.value -ne 'Off') }).Count 'the blank design file on disk was not touched by filling'

Write-Host ''
Write-Host "RESULT: $script:Passes checks passed, $script:Failures failed"
Write-Host "Files: $blankForm"
Write-Host "       $filledForm"
exit $script:Failures
