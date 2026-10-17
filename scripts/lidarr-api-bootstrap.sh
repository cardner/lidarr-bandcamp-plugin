#!/usr/bin/env bash
# Configure Bandcamp indexer + download client via Lidarr API and run Test.
# Secrets via env only — never commit cookies.
set -euo pipefail

LIDARR_URL="${LIDARR_URL:-http://127.0.0.1:8687}"
LIDARR_API_KEY="${LIDARR_API_KEY:?Set LIDARR_API_KEY (from config.xml <ApiKey>)}"
BANDCAMP_IDENTITY_COOKIE="${BANDCAMP_IDENTITY_COOKIE:?Set BANDCAMP_IDENTITY_COOKIE}"
BANDCAMP_DOWNLOAD_PATH="${BANDCAMP_DOWNLOAD_PATH:-/downloads/bandcamp}"

auth_hdr=(-H "X-Api-Key: ${LIDARR_API_KEY}" -H "Content-Type: application/json")

echo "Lidarr: $LIDARR_URL"
curl -fsS "${LIDARR_URL}/ping" >/dev/null
curl -fsS "${LIDARR_URL}/api/v1/system/status" -H "X-Api-Key: ${LIDARR_API_KEY}" \
  | python3 -c 'import json,sys; d=json.load(sys.stdin); print("version=%s branch=%s" % (d.get("version"), d.get("branch")))'

plugins=$(curl -fsS "${LIDARR_URL}/api/v1/system/plugins" -H "X-Api-Key: ${LIDARR_API_KEY}")
echo "$plugins" | python3 -c 'import json,sys; a=json.load(sys.stdin); print("plugins:", [p["name"]+"@"+p.get("installedVersion","?") for p in a])'
echo "$plugins" | python3 -c 'import json,sys; a=json.load(sys.stdin); assert any("Bandcamp" in p.get("name","") for p in a), "Bandcamp plugin not loaded — install DLL and restart"'

# Enable Bandcamp on default delay profile
dp=$(curl -fsS "${LIDARR_URL}/api/v1/delayprofile/1" -H "X-Api-Key: ${LIDARR_API_KEY}")
dp_enabled=$(echo "$dp" | python3 -c '
import json,sys
dp=json.load(sys.stdin)
for i in dp["items"]:
  if i.get("protocol")=="BandcampDownloadProtocol":
    i["allowed"]=True
json.dump(dp, sys.stdout)
')
curl -fsS -X PUT "${LIDARR_URL}/api/v1/delayprofile/1" "${auth_hdr[@]}" -d "$dp_enabled" >/dev/null
echo "Delay profile: Bandcamp protocol allowed"

mkdir -p "$BANDCAMP_DOWNLOAD_PATH"

build_payload() {
  local schema_url="$1" impl="$2" name="$3" extra_py="$4"
  curl -fsS "$schema_url" -H "X-Api-Key: ${LIDARR_API_KEY}" | python3 -c "
import json,sys,os
arr=json.load(sys.stdin)
bc=next(x for x in arr if x.get('implementation')=='$impl')
cookie=os.environ['BANDCAMP_IDENTITY_COOKIE']
for f in bc['fields']:
  if f.get('name')=='cookies':
    f['value']=cookie
$extra_py
payload={
  'enableRss': False,
  'enableAutomaticSearch': True,
  'enableInteractiveSearch': True,
  'supportsRss': False,
  'supportsSearch': True,
  'enable': True,
  'protocol': bc['protocol'],
  'priority': 25,
  'downloadClientId': 0,
  'name': '$name',
  'fields': bc['fields'],
  'implementationName': bc.get('implementationName') or 'Bandcamp',
  'implementation': '$impl',
  'configContract': bc.get('configContract'),
  'infoLink': bc.get('infoLink'),
  'tags': []
}
json.dump(payload, sys.stdout)
"
}

# Merge schema-built cookie/path fields into an existing resource (keeps id/name for Test uniqueness).
merge_existing() {
  local existing_json="$1" impl="$2" fresh_payload="$3" path_field="${4:-}"
  EXISTING_JSON="$existing_json" IMPL="$impl" PATH_FIELD="$path_field" \
  python3 -c "
import json,os,sys
arr=json.loads(os.environ['EXISTING_JSON'])
impl=os.environ['IMPL']
path_field=os.environ.get('PATH_FIELD') or ''
fresh=json.load(sys.stdin)
ex=next((x for x in arr if x.get('implementation')==impl), None)
if not ex:
  json.dump(fresh, sys.stdout)
  sys.exit(0)
# Update cookie (and download path) on the saved resource
fresh_fields={f.get('name'): f.get('value') for f in fresh.get('fields') or []}
for f in ex.get('fields') or []:
  name=f.get('name')
  if name=='cookies' and 'cookies' in fresh_fields:
    f['value']=fresh_fields['cookies']
  if path_field and name==path_field and path_field in fresh_fields:
    f['value']=fresh_fields[path_field]
json.dump(ex, sys.stdout)
" <<<"$fresh_payload"
}

IDX=$(build_payload "${LIDARR_URL}/api/v1/indexer/schema" BandcampIndexer "Bandcamp Library" "")
DC=$(BANDCAMP_DOWNLOAD_PATH="$BANDCAMP_DOWNLOAD_PATH" build_payload \
  "${LIDARR_URL}/api/v1/downloadclient/schema" BandcampDownloadClient "Bandcamp" \
  "path=os.environ['BANDCAMP_DOWNLOAD_PATH']
for f in bc['fields']:
  if f.get('name')=='downloadPath':
    f['value']=path")

existing_idx=$(curl -fsS "${LIDARR_URL}/api/v1/indexer" -H "X-Api-Key: ${LIDARR_API_KEY}")
existing_dc=$(curl -fsS "${LIDARR_URL}/api/v1/downloadclient" -H "X-Api-Key: ${LIDARR_API_KEY}")

IDX_TEST=$(merge_existing "$existing_idx" BandcampIndexer "$IDX")
DC_TEST=$(merge_existing "$existing_dc" BandcampDownloadClient "$DC" downloadPath)

echo "Testing indexer..."
idx_test=$(curl -sS -X POST "${LIDARR_URL}/api/v1/indexer/test" "${auth_hdr[@]}" -d "$IDX_TEST")
echo "$idx_test" | python3 -c 'import json,sys; d=json.load(sys.stdin);
print("indexer test OK" if d in ({}, [], None) else d);
assert d in ({}, [], None) or (isinstance(d,list) and len(d)==0), d'

echo "Testing download client..."
dc_test=$(curl -sS -X POST "${LIDARR_URL}/api/v1/downloadclient/test" "${auth_hdr[@]}" -d "$DC_TEST")
echo "$dc_test" | python3 -c 'import json,sys; d=json.load(sys.stdin);
print("download client test OK" if d in ({}, [], None) else d);
assert d in ({}, [], None) or (isinstance(d,list) and len(d)==0), d'

# Create or update so the cookie from env is what Lidarr uses
if echo "$existing_idx" | python3 -c 'import json,sys; a=json.load(sys.stdin); sys.exit(0 if any(i.get("implementation")=="BandcampIndexer" for i in a) else 1)'; then
  id=$(echo "$IDX_TEST" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
  curl -fsS -X PUT "${LIDARR_URL}/api/v1/indexer/${id}?forceSave=true" "${auth_hdr[@]}" -d "$IDX_TEST" >/dev/null
  echo "Indexer updated (id=$id)"
else
  curl -fsS -X POST "${LIDARR_URL}/api/v1/indexer?forceSave=true" "${auth_hdr[@]}" -d "$IDX" >/dev/null
  echo "Indexer created"
fi

if echo "$existing_dc" | python3 -c 'import json,sys; a=json.load(sys.stdin); sys.exit(0 if any(i.get("implementation")=="BandcampDownloadClient" for i in a) else 1)'; then
  id=$(echo "$DC_TEST" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
  curl -fsS -X PUT "${LIDARR_URL}/api/v1/downloadclient/${id}?forceSave=true" "${auth_hdr[@]}" -d "$DC_TEST" >/dev/null
  echo "Download client updated (id=$id)"
else
  curl -fsS -X POST "${LIDARR_URL}/api/v1/downloadclient?forceSave=true" "${auth_hdr[@]}" -d "$DC" >/dev/null
  echo "Download client created"
fi

echo "Bootstrap complete. Next: UI search/grab for an owned album (see docs/lidarr-ui-checklist.md)."
