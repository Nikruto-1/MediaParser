"""HTTP integration checks against a running, local MediaParser instance (Python stdlib)."""
import argparse
import html
import http.cookiejar
import io
import json
import re
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument("--base-url", default="http://localhost:5125")
args = parser.parse_args()
base = args.base_url.rstrip("/")
if urllib.parse.urlparse(base).hostname not in {"localhost", "127.0.0.1", "::1"}:
    raise SystemExit("Smoke checks must target a local instance.")

client = urllib.request.build_opener(
    urllib.request.ProxyHandler({}),
    urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()),
)

def request(path, data=None):
    payload = None if data is None else urllib.parse.urlencode(data).encode()
    try:
        response = client.open(base + path, data=payload, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    return response.status, response.read(), response.geturl(), response.headers

def token(page):
    match = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', page.decode())
    assert match, "Antiforgery token missing"
    return html.unescape(match.group(1))

for path in ["/", "/Parser/SearchByMedia", "/Parser/MyResults", "/Media/EditMedia", "/Media/Create"]:
    status, page, _, _ = request(path)
    assert status == 200, (path, status)
    assert b"__RequestVerificationToken" in page
    print("PASS GET", path)

assert request("/Media/Delete", {"id": "1"})[0] == 400
assert request("/Media/Update/2147483647")[0] == 404
print("PASS CSRF rejects unverified POST and missing records return 404")

_, page, _, _ = request("/Parser/SearchByMedia")
status, payload, _, _ = request("/Parser/SelectMedia", {"__RequestVerificationToken": token(page)})
assert status == 200 and json.loads(payload)["success"]
status, page, _, _ = request("/Parser/SearchByMedia", {
    "__RequestVerificationToken": token(page), "Nicknames[0]": "local-smoke", "Profile": "Default"
})
assert status == 200 and "Не вибрано соцмережі" in html.unescape(page.decode())
print("PASS Empty selection produces a validation message")

name = "Smoke-" + uuid.uuid4().hex[:12]
source_id = None
try:
    _, page, _, _ = request("/Media/Create")
    data = {"__RequestVerificationToken": token(page), "Name": name,
            "SearchUrl": "https://example.com/{0}", "ErrorKeywordsRaw": "not json", "LinkType": "User"}
    status, page, _, _ = request("/Media/Create", data)
    assert status == 200 and "JSON-масив" in html.unescape(page.decode())
    print("PASS Invalid error-keyword JSON returns form validation")
    data.update({"__RequestVerificationToken": token(page), "ErrorKeywordsRaw": "[]"})
    status, page, url, _ = request("/Media/Create", data)
    assert status == 200 and "/Media/EditMedia" in url
    source_id = re.search(r'id="media_(\d+)"\s+[^>]*value="' + re.escape(name) + r'"', page.decode())
    assert source_id, "Created source missing from selection list"
    source_id = int(source_id.group(1))
    print("PASS Create source and reload the media selection")
    _, page, _, _ = request(f"/Media/Update/{source_id}")
    assert f'value="{source_id}"' in page.decode()
    data.update({"__RequestVerificationToken": token(page), "Id": str(source_id), "Name": name + "-edited"})
    status, page, _, _ = request("/Media/Update", data)
    assert status == 200 and (name + "-edited") in page.decode()
    print("PASS Update keeps the source ID and displays changed data")
    status, workbook, _, headers = request("/Parser/ExportExcel", {
        "__RequestVerificationToken": token(page), "isExportLastSearch": "false"
    })
    assert status == 200 and "spreadsheetml" in headers["Content-Type"]
    with zipfile.ZipFile(io.BytesIO(workbook)) as archive:
        assert "xl/worksheets/sheet1.xml" in archive.namelist()
    print("PASS Excel export returns a valid XLSX archive")
finally:
    if source_id is not None:
        _, page, _, _ = request("/Media/EditMedia")
        status, payload, _, _ = request("/Media/Delete", {
            "__RequestVerificationToken": token(page), "id": str(source_id)
        })
        assert status == 200 and json.loads(payload)["success"]
        assert request(f"/Media/Update/{source_id}")[0] == 404
        print("PASS Delete removes only the generated smoke source")

print("All HTTP smoke checks passed.")
