"""Run against a local MediaParser instance. Uses isolated temporary sources and a local API."""
import argparse
import html
import http.cookiejar
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import io
import json
import re
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--base-url', default='http://localhost:5125')
args = parser.parse_args()
base = args.base_url.rstrip('/')
if urllib.parse.urlparse(base).hostname not in {'localhost', '127.0.0.1', '::1'}:
    raise SystemExit('Smoke checks must target a local instance.')

def make_client():
    return urllib.request.build_opener(urllib.request.ProxyHandler({}),
        urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
client, stranger = make_client(), make_client()

def request(path, data=None, *, as_json=False, csrf=None, browser=client):
    headers = {}
    if csrf: headers['RequestVerificationToken'] = csrf
    if as_json:
        payload = json.dumps(data).encode()
        headers['Content-Type'] = 'application/json'
    else:
        payload = None if data is None else urllib.parse.urlencode(data).encode()
    try:
        response = browser.open(urllib.request.Request(base + path, payload, headers), timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    return response.status, response.read(), response.geturl(), response.headers

def token(page):
    match = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', page.decode())
    assert match, 'Antiforgery token missing'
    return html.unescape(match.group(1))

def api(path, data=None, *, csrf=None, browser=client):
    status, body, _, _ = request(path, data, as_json=data is not None, csrf=csrf, browser=browser)
    try: data = json.loads(body)
    except json.JSONDecodeError: data = {}
    return status, data

def finish(job_id):
    deadline, last_completed = time.monotonic() + 20, -1
    while time.monotonic() < deadline:
        status, job = api('/Parser/SearchProgress/' + job_id)
        assert status == 200
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
        except (BrokenPipeError, ConnectionResetError): pass
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
    status, job = api('/Parser/StartSearch',{'identifiers':['fixture_user'],'mode':'User','sourceIds':[ids[k] for k in ['ok','missing','blocked','error']]},csrf=csrf)
    assert status == 202
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
    print('PASS Another session cannot read, export, cancel, or retry this job')
    status, retry = api('/Parser/RetrySearch/'+job['id'],{},csrf=csrf)
    assert status == 202 and retry['total'] == 3
    finish(retry['id'])
    print('PASS Retry skips confirmed absent results and rechecks only unresolved outcomes')
    status, slow = api('/Parser/StartSearch',{'identifiers':['first','second','third'],'mode':'User','sourceIds':[ids['slow']]},csrf=csrf)
    assert status == 202
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
finally:
    for _, source_id in sources:
        _, page, _, _ = request('/Media/Create')
        status, body, _, _ = request('/Media/Delete',{'__RequestVerificationToken':token(page),'id':str(source_id)})
        assert status == 200 and json.loads(body)['success']
    fixture.shutdown()
    fixture.server_close()
print('All HTTP smoke checks passed; temporary sources removed.')
