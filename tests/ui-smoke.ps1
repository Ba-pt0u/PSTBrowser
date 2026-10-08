# UI smoke test of PstBrowser.exe (run by .github/workflows/ui-smoke.yml on a Windows runner):
# starts the application on a prepared case workspace, drives it through UI Automation
# (search, select a result, open an embedded message, search in attachment contents, native RTF message,
# "Investigation" column view preset in settings.json, column chooser) and takes screenshots.
# Also checks the attachment extraction worker of the application executable (PstBrowser.exe --extract-worker).
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
function Expect($condition, $message) {
    if (-not $condition) { throw "Vérification échouée : $message" }
    Log "  ok : $message"
}

# --- the extraction worker built into the application executable (binary protocol on stdin / stdout)
function Test-Worker($exe) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo $exe
    $psi.Arguments = '--extract-worker'
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    try {
        $bw = New-Object System.IO.BinaryWriter($p.StandardInput.BaseStream)
        $data = [System.Text.Encoding]::UTF8.GetBytes('Texte lu par le worker de l application')
        $bw.Write([byte]1); $bw.Write('note.txt'); $bw.Write([int]1000); $bw.Write([int]$data.Length); $bw.Write($data); $bw.Flush()
        $expected = 4 + 1 + 1 + 4 + $data.Length
        $all = New-Object System.Collections.Generic.List[byte]
        $buf = New-Object byte[] 4096
        while ($all.Count -lt $expected) {
            $t = $p.StandardOutput.BaseStream.ReadAsync($buf, 0, $buf.Length)
            if (-not $t.Wait(30000)) { throw "Le worker ne répond pas" }
            if ($t.Result -le 0) { throw "Le worker s'est arrêté" }
            $all.AddRange([byte[]]$buf[0..($t.Result - 1)])
        }
        $bytes = $all.ToArray()
        Expect ([BitConverter]::ToInt32($bytes, 0) -eq 0x31584250) "worker : en-tête de réponse valide"
        Expect ($bytes[4] -eq 0) "worker : statut « texte extrait »"
        $text = [System.Text.Encoding]::UTF8.GetString($bytes, 10, $bytes.Length - 10)
        Expect ($text -eq 'Texte lu par le worker de l application') "worker : texte restitué ($text)"
        $bw.Write([byte]0); $bw.Flush()
        Expect ($p.WaitForExit(15000)) "worker : arrêt propre à la fermeture de stdin"
    }
    finally { if (-not $p.HasExited) { $p.Kill() } }
}
Test-Worker $App

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

# Maximised window, no recent list, "Investigation" column view preset by settings.json
$settingsDir = Join-Path $env:LOCALAPPDATA 'PstBrowser'
New-Item -ItemType Directory -Force $settingsDir | Out-Null
$settingsFile = Join-Path $settingsDir 'settings.json'
'{ "Maximized": true, "RecentWorkspaces": [], "ColumnPreset": "Investigation" }' | Set-Content $settingsFile

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
    $r = (ById 'TxtResults').Current.Name
    Log ("Recherche « $text » : " + $r)
    return $r
}
function TopWindow($name) {
    for ($i = 0; $i -lt 20; $i++) {
        $w = $AE::RootElement.FindFirst($Scope::Descendants, (Cond $AE::NameProperty $name))
        if ($null -ne $w) { return $w }
        Start-Sleep -Milliseconds 500
    }
    return $null
}
function Headers {
    $win.FindAll($Scope::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::HeaderItem))) | ForEach-Object { $_.Current.Name }
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

    $r = Search 'tahoe'
    Expect ($r -match 'élément') 'résultats pour tahoe'
    $h = @(Headers)
    Log ('  en-têtes visibles : ' + ($h -join ' | '))
    Expect (($h -contains 'Envoyé le') -and ($h -contains 'Reçu le')) 'vue Investigation appliquée depuis settings.json (colonnes Reçu le / Envoyé le)'
    SelectRow 0
    Shot '2-recherche-tahoe'

    $null = Search 'objet:"This is the subject"'
    SelectRow 0
    Shot '3-image-integree'

    $null = Search 'objet:"Outer mail"'
    SelectRow 0
    Shot '4-message-avec-message-joint'
    $inner = $win.FindAll($Scope::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Button))) |
             Where-Object { $_.Current.Name -like '*Inner mail*' } | Select-Object -First 1
    if ($null -eq $inner) { throw "Bouton du message joint introuvable" }
    $inner.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep 5
    Log ("  message joint affiché : " + (ById 'TxtSubject').Current.Name)
    Shot '4b-message-joint-ouvert'

    $null = Search 'objet:HtmlSampleEmail prince'
    SelectRow 0
    Shot '5-html-encapsule-rtf'


    # Native RTF message (formatting rendered by RtfPipe)
    $null = Search 'objet:RtfSampleEmail'
    SelectRow 0
    Shot '5b-rtf-mis-en-forme'

    # Search in the contents of attachments (tahoe.xls inside enron.pst)
    $r = Search 'pjtexte:proaliance'
    Expect ($r -match '— 1 élément') 'pjtexte:proaliance : 1 message (tahoe.xls)'
    SelectRow 0
    Shot '5c-pjtexte'
    $marked = $win.FindAll($Scope::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Button))) |
              Where-Object { $_.Current.Name -like 'Contient les termes recherchés*' } | Select-Object -First 1
    Expect ($null -ne $marked) 'la pièce jointe qui contient les termes est signalée dans le lecteur'
    try {
        $marked.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep 1
        $item = $AE::RootElement.FindFirst($Scope::Descendants, (Cond $AE::NameProperty 'Aperçu du texte'))
        if ($null -eq $item) { throw "entrée de menu « Aperçu du texte » introuvable" }
        $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $preview = TopWindow 'Aperçu du texte — tahoe.xls'
        if ($null -eq $preview) { throw "fenêtre d'aperçu introuvable" }
        Start-Sleep 5
        Shot '5d-apercu-texte-piece-jointe'
        $preview.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        Start-Sleep 1
    }
    catch { Log ("  AVERTISSEMENT (aperçu du texte) : " + $_.Exception.Message); try { Shot '5e-apercu-echec' } catch { } }

    $r = Search '"BALANCING AGREEMENT"'
    Expect ($r -match '— [1-9][0-9]* élément') '« BALANCING AGREEMENT » trouvé dans le document Word OBA_2760.doc'
    SelectRow 0
    Shot '5f-doc-balancing-agreement'

    # Column chooser: back to the "Standard" view
    try {
        (ById 'BtnColumns').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $colDlg = TopWindow 'Colonnes de la liste des messages'
        if ($null -eq $colDlg) { throw "fenêtre des colonnes introuvable" }
        Start-Sleep 1
        Shot '5g-colonnes'
        $std = $colDlg.FindFirst($Scope::Descendants, (Cond $AE::AutomationIdProperty 'BtnStandard'))
        $std.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $ok = $colDlg.FindFirst($Scope::Descendants, (Cond $AE::NameProperty 'OK'))
        $ok.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep 2
        $h = @(Headers)
        Log ('  en-têtes visibles : ' + ($h -join ' | '))
        Expect (-not ($h -contains 'Envoyé le')) 'vue Standard : la colonne « Envoyé le » est masquée'
        Shot '5h-vue-standard'
        $columnsChanged = $true
    }
    catch { Log ("  ÉCHEC (colonnes) : " + $_.Exception.Message); throw }

    # Sources window (modal)
    $btn = $win.FindAll($Scope::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Button))) |
           Where-Object { $_.Current.Name -like 'Sources*' } | Select-Object -First 1
    if ($null -ne $btn) {
        $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep 3
        Shot '6-sources'
        $dlg = $win.FindFirst($Scope::Children, (Cond $AE::NameProperty "Sources du dossier d'affaire"))
        if ($null -eq $dlg) { $dlg = $AE::RootElement.FindFirst($Scope::Descendants, (Cond $AE::NameProperty "Sources du dossier d'affaire")) }
        if ($null -ne $dlg) {
            $chk = $dlg.FindFirst($Scope::Descendants, (Cond $AE::AutomationIdProperty 'ChkAttachments'))
            Expect ($null -ne $chk) "case « Indexer le contenu des pièces jointes » présente dans la fenêtre Sources"
            $state = $chk.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
            Expect ($state.ToString() -eq 'On') 'la case est cochée par défaut'
            $dlg.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep 1
        }
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
if (-not $failed -and $columnsChanged) {
    # the column layout chosen in the dialog is stored per user in settings.json
    $saved = Get-Content $settingsFile -Raw | ConvertFrom-Json
    Log ("settings.json : ColumnPreset = " + $saved.ColumnPreset + ", " + @($saved.ListColumns).Count + " colonnes enregistrées")
    if ($saved.ColumnPreset -ne 'Standard') { Log "ÉCHEC : la vue choisie n'est pas enregistrée dans settings.json"; $failed = $true }
}
if ($failed) { exit 1 }
