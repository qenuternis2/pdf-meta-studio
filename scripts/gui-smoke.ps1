# Дымовой тест GUI через UI Automation (Windows PowerShell 5.1):
# главный экран → «Change meta info» → выбор PDF → правка названия → «Проверить и сохранить» →
# «Сохранить копию…» → проверка результата независимым запуском worker.
# Пишет gui-smoke.log и снимки экрана в $OutDir. Код возврата 1 при сбое шага.
param(
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$Pdf,
    [Parameter(Mandatory)] [string]$OutDir,
    [string]$NewTitle = 'Проверка GUI ✓'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$logPath = Join-Path $OutDir 'gui-smoke.log'
Set-Content -Path $logPath -Value '' -Encoding UTF8

function Log([string]$m) { Write-Output $m; Add-Content -Path $logPath -Value $m -Encoding UTF8 }
function Shot([string]$name) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save((Join-Path $OutDir "$name.png"))
    $g.Dispose(); $bmp.Dispose()
}
function WaitFor([scriptblock]$f, [int]$sec = 30) {
    $until = [DateTime]::Now.AddSeconds($sec)
    while ([DateTime]::Now -lt $until) {
        try { $r = & $f; if ($r) { return $r } } catch { }
        Start-Sleep -Milliseconds 300
    }
    return $null
}
function Cond($prop, $value) { New-Object System.Windows.Automation.PropertyCondition($prop, $value) }
function AndCond($a, $b) { New-Object System.Windows.Automation.AndCondition($a, $b) }
function ByName($root, [string]$name) { $root.FindFirst($TS::Descendants, (Cond $AE::NameProperty $name)) }
function Press($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function SetValue($el, [string]$v) { $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($v) }
function Dialog([int]$processId) {
    $AE::RootElement.FindFirst($TS::Children,
        (AndCond (Cond $AE::ProcessIdProperty $processId) (Cond $AE::ClassNameProperty '#32770')))
}
function DumpTree($root, [int]$max = 120) {
    $all = $root.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $n = 0
    foreach ($e in $all) {
        if ($n -ge $max) { break }
        $c = $e.Current
        if ($c.Name) { Log ("    [{0}] {1} id={2}" -f $c.ControlType.ProgrammaticName, $c.Name, $c.AutomationId); $n++ }
    }
}

$proc = $null
$step = 'start'
try {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process -FilePath $Exe -PassThru
    $hwnd = WaitFor { $proc.Refresh(); if ($proc.HasExited) { throw 'process exited' }; $proc.MainWindowHandle -ne 0 } 60
    if (-not $hwnd) { throw 'главное окно не появилось за 60 с' }
    Start-Sleep -Seconds 2
    $win = $AE::FromHandle($proc.MainWindowHandle)
    Log ("OK  запуск: окно «{0}» за {1:N1} с" -f $win.Current.Name, $sw.Elapsed.TotalSeconds)

    $step = 'home'
    $buttons = $win.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::Button))
    $visible = @($buttons | Where-Object { -not $_.Current.IsOffscreen })
    Log ("OK  главный экран: видимых кнопок {0}: {1}" -f $visible.Count, (($visible | ForEach-Object { $_.Current.Name }) -join ' | '))
    $open = WaitFor { $visible | Where-Object { $_.Current.Name -like 'Change meta info*' } | Select-Object -First 1 } 5
    if (-not $open) { throw 'кнопка Change meta info не найдена' }
    Shot '1-home'

    $step = 'open-dialog'
    Press $open
    $dlg = WaitFor { Dialog $proc.Id } 20
    if (-not $dlg) { throw 'диалог выбора файла не появился' }
    Log ("OK  диалог выбора файла: «{0}»" -f $dlg.Current.Name)
    $edit = WaitFor { $dlg.FindFirst($TS::Descendants, (AndCond (Cond $AE::AutomationIdProperty '1148') (Cond $AE::ControlTypeProperty $CT::Edit))) } 10
    SetValue $edit $Pdf
    Press ($dlg.FindFirst($TS::Children, (AndCond (Cond $AE::AutomationIdProperty '1') (Cond $AE::ControlTypeProperty $CT::Button))))

    $step = 'editor'
    $review = WaitFor { ByName $win 'Проверить и сохранить' } 60
    if (-not $review) { throw 'редактор не открылся' }
    Start-Sleep -Seconds 1
    $sections = ByName $win 'Разделы метаданных'
    $items = $sections.FindAll($TS::Children, (Cond $AE::ControlTypeProperty $CT::ListItem))
    Log ("OK  редактор открыт за {0:N1} с; разделы: {1}" -f $sw.Elapsed.TotalSeconds, (($items | ForEach-Object { $_.Current.Name }) -join ' | '))
    Shot '2-editor'

    $step = 'edit-title'
    $title = WaitFor { $win.FindFirst($TS::Descendants, (AndCond (Cond $AE::ControlTypeProperty $CT::Edit) (Cond $AE::NameProperty 'Название документа'))) } 10
    if (-not $title) { throw 'поле «Название документа» не найдено' }
    $before = $title.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    SetValue $title $NewTitle
    Log ("OK  название: «{0}» → «{1}»" -f $before, $NewTitle)

    $step = 'review'
    Press $review
    $saveCopy = WaitFor { $b = ByName $win 'Сохранить копию…'; if ($b -and $b.Current.IsEnabled) { $b } } 60
    if (-not $saveCopy) { throw 'кнопка «Сохранить копию…» не стала доступной после проверки' }
    $rows = ByName $win 'Список изменений'
    $texts = @()
    if ($rows) { $texts = @($rows.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::Text)) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) }
    Log ("OK  «Было → Станет»: {0}" -f (($texts | Select-Object -First 16) -join ' | '))
    Shot '3-review'

    $step = 'save-copy'
    $target = Join-Path $OutDir 'gui_meta.pdf'
    Remove-Item $target -ErrorAction SilentlyContinue
    Press $saveCopy
    $sdlg = WaitFor { Dialog $proc.Id } 20
    if (-not $sdlg) { throw 'диалог сохранения не появился' }
    $sedit = WaitFor { $sdlg.FindFirst($TS::Descendants, (AndCond (Cond $AE::AutomationIdProperty '1001') (Cond $AE::ControlTypeProperty $CT::Edit))) } 10
    SetValue $sedit $target
    Press ($sdlg.FindFirst($TS::Children, (AndCond (Cond $AE::AutomationIdProperty '1') (Cond $AE::ControlTypeProperty $CT::Button))))
    # После записи приложение показывает окно «Готово» (или «Файл не сохранён») — тоже класс #32770.
    $box = WaitFor { $d = Dialog $proc.Id; if ($d -and $d.Current.Name -ne $sdlg.Current.Name) { $d } } 120
    if (-not $box) { throw 'после сохранения не появилось сообщение о результате' }
    $boxText = @($box.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::Text)) | ForEach-Object { $_.Current.Name }) -join ' '
    Shot '4-saved'
    if ($box.Current.Name -ne 'Готово') { throw ("сообщение «{0}»: {1}" -f $box.Current.Name, $boxText) }
    if (-not (Test-Path $target)) { throw 'файл копии не появился' }
    Log ("OK  копия записана: {0} ({1} байт); сообщение: {2}" -f $target, (Get-Item $target).Length, $boxText)
    $okBtn = $box.FindFirst($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::Button))
    if ($okBtn) { Press $okBtn }

    $step = 'verify'
    $worker = Join-Path (Split-Path $Exe) 'pdfmeta-worker.exe'
    $req = (@{ id = 1; cmd = 'open'; path = $target } | ConvertTo-Json -Compress)
    $psi = New-Object Diagnostics.ProcessStartInfo $worker
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.UseShellExecute = $false
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $wp = [Diagnostics.Process]::Start($psi)
    $utf8 = New-Object Text.UTF8Encoding $false
    $stdin = New-Object IO.StreamWriter($wp.StandardInput.BaseStream, $utf8)
    $stdin.WriteLine($req); $stdin.WriteLine('{"id":2,"cmd":"shutdown"}'); $stdin.Close()
    $out = $wp.StandardOutput.ReadToEnd(); $wp.WaitForExit()
    $result = ($out -split "`n" | Where-Object { $_ -match '"id":1' -and $_ -match '"type":"result"' } | Select-Object -First 1) | ConvertFrom-Json
    $t = ($result.data.info.entries | Where-Object { $_.key -eq '/Title' }).value
    if ($t -ne $NewTitle) { throw "в копии /Title = «$t», ожидалось «$NewTitle»" }
    Log ("OK  независимое чтение копии: /Title = «{0}»" -f $t)

    $step = 'close'
    $proc.CloseMainWindow() | Out-Null
    $closed = WaitFor { $proc.Refresh(); $proc.HasExited } 15
    Log ("{0}  закрытие окна после сохранения{1}" -f $(if ($closed) { 'OK ' } else { 'WARN' }), $(if ($closed) { '' } else { ': окно не закрылось (возможен запрос о несохранённых правках)' }))
    if (-not $closed) { Shot '5-close' }
    Log 'GUI SMOKE PASSED'
    exit 0
}
catch {
    Log ("FAIL шаг {0}: {1}" -f $step, $_.Exception.Message)
    try { Shot "fail-$step" } catch { }
    try { if ($proc -and -not $proc.HasExited) { Log '  элементы окна:'; DumpTree ($AE::FromHandle($proc.MainWindowHandle)) } } catch { }
    exit 1
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
