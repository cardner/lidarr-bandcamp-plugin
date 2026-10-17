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
  | python3 -c 'import json,sys; d=json.load(sys.stdin); print(f"version={d.get(\"version\")} branch={d.get(\"branch\")}")'

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

IDX=$(build_payload "${LIDARR_URL}/api/v1/indexer/schema" BandcampIndexer "Bandcamp Library" "")
DC=$(BANDCAMP_DOWNLOAD_PATH="$BANDCAMP_DOWNLOAD_PATH" build_payload \
  "${LIDARR_URL}/api/v1/downloadclient/schema" BandcampDownloadClient "Bandcamp" \
  "path=os.environ['BANDCAMP_DOWNLOAD_PATH']
for f in bc['fields']:
  if f.get('name')=='downloadPath':
    f['value']=path")

echo "Testing indexer..."
idx_test=$(curl -sS -X POST "${LIDARR_URL}/api/v1/indexer/test" "${auth_hdr[@]}" -d "$IDX")
echo "$idx_test" | python3 -c 'import json,sys; d=json.load(sys.stdin); 
print("indexer test OK" if d=={} or d==[] or d is None else d); 
assert d in ({}, [], None) or (isinstance(d,list) and len(d)==0), d'

echo "Testing download client..."
dc_test=$(curl -sS -X POST "${LIDARR_URL}/api/v1/downloadclient/test" "${auth_hdr[@]}" -d "$DC")
echo "$dc_test" | python3 -c 'import json,sys; d=json.load(sys.stdin);
print("download client test OK" if d in ({}, [], None) else d);
assert d in ({}, [], None) or (isinstance(d,list) and len(d)==0), d'

# Create if not already present
existing_idx=$(curl -fsS "${LIDARR_URL}/api/v1/indexer" -H "X-Api-Key: ${LIDARR_API_KEY}")
echo "$existing_idx" | python3 -c 'import json,sys; a=json.load(sys.stdin); sys.exit(0 if any(i.get("implementation")=="BandcampIndexer" for i in a) else 1)' \
  && echo "Indexer already exists" \
  || curl -fsS -X POST "${LIDARR_URL}/api/v1/indexer?forceSave=true" "${auth_hdr[@]}" -d "$IDX" >/dev/null

existing_dc=$(curl -fsS "${LIDARR_URL}/api/v1/downloadclient" -H "X-Api-Key: ${LIDARR_API_KEY}")
echo "$existing_dc" | python3 -c 'import json,sys; a=json.load(sys.stdin); sys.exit(0 if any(i.get("implementation")=="BandcampDownloadClient" for i in a) else 1)' \
  && echo "Download client already exists" \
  || curl -fsS -X POST "${LIDARR_URL}/api/v1/downloadclient?forceSave=true" "${auth_hdr[@]}" -d "$DC" >/dev/null

echo "Bootstrap complete. Next: UI search/grab for an owned album (see docs/lidarr-ui-checklist.md)."
