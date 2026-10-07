#!/usr/bin/env python3
"""Сквозные тесты pdfmeta-worker на настоящих PDF.

Запуск:  python3 run_tests.py <путь к pdfmeta-worker> [--corpus DIR] [--report report.json]

Независимая проверка результатов: pypdf (отдельная реализация чтения PDF), если установлен,
и визуальное сравнение страниц через pdftoppm (poppler), если он есть в PATH.
"""

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import traceback

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pdfgen  # noqa: E402

DC = "http://purl.org/dc/elements/1.1/"
XMP = "http://ns.adobe.com/xap/1.0/"
PDF = "http://ns.adobe.com/pdf/1.3/"
ATLAS = "https://example.org/atlas/1.0/"
PM = "https://example.org/pm/"


class WorkerError(Exception):
    def __init__(self, msg):
        super().__init__(msg.get("message"))
        self.code = msg.get("code")
        self.details = msg.get("details")


# --wine: запуск pdfmeta-worker.exe (кросс-сборка MinGW) под Wine. Пути Linux передаются
# как Z:\..., ответы переводятся обратно, чтобы тесты читали файлы привычными путями.
WINE = False


def to_wire(v, key=None):
    if isinstance(v, dict):
        return {k: to_wire(x, k) for k, x in v.items()}
    if isinstance(v, list):
        return [to_wire(x) for x in v]
    if WINE and key in ("path", "target") and isinstance(v, str) and v.startswith("/"):
        return "Z:" + v.replace("/", "\\")
    return v


def from_wire(v):
    if isinstance(v, dict):
        return {k: from_wire(x) for k, x in v.items()}
    if isinstance(v, list):
        return [from_wire(x) for x in v]
    if WINE and isinstance(v, str) and v[:3].upper() == "Z:\\":
        return v[2:].replace("\\", "/")
    return v


class Worker:
    def __init__(self, exe):
        cmd = ["wine", exe] if WINE else [exe]
        self.p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.next_id = 1
        self.lock = threading.Lock()
        ready = json.loads(self.p.stdout.readline())
        assert ready["type"] == "ready", ready
        self.progress = []

    def send(self, cmd, **kw):
        rid = self.next_id
        self.next_id += 1
        req = to_wire(dict(id=rid, cmd=cmd, **kw))
        self.p.stdin.write((json.dumps(req, ensure_ascii=False) + "\n").encode("utf-8"))
        self.p.stdin.flush()
        return rid

    def wait(self, rid):
        while True:
            line = self.p.stdout.readline()
            if not line:
                raise RuntimeError("worker exited: " + self.p.stderr.read().decode(errors="replace"))
            m = json.loads(line)
            if m.get("id") != rid:
                continue
            if m["type"] == "progress":
                self.progress.append(m)
                continue
            if m["type"] == "error":
                raise WorkerError(from_wire(m))
            return from_wire(m["data"])

    def call(self, cmd, **kw):
        return self.wait(self.send(cmd, **kw))

    def cancel(self, rid):
        self.p.stdin.write((json.dumps({"cmd": "cancel", "target": rid}) + "\n").encode())
        self.p.stdin.flush()

    def close(self):
        try:
            self.call("shutdown")
        except Exception:
            pass
        self.p.wait(timeout=10)


# ---------------------------------------------------------------- helpers

def prop(ns, name, prefix=None):
    s = {"t": "prop", "ns": ns, "name": name}
    if prefix:
        s["prefix"] = prefix
    return s


def item(i):
    return {"t": "item", "i": i}


def field(ns, name):
    return {"t": "field", "ns": ns, "name": name}


def qual(ns, name):
    return {"t": "qual", "ns": ns, "name": name}


def doc_stream(opened):
    return next(s for s in opened["metadataStreams"] if s["document"])


def nodes(stream_or_model):
    model = stream_or_model.get("model", stream_or_model)
    return {n["path"]: n for n in model["nodes"]}


def ident(model):
    """Семантика модели: (путь шагов с URI) -> (форма, значение). Префиксы не участвуют."""
    out = {}
    for n in model["nodes"]:
        key = json.dumps([[s.get("t"), s.get("ns"), s.get("name"), s.get("i")] for s in n["steps"]], ensure_ascii=False)
        out[key] = (n["form"], n.get("value"))
    return out


def info_map(opened_or_info):
    info = opened_or_info.get("info", opened_or_info)
    return {e["key"]: (e["kind"], e["value"]) for e in info["entries"]}


def sha(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest()


def write(path, data):
    with open(path, "wb") as f:
        f.write(data)
    return path


class Ctx:
    def __init__(self, exe, tmp):
        self.exe = exe
        self.tmp = tmp
        self.w = Worker(exe)

    def pdf(self, name, **kw):
        return write(os.path.join(self.tmp, name), pdfgen.make_pdf(**kw))

    def open(self, path, password=None):
        kw = {"path": path}
        if password is not None:
            kw["password"] = password
        return self.w.call("open", **kw)

    def save(self, path, opened, edits, target=None, mode="copy", password=None, options=None):
        kw = {"path": path, "expect": opened["file"]["fingerprint"], "edits": edits, "mode": mode}
        if target:
            kw["target"] = target
        if password is not None:
            kw["password"] = password
        if options:
            kw["options"] = options
        return self.w.call("save", **kw)


def assert_checks_ok(result):
    bad = [c for c in result["checks"] if not c["ok"]]
    assert not bad, bad


def expect_error(code, fn):
    try:
        fn()
    except WorkerError as e:
        assert e.code == code, "ожидался код %s, получен %s: %s" % (code, e.code, e)
        return e
    raise AssertionError("ожидалась ошибка " + code)


def no_temp_left(directory):
    return not [f for f in os.listdir(directory) if f.startswith(".pdfmeta-")]


# ---------------------------------------------------------------- tests

def test_open_edit_reread(c):
    src = c.pdf("basic.pdf", info={"Title": "Старое название", "Author": "Анна", "CustomKey": "custom ✓"})
    o = c.open(src)
    assert o["pdf"]["pageCount"] == 3
    assert info_map(o)["/Title"] == ("string", "Старое название")
    ds = doc_stream(o)
    edits = {
        "info": [{"op": "set", "key": "/Title", "value": "Новое название 🚀"}],
        "xmp": [{"stream": ds["ref"], "ops": [
            {"op": "setLangAlt", "steps": [prop(DC, "title")], "lang": "x-default", "value": "Новое название 🚀"}]}],
    }
    out = os.path.join(c.tmp, "basic_meta.pdf")
    r = c.save(src, o, edits, target=out)
    assert_checks_ok(r)
    assert sha(src) == o["file"]["fingerprint"]["sha256"], "исходник изменён"
    o2 = c.open(out)
    im = info_map(o2)
    assert im["/Title"] == ("string", "Новое название 🚀")
    assert im["/CustomKey"] == ("string", "custom ✓"), "пользовательский ключ /Info потерян"
    n = nodes(doc_stream(o2))
    title = {n[p]["value"]: p for p in n if p.startswith("dc:title[") and "/" not in p}
    assert "Новое название 🚀" in title
    assert "Проект «Атлас» — обзор" in title and "Project Atlas overview" in title, "переводы потеряны"


def test_untouched_compressed_metadata_preserves_encoding(c):
    from pypdf import PdfReader
    source = c.pdf("compressed-metadata.pdf", compress_xmp=True)
    opened = c.open(source)
    target = os.path.join(c.tmp, "compressed-metadata-copy.pdf")
    result = c.save(source, opened, {"info": [{"op": "set", "key": "/Title", "value": "Updated title"}]}, target=target)
    assert_checks_ok(result)
    before = PdfReader(source).trailer["/Root"]["/Metadata"]
    after = PdfReader(target).trailer["/Root"]["/Metadata"]
    assert before["/Filter"] == after["/Filter"] == "/FlateDecode"
    assert before._data == after._data, "Untouched compressed metadata bytes changed"
    assert ident(doc_stream(opened)["model"]) == ident(doc_stream(c.open(target))["model"])


def test_unknown_tags_preserved_add_delete(c):
    src = c.pdf("custom.pdf", info={"Title": "T"})
    o = c.open(src)
    before = ident(doc_stream(o)["model"])
    ds = doc_stream(o)
    out = os.path.join(c.tmp, "custom_meta.pdf")
    edits = {"xmp": [{"stream": ds["ref"], "ops": [
        {"op": "set", "steps": [prop(PDF, "Producer")], "value": "Другой производитель"},
        {"op": "create", "steps": [prop(ATLAS, "Status", "atlas")], "value": "draft"},
        {"op": "create", "steps": [prop("https://example.org/brand-new/", "Score", "bn")], "value": "5"},
        {"op": "delete", "steps": [prop(ATLAS, "Year")]},
    ]}]}
    assert_checks_ok(c.save(src, o, edits, target=out))
    after = ident(doc_stream(c.open(out))["model"])
    changed = {k for k in set(before) | set(after) if before.get(k) != after.get(k)}
    names = sorted(json.loads(k)[0][2] for k in changed)
    assert names == ["Producer", "Score", "Status", "Year"], names
    # Неизвестные поля и структура сохранены, тип не угадан по тексту.
    n = nodes(doc_stream(c.open(out)))
    assert n["atlas:Flag"]["value"] == "True" and n["atlas:Flag"]["form"] == "simple"
    assert n["atlas:Info/atlas:Nested/atlas:Level"]["value"] == "2"


def test_rich_values_roundtrip(c):
    src = c.pdf("rich.pdf", info={"Title": "T"})
    o = c.open(src)
    n = nodes(doc_stream(o))
    # Seq: порядок, запятая внутри имени не разбивается
    assert [n["dc:creator[%d]" % i]["value"] for i in (1, 2, 3)] == ["Анна Смирнова", "Михаил Орлов, мл.", "🤖 Bot"]
    assert n["dc:creator"]["form"] == "seq"
    # Bag: повторы и квалификаторы
    assert n["dc:subject"]["form"] == "bag"
    assert [n["dc:subject[%d]" % i]["value"] for i in (1, 2, 3)] == ["atlas", "план", "atlas"]
    assert n["dc:subject[4]/?ex:source"]["value"] == "manual"
    # Даты хранятся как есть: дробные секунды, неполные даты, отсутствие пояса
    assert n["xmp:CreateDate"]["value"] == "2026-09-18T10:30:00.123+04:00"
    assert n["xmp:ModifyDate"]["value"] == "2026-10"
    assert n["xmp:MetadataDate"]["value"] == "2026-10-01T16:20:00"
    # Структуры
    assert n["xmpMM:History[2]/stEvt:action"]["value"] == "saved"
    before = ident(doc_stream(o)["model"])
    # Правка одного автора и одного перевода: остальное без изменений
    ds = doc_stream(o)
    edits = {"xmp": [{"stream": ds["ref"], "ops": [
        {"op": "setArray", "steps": [prop(DC, "creator")], "form": "seq",
         "items": ["Анна Смирнова", "Михаил Орлов, мл.", "🤖 Bot", "Ещё автор 👩‍💻"]},
        {"op": "setLangAlt", "steps": [prop(DC, "description")], "lang": "x-default", "value": "Новое описание"},
    ]}]}
    out = os.path.join(c.tmp, "rich_meta.pdf")
    assert_checks_ok(c.save(src, o, edits, target=out))
    o2 = c.open(out)
    after = ident(doc_stream(o2)["model"])
    changed = sorted(k for k in set(before) | set(after) if before.get(k) != after.get(k))
    assert len(changed) == 2, changed  # новый автор и x-default описания
    n2 = nodes(doc_stream(o2))
    assert n2["dc:description[2]"]["value"] == "Beschreibung"
    assert n2["dc:subject[4]/?ex:source"]["value"] == "manual"
    assert n2["xmp:CreateDate"]["value"] == "2026-09-18T10:30:00.123+04:00"


def test_x_default_does_not_touch_translations(c):
    # SDK при SetLocalizedText меняет и совпадающий по значению перевод; наш setLangAlt — нет.
    xmp = """<rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/">
      <dc:title><rdf:Alt><rdf:li xml:lang="x-default">Same</rdf:li><rdf:li xml:lang="en">Same</rdf:li></rdf:Alt></dc:title>
    </rdf:Description>"""
    src = c.pdf("alt.pdf", xmp=xmp)
    o = c.open(src)
    out = os.path.join(c.tmp, "alt_meta.pdf")
    edits = {"xmp": [{"stream": doc_stream(o)["ref"], "ops": [
        {"op": "setLangAlt", "steps": [prop(DC, "title")], "lang": "x-default", "value": "Changed"}]}]}
    assert_checks_ok(c.save(src, o, edits, target=out))
    n = nodes(doc_stream(c.open(out)))
    vals = {n["dc:title[%d]/?xml:lang" % i]["value"]: n["dc:title[%d]" % i]["value"] for i in (1, 2)}
    assert vals == {"x-default": "Changed", "en": "Same"}, vals


def test_invalid_dates_rejected(c):
    src = c.pdf("dates.pdf")
    o = c.open(src)
    ref = doc_stream(o)["ref"]
    for bad in ["2026-02-30", "2026-13", "2026-10-01T25:00:00", "вчера"]:
        e = expect_error("invalid_value", lambda: c.w.call("preview", path=src, edits={"xmp": [{"stream": ref, "ops": [
            {"op": "set", "steps": [prop(XMP, "CreateDate")], "value": bad}]}]}))
        assert "CreateDate" in str(e)
    for good in ["2026", "2026-10", "2026-02-28", "2024-02-29T23:59:59.5", "2026-10-01T10:00:00Z", "2026-10-01T10:00+03:00"]:
        c.w.call("preview", path=src, edits={"xmp": [{"stream": ref, "ops": [
            {"op": "set", "steps": [prop(XMP, "CreateDate")], "value": good}]}]})


def test_preview_does_not_write(c):
    src = c.pdf("preview.pdf", info={"Title": "A"})
    h = sha(src)
    o = c.open(src)
    r = c.w.call("preview", path=src, edits={"info": [{"op": "set", "key": "/Title", "value": "B"}]})
    assert r["info"]["changed"] and info_map(r["info"]["after"])["/Title"] == ("string", "B")
    assert sha(src) == h
    assert os.listdir(c.tmp).count("preview_meta.pdf") == 0


def test_no_changes_not_written(c):
    src = c.pdf("nochange.pdf", info={"Title": "A"})
    o = c.open(src)
    expect_error("no_changes", lambda: c.save(src, o, {"info": [{"op": "set", "key": "/Title", "value": "A"}]},
                                              target=os.path.join(c.tmp, "nochange_meta.pdf")))
    assert not os.path.exists(os.path.join(c.tmp, "nochange_meta.pdf"))


def test_trapped_name_and_delete(c):
    src = c.pdf("trapped.pdf", info={"Title": "A", "Trapped": b"/False", "Obsolete": "x"})
    o = c.open(src)
    assert info_map(o)["/Trapped"] == ("name", "/False")
    out = os.path.join(c.tmp, "trapped_meta.pdf")
    edits = {"info": [{"op": "set", "key": "/Trapped", "type": "name", "value": "/True"},
                      {"op": "delete", "key": "/Obsolete"}],
             "xmp": [{"stream": doc_stream(o)["ref"], "ops": [
                 {"op": "create", "steps": [prop(PDF, "Trapped")], "value": "True"}]}]}
    assert_checks_ok(c.save(src, o, edits, target=out))
    im = info_map(c.open(out))
    assert im["/Trapped"] == ("name", "/True") and "/Obsolete" not in im
    expect_error("invalid_value", lambda: c.w.call("preview", path=src, edits={"xmp": [{"stream": doc_stream(o)["ref"], "ops": [
        {"op": "create", "steps": [prop(PDF, "Trapped")], "value": "Yes"}]}]}))


def test_missing_metadata_created(c):
    src = c.pdf("bare.pdf", info=None, xmp=None, version="1.3", annotation=False, attachment=False, outline=False, form=False)
    o = c.open(src)
    assert not o["info"]["present"] and not o["metadataStreams"]
    out = os.path.join(c.tmp, "bare_meta.pdf")
    edits = {"info": [{"op": "set", "key": "/Title", "value": "Создано"}],
             "xmp": [{"stream": None, "owner": "catalog", "ops": [
                 {"op": "setLangAlt", "steps": [prop(DC, "title")], "lang": "x-default", "value": "Создано"}]}]}
    r = c.save(src, o, edits, target=out)
    assert_checks_ok(r)
    o2 = c.open(out)
    assert o2["pdf"]["version"] >= "1.4", o2["pdf"]["version"]
    assert nodes(doc_stream(o2))["dc:title[1]"]["value"] == "Создано"


def test_damaged_xmp(c):
    broken = b'<?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?><x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>unclosed</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end="w"?>'
    src = c.pdf("damaged.pdf", raw_xmp=broken, info={"Title": "A"})
    o = c.open(src)
    ds = doc_stream(o)
    assert ds["parse"]["ok"] is False
    # Правка /Info: повреждённый пакет сохраняется байт в байт
    out = os.path.join(c.tmp, "damaged_meta.pdf")
    assert_checks_ok(c.save(src, o, {"info": [{"op": "set", "key": "/Title", "value": "B"}]}, target=out))
    assert broken in open(out, "rb").read()
    # Правка повреждённого XMP без полной замены заблокирована
    expect_error("xmp_source_invalid", lambda: c.w.call("preview", path=src, edits={"xmp": [{"stream": ds["ref"], "ops": [
        {"op": "create", "steps": [prop(DC, "format")], "value": "application/pdf"}]}]}))
    # Невалидная замена блокируется, валидная проходит
    expect_error("xmp_op_failed", lambda: c.w.call("preview", path=src, edits={"xmp": [{"stream": ds["ref"], "ops": [
        {"op": "replacePacket", "xml": "<x:xmpmeta xmlns:x='adobe:ns:meta/'><broken"}]}]}))
    good = pdfgen.xmp_packet('<rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/" dc:format="application/pdf"/>').decode()
    c.w.call("preview", path=src, edits={"xmp": [{"stream": ds["ref"], "ops": [{"op": "replacePacket", "xml": good}]}]})


def test_xxe_and_doctype_blocked(c):
    evil = b'<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/" dc:format="&e;"/></rdf:RDF></x:xmpmeta>'
    src = c.pdf("xxe.pdf", raw_xmp=evil)
    o = c.open(src)
    ds = doc_stream(o)
    assert ds["parse"]["ok"] is False and ds["parse"]["code"] == "xmp_forbidden_dtd"
    assert "root:" not in json.dumps(o)


def test_utf16_xmp_rejects_unpaired_surrogate(c):
    # Small synthetic regression for Expat CVE-2026-93990; valid UTF-16 must still work.
    xml = pdfgen.xmp_packet('<rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/" dc:format="AXB"/>').decode('utf-8')
    good = c.pdf("utf16-valid.pdf", raw_xmp=xml.encode('utf-16'))
    assert doc_stream(c.open(good))["parse"]["ok"] is True
    broken = xml.replace('AXB', 'A\ud800B').encode('utf-16', errors='surrogatepass')
    bad = c.pdf("utf16-invalid-surrogate.pdf", raw_xmp=broken)
    parsed = doc_stream(c.open(bad))["parse"]
    assert parsed["ok"] is False and parsed["code"] == "xmp_invalid", parsed


def test_shared_stream_scope(c):
    src = c.pdf("shared.pdf", page_meta=[0, 1], shared_page_meta=True)
    o = c.open(src)
    shared = next(s for s in o["metadataStreams"] if len(s["owners"]) == 2)
    p1 = next(ow for ow in shared["owners"] if ow.get("page") == 1)
    ops = [{"op": "set", "steps": [prop(PM, "Marker", "pm")], "value": "EDITED"}]
    expect_error("scope_required", lambda: c.w.call("preview", path=src, edits={"xmp": [{"stream": shared["ref"], "ops": ops}]}))
    # Отделение копии только для страницы 1
    out = os.path.join(c.tmp, "shared_detach.pdf")
    assert_checks_ok(c.save(src, o, {"xmp": [{"stream": shared["ref"], "scope": "detach", "owner": p1["ref"], "ops": ops}]}, target=out))
    o2 = c.open(out)
    by_page = {}
    for s in o2["metadataStreams"]:
        for ow in s["owners"]:
            if ow["kind"] == "page":
                by_page[ow["page"]] = nodes(s)["pm:Marker"]["value"]
    assert by_page == {1: "EDITED", 2: "SHARED-MARKER-77"}, by_page
    # Правка для всех владельцев
    out2 = os.path.join(c.tmp, "shared_all.pdf")
    assert_checks_ok(c.save(src, o, {"xmp": [{"stream": shared["ref"], "scope": "all", "ops": ops}]}, target=out2))
    o3 = c.open(out2)
    vals = [nodes(s)["pm:Marker"]["value"] for s in o3["metadataStreams"] if any(ow["kind"] == "page" for ow in s["owners"])]
    assert vals == ["EDITED"], vals


def test_removed_stream_not_left_orphaned(c):
    src = c.pdf("remove.pdf", page_meta=[0], orphan_meta=True)
    o = c.open(src)
    assert any(not s["owners"] for s in o["metadataStreams"]), "сирота не найден в таблице ссылок"
    page_stream = next(s for s in o["metadataStreams"] if any(ow["kind"] == "page" for ow in s["owners"]))
    out = os.path.join(c.tmp, "remove_meta.pdf")
    r = c.save(src, o, {"xmp": [{"stream": page_stream["ref"], "action": "remove", "ops": []}]}, target=out)
    assert_checks_ok(r)
    data = open(out, "rb").read()
    assert b"PAGE-MARKER-1" not in data, "удалённые данные остались в файле"
    assert b"ORPHAN-MARKER-55" in data, "unrelated orphan metadata was lost"
    assert r["writer"]["unreachableDropped"] == 0


def test_object_metadata_addressed(c):
    src = c.pdf("pages.pdf", page_meta=[0, 2])
    o = c.open(src)
    p3 = next(s for s in o["metadataStreams"] if any(ow.get("page") == 3 for ow in s["owners"]))
    out = os.path.join(c.tmp, "pages_meta.pdf")
    assert_checks_ok(c.save(src, o, {"xmp": [{"stream": p3["ref"], "ops": [
        {"op": "setLangAlt", "steps": [prop(DC, "description")], "lang": "x-default", "value": "Третья страница"}]}]}, target=out))
    got = {}
    for s in c.open(out)["metadataStreams"]:
        for ow in s["owners"]:
            if ow["kind"] == "page":
                got[ow["page"]] = nodes(s)["dc:description[1]"]["value"]
    assert got == {1: "Страница 1", 3: "Третья страница"}, got


def test_annotations_attachments_reported(c):
    src = c.pdf("objs.pdf")
    o = c.open(src)
    a = o["annotations"][0]
    assert a["T"] == "Рецензент" and a["Subj"] == "Замечание" and a["M"].startswith("D:2026")
    att = o["attachments"][0]
    assert att["filename"] == "данные.bin" and att["description"] == "Вложение"


def test_annotation_attachment_fields_edit(c):
    src = c.pdf("objfields.pdf", info={"Title": "A"})
    o = c.open(src)
    ann, att = o["annotations"][0], o["attachments"][0]
    assert ann["editable"] and ann["ref"], ann
    edits = {"objects": [
        {"kind": "annotation", "address": ann["ref"], "field": "author", "op": "set", "value": "Новый автор 🖊"},
        {"kind": "annotation", "address": ann["ref"], "field": "subject", "op": "delete"},
        {"kind": "annotation", "address": ann["ref"], "field": "modified", "op": "set", "value": "D:20261002120000+03'00'"},
        {"kind": "annotation", "address": ann["ref"], "field": "created", "op": "set", "value": "D:2026"},
        {"kind": "attachment", "address": att["name"], "field": "filename", "op": "set", "value": "отчёт.bin"},
        {"kind": "attachment", "address": att["name"], "field": "description", "op": "set", "value": "Новое описание"},
        {"kind": "attachment", "address": att["name"], "field": "modified", "op": "delete"},
        {"kind": "attachment", "address": att["name"], "field": "created", "op": "set", "value": "D:20260930"},
    ]}
    pv = c.w.call("preview", path=src, edits=edits)
    assert len(pv["objects"]) == 8, pv["objects"]
    assert {(x["field"], x["after"]) for x in pv["objects"] if x["kind"] == "annotation"} == {
        ("author", "Новый автор 🖊"), ("subject", None), ("modified", "D:20261002120000+03'00'"), ("created", "D:2026")}
    out = os.path.join(c.tmp, "objfields_meta.pdf")
    r = c.save(src, o, edits, target=out)
    assert_checks_ok(r)
    names = {ch["name"] for ch in r["checks"]}
    assert "objects" in names and "annotation_contents" in names, names
    n = c.open(out)
    a2, f2 = n["annotations"][0], n["attachments"][0]
    assert a2["T"] == "Новый автор 🖊" and "Subj" not in a2 and a2["M"] == "D:20261002120000+03'00'", a2
    assert a2["CreationDate"] == "D:2026", a2
    assert f2["filename"] == "отчёт.bin" and f2["description"] == "Новое описание", f2
    assert f2["modDate"] == "" and f2["creationDate"] == "D:20260930", f2
    assert f2["name"] == att["name"], "ключ в дереве вложений не должен меняться"
    assert f2["fields"] == {"filename": "отчёт.bin", "description": "Новое описание",
                            "created": "D:20260930", "modified": None}, f2["fields"]
    assert a2["fields"] == {"author": "Новый автор 🖊", "subject": None,
                            "modified": "D:20261002120000+03'00'", "created": "D:2026"}, a2["fields"]
    try:
        import pypdf
    except ImportError:
        return
    r1, r2 = pypdf.PdfReader(src), pypdf.PdfReader(out)
    c1 = r1.pages[0]["/Annots"][0].get_object()["/Contents"]
    c2 = r2.pages[0]["/Annots"][0].get_object()["/Contents"]
    assert c1 == c2, "текст комментария изменился"
    b1 = list(r1.attachments.values())[0][0]
    b2 = list(r2.attachments.values())[0][0]
    assert b1 == b2 == b"attachment bytes \x00\x01\x02 must stay intact", "байты вложения изменились"


def test_annotation_attachment_fields_rejected(c):
    src = c.pdf("objbad.pdf")
    o = c.open(src)
    ann, att = o["annotations"][0], o["attachments"][0]
    def save(op):
        return lambda: c.save(src, o, {"objects": [op]}, target=os.path.join(c.tmp, "objbad_meta.pdf"))
    expect_error("invalid_value", save({"kind": "annotation", "address": ann["ref"], "field": "modified", "value": "D:20260230"}))
    expect_error("invalid_value", save({"kind": "attachment", "address": att["name"], "field": "created", "value": "вчера"}))
    expect_error("bad_request", save({"kind": "annotation", "address": ann["ref"], "field": "contents", "value": "x"}))
    expect_error("bad_request", save({"kind": "attachment", "address": att["name"], "field": "filename", "op": "delete"}))
    expect_error("bad_request", save({"kind": "annotation", "address": "1 0", "field": "author", "value": "x"}))
    expect_error("no_changes", save({"kind": "annotation", "address": ann["ref"], "field": "author", "value": ann["T"]}))
    assert not os.path.exists(os.path.join(c.tmp, "objbad_meta.pdf"))


def test_private_data_copy_only(c):
    src = c.pdf("piece.pdf", piece_info=True, info={"Title": "A"})
    o = c.open(src)
    assert o["pieceInfo"], "PieceInfo не найден"
    edits = {"info": [{"op": "set", "key": "/Title", "value": "B"}]}
    pv = c.w.call("preview", path=src, edits=edits)
    assert any("PieceInfo" in n for n in pv["notes"]), pv["notes"]
    before = sha(src)
    expect_error("private_data_copy_only", lambda: c.save(src, o, edits, mode="replace"))
    assert sha(src) == before
    out = os.path.join(c.tmp, "piece_meta.pdf")
    assert_checks_ok(c.save(src, o, edits, target=out))
    assert c.open(out)["pieceInfo"], "PieceInfo потерян в копии"


def qpdf_cli():
    for cand in [os.environ.get("QPDF_CLI"), shutil.which("qpdf"), "/home/claude/deps/install/bin/qpdf"]:
        if cand and os.path.exists(cand):
            return cand
    return None


def test_password_and_encryption_preserved(c):
    q = qpdf_cli()
    if not q:
        return "SKIP: нет qpdf CLI для шифрования"
    plain = c.pdf("plain.pdf", info={"Title": "Secret"})
    enc = os.path.join(c.tmp, "enc.pdf")
    subprocess.run([q, "--encrypt", "user-pw", "owner-pw", "256", "--", plain, enc], check=True)
    expect_error("password_required", lambda: c.open(enc))
    expect_error("password_incorrect", lambda: c.open(enc, "wrong"))
    o = c.open(enc, "user-pw")
    assert o["encryption"]["encrypted"] and o["encryption"]["R"] == 6
    out = os.path.join(c.tmp, "enc_meta.pdf")
    r = c.save(enc, o, {"info": [{"op": "set", "key": "/Title", "value": "Новый секрет"}]}, target=out, password="user-pw")
    assert_checks_ok(r)
    expect_error("password_required", lambda: c.open(out))
    o2 = c.open(out, "user-pw")
    assert info_map(o2)["/Title"][1] == "Новый секрет" and o2["encryption"]["R"] == 6
    # Ограничения соблюдаются: изменение запрещено → нужен пароль владельца
    restricted = os.path.join(c.tmp, "restricted.pdf")
    subprocess.run([q, "--encrypt", "u", "o", "256", "--modify=none", "--", plain, restricted], check=True)
    ro = c.open(restricted, "u")
    expect_error("permission_denied", lambda: c.save(restricted, ro, {"info": [{"op": "set", "key": "/Title", "value": "x"}]},
                                                      target=os.path.join(c.tmp, "r_meta.pdf"), password="u"))
    oo = c.open(restricted, "o")
    assert_checks_ok(c.save(restricted, oo, {"info": [{"op": "set", "key": "/Title", "value": "x"}]},
                            target=os.path.join(c.tmp, "r_meta.pdf"), password="o"))


def test_password_not_logged(c):
    q = qpdf_cli()
    if not q:
        return "SKIP: нет qpdf CLI"
    plain = c.pdf("plain2.pdf", info={"Title": "x"})
    enc = os.path.join(c.tmp, "enc2.pdf")
    subprocess.run([q, "--encrypt", "TopSecret-42", "TopSecret-42", "256", "--", plain, enc], check=True)
    w = Worker(c.exe)
    try:
        w.call("open", path=enc, password="TopSecret-42")
        try:
            w.call("open", path=enc, password="TopSecret-43")
        except WorkerError:
            pass
    finally:
        w.close()
    err = w.p.stderr.read().decode(errors="replace")
    assert "TopSecret" not in err


def test_signed_document_copy_only(c):
    corpus = os.environ.get("QPDF_CORPUS")
    src = os.path.join(corpus, "digitally-signed.pdf") if corpus else None
    if not src or not os.path.exists(src):
        return "SKIP: нет digitally-signed.pdf"
    o = c.open(src)
    assert o["signatures"]["signed"]
    out = os.path.join(c.tmp, "signed_meta.pdf")
    edits = {"info": [{"op": "set", "key": "/Title", "value": "Подписанный"}]}
    expect_error("signed_document", lambda: c.save(src, o, edits, target=out))
    local = shutil.copy(src, os.path.join(c.tmp, "signed.pdf"))
    lo = c.open(local)
    expect_error("signed_document", lambda: c.save(local, lo, edits, mode="replace", options={"allowSignedCopy": True}))
    assert_checks_ok(c.save(src, o, edits, target=out, options={"allowSignedCopy": True}))


def test_external_change_detected(c):
    src = c.pdf("ext.pdf", info={"Title": "A"})
    o = c.open(src)
    time.sleep(0.01)
    with open(src, "ab") as f:
        f.write(b"\n% appended by another program\n")
    expect_error("external_change", lambda: c.save(src, o, {"info": [{"op": "set", "key": "/Title", "value": "B"}]},
                                                    target=os.path.join(c.tmp, "ext_meta.pdf")))
    assert no_temp_left(c.tmp)


def test_replace_original_with_backup(c):
    d = os.path.join(c.tmp, "replace")
    os.makedirs(d)
    src = write(os.path.join(d, "doc.pdf"), pdfgen.make_pdf(info={"Title": "Old"}))
    h = sha(src)
    o = c.open(src)
    r = c.save(src, o, {"info": [{"op": "set", "key": "/Title", "value": "New"}]}, mode="replace")
    assert_checks_ok(r)
    assert os.path.exists(r["backup"]) and sha(r["backup"]) == h
    assert info_map(c.open(src))["/Title"][1] == "New"
    assert no_temp_left(d)
    # Повторная замена создаёт другое имя резервной копии
    o2 = c.open(src)
    r2 = c.save(src, o2, {"info": [{"op": "set", "key": "/Title", "value": "Newer"}]}, mode="replace")
    assert r2["backup"] != r["backup"]


def test_metadata_bomb_capped(c):
    # 2 МиБ сжатых данных разворачиваются в 2 ГиБ: worker не должен распаковывать их целиком.
    import zlib
    z = zlib.compressobj(9)
    chunk = b" " * (16 << 20)
    bomb = b"".join(z.compress(chunk) for _ in range(128)) + z.flush()
    b = pdfgen.Builder()
    cat = b.reserve()
    pages = b.reserve()
    page = b.add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 100 100] /Resources << >> >>" % pages)
    b.set(pages, b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    meta = b.add(b"<< /Type /Metadata /Subtype /XML /Filter /FlateDecode /Length %d >>\nstream\n" % len(bomb)
                 + bomb + b"\nendstream")
    b.set(cat, b"<< /Type /Catalog /Pages %d 0 R /Metadata %d 0 R >>" % (pages, meta))
    src = write(os.path.join(c.tmp, "bomb.pdf"), b.build(cat))
    o = c.open(src)
    ds = doc_stream(o)
    assert ds["parse"]["ok"] is False and ds["parse"]["code"] == "xmp_too_large", ds["parse"]
    assert "packet" not in ds and "packetBase64" not in ds
    # Такой поток можно удалить.
    r = c.save(src, o, {"xmp": [{"stream": ds["ref"], "action": "remove"}]}, target=os.path.join(c.tmp, "out.pdf"))
    assert_checks_ok(r)
    assert not c.open(r["target"])["metadataStreams"]


def test_permissions_carried_over(c):
    if os.name == "nt" or WINE:
        return "SKIP: права POSIX проверяются только в Linux"
    src = c.pdf("private.pdf", info={"Title": "A"})
    os.chmod(src, 0o600)
    o = c.open(src)
    r = c.save(src, o, {"info": [{"op": "set", "key": "/Title", "value": "B"}]}, mode="replace")
    assert_checks_ok(r)
    assert os.stat(src).st_mode & 0o777 == 0o600, oct(os.stat(src).st_mode)
    o = c.open(src)
    new = os.path.join(c.tmp, "copy.pdf")
    c.save(src, o, {"info": [{"op": "set", "key": "/Title", "value": "C"}]}, target=new)
    assert os.stat(new).st_mode & 0o777 == 0o600, oct(os.stat(new).st_mode)
    existing = write(os.path.join(c.tmp, "existing.pdf"), b"old")
    os.chmod(existing, 0o640)
    c.save(src, o, {"info": [{"op": "set", "key": "/Title", "value": "D"}]}, target=existing)
    assert os.stat(existing).st_mode & 0o777 == 0o640, oct(os.stat(existing).st_mode)


def test_mark_of_the_web_kept(c):
    if os.name != "nt":
        return "SKIP: потоки NTFS (Zone.Identifier) есть только в Windows"
    zone = b"[ZoneTransfer]\r\nZoneId=3\r\n"
    src = c.pdf("downloaded.pdf", info={"Title": "A"})
    write(src + ":Zone.Identifier", zone)
    o = c.open(src)
    new = os.path.join(c.tmp, "copy.pdf")
    c.save(src, o, {"info": [{"op": "set", "key": "/Title", "value": "B"}]}, target=new)
    assert open(new + ":Zone.Identifier", "rb").read() == zone
    r = c.save(src, o, {"info": [{"op": "set", "key": "/Title", "value": "C"}]}, mode="replace")
    assert_checks_ok(r)
    assert open(src + ":Zone.Identifier", "rb").read() == zone
    assert info_map(c.open(src))["/Title"][1] == "C"


def test_copy_onto_source_refused(c):
    src = c.pdf("self.pdf", info={"Title": "A"})
    o = c.open(src)
    expect_error("target_is_source", lambda: c.save(src, o, {"info": [{"op": "set", "key": "/Title", "value": "B"}]}, target=src))


def test_cancel_save(c):
    src = c.pdf("big.pdf", info={"Title": "A"}, big_stream_bytes=300 * 1024 * 1024)
    o = c.open(src)
    rid = c.w.send("save", path=src, expect=o["file"]["fingerprint"], target=os.path.join(c.tmp, "big_meta.pdf"),
                   edits={"info": [{"op": "set", "key": "/Title", "value": "B"}]})
    time.sleep(0.2)
    c.w.cancel(rid)
    expect_error("cancelled", lambda: c.w.wait(rid))
    assert not os.path.exists(os.path.join(c.tmp, "big_meta.pdf"))
    assert no_temp_left(c.tmp)
    os.remove(src)


def test_unwritable_target_dir(c):
    if os.name == "nt":
        return "SKIP: в Windows chmod не ограничивает запись в каталог"
    if os.geteuid() == 0:
        return "SKIP: под root права каталога не ограничивают запись"
    d = os.path.join(c.tmp, "ro")
    os.makedirs(d)
    os.chmod(d, 0o555)
    src = c.pdf("ro_src.pdf", info={"Title": "A"})
    o = c.open(src)
    try:
        c.save(src, o, {"info": [{"op": "set", "key": "/Title", "value": "B"}]}, target=os.path.join(d, "x.pdf"))
        raise AssertionError("ожидалась ошибка записи")
    except WorkerError as e:
        assert e.code in ("write_failed", "io_error", "access_denied"), e.code


def test_no_space(c):
    if os.name == "nt" or shutil.which("mount") is None:
        return "SKIP: маленький tmpfs доступен только в Linux"
    mnt = os.path.join(c.tmp, "tiny")
    os.makedirs(mnt)
    if subprocess.run(["mount", "-t", "tmpfs", "-o", "size=600k", "tmpfs", mnt], capture_output=True).returncode != 0:
        return "SKIP: нельзя смонтировать маленький tmpfs"
    try:
        src = c.pdf("space.pdf", info={"Title": "A"}, big_stream_bytes=2 * 1024 * 1024)
        o = c.open(src)
        expect_error("no_space", lambda: c.save(src, o, {"info": [{"op": "set", "key": "/Title", "value": "B"}]},
                                                target=os.path.join(mnt, "out.pdf")))
        assert not os.listdir(mnt)
    finally:
        subprocess.run(["umount", mnt])


def test_independent_read_and_visual(c):
    try:
        import pypdf
    except ImportError:
        return "SKIP: pypdf не установлен"
    src = c.pdf("indep.pdf", info={"Title": "Old", "Author": "A"}, page_meta=[1])
    o = c.open(src)
    out = os.path.join(c.tmp, "indep_meta.pdf")
    assert_checks_ok(c.save(src, o, {"info": [{"op": "set", "key": "/Title", "value": "Новое 🚀"},
                                              {"op": "set", "key": "/Author", "value": "Анна; Михаил"}],
                                     "xmp": [{"stream": doc_stream(o)["ref"], "ops": [
                                         {"op": "setArray", "steps": [prop(DC, "creator")], "form": "seq", "items": ["Анна", "Михаил"]}]}]},
                            target=out))
    r1, r2 = pypdf.PdfReader(src), pypdf.PdfReader(out)
    assert r2.metadata.title == "Новое 🚀" and r2.metadata.author == "Анна; Михаил"
    assert len(r1.pages) == len(r2.pages)
    for a, b in zip(r1.pages, r2.pages):
        assert a.extract_text() == b.extract_text()
        assert a.mediabox == b.mediabox
    assert list(r2.attachments) == list(r1.attachments)
    assert r2.attachments["данные.bin"] == r1.attachments["данные.bin"]
    assert [x.title for x in r2.outline] == [x.title for x in r1.outline]
    assert r2.get_fields()["name"]["/V"] == "Значение поля"
    xmp = r2.xmp_metadata
    assert xmp.dc_creator == ["Анна", "Михаил"], xmp.dc_creator
    if shutil.which("pdftoppm"):
        imgs = []
        for p in (src, out):
            prefix = p + ".png"
            subprocess.run(["pdftoppm", "-r", "50", "-png", p, prefix], check=True, capture_output=True)
            imgs.append(sorted(hashlib.sha256(open(os.path.join(c.tmp, f), "rb").read()).hexdigest()
                               for f in os.listdir(c.tmp) if f.startswith(os.path.basename(prefix))))
        assert imgs[0] == imgs[1] and imgs[0], "страницы визуально различаются"
    else:
        return "PARTIAL: pdftoppm не найден, визуальное сравнение пропущено"


def test_unrelated_orphan_metadata_preserved(c):
    src = c.pdf("orphan-preserved.pdf", info={"Title": "Before"}, orphan_meta=True)
    opened = c.open(src)
    out = os.path.join(c.tmp, "orphan-preserved_meta.pdf")
    result = c.save(src, opened, {"info": [{"op": "set", "key": "/Title", "value": "After"}]}, target=out)
    assert_checks_ok(result)
    orphan = [s for s in c.open(out)["metadataStreams"] if not s["reachable"]]
    assert len(orphan) == 1 and "ORPHAN-MARKER-55" in orphan[0]["packet"]


def test_discovery_includes_orphan_containers_not_arbitrary_xml(c):
    b = pdfgen.Builder()
    pages = b.add(b"<< /Type /Pages /Kids [] /Count 0 >>")
    root = b.add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages)
    b.add(b.stream(b"<ordinary>not XMP</ordinary>", b" /Subtype /XML"))
    meta = b.add(b.stream(pdfgen.page_xmp("Orphan owner", "OWNED-ORPHAN"), b" /Type /Metadata /Subtype /XML"))
    b.add(b"<< /Metadata %d 0 R /PieceInfo << /AuditApp << /Private (opaque) >> >> >>" % meta)
    src = os.path.join(c.tmp, "orphan-containers.pdf")
    with open(src, "wb") as f:
        f.write(b.build(root))
    opened = c.open(src)
    assert opened["scan"]["complete"]
    assert len(opened["pieceInfo"]) == 1
    assert len(opened["metadataStreams"]) == 1
    assert opened["metadataStreams"][0]["owners"]
    assert not opened["metadataStreams"][0]["reachable"]


def test_unchanged_broken_xmp_requires_copy(c):
    src = c.pdf("broken-copy-only.pdf", info={"Title": "Before"}, raw_xmp=b"<broken")
    opened = c.open(src)
    edits = {"info": [{"op": "set", "key": "/Title", "value": "After"}]}
    expect_error("xmp_copy_only", lambda: c.save(src, opened, edits, mode="replace"))
    assert sha(src) == opened["file"]["fingerprint"]["sha256"]
    out = os.path.join(c.tmp, "broken-copy-only_meta.pdf")
    assert_checks_ok(c.save(src, opened, edits, target=out))
    assert doc_stream(c.open(out))["packet"] == doc_stream(opened)["packet"]


def test_known_date_shape_rejected(c):
    src = c.pdf("date-shape.pdf")
    opened = c.open(src)
    packet = pdfgen.xmp_packet(
        '<rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/" '
        'xmlns:ex="https://example.org/audit/">'
        '<xmp:CreateDate rdf:parseType="Resource"><ex:value>not a date</ex:value></xmp:CreateDate>'
        '</rdf:Description>').decode()
    expect_error("invalid_value", lambda: c.w.call("preview", path=src, edits={"xmp": [
        {"stream": doc_stream(opened)["ref"], "ops": [{"op": "replacePacket", "xml": packet}]}]}))


def test_nested_shared_owner_detach_and_remove(c):
    b = pdfgen.Builder()
    pages = b.add(b"<< /Type /Pages /Kids [] /Count 0 >>")
    meta = b.add(b.stream(pdfgen.page_xmp("Shared", "NESTED-MARKER"), b" /Type /Metadata /Subtype /XML"))
    root = b.add(b"<< /Type /Catalog /Pages %d 0 R /First << /Metadata %d 0 R >> /Second << /Metadata %d 0 R >> >>" % (pages, meta, meta))
    src = os.path.join(c.tmp, "nested-shared.pdf")
    with open(src, "wb") as f:
        f.write(b.build(root))
    opened = c.open(src)
    stream = opened["metadataStreams"][0]
    first = next(o for o in stream["owners"] if o["path"] == ["/First"])
    out = os.path.join(c.tmp, "nested-detached.pdf")
    assert_checks_ok(c.save(src, opened, {"xmp": [{"stream": stream["ref"], "scope": "detach",
        "owner": first["ref"], "ownerPath": first["path"], "ops": [
        {"op": "set", "steps": [prop(PM, "Marker")], "value": "DETACHED-MARKER"}]}]}, target=out))
    detached = c.open(out)
    assert len(detached["metadataStreams"]) == 2
    selected = next(s for s in detached["metadataStreams"] if any(o["path"] == ["/First"] for o in s["owners"]))
    assert "DETACHED-MARKER" in selected["packet"]
    remaining = next(s for s in detached["metadataStreams"] if any(o["path"] == ["/Second"] for o in s["owners"]))
    assert "NESTED-MARKER" in remaining["packet"]
    removed = os.path.join(c.tmp, "nested-removed.pdf")
    assert_checks_ok(c.save(out, detached, {"xmp": [{"stream": selected["ref"], "action": "remove"}]}, target=removed))
    assert b"DETACHED-MARKER" not in open(removed, "rb").read()
    assert b"NESTED-MARKER" in open(removed, "rb").read()


def test_conflicting_object_generations_cannot_change_pages(c):
    corpus = os.environ.get("QPDF_CORPUS")
    src = os.path.join(corpus, "issue-149.pdf") if corpus else None
    if not src or not os.path.exists(src):
        return "SKIP: qpdf issue-149 fixture is unavailable"
    opened = c.open(src)
    original_hash = sha(src)
    out = os.path.join(c.tmp, "issue-149_meta.pdf")
    try:
        result = c.save(src, opened, {"info": [{"op": "set", "key": "/Title", "value": "Edited"}]}, target=out)
        # A future writer fix may preserve both generations without refusing this file.
        assert_checks_ok(result)
        import pypdf
        assert pypdf.PdfReader(out).pages[0].extract_text() == pypdf.PdfReader(src).pages[0].extract_text()
    except WorkerError as error:
        assert error.code in ("verification_failed", "pdf_damaged"), error.code
        if error.code == "verification_failed":
            assert any(check["name"] == "pages" and not check["ok"] for check in error.details["checks"])
        else:
            assert error.details["warnings"]
        assert not os.path.exists(out)
    assert sha(src) == original_hash



def test_array_movement_rename_and_qualifiers(c):
    source = c.pdf("typed.pdf")
    opened = c.open(source)
    stream = doc_stream(opened)
    operations = [
        {"op": "moveItem", "steps": [prop(DC, "subject")], "from": 4, "to": 1},
        {"op": "set", "steps": [prop(DC, "subject"), item(1)], "value": "Changed"},
        {"op": "appendItem", "steps": [prop(ATLAS, "Info"), field(ATLAS, "Contacts")], "value": "new@example.org"},
        {"op": "insertItem", "steps": [prop(ATLAS, "Info"), field(ATLAS, "Contacts")], "index": 2, "value": "insert@example.org"},
        {"op": "rename", "steps": [prop(ATLAS, "Info")], "toSteps": [prop(ATLAS, "Renamed")]},
    ]
    target = os.path.join(c.tmp, "typed-out.pdf")
    saved = c.save(source, opened, {"xmp": [{"stream": stream["ref"], "ops": operations}]}, target)
    assert_checks_ok(saved)
    model = doc_stream(c.open(target))["model"]
    values = nodes(model)
    assert values["dc:subject[1]"]["value"] == "Changed"
    assert values["dc:subject[1]/?ex:source"]["value"] == "manual"
    assert "atlas:Info" not in values
    assert values["atlas:Renamed/atlas:Contacts[2]"]["value"] == "insert@example.org"
    assert values["atlas:Renamed/atlas:Contacts[4]"]["value"] == "new@example.org"


def test_compound_array_items_and_positional_restore(c):
    source = c.pdf("compound.pdf")
    opened = c.open(source)
    stream = doc_stream(opened)
    operations = [
        {"op": "delete", "steps": [prop(DC, "creator"), item(3)]},
        {"op": "restore", "steps": [prop(DC, "creator"), item(3)], "xml": stream["packet"]},
        {"op": "appendItem", "steps": [prop(ATLAS, "Info"), field(ATLAS, "Contacts")], "form": "struct"},
        {"op": "create", "steps": [prop(ATLAS, "Info"), field(ATLAS, "Contacts"), item(3), field(ATLAS, "Name")], "value": "Nested"},
        {"op": "moveItem", "steps": [prop(ATLAS, "Info"), field(ATLAS, "Contacts")], "from": 3, "to": 1},
    ]
    target = os.path.join(c.tmp, "compound-out.pdf")
    assert_checks_ok(c.save(source, opened, {"xmp": [{"stream": stream["ref"], "ops": operations}]}, target))
    values = nodes(doc_stream(c.open(target)))
    assert values["dc:creator[3]"]["value"] == "🤖 Bot"
    assert values["atlas:Info/atlas:Contacts[1]/atlas:Name"]["value"] == "Nested"


def test_direct_annotations_and_known_private_adapter(c):
    builder = pdfgen.Builder()
    root, pages, page = builder.reserve(), builder.reserve(), builder.reserve()
    builder.set(root, b"<< /Type /Catalog /Pages %d 0 R /PieceInfo << /PdfMetaStudio << /Private << /Schema /PdfMetaStudioV1 /Label (Before) >> /LastModified (D:2026) >> >> >>" % pages)
    builder.set(pages, b"<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    builder.set(page, b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /T (Author) /Contents (Keep this) >>] >>" % pages)
    source = write(os.path.join(c.tmp, "direct-private.pdf"), builder.build(root))
    opened = c.open(source)
    app = opened["pieceInfo"][0]["apps"][0]
    assert app["adapter"] == "PdfMetaStudioV1", app
    assert opened["annotations"][0]["editable"]
    address = opened["annotations"][0]["ref"]
    assert address == "page:1:annot:1"
    export = os.path.join(c.tmp, "private.json")
    c.w.call("exportPrivate", path=source, address=app["address"], target=export)
    exported = json.load(open(export, encoding="utf-8"))
    assert exported["root"]["type"] == "dictionary"
    assert_checks_ok(c.save(source, opened, {"objects": [
        {"kind": "annotation", "address": address, "field": "author", "value": "Changed", "op": "set"},
        {"kind": "private", "address": app["address"], "field": "label", "value": "After", "op": "set"}
    ]}, mode="replace"))
    again = c.open(source)
    assert again["annotations"][0]["fields"]["author"] == "Changed"
    assert again["pieceInfo"][0]["apps"][0]["fields"]["label"] == "After"


def test_private_opaque_export_keeps_raw_stream_bytes(c):
    source = c.pdf("private.pdf", big_stream_bytes=4096)
    opened = c.open(source)
    app = opened["pieceInfo"][0]["apps"][0]
    assert app["adapter"] is None
    export = os.path.join(c.tmp, "opaque.json")
    c.w.call("exportPrivate", path=source, address=app["address"], target=export)
    data = json.load(open(export, encoding="utf-8"))
    assert any("rawStreamBase64" in value for value in data["objects"].values())
    expect_error("target_is_source", lambda: c.w.call("exportPrivate", path=source, address=app["address"], target=source))


def test_pdf_identity_detects_replacement_with_identical_bytes(c):
    source = c.pdf("identity.pdf")
    opened = c.open(source)
    assert opened["file"]["fingerprint"]["identity"]
    old_stat = os.stat(source)
    replacement = os.path.join(c.tmp, "replacement.pdf")
    shutil.copyfile(source, replacement)
    os.utime(replacement, ns=(old_stat.st_atime_ns, old_stat.st_mtime_ns))
    os.replace(replacement, source)
    expect_error("external_change", lambda: c.save(source, opened, {"info": [{"op": "set", "key": "/Title", "value": "x"}]}, mode="replace"))


def test_invalid_protocol_envelopes_do_not_crash_worker(c):
    for request in ['[]', '{"cmd":1}', '{"cmd":"open","id":"wrong"}']:
        c.w.p.stdin.write((request + "\n").encode()); c.w.p.stdin.flush()
        response = json.loads(c.w.p.stdout.readline())
        assert response["code"] == "bad_request", response
    assert c.w.call("hello")["protocol"] == 1


def test_declared_profile_edit_constraints(c):
    description = pdfgen.RICH_XMP.replace('xmp:CreateDate=', 'xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/" pdfaid:part="2" pdfaid:conformance="B" xmp:CreateDate=', 1)
    source = c.pdf("profile.pdf", xmp=description)
    opened = c.open(source)
    stream = doc_stream(opened)
    expect_error("unsupported_profile_edit", lambda: c.w.call("preview", path=source, edits={"xmp": [{"stream": stream["ref"], "ops": [
        {"op": "create", "steps": [prop("https://example.org/new-schema/", "Custom")], "value": "New"}]}]}))
    modern = c.pdf("pdf20.pdf", version="2.0")
    modern_opened = c.open(modern)
    expect_error("unsupported_profile_edit", lambda: c.save(modern, modern_opened, {"info": [
        {"op": "set", "key": "/NewCustom", "value": "New"}]}, target=os.path.join(c.tmp, "modern-out.pdf")))
    pdfx = c.pdf("pdfx.pdf", info={"GTS_PDFXVersion": "PDF/X-4", "Title": "Original"})
    opened_x = c.open(pdfx)
    expect_error("unsupported_profile_edit", lambda: c.w.call("preview", path=pdfx, edits={"info": [
        {"op": "set", "key": "/Trapped", "type": "name", "value": "/Unknown"}]}))


def test_repaired_structure_is_never_silently_saved(c):
    corpus = os.environ.get("QPDF_CORPUS")
    if not corpus:
        return "SKIP: qpdf damaged fixtures are unavailable"
    for name in ["bad39.pdf", "direct-pages.pdf", "stream-line-enders.pdf"]:
        source = os.path.join(corpus, name)
        opened = c.open(source)
        assert not opened["scan"]["complete"]
        before = sha(source)
        target = os.path.join(c.tmp, name)
        expect_error("pdf_damaged", lambda: c.save(source, opened, {"info": [{"op": "set", "key": "/Title", "value": "Blocked"}]}, target=target))
        assert sha(source) == before and not os.path.exists(target)


def test_original_packet_export_is_byte_exact(c):
    source = c.pdf("packet.pdf")
    opened = c.open(source)
    stream = doc_stream(opened)
    target = os.path.join(c.tmp, "packet.xmp")
    c.w.call("exportMetadata", path=source, stream=stream["ref"], target=target)
    assert open(target, "rb").read() == stream["packet"].encode("utf-8")
    expect_error("target_is_source", lambda: c.w.call("exportMetadata", path=source, stream=stream["ref"], target=source))


def test_secure_temporary_file_descriptor_and_permissions(c):
    name = 'pdfmeta-fileutil-tests.exe' if os.name == 'nt' or WINE else 'pdfmeta-fileutil-tests'
    helper = os.environ.get('PDFMETA_FILEUTIL_TESTS') or os.path.join(os.path.dirname(c.exe), name)
    assert os.path.isfile(helper), 'Build test-tools and provide the secure temporary-file helper'
    command = ['wine', helper, to_wire(c.tmp, 'path')] if WINE else [helper, c.tmp]
    result = subprocess.run(command, capture_output=True, text=True, timeout=15)
    assert result.returncode == 0, result.stdout + result.stderr


def test_windows_locked_original_can_only_be_copied(c):
    if os.name != "nt":
        return "SKIP: Windows sharing modes require Windows"
    import ctypes
    from ctypes import wintypes
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    source = c.pdf("locked.pdf")
    opened = c.open(source)
    before = sha(source)
    handle = kernel.CreateFileW(source, 0x80000000, 1, None, 3, 0, None)
    assert handle != wintypes.HANDLE(-1).value
    try:
        edits = {"info": [{"op": "set", "key": "/Title", "value": "Copy"}]}
        expect_error("file_locked", lambda: c.save(source, opened, edits, mode="replace"))
        assert_checks_ok(c.save(source, opened, edits, target=os.path.join(c.tmp, "unlocked-copy.pdf")))
        assert sha(source) == before
    finally:
        kernel.CloseHandle(handle)


def test_windows_backup_retains_protected_acl(c):
    if os.name != "nt":
        return "SKIP: Windows DACL acceptance requires Windows"
    import ctypes
    from ctypes import wintypes
    security = ctypes.WinDLL('advapi32', use_last_error=True)
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    pointer = ctypes.c_void_p
    security.GetNamedSecurityInfoW.argtypes = [wintypes.LPWSTR, wintypes.DWORD, wintypes.DWORD,
                                              pointer, pointer, pointer, pointer, ctypes.POINTER(pointer)]
    security.GetNamedSecurityInfoW.restype = wintypes.DWORD
    security.ConvertSecurityDescriptorToStringSecurityDescriptorW.argtypes = [pointer, wintypes.DWORD,
                                                                              wintypes.DWORD, ctypes.POINTER(pointer), pointer]
    security.ConvertSecurityDescriptorToStringSecurityDescriptorW.restype = wintypes.BOOL
    kernel.LocalFree.argtypes = [pointer]
    kernel.LocalFree.restype = pointer
    def acl(path):
        descriptor, text = pointer(), pointer()
        error = security.GetNamedSecurityInfoW(path, 1, 4, None, None, None, None, ctypes.byref(descriptor))
        assert error == 0, ctypes.WinError(error)
        try:
            assert security.ConvertSecurityDescriptorToStringSecurityDescriptorW(descriptor, 1, 4, ctypes.byref(text), None), ctypes.WinError(ctypes.get_last_error())
            return ctypes.wstring_at(text)
        finally:
            if text: kernel.LocalFree(text)
            kernel.LocalFree(descriptor)
    source = c.pdf("acl.pdf")
    # Change only DACL inheritance, without asking Set-Acl to restore owner/group privileges.
    setup = subprocess.run(['icacls.exe', source, '/inheritance:d'], capture_output=True)
    assert setup.returncode == 0, setup.stderr.decode(errors='replace')
    original_acl = acl(source)
    opened = c.open(source)
    result = c.save(source, opened, {"info": [{"op": "set", "key": "/Title", "value": "ACL"}]}, mode="replace")
    assert_checks_ok(result)
    assert acl(source) == original_acl, 'Replaced source ACL differs'
    assert acl(result['backup']) == original_acl, 'Backup ACL differs'


def test_windows_long_path_copy(c):
    if os.name != "nt":
        return "SKIP: Windows long-path acceptance requires Windows"
    import winreg
    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r'SYSTEM\CurrentControlSet\Control\FileSystem') as key:
        try:
            enabled = winreg.QueryValueEx(key, 'LongPathsEnabled')[0]
        except FileNotFoundError:
            enabled = 0
    if not enabled:
        return "SKIP: Windows long paths are not enabled on this host"
    original = c.pdf('long-path-original.pdf')
    directory = os.path.join(os.path.dirname(original), *(['long-directory-' + 'x' * 55] * 5))
    os.makedirs(directory)
    source = os.path.join(directory, 'источник.pdf')
    shutil.copyfile(original, source)
    opened = c.open(source)
    target = os.path.join(directory, 'результат.pdf')
    result = c.save(source, opened, {'info': [{'op': 'set', 'key': '/Title', 'value': 'Long path'}]}, target=target)
    assert_checks_ok(result)
    assert info_map(c.open(target))['/Title'] == ('string', 'Long path')


def test_bounded_queue_and_queued_cancellation(c):
    xml = pdfgen.xmp_packet('<rdf:Description rdf:about="" xmlns:ex="https://example.org/queue/"><ex:Rows><rdf:Seq>' + '<rdf:li>item</rdf:li>' * 20000 + '</rdf:Seq></ex:Rows></rdf:Description>').decode('utf-8')
    primary = c.w.send('validateXmp', xml=xml)
    queued = [c.w.send('hello') for _ in range(8)]
    c.w.cancel(queued[1])
    queued.extend(c.w.send('hello') for _ in range(42))
    terminal = {}
    while len(terminal) != 51:
        response = json.loads(c.w.p.stdout.readline())
        if response.get('type') in ('result', 'error'):
            terminal[response['id']] = response
    assert terminal[primary]['type'] == 'result'
    assert any(response.get('code') == 'queue_full' for response in terminal.values())
    assert terminal[queued[1]].get('code') == 'cancelled', terminal[queued[1]]
    assert c.w.call('hello')['protocol'] == 1

TESTS = [v for k, v in sorted(globals().items()) if k.startswith("test_")]


def run_corpus(exe, corpus, tmp):
    """Правка на каждом PDF корпуса: открыть, изменить /Info и XMP, сохранить копию, проверить."""
    stats = {"files": 0, "opened": 0, "saved": 0, "refused": {}, "verification_blocked": [], "failed": []}
    w = Worker(exe)
    files = sorted(f for f in os.listdir(corpus) if f.endswith(".pdf"))
    for f in files:
        path = os.path.join(corpus, f)
        stats["files"] += 1
        try:
            o = w.call("open", path=path)
        except WorkerError as e:
            stats["refused"][e.code] = stats["refused"].get(e.code, 0) + 1
            continue
        except RuntimeError:
            stats["failed"].append((f, "worker crashed on open"))
            w = Worker(exe)
            continue
        stats["opened"] += 1
        edits = {"info": [{"op": "set", "key": "/Title", "value": "Корпус ✓"}]}
        doc = [s for s in o["metadataStreams"] if s["document"] and s["parse"]["ok"]]
        if doc and len(doc[0]["owners"]) == 1:
            edits["xmp"] = [{"stream": doc[0]["ref"], "ops": [
                {"op": "setLangAlt", "steps": [prop(DC, "title")], "lang": "x-default", "value": "Корпус ✓"}]}]
        out = os.path.join(tmp, "corpus_out.pdf")
        try:
            r = w.call("save", path=path, expect=o["file"]["fingerprint"], target=out, edits=edits,
                       options={"allowSignedCopy": True})
            stats["saved"] += 1
        except WorkerError as e:
            if e.code == "verification_failed":
                # Запись остановлена проверкой — исходник цел, копия не создана. Это верное поведение.
                stats["verification_blocked"].append((f, [x["name"] for x in e.details["checks"] if not x["ok"]]))
                assert not os.path.exists(out)
            else:
                stats["refused"][e.code] = stats["refused"].get(e.code, 0) + 1
        except RuntimeError as e:
            stats["failed"].append((f, "worker crashed: " + str(e)[-300:]))
            w = Worker(exe)
        if os.path.exists(out):
            os.remove(out)
    w.close()
    return stats


def main():
    # Консоль Windows по умолчанию в cp1252/cp866: вывод с кириллицей иначе падает.
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser()
    ap.add_argument("worker")
    ap.add_argument("--corpus")
    ap.add_argument("--report")
    ap.add_argument("-k")
    ap.add_argument("--wine", action="store_true", help="run a Windows build of the worker under Wine")
    args = ap.parse_args()
    global WINE
    WINE = args.wine
    if WINE:
        os.environ.setdefault("WINEDEBUG", "-all")
    exe = os.path.abspath(args.worker)
    if args.corpus:
        os.environ["QPDF_CORPUS"] = args.corpus
    results = []
    tmp_root = tempfile.mkdtemp(prefix="pdfmeta-tests-")
    failed = 0
    for t in TESTS:
        if args.k and args.k not in t.__name__:
            continue
        tmp = os.path.join(tmp_root, t.__name__)
        os.makedirs(tmp)
        c = Ctx(exe, tmp)
        status, detail = "PASS", ""
        try:
            r = t(c)
            if isinstance(r, str):
                status, detail = r.split(":", 1)[0], r.split(":", 1)[1].strip()
        except Exception as e:  # noqa: BLE001
            status, detail = "FAIL", "".join(traceback.format_exception_only(type(e), e)).strip()
            failed += 1
        finally:
            c.w.close()
        results.append({"test": t.__name__, "status": status, "detail": detail})
        print("%-5s %s %s" % (status, t.__name__, detail))
    report = {"tests": results}
    if args.corpus:
        stats = run_corpus(exe, args.corpus, tmp_root)
        report["corpus"] = stats
        print("CORPUS files=%d opened=%d saved=%d refused=%s blocked_by_verification=%s failed=%d" % (
            stats["files"], stats["opened"], stats["saved"], stats["refused"], stats["verification_blocked"],
            len(stats["failed"])))
        for f, why in stats["failed"]:
            print("  FAIL", f, json.dumps(why, ensure_ascii=False)[:300])
        failed += len(stats["failed"])
    if args.report:
        with open(args.report, "w", encoding="utf-8") as fp:
            json.dump(report, fp, ensure_ascii=False, indent=2)
    shutil.rmtree(tmp_root, ignore_errors=True)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
