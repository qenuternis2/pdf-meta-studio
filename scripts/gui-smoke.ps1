# Дымовой тест GUI через UI Automation (Windows PowerShell 5.1):
# главный экран → «Change meta info» → выбор PDF → правка названия и автора аннотации → «Проверить и сохранить» →
# «Сохранить копию…» → проверка результата независимым запуском worker.
# Пишет gui-smoke.log и снимки экрана в $OutDir. Код возврата 1 при сбое шага.
param(
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$Pdf,
    [Parameter(Mandatory)] [string]$OutDir,
    [string]$NewTitle = 'Проверка GUI ✓',
    [string]$NewAuthor = 'Автор из GUI ✓',
    [switch]$KeyboardChecks,
    [switch]$LayoutChecks,
    [switch]$StressChecks,
    [switch]$CalendarChecks,
    [ValidateSet('', 'light', 'dark')][string]$ExpectedTheme = ''
)
$ErrorActionPreference = 'Stop'
# Shell file dialogs require native separators even when CI supplied mixed paths.
$Exe = [IO.Path]::GetFullPath($Exe).Replace('/', '\')
$Pdf = [IO.Path]::GetFullPath($Pdf).Replace('/', '\')
$OutDir = [IO.Path]::GetFullPath($OutDir).Replace('/', '\')
# Prefer Windows PowerShell modules when this process is launched from PowerShell 7.
$env:PSModulePath = (Join-Path $PSHOME 'Modules') + [IO.Path]::PathSeparator + $env:PSModulePath
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AcceptanceFocus {
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr id);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr h);
    delegate bool EnumChild(IntPtr h, IntPtr arg);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumChild cb, IntPtr arg);
    public static void Activate(IntPtr window, int controlId) {
        uint current = GetCurrentThreadId();
        uint foreground = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        uint target = GetWindowThreadProcessId(window, IntPtr.Zero);
        bool attachedForeground = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
        bool attachedTarget = target != 0 && target != current && target != foreground && AttachThreadInput(current, target, true);
        try {
            BringWindowToTop(window);
            SetForegroundWindow(window);
            if (controlId != 0) {
                IntPtr input = IntPtr.Zero;
                EnumChildWindows(window, delegate(IntPtr h, IntPtr arg) {
                    if (GetDlgCtrlID(h) == controlId) { input = h; return false; }
                    return true;
                }, IntPtr.Zero);
                if (input != IntPtr.Zero) SetFocus(input);
            }
        } finally {
            if (attachedTarget) AttachThreadInput(current, target, false);
            if (attachedForeground) AttachThreadInput(current, foreground, false);
        }
    }
}
'@
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
function Value($el) { $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function FocusId($el) { (@($el.GetRuntimeId()) -join '.') }
function Keys([string]$keys) { [System.Windows.Forms.SendKeys]::SendWait($keys); Start-Sleep -Milliseconds 70 }
function AssertTabCycle($anchor, [string]$keys) {
    $anchor.SetFocus()
    $start = FocusId $anchor
    $visited = @()
    for ($i = 0; $i -lt 128; $i++) {
        Keys $keys
        $focus = $AE::FocusedElement
        if ($focus.Current.ProcessId -ne $proc.Id) { throw 'Keyboard focus left the application' }
        $id = FocusId $focus
        if ($id -eq $start -and $visited.Count -gt 0) {
            Log ("OK  keyboard cycle {0}: {1} focus targets; {2}" -f $keys, $visited.Count, ($visited -join ' | '))
            return
        }
        $visited += $focus.Current.Name
    }
    throw ("Keyboard focus did not return within 128 steps: {0}; visited: {1}" -f $keys, ($visited -join ' | '))
}
# Диалоги (выбор файла, MessageBox) принадлежат главному окну и в дереве UIA лежат под ним.
function Dialog([int]$processId) {
    $cls = Cond $AE::ClassNameProperty '#32770'
    $main = $AE::RootElement.FindFirst($TS::Children, (AndCond (Cond $AE::ProcessIdProperty $processId) (Cond $AE::ControlTypeProperty $CT::Window)))
    if ($main) { $d = $main.FindFirst($TS::Children, $cls); if ($d) { return $d } }
    $AE::RootElement.FindFirst($TS::Children, (AndCond (Cond $AE::ProcessIdProperty $processId) $cls))
}
# Поле имени файла в системном диалоге не всегда видно через UI Automation —
# вводим путь с клавиатуры, как пользователь: фокус по умолчанию стоит в поле имени.
function TypeIntoDialog($dlg, [string]$text) {
    try { $dlg.SetFocus() } catch { }
    # Shell controls may be exposed only as panes on ARM64; focus the native filename edit.
    [AcceptanceFocus]::Activate([IntPtr]$dlg.Current.NativeWindowHandle, 1148)
    Start-Sleep -Milliseconds 500
    # Prefer the Shell dialog's filename control instead of relying on initial focus.
    $fileName = $dlg.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty '1148'))
    if ($fileName) { try { $fileName.SetFocus() } catch { } }
    $escaped = [regex]::Replace($text, '[+^%~(){}\[\]]', '{$0}')
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    [System.Windows.Forms.SendKeys]::SendWait($escaped)
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
}
# Заголовки разделов редактора. Свёрнутые (Collapsed) элементы в дерево UI Automation не попадают,
# поэтому виден ровно один заголовок — иначе разделы рисуются друг поверх друга.
$SectionHeadings = [ordered]@{
    'Основные' = 'Основные сведения'; 'Даты и ПО' = 'Даты и программы'
    'Все теги' = 'Все теги документа'; 'Объекты PDF' = 'Метаданные объектов'
}
function AssertOnlySection($win, [string]$section) {
    $shown = @($SectionHeadings.Values | Where-Object { ByName $win $_ })
    $want = $SectionHeadings[$section]
    if ($shown.Count -ne 1 -or $shown[0] -ne $want) {
        throw ("раздел «{0}»: видны заголовки [{1}], ожидался только «{2}»" -f $section, ($shown -join ', '), $want)
    }
}
function CountByName($root, [string]$name) { @($root.FindAll($TS::Descendants, (Cond $AE::NameProperty $name))).Count }
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
    if ($env:GITHUB_ACTIONS -eq 'true' -and $env:RUNNER_ARCH -eq 'ARM64') {
        & (Join-Path $PSScriptRoot 'prepare-windows-desktop.ps1') -OutDir $OutDir
    }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process -FilePath $Exe -PassThru
    $hwnd = WaitFor { $proc.Refresh(); if ($proc.HasExited) { throw 'process exited' }; $proc.MainWindowHandle -ne 0 } 60
    if (-not $hwnd) { throw 'главное окно не появилось за 60 с' }
    Start-Sleep -Seconds 2
    $win = $AE::FromHandle($proc.MainWindowHandle)
    [AcceptanceFocus]::Activate($proc.MainWindowHandle, 0)
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
    TypeIntoDialog $dlg $Pdf

    $step = 'editor'
    $review = WaitFor { ByName $win 'Проверить и сохранить' } 60
    if (-not $review) { throw 'редактор не открылся' }
    Start-Sleep -Seconds 1
    $sections = ByName $win 'Разделы метаданных'
    $items = $sections.FindAll($TS::Children, (Cond $AE::ControlTypeProperty $CT::ListItem))
    Log ("OK  редактор открыт за {0:N1} с; разделы: {1}" -f $sw.Elapsed.TotalSeconds, (($items | ForEach-Object { $_.Current.Name }) -join ' | '))
    Shot '2-editor'
    if ($ExpectedTheme) {
        $image = [Drawing.Bitmap]::FromFile((Join-Path $OutDir '2-editor.png'))
        try {
            $bounds = $win.Current.BoundingRectangle
            $pixel = $image.GetPixel([Math]::Min($image.Width - 1, [int]$bounds.Right - 40), [Math]::Max(0, [int]$bounds.Top + 50))
            $brightness = [int]$pixel.R + [int]$pixel.G + [int]$pixel.B
            if (($ExpectedTheme -eq 'dark' -and $brightness -ge 384) -or ($ExpectedTheme -eq 'light' -and $brightness -le 600)) {
                throw "Requested $ExpectedTheme theme did not produce the expected background: $pixel"
            }
            Log "OK  observed $ExpectedTheme application background: $pixel"
        } finally { $image.Dispose() }
    }

    if ($LayoutChecks) {
        $transform = $win.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
        $transform.Move(0, 0)
        foreach ($size in @(@(640, 480), @(1000, 700))) {
            $transform.Resize($size[0], $size[1])
            Start-Sleep -Milliseconds 500
            foreach ($section in @('Основные', 'Даты и ПО', 'Все теги', 'Объекты PDF')) {
                ($items | Where-Object { $_.Current.Name -eq $section } | Select-Object -First 1).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                Start-Sleep -Milliseconds 150
                $bounds = $win.Current.BoundingRectangle
                $buttonBounds = $review.Current.BoundingRectangle
                if ($review.Current.IsOffscreen -or $buttonBounds.Left -lt $bounds.Left -or $buttonBounds.Right -gt $bounds.Right -or $buttonBounds.Bottom -gt $bounds.Bottom) {
                    throw "Review action is clipped at $($size -join 'x') in $section"
                }
                Shot ("layout-{0}-{1}" -f ($size -join 'x'), $section)
            }
            Log ("OK  resize {0}: all sections and review action accessible" -f ($size -join 'x'))
        }
    }

    $step = 'sections'
    foreach ($name in @('Все теги', 'Объекты PDF', 'Даты и ПО', 'Основные')) {
        $item = $items | Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1
        if (-not $item) { throw "в списке разделов нет «$name»" }
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $ok = WaitFor { AssertOnlySection $win $name; $true } 5
        if (-not $ok) { AssertOnlySection $win $name }
        $marks = CountByName $win ' · изменено'
        if ($marks -ne 0) { throw "раздел «$name» без правок: пометок «изменено» $marks" }
        if ($name -eq 'Все теги') { Shot '2b-all-tags' }
    }
    Log 'OK  разделы: при выборе каждого виден только его заголовок'

    $step = 'clean-state'
    $marks = CountByName $win ' · изменено'
    $deleted = CountByName $win 'Значение удалено. Нажмите кнопку корзины ещё раз, чтобы восстановить.'
    if ($marks -ne 0 -or $deleted -ne 0) { throw "без правок видны пометки: «изменено» ×$marks, «удалено» ×$deleted" }
    Log 'OK  без правок нет пометок «изменено» и «удалено»'

    if ($CalendarChecks) {
        $step = 'calendar'
        ($items | Where-Object { $_.Current.Name -eq 'Даты и ПО' } | Select-Object -First 1).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $components = WaitFor { $win.FindFirst($TS::Descendants, (AndCond (Cond $AE::ControlTypeProperty $CT::Group) (Cond $AE::NameProperty 'Компоненты даты, точность и часовой пояс'))) } 10
        if (-not $components) { throw 'Date component expander is missing' }
        $components.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        $calendarInput = WaitFor { ByName $win 'Календарь: Дата создания' } 10
        if (-not $calendarInput) { throw 'Creation date calendar is missing' }
        $originalDate = Value (ByName $win 'Исходная дата: Дата создания')
        $calendarPattern = $calendarInput.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $calendarPattern.Expand()
        $popup = WaitFor { $AE::RootElement.FindFirst($TS::Descendants, (AndCond (Cond $AE::ProcessIdProperty $proc.Id) (Cond $AE::ControlTypeProperty $CT::Calendar))) } 10
        if (-not $popup) { throw 'Calendar popup did not open' }
        $bounds = $popup.Current.BoundingRectangle
        $screen = [Windows.Forms.SystemInformation]::VirtualScreen
        if ($popup.Current.IsOffscreen -or $bounds.Left -lt $screen.Left -or $bounds.Top -lt $screen.Top -or $bounds.Right -gt $screen.Right -or $bounds.Bottom -gt $screen.Bottom) { throw 'Calendar popup is clipped by the desktop' }
        Shot 'calendar-popup'
        Keys '{ESC}'
        if (-not (WaitFor { $calendarPattern.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed } 5)) { throw 'Escape did not close the calendar popup' }
        if ((Value (ByName $win 'Исходная дата: Дата создания')) -ne $originalDate -or (CountByName $win ' · изменено') -ne 0) { throw 'Calendar inspection changed the document date' }
        $components.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
        ($items | Where-Object { $_.Current.Name -eq 'Основные' } | Select-Object -First 1).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Log 'OK  calendar opens within the desktop, Escape closes it and original date precision/value remains unchanged'
    }

    if ($StressChecks) {
        ($items | Where-Object { $_.Current.Name -eq 'Все теги' } | Select-Object -First 1).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $search = ByName $win 'Поиск по тегам: имя, пространство имён или значение'
        $timer = [Diagnostics.Stopwatch]::StartNew()
        SetValue $search 'Tag09999'
        $found = WaitFor { $win.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::TreeItem)) | Where-Object { $_.Current.Name -like 'load:Tag09999,*' } | Select-Object -First 1 } 15
        if (-not $found) { throw 'The last of 10,000 tags was not reachable by search' }
        Log ("OK  10,000-tag search completed in {0:N1} s" -f $timer.Elapsed.TotalSeconds)
        SetValue $search 'LongValue'
        $long = WaitFor { $win.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::TreeItem)) | Where-Object { $_.Current.Name -like 'load:LongValue,*' } | Select-Object -First 1 } 15
        if (-not $long) { throw 'The multiline stress value was not found' }
        $long.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $input = WaitFor { ByName $win 'Значение выбранного тега' } 15
        if (-not $input -or (Value $input).Length -ne 1048576) { throw 'The 1 MiB multiline value was truncated' }
        $stressValue = Value $input
        $input.SetFocus(); Keys '{TAB}'; Keys '+{TAB}'
        if ((FocusId $AE::FocusedElement) -ne (FocusId $input)) { throw 'Keyboard navigation failed in the long-value editor' }
        Shot 'stress-long-value'
        Log 'OK  1 MiB multiline value is complete and keyboard navigation remains available'
        SetValue $search ''
        ($items | Where-Object { $_.Current.Name -eq 'Основные' } | Select-Object -First 1).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    }

    $step = 'edit-title'
    $title = WaitFor { $win.FindFirst($TS::Descendants, (AndCond (Cond $AE::ControlTypeProperty $CT::Edit) (Cond $AE::NameProperty 'Название документа'))) } 10
    if (-not $title) { throw 'поле «Название документа» не найдено' }
    $before = $title.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    SetValue $title $NewTitle
    Log ("OK  название: «{0}» → «{1}»" -f $before, $NewTitle)
    $marks = WaitFor { $n = CountByName $win ' · изменено'; if ($n -eq 1) { $n } } 5
    if (-not $marks) { throw ("после правки названия пометок «изменено»: {0}, ожидалась 1" -f (CountByName $win ' · изменено')) }
    if ($KeyboardChecks) {
        AssertTabCycle $title '{TAB}'
        AssertTabCycle $title '+{TAB}'
        if ((Value $title) -ne $NewTitle) { throw 'Keyboard navigation changed the title without editing it' }
        if ((Value (ByName $win 'Язык поля: Название документа')) -ne 'x-default') { throw 'Preview or keyboard navigation cleared the selected language' }
        $title.SetFocus(); Keys '^z'
        if (-not (WaitFor { (Value $title) -eq $before } 5)) { throw 'Ctrl+Z did not restore the original title' }
        Keys '^y'
        if (-not (WaitFor { (Value $title) -eq $NewTitle } 5)) { throw 'Ctrl+Y did not redo the title' }
        Keys '^z'; Keys '^+z'
        if (-not (WaitFor { (Value $title) -eq $NewTitle } 5)) { throw 'Ctrl+Shift+Z did not redo the title' }
        Log 'OK  Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z with text input focused'
    }

    $step = 'edit-annotation'
    $objItem = $items | Where-Object { $_.Current.Name -eq 'Объекты PDF' } | Select-Object -First 1
    $objItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $author = WaitFor { $win.FindFirst($TS::Descendants, (AndCond (Cond $AE::ControlTypeProperty $CT::Edit) (Cond $AE::NameProperty 'Автор — аннотация, Страница 1 · Text'))) } 10
    if (-not $author) { throw 'поле «Автор» аннотации не найдено в разделе «Объекты PDF»' }
    $beforeAuthor = $author.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    SetValue $author $NewAuthor
    $marks = WaitFor { $n = CountByName $win ' · изменено'; if ($n -eq 1) { $n } } 5
    if (-not $marks) { throw ("после правки автора аннотации пометок «изменено»: {0}, ожидалась 1" -f (CountByName $win ' · изменено')) }
    Log ("OK  автор аннотации: «{0}» → «{1}»" -f $beforeAuthor, $NewAuthor)
    Shot '2c-objects'

    $step = 'review'
    if ($KeyboardChecks) { $author.SetFocus(); Keys '^s' } else { Press $review }
    $saveCopy = WaitFor { $b = ByName $win 'Сохранить копию…'; if ($b -and $b.Current.IsEnabled) { $b } } 60
    if (-not $saveCopy) { throw 'кнопка «Сохранить копию…» не стала доступной после проверки' }
    $rows = ByName $win 'Список изменений'
    $texts = @()
    if ($rows) { $texts = @($rows.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::Text)) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) }
    Log ("OK  «Было → Станет»: {0}" -f (($texts | Select-Object -First 16) -join ' | '))
    Shot '3-review'
    if ($KeyboardChecks) {
        $menuButton = ByName $win 'Дополнительно ▾'
        $menuButton.SetFocus(); Keys ' '
        $menu = WaitFor { $AE::RootElement.FindFirst($TS::Descendants, (AndCond (Cond $AE::ProcessIdProperty $proc.Id) (Cond $AE::NameProperty 'Заменить оригинал…'))) } 10
        if (-not $menu) { throw 'The additional save menu could not be opened from the keyboard' }
        Keys '{ESC}'
        Log 'OK  Ctrl+S opens review and the additional save menu opens from the keyboard'
    }

    $step = 'save-copy'
    $target = Join-Path $OutDir 'gui_meta.pdf'
    Remove-Item $target -ErrorAction SilentlyContinue
    if ($KeyboardChecks) { $saveCopy.SetFocus(); Keys ' ' } else { Press $saveCopy }
    $sdlg = WaitFor { Dialog $proc.Id } 20
    if (-not $sdlg) { throw 'диалог сохранения не появился' }
    Log ("OK  диалог сохранения: «{0}»" -f $sdlg.Current.Name)
    TypeIntoDialog $sdlg $target
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
    else {
        # Native message-box buttons may be exposed as Pane by the CI accessibility provider.
        try { $box.SetFocus() } catch { }
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    }
    if (-not (WaitFor { -not (Dialog $proc.Id) } 15)) { throw 'The save result dialog did not close after confirmation' }

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
    $a = $result.data.annotations[0].fields.author
    if ($a -ne $NewAuthor) { throw "в копии автор аннотации = «$a», ожидалось «$NewAuthor»" }
    $c = $result.data.annotations[0].hasContents
    if (-not $c) { throw 'в копии у аннотации пропал текст комментария' }
    Log ("OK  независимое чтение копии: /Title = «{0}», автор аннотации = «{1}»" -f $t, $a)
    if ($StressChecks) {
        $loadNodes = @($result.data.metadataStreams | Where-Object { $_.document } | ForEach-Object { $_.model.nodes } | Where-Object { $_.ns -eq 'https://example.org/acceptance/load/' })
        $tags = @($loadNodes | Where-Object { $_.steps.Count -eq 1 -and $_.steps[0].name -match '^Tag\d{5}$' })
        if ($tags.Count -ne 10000 -or @($tags | Group-Object { $_.steps[0].name }).Count -ne 10000) { throw 'Saved copy did not preserve all 10,000 distinct tags' }
        foreach ($tag in $tags) {
            if ($tag.value -cne ('value ' + $tag.steps[0].name.Substring(3))) { throw 'Saved copy changed a stress tag value' }
        }
        $savedLong = @($loadNodes | Where-Object { $_.steps.Count -eq 1 -and $_.steps[0].name -eq 'LongValue' })
        if ($savedLong.Count -ne 1 -or $savedLong[0].value -cne $stressValue) { throw 'Saved copy changed the complete multiline stress value' }
        Log 'OK  independent read preserves every stress tag and the exact 1 MiB multiline value'
    }

    if ($KeyboardChecks) {
        $openAgain = WaitFor { $win.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::Button)) | Where-Object { $_.Current.Name -like 'Change meta info*' -and -not $_.Current.IsOffscreen } | Select-Object -First 1 } 15
        if (-not $openAgain) { throw 'The editor did not return to the start screen' }
        Press $openAgain
        $dialogAgain = WaitFor { Dialog $proc.Id } 15
        TypeIntoDialog $dialogAgain $Pdf
        $titleAgain = WaitFor { $win.FindFirst($TS::Descendants, (AndCond (Cond $AE::ControlTypeProperty $CT::Edit) (Cond $AE::NameProperty 'Название документа'))) } 60
        if (-not $titleAgain) { throw 'Could not reopen the document for Ctrl+W acceptance' }
        $titleAgain.SetFocus(); Keys '^w'
        if (-not (WaitFor { $win.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::Button)) | Where-Object { $_.Current.Name -like 'Change meta info*' -and -not $_.Current.IsOffscreen } | Select-Object -First 1 } 15)) { throw 'Ctrl+W did not close the unchanged editor' }
        Log 'OK  Ctrl+W closes the editor with a text input focused'
    }

    $step = 'close'
    $proc.CloseMainWindow() | Out-Null
    $closed = WaitFor { $proc.Refresh(); $proc.HasExited } 15
    Log ("{0}  закрытие окна после сохранения{1}" -f $(if ($closed) { 'OK ' } else { 'FAIL' }), $(if ($closed) { '' } else { ': окно не закрылось' }))
    if (-not $closed) { Shot '5-close'; throw 'The application did not close after a successful save' }
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
