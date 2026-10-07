#!/usr/bin/env python3
"""Independent pypdf and complete-page Poppler comparisons, with explicit exclusions and deadlines."""
import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess
import tempfile
import time
from collections import Counter
from pypdf import PdfReader
from run_tests import Worker, WorkerError


def render(pdf, prefix, pages):
    command = ['pdftoppm', '-r', '72', '-scale-to', '1024', '-f', '1', '-l', str(pages), str(pdf), str(prefix)]
    result = subprocess.run(command, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, timeout=10)
    images = sorted(prefix.parent.glob(prefix.name + '-*.ppm'))
    if result.returncode != 0 or len(images) != pages or b'Syntax Error' in result.stderr or b'Internal Error' in result.stderr:
        raise ValueError('Poppler could not render every page')
    return [hashlib.sha256(image.read_bytes()).hexdigest() for image in images]


def independent(pdf):
    reader = PdfReader(pdf, strict=False)
    # Compare actual field values and attachment bytes, not only their counts.
    fields = {key: str(value.get('/V')) for key, value in (reader.get_fields() or {}).items()}
    attachments = {key: [hashlib.sha256(value).hexdigest() for value in values] for key, values in reader.attachments.items()}
    return {'pages': len(reader.pages), 'boxes': [list(page.mediabox) for page in reader.pages], 'fields': fields, 'attachments': attachments}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('worker')
    parser.add_argument('corpus', type=pathlib.Path)
    parser.add_argument('--report', type=pathlib.Path, required=True)
    args = parser.parse_args()
    if not shutil.which('pdftoppm'):
        parser.error('Poppler pdftoppm is required; no visual comparisons were performed.')
    report = {'renderer': 'Poppler, all pages at 72 DPI scaled to a maximum dimension of 1024 pixels', 'limits': {'pages': 50, 'renderSeconds': 10}, 'files': []}
    counts = Counter()
    worker = Worker(str(pathlib.Path(args.worker).resolve()))
    try:
        for index, source in enumerate(sorted(args.corpus.rglob('*.pdf')), 1):
            record = {'file': str(source.relative_to(args.corpus))}
            started = time.monotonic()
            with tempfile.TemporaryDirectory(prefix='pdfmeta-independent-') as directory:
                root = pathlib.Path(directory)
                target = root / 'output.pdf'
                try:
                    document = worker.call('open', path=str(source.resolve()))
                    pages = document['pdf'].get('pageCount', 0)
                    if not 1 <= pages <= 50:
                        record.update(status='excluded_page_limit', pages=pages)
                    else:
                        worker.call('save', path=str(source.resolve()), expect=document['file']['fingerprint'], target=str(target),
                                    edits={'info': [{'op': 'set', 'key': '/Title', 'value': 'Independent preservation check'}]}, options={'allowSignedCopy': True})
                        try:
                            before = independent(source)
                            source_images = render(source, root / 'source', pages)
                        except (Exception, subprocess.TimeoutExpired) as error:
                            record.update(status='excluded_source_parser_or_renderer', reason=type(error).__name__ + ': ' + str(error)[:180])
                        else:
                            after = independent(target)
                            output_images = render(target, root / 'output', pages)
                            if before != after or source_images != output_images:
                                record.update(status='failed', reason='Independent structure or page pixels changed', structureBefore=before, structureAfter=after, pixelPages=[i+1 for i,(a,b) in enumerate(zip(source_images, output_images)) if a != b])
                            else:
                                record.update(status='passed', pages=pages)
                except WorkerError as error:
                    record.update(status='refused_' + error.code)
                except Exception as error:
                    record.update(status='failed', reason=type(error).__name__ + ': ' + str(error)[:180])
            record['seconds'] = round(time.monotonic() - started, 3)
            counts[record['status']] += 1
            report['files'].append(record)
            if index % 25 == 0:
                print(index, dict(counts), flush=True)
                report['summary'] = dict(counts)
                args.report.write_text(json.dumps(report, indent=2, default=str), encoding='utf-8')
    finally:
        worker.close()
    report['summary'] = dict(counts)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report['summary']), flush=True)
    raise SystemExit(1 if counts['failed'] else 0)

if __name__ == '__main__':
    main()
