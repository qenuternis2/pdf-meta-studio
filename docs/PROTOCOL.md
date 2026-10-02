# Протокол GUI ↔ pdfmeta-worker

Транспорт — стандартные потоки процесса `pdfmeta-worker.exe`: одна строка UTF-8 = один JSON-объект (JSON Lines).
GUI запускает worker без аргументов (только `--no-limits` для отладки), без повышения прав.
Пароли передаются только в теле запроса через stdin; в командную строку, stderr и журналы они не попадают
(проверяется тестом `test_password_not_logged`).

## Запуск

После старта worker пишет `{"type":"ready","protocol":1}`.

## Запрос

```json
{"id": 7, "cmd": "open", "path": "C:\\Docs\\a.pdf", "password": "…"}
```

`id` — целое, уникальное в пределах сеанса. Команды выполняются по одной, в порядке поступления.

| cmd | Назначение | Основные поля |
|---|---|---|
| `hello` | Версии worker, qpdf, XMP Toolkit | — |
| `open` | Прочитать документ (ничего не пишет) | `path`, `password?` |
| `preview` | Применить правки в памяти и вернуть «Было → Станет» | `path`, `password?`, `edits` |
| `save` | Записать копию или заменить оригинал | `path`, `password?`, `expect` (отпечаток файла из `open`), `edits`, `mode`: `copy`/`replace`, `target` (для copy), `options.allowSignedCopy` |
| `cancel` | Отменить выполняющуюся команду | `target`: id отменяемого запроса |
| `shutdown` | Завершить процесс | — |

`cancel` обрабатывается отдельным потоком чтения сразу, не дожидаясь очереди.

## Ответы

```json
{"id": 7, "type": "progress", "stage": "write", "percent": 40}
{"id": 7, "type": "result", "data": { … }}
{"id": 7, "type": "error", "code": "file_locked", "message": "…", "details": { … }}
```

Стадии прогресса: `hash`, `open`, `inspect`, `check`, `apply`, `write`, `verify`, `commit`.

## Правки (`edits`)

```json
{
  "info": [ {"op": "set", "key": "/Title", "value": "…", "type": "string|name"},
            {"op": "delete", "key": "/Custom"} ],
  "xmp": [ {"stream": "12 0" | null, "owner": "catalog" | "5 0", "scope": "all" | "detach",
            "action": "edit" | "remove", "ops": [ … ] } ],
  "objects": [ {"kind": "annotation", "address": "7 0", "field": "author", "op": "set", "value": "…"},
               {"kind": "attachment", "address": "данные.bin", "field": "modified", "op": "delete"} ]
}
```

- `stream: null` с `owner` — создать новый поток XMP (для документа: `owner: "catalog"`).
- Для общего потока (несколько владельцев) обязателен `scope`: `all` — изменить для всех,
  `detach` + `owner` — отделить копию для одного владельца. Без него — ошибка `scope_required`.
- `action: "remove"` убирает `/Metadata` у владельцев; поток не остаётся в файле осиротевшим.

### Поля аннотаций и вложений (`objects`)

Аннотация адресуется ссылкой на её словарь (`ref` из `open`; только аннотации — косвенные объекты, `editable: true`),
вложение — ключом в дереве `/EmbeddedFiles` (`name` из `open`). Текущие значения — в `fields` каждой аннотации
и вложения (`null` — ключа нет).

| kind | field | Ключ PDF | Удаление |
|---|---|---|---|
| `annotation` | `author`, `subject` | `/T`, `/Subj` | да |
| `annotation` | `modified`, `created` | `/M`, `/CreationDate` (дата PDF) | да |
| `attachment` | `filename` | `/UF` (и `/F`, если имя в ASCII) | нет |
| `attachment` | `description` | `/Desc` | да |
| `attachment` | `created`, `modified` | `/Params /CreationDate`, `/Params /ModDate` (дата PDF) | да |

Даты проверяются строго: `D:YYYY[MM[DD[HH[mm[SS]]]]]` с необязательным поясом, по календарю (`invalid_value`).
Текст комментария (`/Contents`) и байты вложенного файла не изменяются: поля для них нет (`bad_request`),
а проверка после записи сравнивает хеши `/Contents` всех аннотаций (`annotation_contents`) и каждое записанное поле (`objects`).
В ответе `preview` массив `objects` содержит `{kind, address, label, field, key, fieldLabel, before, after}`.

Операции XMP адресуют узел по точному пути — массиву шагов
`{"t":"prop","ns":URI,"name":…}`, `{"t":"item","i":N}`, `{"t":"field","ns":…,"name":…}`, `{"t":"qual","ns":…,"name":…}`.
Префикс не является идентификатором.

| op | Действие |
|---|---|
| `set` | Изменить простое значение (флаг URI сохраняется) |
| `create` | Создать свойство: `form` = `simple`/`seq`/`bag`/`alt`/`altText`/`struct`; для `simple` — `value`, контейнеры создаются пустыми и заполняются следующими операциями |
| `delete` | Удалить узел |
| `appendItem`, `insertItem` | Элемент массива (`value`, для вставки — `index` с 1) |
| `setArray` | Заменить элементы Seq/Bag: `form`, `items` (квалификаторы сохранившихся элементов не теряются) |
| `setLangAlt`, `deleteLangAlt` | Языковой вариант: `lang`, `value`; `x-default` не перезаписывает другие переводы |
| `replacePacket` | Заменить весь пакет исходным XML из редактора |

Поток метаданных распаковывается не более чем до 64 МиБ, а суммарный объём пакетов в ответе `open` ограничен 256 МиБ;
сверх этого поток отмечается `parse.code = "xmp_too_large"` без пакета (его можно удалить или заменить целиком).

Все операции одного потока применяются транзакционно к копии модели; затем пакет сериализуется,
повторно разбирается и семантически сравнивается с ожидаемым (`xmp_roundtrip_mismatch` при расхождении).

## Коды ошибок

| code | Смысл |
|---|---|
| `password_required`, `password_incorrect` | Нужен пароль / неверный пароль |
| `unsupported_encryption`, `unsupported` | Защита или формат не поддерживаются — запись заблокирована |
| `pdf_damaged`, `pdf_open_failed` | Документ повреждён |
| `file_not_found`, `file_locked`, `access_denied`, `io_error`, `no_space` | Файловые ошибки |
| `external_change` | Файл изменён после открытия (сравнение размера, времени и SHA-256) |
| `target_is_source` | Копия указывает на исходный файл |
| `signed_document` | Подписанный документ: только копия с `allowSignedCopy` |
| `private_data_copy_only` | Есть `/PieceInfo` без адаптера: только копия |
| `permission_denied` | Ограничения документа запрещают изменение |
| `no_changes` | Нет изменений — файл не перезаписывается |
| `scope_required`, `unsupported_owner` | Ошибка адресации общего/объектного потока |
| `xmp_invalid`, `xmp_forbidden_dtd`, `xmp_too_large`, `xmp_source_invalid`, `xmp_op_failed`, `invalid_value`, `xmp_roundtrip_mismatch` | Ошибки XMP |
| `write_failed`, `verification_failed` | Запись не удалась или проверка записанного файла не пройдена (`details.checks`) |
| `cancelled`, `out_of_memory`, `bad_request`, `internal` | Прочее |

При любой ошибке `save` исходный файл не изменён, временные файлы удалены.
