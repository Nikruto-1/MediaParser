"""Run against a local MediaParser instance. Uses isolated temporary sources and a local API."""
import argparse
import html
import http.cookiejar
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from html.parser import HTMLParser
import io
import json
import re
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import xml.etree.ElementTree as ET
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--base-url', default='http://localhost:5125')
args = parser.parse_args()
base = args.base_url.rstrip('/')
if urllib.parse.urlparse(base).hostname not in {'localhost', '127.0.0.1', '::1'}:
    raise SystemExit('Smoke checks must target a local instance.')

class RedirectControl(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if not getattr(req, 'follow_redirects', True):
            return None
        return super().redirect_request(req, fp, code, msg, headers, newurl)

def make_client():
    return urllib.request.build_opener(urllib.request.ProxyHandler({}),
        urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()), RedirectControl())
client, stranger = make_client(), make_client()

def open_request(req, browser):
    try:
        response = browser.open(req, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        return response.status, response.read(), response.geturl(), response.headers

def request(path, data=None, *, as_json=False, csrf=None, browser=client):
    headers = {}
    if csrf: headers['RequestVerificationToken'] = csrf
    if as_json:
        payload = json.dumps(data).encode()
        headers['Content-Type'] = 'application/json'
    else:
        payload = None if data is None else urllib.parse.urlencode(data).encode()
    return open_request(urllib.request.Request(base + path, payload, headers), browser)

def multipart(path, fields, files, *, csrf=None, browser=client):
    """Submit native form fields and in-memory files, retaining the initial redirect."""
    boundary = 'MediaParserSmoke' + uuid.uuid4().hex
    parts = []
    values = dict(fields)
    if csrf is not None:
        values['__RequestVerificationToken'] = csrf
    for name, value in values.items():
        parts.append((f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n'
            + str(value) + '\r\n').encode('utf-8'))
    for name, filename, content_type, content in files:
        parts.append((f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"; filename="{filename}"\r\n'
            f'Content-Type: {content_type}\r\n\r\n').encode('utf-8'))
        parts.append(content.encode('utf-8') if isinstance(content, str) else content)
        parts.append(b'\r\n')
    parts.append(f'--{boundary}--\r\n'.encode('ascii'))
    req = urllib.request.Request(base + path, b''.join(parts),
        {'Content-Type': f'multipart/form-data; boundary={boundary}'})
    req.follow_redirects = False
    return open_request(req, browser)

def token(page):
    match = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', page.decode())
    assert match, 'Antiforgery token missing'
    return html.unescape(match.group(1))

def result_cards(page):
    return re.findall(r'<article\b[^>]*\bdata-result-card\b[^>]*>.*?</article>', page.decode(), re.DOTALL)

def sheet_rows(archive, path):
    ns = {'s': 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'}
    root = ET.fromstring(archive.read(path))
    return [[ ''.join(cell.itertext()) for cell in row.findall('s:c', ns)]
        for row in root.findall('s:sheetData/s:row', ns)]

class SourceMarkup(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.sources, self.hints, self.active_hint = [], {}, None
    def handle_starttag(self, tag, attrs):
        attributes = dict(attrs)
        if 'source-option' in attributes.get('class', '').split():
            self.sources.append(attributes)
        if attributes.get('id', '').startswith('sourceHint_'):
            self.active_hint = attributes['id']
            self.hints[self.active_hint] = ''
    def handle_data(self, data):
        if self.active_hint is not None:
            self.hints[self.active_hint] += data
    def handle_endtag(self, tag):
        if tag == 'span': self.active_hint = None

def api(path, data=None, *, csrf=None, browser=client):
    status, body, _, _ = request(path, data, as_json=data is not None, csrf=csrf, browser=browser)
    try: data = json.loads(body)
    except json.JSONDecodeError: data = {}
    return status, data

def validate_planned(job):
    planned = job.get('plannedResults')
    assert isinstance(planned, list) and len(planned) == job['total'], 'Missing selected sources in planned rows'
    assert len({(row['siteId'], row['identifier']) for row in planned}) == job['total'], 'Duplicate planned work items'
    assert sum(row['status'] == 'checking' for row in planned) <= 1, 'More than one active check inside a job'
    assert all(row.get('checkedAt') is None for row in planned if row['status'] in {'pending', 'checking'}), 'Unfinished rows have check timestamps'
    assert all(row['status'] not in {'pending', 'checking'} for row in job['results']), 'Placeholders were published as finished results'
    assert job['completed'] <= len(job['results']) <= job['total'], 'Placeholder rows changed completed progress'
    serialized = json.dumps(job)
    assert 'not-a-real-secret' not in serialized and 'apiKey' not in serialized and 'apiUrl' not in serialized, 'Public plan contains private API data'

def finish(job_id):
    deadline, last_completed = time.monotonic() + 20, -1
    while time.monotonic() < deadline:
        status, job = api('/Parser/SearchProgress/' + job_id)
        assert status == 200
        validate_planned(job)
        assert job['completed'] >= last_completed, 'Progress moved backwards'
        last_completed = job['completed']
        if job['state'] in {'completed','cancelled','failed'}: return job
        time.sleep(.05)
    raise AssertionError('Local API job did not finish')

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        kind = self.path.split('/')[1]
        if kind == 'slow': time.sleep(.7)
        status = {'missing':404,'blocked':403,'error':500}.get(kind,200)
        body = json.dumps({'username':self.path.split('/')[-1],'id':42}).encode()
        self.send_response(status)
        self.send_header('Content-Type','application/json')
        self.send_header('Content-Length',str(len(body)))
        self.end_headers()
        try: self.wfile.write(body)
        except ConnectionError: pass
    def log_message(self,*args): pass

fixture = ThreadingHTTPServer(('127.0.0.1',0),Handler)
threading.Thread(target=fixture.serve_forever,daemon=True).start()
sources = []
try:
    for path in ['/', '/Parser/SearchByMedia', '/Parser/MyResults', '/Media/EditMedia', '/Media/Create', '/Home/Privacy']:
        status, _, _, _ = request(path)
        assert status == 200, (path,status)
        print('PASS GET',path)
    assert request('/Media/Delete',{'id':'1'})[0] == 400
    assert request('/Media/Update/2147483647')[0] == 404
    assert api('/Parser/StartSearch',{'identifiers':['x'],'sourceIds':[1],'mode':'User'})[0] == 400
    _, search_page, _, _ = request('/Parser/SearchByMedia')
    csrf = token(search_page)
    markup = SourceMarkup()
    markup.feed(search_page.decode())
    nazk_sources = [source for source in markup.sources if source.get('data-source-name') == 'НАЗК — декларації']
    assert len(nazk_sources) == 1, 'NAZK declaration source is missing or duplicated in inline selection'
    nazk_source = nazk_sources[0]
    assert nazk_source.get('data-mode') == 'FullName' and nazk_source.get('data-manual-supported') == 'false', 'NAZK should use full-name API lookup rather than manual Chrome'
    nazk_hint = markup.hints.get('sourceHint_' + nazk_source['data-source-id'], '')
    assert 'НАЗК' in nazk_hint and 'API' in nazk_hint, 'NAZK public API requirements are not explained next to its source'
    # Deliberately use the incompatible mode: validation must reject before the official API can be called.
    assert api('/Parser/StartSearch',{'identifiers':['fixture_user'],'sourceIds':[int(nazk_source['data-source-id'])],'mode':'User'},csrf=csrf)[0] == 400
    print('PASS NAZK is a single full-name API source with guidance; nickname mode rejects it without an external request')
    youcontrol_sources = [source for source in markup.sources if source.get('data-source-name') == 'YouControl — декларації']
    assert len(youcontrol_sources) == 1, 'YouControl declaration source is missing or duplicated in inline selection'
    youcontrol_source = youcontrol_sources[0]
    assert youcontrol_source.get('data-mode') == 'FullName' and youcontrol_source.get('data-manual-supported') == 'true', 'YouControl full-name declarations should support the explicit manual public-page reader'
    youcontrol_hint = markup.hints.get('sourceHint_' + youcontrol_source['data-source-id'], '')
    assert 'YouControl' in youcontrol_hint and any(part in youcontrol_hint.lower() for part in ['службов', 'метадан']), 'YouControl public service metadata scope is not explained next to its source'
    # An incompatible-mode request cannot trigger a real provider lookup or CAPTCHA work.
    assert api('/Parser/StartSearch',{'identifiers':['fixture_user'],'sourceIds':[int(youcontrol_source['data-source-id'])],'mode':'User'},csrf=csrf)[0] == 400
    print('PASS YouControl is a single full-name declaration source with guidance; nickname mode rejects it without an external request')
    for invalid in [
        {'identifiers':['x'],'sourceIds':[],'mode':'User'},
        {'identifiers':[],'sourceIds':[1],'mode':'User'},
        {'identifiers':['x'],'sourceIds':[1],'mode':'Wrong'},
        {'identifiers':['x'],'sourceIds':[19],'mode':'User'},
    ]:
        assert api('/Parser/StartSearch',invalid,csrf=csrf)[0] == 400
    print('PASS Request validation, mode separation, missing records, and CSRF')
    marker = 'Smoke-' + uuid.uuid4().hex[:10]
    for kind in ['ok','missing','blocked','error','slow']:
        _, page, _, _ = request('/Media/Create')
        name = marker + '-' + kind
        data = {'__RequestVerificationToken':token(page),'Name':name,
                'SearchUrl':'https://example.com/{0}',
                'ApiUrl':f'http://127.0.0.1:{fixture.server_port}/{kind}/'+'{0}',
                'ApiKey':'not-a-real-secret','ErrorKeywordsRaw':'[]','LinkType':'User'}
        status, _, url, _ = request('/Media/Create',data)
        assert status == 200 and '/Media/EditMedia' in url
        _, page, _, _ = request('/Parser/SearchByMedia')
        match = re.search(r'data-source-id="(\d+)"\s+data-source-name="'+re.escape(name)+'"',page.decode())
        assert match, 'Created source missing from inline selection'
        sources.append((kind,int(match.group(1))))
    ids = dict(sources)
    print('PASS Temporary API sources appear in inline selection')
    captcha_input = re.search(r'<input[^>]+id="automaticCaptcha"[^>]*>', page.decode())
    assert captcha_input, 'Automatic CAPTCHA option missing'
    if 'disabled' in captcha_input.group(0):
        assert api('/Parser/StartSearch',{'identifiers':['fixture_user'],'mode':'User','sourceIds':[ids['ok']],'automaticCaptcha':True},csrf=csrf)[0] == 400
        print('PASS Unconfigured paid CAPTCHA cannot be enabled by a crafted request')
    status, job = api('/Parser/StartSearch',{'identifiers':['fixture_user'],'mode':'User','sourceIds':[ids[k] for k in ['ok','missing','blocked','error']]},csrf=csrf)
    assert status == 202
    validate_planned(job)
    assert not job['isInteractive'] and job['completed'] == 0 and job['results'] == []
    assert not job['automaticCaptcha'], 'A normal search unexpectedly enabled paid CAPTCHA'
    assert all(row['status'] == 'pending' for row in job['plannedResults']), 'Admission response does not expose every selected source as pending'
    job = finish(job['id'])
    assert job['completed'] == job['total'] == 4
    assert {item['status'] for item in job['results']} == {'unconfirmed','notfound','blocked','error'}
    assert 'not-a-real-secret' not in json.dumps(job) and 'apiKey' not in json.dumps(job)
    print('PASS Background progress publishes typed statuses without credentials')
    for route in ['SearchProgress','ExportJob']:
        assert request(f'/Parser/{route}/{job["id"]}',browser=stranger)[0] == 404
    _, stranger_page, _, _ = request('/Parser/SearchByMedia',browser=stranger)
    stranger_token = token(stranger_page)
    for route in ['CancelSearch','RetrySearch']:
        assert request(f'/Parser/{route}/{job["id"]}',{},csrf=stranger_token,browser=stranger)[0] == 404
    manual = '/Parser/ManualCheck/' + job['id']
    unresolved = {'siteId':ids['ok'],'identifier':'fixture_user'}
    # These checks must reject before creating interactive work; the smoke runner never opens Chrome.
    assert api(manual,unresolved)[0] == 400, 'Manual check accepted missing CSRF'
    assert api(manual,unresolved,csrf=stranger_token,browser=stranger)[0] == 404
    assert api('/Parser/ManualCheck/'+str(uuid.uuid4()),unresolved,csrf=csrf)[0] == 404
    assert api(manual,{},csrf=csrf)[0] == 400
    assert api(manual,{'siteId':2147483647,'identifier':'fixture_user'},csrf=csrf)[0] == 400
    assert api(manual,{'siteId':ids['missing'],'identifier':'fixture_user'},csrf=csrf)[0] == 400, 'Confirmed absent result admitted a manual browser'
    print('PASS Another session cannot access job actions; manual rejects CSRF, missing jobs, invalid sources, and resolved rows')
    status, retry = api('/Parser/RetrySearch/'+job['id'],{},csrf=csrf)
    assert status == 202 and retry['total'] == 3
    finish(retry['id'])
    print('PASS Retry skips confirmed absent results and rechecks only unresolved outcomes')
    status, slow = api('/Parser/StartSearch',{'identifiers':['first','second','third'],'mode':'User','sourceIds':[ids['slow']]},csrf=csrf)
    assert status == 202
    validate_planned(slow)
    assert len(slow['plannedResults']) == 3 and all(row['status'] == 'pending' for row in slow['plannedResults'])
    assert api('/Parser/ManualCheck/'+slow['id'],{'siteId':ids['slow'],'identifier':'first'},csrf=csrf)[0] == 409, 'Manual check admitted an unfinished original job'
    assert api('/Parser/StartSearch',{'identifiers':['duplicate'],'mode':'User','sourceIds':[ids['slow']]},csrf=csrf)[0] == 409
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        _, progress = api('/Parser/SearchProgress/'+slow['id'])
        if progress['completed'] == 1: break
        time.sleep(.04)
    else: raise AssertionError('Expected a partial result before cancellation')
    started = time.monotonic()
    _, cancelled = api('/Parser/CancelSearch/'+slow['id'],{},csrf=csrf)
    assert time.monotonic()-started < 2
    assert cancelled['state'] == 'cancelled' and cancelled['completed'] == 1
    validate_planned(cancelled)
    assert len(cancelled['results']) == 3
    assert sum(row['status']=='cancelled' for row in cancelled['results']) == 2
    print('PASS Cancel is prompt, retains partial results, and prevents duplicate work')
    status, workbook, _, headers = request('/Parser/ExportJob/'+cancelled['id'])
    assert status == 200 and 'spreadsheetml' in headers['Content-Type']
    with zipfile.ZipFile(io.BytesIO(workbook)) as archive:
        sheet = archive.read('xl/worksheets/sheet1.xml').decode()
        assert 'first' in sheet and 'second' in sheet and 'third' in sheet
    _, history, _, _ = request('/Parser/MyResults')
    assert 'fixture_user' in history.decode() and 'third' in history.decode()
    print('PASS Partial XLSX export and typed history include cancelled work')
    status, background = api('/Parser/StartSearch',{'identifiers':['history_without_final_poll'],'mode':'User','sourceIds':[ids['ok']]},csrf=csrf)
    assert status == 202
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        _, history, _, _ = request('/Parser/MyResults')
        if 'history_without_final_poll' in history.decode(): break
        time.sleep(.05)
    else: raise AssertionError('History did not recover a finished job without a final progress poll')
    print('PASS History recovers a finished job even without its final progress poll')

    # Offline import must parse public metadata without opening Chrome or contacting YouControl.
    import_name = 'Імпортний Тестовий Декларант'
    import_url = 'https://youcontrol.com.ua/catalog/individuals/smoke-synthetic-declarant/'
    first_document = '11111111-1111-4111-8111-111111111111'
    second_document = '22222222-2222-4222-8222-222222222222'
    import_html = f'''<!doctype html><html><head><meta charset="utf-8"><title>Синтетична публічна картка</title>
        <script>window.privateData = "PRIVATE-SCRIPT-SENTINEL";</script></head><body>
        <h2>Суб'єкти декларування — {import_name}<div class="label label-info">2</div></h2>
        <table class="table-yc table-declaration-subj">
        <tr><th>Тип декларації</th><th>Подана</th><th>Місце роботи/посада</th><th>Місто, селище чи село</th><th></th></tr>
        <tr><td><div class="td-title">Тип декларації</div>Річна</td>
        <td><div class="td-title">Подана</div>24.03.2026</td>
        <td><div class="td-title">Місце роботи/посада</div>Тестова установа &amp; філія / Головний спеціаліст<script>"PRIVATE-CELL-SCRIPT"</script></td>
        <td>PRIVATE-LOCATION-SENTINEL</td><td><a href="/catalog/individuals/declaration/{first_document}/">ДЕТАЛЬНІШЕ</a></td></tr>
        <tr><td>Перед звільненням</td><td>17.12.2020</td><td>Інша установа / Фахівець</td>
        <td>PRIVATE-FAMILY-SENTINEL</td><td><a href="/catalog/individuals/declaration/{second_document}/">ДЕТАЛЬНІШЕ</a></td></tr>
        </table><section>PRIVATE-SALARY-SENTINEL</section></body></html>'''
    saved_page = [('page', 'PRIVATE-UPLOAD-FILENAME.html', 'text/html; charset=utf-8', import_html)]
    before_import = result_cards(history)
    status, _, _, _ = multipart('/Parser/ImportYouControl', {'sourceUrl': import_url}, saved_page)
    assert status == 400, 'Saved-page import accepted missing CSRF'
    status, unchanged_history, _, _ = request('/Parser/MyResults')
    assert status == 200 and result_cards(unchanged_history) == before_import, 'Rejected CSRF changed history'
    print('PASS Multipart saved-page import rejects missing native-form CSRF and preserves history')

    status, _, _, headers = multipart('/Parser/ImportYouControl', {'sourceUrl': import_url}, saved_page, csrf=csrf)
    assert status == 302 and urllib.parse.urlparse(headers.get('Location', '')).path == '/Parser/MyResults', 'Valid import did not redirect to history'
    status, imported_history, _, _ = request('/Parser/MyResults')
    assert status == 200
    imported_cards = result_cards(imported_history)
    assert len(imported_cards) == len(before_import) + 1 and all(card in imported_cards for card in before_import), 'Import replaced existing history'
    imported = [card for card in imported_cards if import_name in html.unescape(card)]
    assert len(imported) == 1, 'Imported public person does not have exactly one history result'
    card = imported[0]
    card_text = ' '.join(html.unescape(re.sub(r'<[^>]+>', ' ', card)).split())
    assert 'data-status="unconfirmed"' in card and 'data-provider="youcontrol"' in card, 'Offline data was presented as a confirmed live lookup or wrong provider'
    for expected in [import_name, 'Тестова установа & філія', 'Головний спеціаліст', 'Інша установа', 'Фахівець',
        'Річна', 'Перед звільненням', '24.03.2026', '17.12.2020', 'Показано 2 із 2', 'Імпортовані дані',
        'Походження та актуальність не перевірено наживо', 'дані можуть стосуватися тезки']:
        assert expected in card_text, ('Imported public metadata or provenance missing', expected)
    assert f'href="{import_url}"' in card and 'Відкрити картку на YouControl' in card_text, 'User-supplied source URL was lost'
    for document in [first_document, second_document]:
        assert f'href="https://youcontrol.com.ua/catalog/individuals/declaration/{document}/"' in card, 'Public document link missing'
    times = re.findall(r'<time\b([^>]*)>(.*?)</time>', card, re.DOTALL)
    for iso, display in [('2026-03-24', '24.03.2026'), ('2020-12-17', '17.12.2020')]:
        assert any(f'datetime="{iso}"' in attributes and 'data-local-time' not in attributes and value.strip() == display
            for attributes, value in times), 'A date-only submission gained a timezone or clock time'
    assert any('Не перевірено' in html.unescape(value) and not re.search(r'datetime="[^"]+"', attributes)
        for attributes, value in times), 'Offline import invented a live CheckedAt timestamp'
    assert 'PRIVATE-' not in html.unescape(imported_history.decode()), 'Private columns, script, or uploaded filename entered public history'
    assert 'Публічні декларації НАЗК' not in card_text and 'Знайдено декларації' not in card_text, 'Import claimed an official or confirmed result'
    print('PASS Saved HTML redirects to rich public history with date-only metadata, safe source links, and unverified provenance')

    challenge_html = '<html><title>Трохи зачекайте…</title><form id="challenge-form">PRIVATE-CHALLENGE</form></html>'
    rejected_imports = [
        (import_url, challenge_html, 'Файл не містить очікуваної публічної картки'),
        ('https://attacker.invalid/catalog/individuals/smoke-synthetic-declarant/', import_html, 'Вкажіть HTTPS-адресу публічної картки'),
    ]
    for rejected_url, rejected_html, expected_error in rejected_imports:
        rejected_file = [('page', 'rejected.html', 'text/html; charset=utf-8', rejected_html)]
        status, _, _, headers = multipart('/Parser/ImportYouControl', {'sourceUrl': rejected_url}, rejected_file, csrf=csrf)
        assert status == 302 and urllib.parse.urlparse(headers.get('Location', '')).path == '/Parser/SearchByMedia', 'Rejected import did not redirect to search'
        status, error_page, _, _ = request('/Parser/SearchByMedia')
        error_text = html.unescape(error_page.decode())
        assert status == 200 and expected_error in error_text and 'role="alert"' in error_text, 'Rejected import did not show its validation error'
        assert 'PRIVATE-' not in error_text, 'Rejected HTML content was echoed into the error page'
        status, preserved_history, _, _ = request('/Parser/MyResults')
        assert status == 200 and result_cards(preserved_history) == imported_cards, 'Rejected import changed saved public history'
    print('PASS Challenge HTML and invalid source URL show errors without changing imported or earlier history')

    status, workbook, _, headers = request('/Parser/ExportExcel', {
        '__RequestVerificationToken': token(preserved_history), 'isExportLastSearch': 'true'})
    assert status == 200 and 'spreadsheetml' in headers['Content-Type'], 'Export-last failed after saved-page import'
    with zipfile.ZipFile(io.BytesIO(workbook)) as archive:
        workbook_xml = archive.read('xl/workbook.xml').decode()
        assert 'name="Declarations"' in workbook_xml and 'xl/worksheets/sheet2.xml' in archive.namelist(), 'Imported declaration worksheet missing'
        summary_rows = sheet_rows(archive, 'xl/worksheets/sheet1.xml')
        declaration_rows = sheet_rows(archive, 'xl/worksheets/sheet2.xml')
        assert len(summary_rows) == 2 and summary_rows[1][0] == import_name and summary_rows[1][1] == '', 'Export-last lost the import or invented a check timestamp'
        assert 'Імпортовані дані' in summary_rows[1][3] and 'не перевірено наживо' in summary_rows[1][3] and summary_rows[1][4] == import_url, 'Export lost unverified provenance or source URL'
        assert len(declaration_rows) == 3, 'Export-last must contain only the two imported public documents'
        for row, date, document in zip(declaration_rows[1:], ['2026-03-24', '2020-12-17'], [first_document, second_document]):
            assert row[0] == row[1] == import_name and row[2] == '' and row[7] == date, 'Export inferred a reporting year or changed a date-only submission'
            assert row[8] == f'https://youcontrol.com.ua/catalog/individuals/declaration/{document}/'
        assert declaration_rows[1][5:7] == ['Головний спеціаліст', 'Тестова установа & філія']
        assert all('PRIVATE-' not in archive.read(name).decode() for name in archive.namelist() if name.endswith('.xml')), 'Private HTML data or filename entered the export'
    print('PASS Export-last keeps the imported declarations sheet, date-only values, unverified origin, and no private HTML data')
finally:
    for _, source_id in sources:
        _, page, _, _ = request('/Media/Create')
        status, body, _, _ = request('/Media/Delete',{'__RequestVerificationToken':token(page),'id':str(source_id)})
        assert status == 200 and json.loads(body)['success']
    fixture.shutdown()
    fixture.server_close()
print('All HTTP smoke checks passed; temporary sources removed.')
