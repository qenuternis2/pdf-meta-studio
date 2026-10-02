# PDF Meta Studio

Настольное приложение для Windows 11 x64: редактирование метаданных PDF (`/Info`, XMP документа и объектов).
Интерфейс — C# WPF (MVVM, .NET 10). PDF — qpdf, XMP — Adobe XMP Core. Их связывает C++ процесс
`pdfmeta-worker.exe`, к которому GUI обращается по протоколу JSON Lines через stdin/stdout ([docs/PROTOCOL.md](docs/PROTOCOL.md)).

Состояние: **этап 1–2 из TASK.md** плюс значительная часть этапов 3–5. Движок PDF/XMP работает и проверен тестами.
Сборка и тесты проходят на Windows (GitHub Actions, windows-2022, MSVC); полный сценарий GUI проверен через UI Automation.

## Структура

| Путь | Что это |
|---|---|
| `worker/` | C++20 worker: `src/` (протокол, разбор и запись PDF, модель XMP), `cmake/XMPCore.cmake` (сборка XMP Core + Expat), `tests/` (e2e-тесты и генератор PDF) |
| `src/PdfMetaStudio.Core/` | .NET-ядро без WPF: клиент worker, модель снимка, кодеки дат PDF/XMP, сессия правок с undo/redo, согласование Info↔XMP, «Было → Станет» |
| `src/PdfMetaStudio.App/` | WPF-приложение (`PdfMetaStudio.exe`) |
| `tests/PdfMetaStudio.Core.Tests/` | xUnit-тесты ядра, включая интеграцию с настоящим worker |
| `scripts/deps.lock` | Закреплённые версии: qpdf v12.4.2, XMP-Toolkit-SDK v2025.03, Expat 2.7.1 (теги и SHA коммитов) |
| `scripts/patches/xmp-keep-translations.patch` | Обязательный патч XMP SDK (см. ниже) |
| `Directory.Packages.props`, `global.json`, `worker/vcpkg.json` | Закреплённые версии .NET SDK, NuGet-пакетов и zlib/libjpeg (базовая линия vcpkg) |

## Сборка под Windows 11 x64

Нужно: Visual Studio 2022 или Build Tools с C++ (MSVC), CMake ≥ 3.21, Git, .NET SDK 10.0.1xx, Python 3 (для тестов, опционально `pip install pypdf==5.1.0`),
vcpkg (переменная `VCPKG_ROOT`). В «Developer PowerShell for VS 2022»:

```powershell
git clone <репозиторий> pdf-meta-studio; cd pdf-meta-studio
.\scripts\build-windows.ps1          # зависимости → worker (MSVC) → тесты worker и ядра → dotnet publish
# результат: dist\PdfMetaStudio\PdfMetaStudio.exe (+ pdfmeta-worker.exe рядом, self-contained, без установки .NET)
```

Шаги скрипта по отдельности:

```powershell
.\scripts\fetch-deps.ps1   # клонирует по тегам, сверяет SHA, применяет патч XMP
cmake -S worker -B build\worker -G "Visual Studio 17 2022" -A x64 `
  -DCMAKE_TOOLCHAIN_FILE=$env:VCPKG_ROOT\scripts\buildsystems\vcpkg.cmake -DVCPKG_TARGET_TRIPLET=x64-windows-static
cmake --build build\worker --config Release --target pdfmeta-worker
python worker\tests\run_tests.py build\worker\Release\pdfmeta-worker.exe
$env:PDFMETA_WORKER="$PWD\build\worker\Release\pdfmeta-worker.exe"; dotnet test tests\PdfMetaStudio.Core.Tests -c Release
dotnet publish src\PdfMetaStudio.App -c Release -r win-x64 --self-contained -p:WorkerBinDir=$PWD\build\worker\Release -o dist\PdfMetaStudio
```

`.github/workflows/windows.yml` выполняет тот же скрипт на `windows-2022` и выкладывает EXE как артефакт сборки.

## Проверка под Linux (то, что реально запускалось)

```bash
./scripts/fetch-deps.sh
cmake -S worker -B build-worker -G Ninja -DCMAKE_BUILD_TYPE=Release && ninja -C build-worker pdfmeta-worker
python3 worker/tests/run_tests.py build-worker/pdfmeta-worker --corpus worker/external/qpdf/qpdf/qtest/qpdf
PDFMETA_WORKER=$PWD/build-worker/pdfmeta-worker dotnet test tests/PdfMetaStudio.Core.Tests -c Release
dotnet build -c Release                                     # включая WPF-проект (EnableWindowsTargeting)
dotnet publish src/PdfMetaStudio.App -c Release -r win-x64 --self-contained -o publish-win
```

Проверочная кросс-сборка worker под Windows (MinGW-w64) и запуск тестов в Wine:

```bash
# zlib 1.3.1 и libjpeg-turbo 3.1.0 собраны MinGW в префикс; scripts/mingw-toolchain.cmake
cmake -S worker -B build-mingw -G Ninja -DCMAKE_TOOLCHAIN_FILE=scripts/mingw-toolchain.cmake -DCMAKE_BUILD_TYPE=Release -DPDFMETA_BUILD_TESTS=OFF
ninja -C build-mingw pdfmeta-worker
python3 worker/tests/run_tests.py build-mingw/pdfmeta-worker.exe --wine --corpus worker/external/qpdf/qpdf/qtest/qpdf
```

Это только проверка Windows-ветки кода worker (`_WIN32`: пути UTF-16, `CreateFileW`, `MoveFileExW`, Job-объект).
XMP Core в этой сборке идёт по POSIX-ветке SDK, потому что ветка `WIN_ENV` рассчитана на MSVC. Рабочая сборка — MSVC.

## Результаты тестов (2026-10-02, фактические запуски)

| Что | Результат |
|---|---|
| **Windows (GitHub Actions windows-2022): worker MSVC 19.44** | 23 пройдено, 2 пропущены (нехватка места и права каталога — проверяются только в Linux) |
| **Windows: корпус qpdf (628 PDF)** | результаты совпадают с Linux: 586 / 578 / 49 отказов с причиной, 1 заблокирован проверкой, **0 сбоев** |
| **Windows: xUnit ядра .NET** | 46 из 46 |
| **Windows: дымовой тест GUI** (`scripts/gui-smoke.ps1`, UI Automation) | запуск за 3,8 с; на главном экране ровно одна кнопка; выбор PDF → редактор с 4 разделами → правка названия → «Было → Станет» (Info и XMP) → «Сохранить копию…» → независимое чтение копии: название записано |
| e2e-тесты worker, Linux (GCC 13) | 28 пройдено, 3 пропущено (нет образца подписанного PDF; под root права каталога не ограничивают запись) |
| те же тесты, `pdfmeta-worker.exe` (MinGW) под Wine 9.0 | 25 пройдено, 1 пропущен (тот же) |
| Корпус qpdf (628 PDF), Linux и Wine — одинаково | открыто 586, сохранено и проверено 578, отказов с понятной причиной 49 (пароль 23, повреждён 21, запрет изменений 5), 1 запись заблокирована проверкой (`bad-encryption-length.pdf`), **0 сбоев** |
| xUnit ядра .NET (с настоящим worker) | 49 из 49 |
| `dotnet build` решения, включая WPF | 0 ошибок, 0 предупреждений |
| `dotnet publish -r win-x64` | `PdfMetaStudio.exe` (PE32+ GUI x64) собран |
| Сборка с нуля по `deps.lock` (чистая копия) | зависимости сверены по SHA, патч применён, worker собран, тесты пройдены |

Что покрывают e2e-тесты: правка и повторное чтение; независимое чтение pypdf и визуальное сравнение страниц pdftoppm;
сохранение неизвестных тегов, добавление и удаление; кириллица, emoji, несколько авторов, переводы, вложенные структуры,
квалификаторы, порядок Seq, повторы Bag; `x-default` не трогает переводы; неполные даты, дробные секунды, отсутствие пояса,
невозможные даты; предпросмотр не пишет на диск; без правок файл не перезаписывается; `/Trapped`; создание XMP при его
отсутствии (1.3 → 1.4); повреждённый XMP; XXE/DOCTYPE; общий поток («для всех» и «отделить копию»); удалённый поток не
остаётся осиротевшим; адресная правка XMP объекта; пароль и сохранение шифрования; пароль не попадает в stderr;
подписанный документ — только копия; `/PieceInfo` — только копия; внешнее изменение; замена оригинала с резервной копией;
запись копии поверх исходника запрещена; отмена; нехватка места (маленький tmpfs); автор, тема и даты аннотаций,
имя, описание и даты вложений (текст комментария и байты вложения не меняются, проверено pypdf; неверные даты отклоняются).

Ошибки, найденные запуском в Wine и исправленные: проверка блокировки при «Заменить оригинал» в Windows всегда давала
«файл занят» из-за собственного дескриптора worker; нехватка места при записи сообщалась как общая ошибка записи.

## Важные решения

- **Патч XMP SDK.** При разборе и сериализации SDK копирует значение `x-default` в единственный другой перевод
  (Alt из двух элементов), то есть молча теряет перевод. Патч убирает это; CMake не соберёт worker без патча,
  а worker после каждой правки дополнительно сверяет семантику пакета (`xmp_roundtrip_mismatch`).
- **Строгий разбор XMP.** SDK по умолчанию пропускает часть ошибок XML; включён строгий обработчик. Повреждённый
  пакет показывается только для чтения и сохраняется неизменным, правка такого потока блокируется.
- **DTD и сущности запрещены** (`BanAllEntityUsage`, `XML_GE=0`, предварительная проверка `<!DOCTYPE`/`<!ENTITY`).
- **Запись:** временный файл в каталоге назначения → повторное открытие и проверка (Info, каждый изменённый XMP
  побайтно, незатронутые XMP, страницы, аннотации, закладки, формы, вложения, шифрование) → замена. Полная перезапись
  без линеаризации, потоки не перекодируются.
- **Опция «обновить дату изменения»** по умолчанию выключена; Producer и идентификаторы XMP автоматически не меняются.
- **Ограничения worker:** Job-объект Windows с лимитом памяти 4 ГиБ, запуск без повышения прав (`asInvoker`).

## Не реализовано

- Адаптеры известных схем `/PieceInfo` и экспорт неизвестных частных блоков — сейчас только показ; документ с ними
  сохраняется только в копию.
- Отключение согласования Info↔XMP в расширенном редакторе (в TASK.md помечено как «можно»).
- Подпись EXE и установщик.

## Что не проверено

- GUI проверен только автоматическим сценарием на Windows Server 2022 (раннер GitHub). На настоящем Windows 11 вручную не запускался; под Wine WPF падает при разметке текста — это ограничение Wine.
- После «Сохранить копию…» закрытие окна спрашивает о несохранённых правках: оригинал не изменён, поэтому вопрос задаётся. Так задумано, но стоит подтвердить, что это нужное поведение.
- Клавиатура, Narrator, масштаб 100–200 %, тёмная тема, отсутствие зависаний GUI.
- Занятый другой программой файл в настоящей Windows (логика есть, тест на блокировку другим процессом не написан);
  права резервной копии (ACL) в Windows.
- Лимиты Job-объекта в настоящей Windows.
