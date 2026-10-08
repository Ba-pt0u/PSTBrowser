# UI smoke test of PstBrowser.exe (run by .github/workflows/ui-smoke.yml on a Windows runner):
# starts the application on a prepared case workspace, drives it through UI Automation
# (search, select a result, open an embedded message) and takes screenshots.
param(
    [Parameter(Mandatory)] [string] $App,
    [Parameter(Mandatory)] [string] $Workspace,
    [Parameter(Mandatory)] [string] $OutDir
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null
$log = Join-Path $OutDir 'ui-smoke.log'
function Log($m) { $m | Tee-Object -FilePath $log -Append | Write-Host }

function Shot($name) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save((Join-Path $OutDir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Log "capture : $name.png ($($b.Width)x$($b.Height))"
}

$AE = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
function Cond($prop, $value) { New-Object System.Windows.Automation.PropertyCondition($prop, $value) }

# Maximised window, no recent list
$settingsDir = Join-Path $env:LOCALAPPDATA 'PstBrowser'
New-Item -ItemType Directory -Force $settingsDir | Out-Null
'{ "Maximized": true, "RecentWorkspaces": [] }' | Set-Content (Join-Path $settingsDir 'settings.json')

$proc = Start-Process -FilePath $App -ArgumentList "`"$Workspace`"" -PassThru
$win = $null
for ($i = 0; $i -lt 60 -and $null -eq $win; $i++) {
    Start-Sleep 1
    if ($proc.HasExited) { throw "L'application s'est arrêtée au démarrage (code $($proc.ExitCode))" }
    $win = $AE::RootElement.FindFirst($Scope::Children, (Cond $AE::ProcessIdProperty $proc.Id))
}
if ($null -eq $win) { throw "Fenêtre principale introuvable" }
Start-Sleep 8
Log "Fenêtre : $($win.Current.Name)"

function ById($id) {
    for ($i = 0; $i -lt 20; $i++) {
        $e = $win.FindFirst($Scope::Descendants, (Cond $AE::AutomationIdProperty $id))
        if ($null -ne $e) { return $e }
        Start-Sleep -Milliseconds 500
    }
    throw "Élément introuvable : $id"
}
function Dialogs {
    # Any other top-level window of the process (error message boxes…)
    $AE::RootElement.FindAll($Scope::Children, (Cond $AE::ProcessIdProperty $proc.Id)) |
        Where-Object { $_.Current.AutomationId -ne $win.Current.AutomationId -or $_.Current.Name -ne $win.Current.Name } |
        ForEach-Object { $_.Current.Name }
}
function Search($text) {
    $q = ById 'TxtQuery'
    $q.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
    $q.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep 3
    Log ("Recherche « $text » : " + (ById 'TxtResults').Current.Name)
}
function SelectRow($index) {
    $list = ById 'MessageList'
    $rows = $list.FindAll($Scope::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::DataItem)))
    Log "  lignes visibles : $($rows.Count)"
    if ($rows.Count -le $index) { throw "Pas assez de résultats" }
    $rows[$index].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep 6
    Log ("  objet affiché : " + (ById 'TxtSubject').Current.Name)
}

try {
    Shot '1-demarrage'

    Search 'tahoe'
    SelectRow 0
    Shot '2-recherche-tahoe'

    Search 'objet:"This is the subject"'
    SelectRow 0
    Shot '3-image-integree'

    Search 'objet:"Outer mail"'
    SelectRow 0
    Shot '4-message-avec-message-joint'
    $inner = $win.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.AndCondition(
        (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Button)),
        (Cond $AE::NameProperty '✉  Inner mail'))))
    if ($null -eq $inner) {
        # the button's name is its TextBlock content: search more loosely
        $inner = $win.FindAll($Scope::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Button))) |
                 Where-Object { $_.Current.Name -like '*Inner mail*' } | Select-Object -First 1
    }
    if ($null -eq $inner) { throw "Bouton du message joint introuvable" }
    $inner.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep 5
    Log ("  message joint affiché : " + (ById 'TxtSubject').Current.Name)
    Shot '4b-message-joint-ouvert'

    Search 'objet:HtmlSampleEmail prince'
    SelectRow 0
    Shot '5-html-encapsule-rtf'

    # Sources window (modal)
    $btn = $win.FindAll($Scope::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Button))) |
           Where-Object { $_.Current.Name -eq 'Sources…' } | Select-Object -First 1
    if ($null -ne $btn) {
        $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep 3
        Shot '6-sources'
        $dlg = $win.FindFirst($Scope::Children, (Cond $AE::NameProperty "Sources du dossier d'affaire"))
        if ($null -eq $dlg) { $dlg = $AE::RootElement.FindFirst($Scope::Descendants, (Cond $AE::NameProperty "Sources du dossier d'affaire")) }
        if ($null -ne $dlg) { $dlg.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep 1 }
        else { Log "  fenêtre Sources introuvable" }
    }

    $d = @(Dialogs)
    if ($d.Count -gt 0) { Log ("Autres fenêtres ouvertes : " + ($d -join ' | ')) }
    if ($proc.HasExited) { throw "L'application s'est arrêtée pendant le test" }
    Log 'SUCCÈS'
}
catch {
    Log ("ÉCHEC : " + $_.Exception.Message)
    try { Shot '9-echec' } catch { }
    $failed = $true
}
finally {
    if (-not $proc.HasExited) {
        $proc.CloseMainWindow() | Out-Null
        if (-not $proc.WaitForExit(10000)) { $proc.Kill() }
    }
}
if ($failed) { exit 1 }
