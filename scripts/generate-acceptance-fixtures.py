"""Generate reproducible GUI load samples; no uploaded files are executed or modified."""
import argparse
import json
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / 'worker' / 'tests'))
import pdfgen

parser = argparse.ArgumentParser()
parser.add_argument('output', type=pathlib.Path)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
long_value = ('Acceptance multiline value 1234567890\n' * 30000)[:1048576]
assert len(long_value.encode('utf-8')) == 1048576
properties = ''.join(f'<load:Tag{i:05d}>value {i:05d}</load:Tag{i:05d}>' for i in range(10000))
body = pdfgen.RICH_XMP + '<rdf:Description rdf:about="" xmlns:load="https://example.org/acceptance/load/">' + properties + '<load:LongValue>' + long_value + '</load:LongValue></rdf:Description>'
(args.output / 'stress.pdf').write_bytes(pdfgen.make_pdf(raw_xmp=pdfgen.xmp_packet(body), compress_xmp=True))
(args.output / 'manifest.json').write_text(json.dumps({'customTags': 10000, 'multilineUtf8Bytes': 1048576, 'namespace': 'https://example.org/acceptance/load/'}), encoding='utf-8')
