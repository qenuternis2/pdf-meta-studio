"""Минимальный генератор тестовых PDF без сторонних библиотек.

Создаёт настоящие PDF-файлы (страницы с текстом, /Info, XMP документа и страниц,
аннотации, вложения, закладки, поле формы), чтобы тесты не зависели от qpdf при генерации.
"""

import zlib


def pdf_text(s):
    """Строка PDF: PDFDocEncoding для ASCII, иначе UTF-16BE с BOM в hex-виде."""
    if all(32 <= ord(c) < 127 for c in s):
        return b"(" + s.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)").encode("ascii") + b")"
    return b"<FEFF" + s.encode("utf-16-be").hex().upper().encode() + b">"


class Builder:
    def __init__(self, version="1.7"):
        self.version = version
        self.objects = []  # bytes или None (резерв)

    def reserve(self):
        self.objects.append(None)
        return len(self.objects)

    def set(self, num, body):
        self.objects[num - 1] = body

    def add(self, body):
        self.objects.append(body)
        return len(self.objects)

    def stream(self, data, extra=b"", compress=False):
        if compress:
            data = zlib.compress(data)
            extra += b" /Filter /FlateDecode"
        return b"<< /Length %d%s >>\nstream\n" % (len(data), extra) + data + b"\nendstream"

    def build(self, root, info=None, file_id=b"<00112233445566778899AABBCCDDEEFF>"):
        out = bytearray(b"%PDF-" + self.version.encode() + b"\n%\xe2\xe3\xcf\xd3\n")
        offsets = []
        for i, body in enumerate(self.objects, start=1):
            offsets.append(len(out))
            out += b"%d 0 obj\n" % i + body + b"\nendobj\n"
        xref = len(out)
        out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(self.objects) + 1)
        for off in offsets:
            out += b"%010d 00000 n \n" % off
        trailer = b"<< /Size %d /Root %d 0 R" % (len(self.objects) + 1, root)
        if info:
            trailer += b" /Info %d 0 R" % info
        trailer += b" /ID [" + file_id + b" " + file_id + b"] >>"
        out += b"trailer\n" + trailer + b"\nstartxref\n%d\n%%%%EOF\n" % xref
        return bytes(out)


def xmp_packet(body_rdf):
    return (
        '<?xpacket begin="﻿" id="W5M0MpCehiHzreSzNTczkc9d"?>\n'
        '<x:xmpmeta xmlns:x="adobe:ns:meta/">\n'
        '<rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">\n'
        + body_rdf
        + "\n</rdf:RDF>\n</x:xmpmeta>\n" + " " * 512 + '\n<?xpacket end="w"?>'
    ).encode("utf-8")


RICH_XMP = """<rdf:Description rdf:about=""
    xmlns:dc="http://purl.org/dc/elements/1.1/"
    xmlns:xmp="http://ns.adobe.com/xap/1.0/"
    xmlns:pdf="http://ns.adobe.com/pdf/1.3/"
    xmlns:xmpMM="http://ns.adobe.com/xap/1.0/mm/"
    xmlns:stEvt="http://ns.adobe.com/xap/1.0/sType/ResourceEvent#"
    xmlns:atlas="https://example.org/atlas/1.0/"
    xmlns:ex="https://example.org/qual/"
    xmp:CreateDate="2026-09-18T10:30:00.123+04:00"
    xmp:ModifyDate="2026-10"
    xmp:MetadataDate="2026-10-01T16:20:00"
    pdf:Producer="Тестовый генератор"
    pdf:Keywords="Atlas, проект, запуск"
    atlas:ProjectCode="ATL-2026"
    atlas:Flag="True"
    atlas:Year="2026">
  <dc:title><rdf:Alt>
    <rdf:li xml:lang="x-default">Project Atlas — обзор</rdf:li>
    <rdf:li xml:lang="ru-RU">Проект «Атлас» — обзор</rdf:li>
    <rdf:li xml:lang="en-US">Project Atlas overview</rdf:li>
  </rdf:Alt></dc:title>
  <dc:description><rdf:Alt>
    <rdf:li xml:lang="x-default">Описание 🚀</rdf:li>
    <rdf:li xml:lang="de">Beschreibung</rdf:li>
  </rdf:Alt></dc:description>
  <dc:creator><rdf:Seq>
    <rdf:li>Анна Смирнова</rdf:li>
    <rdf:li>Михаил Орлов, мл.</rdf:li>
    <rdf:li>🤖 Bot</rdf:li>
  </rdf:Seq></dc:creator>
  <dc:subject><rdf:Bag>
    <rdf:li>atlas</rdf:li>
    <rdf:li>план</rdf:li>
    <rdf:li>atlas</rdf:li>
    <rdf:li rdf:parseType="Resource"><rdf:value>квалифицированный</rdf:value><ex:source>manual</ex:source></rdf:li>
  </rdf:Bag></dc:subject>
  <atlas:Info rdf:parseType="Resource">
    <atlas:Owner>Atlas Studio</atlas:Owner>
    <atlas:Contacts><rdf:Seq><rdf:li>a@example.org</rdf:li><rdf:li>b@example.org</rdf:li></rdf:Seq></atlas:Contacts>
    <atlas:Nested rdf:parseType="Resource"><atlas:Level>2</atlas:Level></atlas:Nested>
  </atlas:Info>
  <xmpMM:History><rdf:Seq>
    <rdf:li rdf:parseType="Resource"><stEvt:action>created</stEvt:action><stEvt:when>2026-09-18T10:30:00+04:00</stEvt:when></rdf:li>
    <rdf:li rdf:parseType="Resource"><stEvt:action>saved</stEvt:action><stEvt:when>2026-10-01</stEvt:when></rdf:li>
  </rdf:Seq></xmpMM:History>
</rdf:Description>"""


def page_xmp(text, marker):
    return xmp_packet(
        '<rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/" '
        'xmlns:pm="https://example.org/pm/">'
        '<dc:description><rdf:Alt><rdf:li xml:lang="x-default">%s</rdf:li></rdf:Alt></dc:description>'
        '<pm:Marker>%s</pm:Marker></rdf:Description>' % (text, marker)
    )


def make_pdf(pages=3, info=None, xmp=RICH_XMP, version="1.7", page_meta=None, shared_page_meta=False,
             annotation=True, attachment=True, outline=True, form=True, compress_xmp=False, raw_xmp=None,
             big_stream_bytes=0, orphan_meta=False, piece_info=False):
    """info: dict ключ->значение (str) или None; page_meta: список индексов страниц с XMP."""
    b = Builder(version)
    catalog = b.reserve()
    pages_obj = b.reserve()
    font = b.add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    page_ids = []
    kids = []
    shared = None
    if shared_page_meta:
        shared = b.add(b.stream(page_xmp("Общее описание страниц", "SHARED-MARKER-77"), b" /Type /Metadata /Subtype /XML"))
    for i in range(pages):
        content = b.add(b.stream(b"BT /F1 24 Tf 72 700 Td (Page %d) Tj ET" % (i + 1), compress=True))
        pid = b.reserve()
        page_ids.append(pid)
        kids.append(b"%d 0 R" % pid)
        extra = b""
        if page_meta and i in page_meta:
            m = shared if shared else b.add(
                b.stream(page_xmp("Страница %d" % (i + 1), "PAGE-MARKER-%d" % (i + 1)), b" /Type /Metadata /Subtype /XML"))
            extra += b" /Metadata %d 0 R" % m
        if annotation and i == 0:
            annot = b.add(b"<< /Type /Annot /Subtype /Text /Rect [100 100 120 120] /T " + pdf_text("Рецензент")
                          + b" /Subj " + pdf_text("Замечание") + b" /Contents " + pdf_text("Текст комментария")
                          + b" /M (D:20260918103000+04'00') >>")
            extra += b" /Annots [%d 0 R]" % annot
        if piece_info and i == 0:
            extra += (b" /PieceInfo << /ExampleApp << /LastModified (D:20260101120000Z)"
                      b" /Private << /Settings (opaque) /Rev 3 >> >> >>")
        b.set(pid, b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 %d 0 R >> >> "
              b"/Contents %d 0 R%s >>" % (pages_obj, font, content, extra))
    b.set(pages_obj, b"<< /Type /Pages /Kids [" + b" ".join(kids) + b"] /Count %d >>" % pages)
    cat = b"<< /Type /Catalog /Pages %d 0 R" % pages_obj
    if raw_xmp is not None or xmp is not None:
        packet = raw_xmp if raw_xmp is not None else xmp_packet(xmp)
        meta = b.add(b.stream(packet, b" /Type /Metadata /Subtype /XML", compress=compress_xmp))
        cat += b" /Metadata %d 0 R" % meta
    if attachment:
        ef = b.add(b.stream(b"attachment bytes \x00\x01\x02 must stay intact", b" /Type /EmbeddedFile /Params << /Size 40 /ModDate (D:20260101000000Z) >>"))
        fs = b.add(b"<< /Type /Filespec /F (data.bin) /UF " + pdf_text("данные.bin") + b" /Desc " + pdf_text("Вложение")
                   + b" /EF << /F %d 0 R >> >>" % ef)
        cat += b" /Names << /EmbeddedFiles << /Names [" + pdf_text("данные.bin") + b" %d 0 R] >> >>" % fs
    if outline:
        outlines = b.reserve()
        item = b.add(b"<< /Title " + pdf_text("Глава 1") + b" /Parent %d 0 R /Dest [%d 0 R /Fit] >>" % (outlines, page_ids[0]))
        b.set(outlines, b"<< /Type /Outlines /First %d 0 R /Last %d 0 R /Count 1 >>" % (item, item))
        cat += b" /Outlines %d 0 R" % outlines
    if form:
        field = b.add(b"<< /FT /Tx /T (name) /V " + pdf_text("Значение поля") + b" /Rect [10 10 200 30] >>")
        cat += b" /AcroForm << /Fields [%d 0 R] >>" % field
    if big_stream_bytes:
        import os
        big = b.add(b.stream(os.urandom(big_stream_bytes), b" /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray"))
        cat += b" /PieceInfo << /TestApp << /LastModified (D:20260101000000Z) /Private %d 0 R >> >>" % big
    if orphan_meta:
        b.add(b.stream(page_xmp("Сирота", "ORPHAN-MARKER-55"), b" /Type /Metadata /Subtype /XML"))
    cat += b" >>"
    b.set(catalog, cat)
    info_id = None
    if info is not None:
        body = b"<< " + b" ".join(b"/" + k.encode() + b" " + (v if isinstance(v, bytes) else pdf_text(v)) for k, v in info.items()) + b" >>"
        info_id = b.add(body)
    return b.build(catalog, info_id)
