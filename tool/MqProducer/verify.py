"""Read-only acceptance check for the explicitly authorized test server."""
import json
import os
import urllib.request
from datetime import datetime, timezone, timedelta

BASE = os.environ["SDATA_API_BASE"].rstrip("/")


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise ValueError("Redirects are not allowed")


opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
headers = {"Content-Type": "application/json"}


def request(path, payload=None):
    if path not in ("/auth/login", "/analytics/search", "/monitor/status"):
        raise ValueError("Unsupported acceptance endpoint")
    data = None if payload is None else json.dumps(payload).encode()
    req = urllib.request.Request(BASE + path, data=data, headers=headers)
    with opener.open(req, timeout=20) as response:
        return json.load(response)


auth = request("/auth/login", {
    "username": os.environ.get("SDATA_USERNAME", "admin"),
    "password": os.environ["SDATA_PASSWORD"],
})
headers["Authorization"] = "Bearer " + auth["token"]
now = datetime.now(timezone.utc)
result = request("/analytics/search", {
    "sql": "SELECT source, count(*) AS rows FROM {{logs}} WHERE ts >= $start AND ts < $end GROUP BY source",
    "from": (now - timedelta(days=1)).isoformat(),
    "to": (now + timedelta(minutes=1)).isoformat(),
    "rowLimit": 100,
})
print(json.dumps(result, ensure_ascii=False, indent=2))
print(json.dumps(request("/monitor/status").get("mqConsumers", []), ensure_ascii=False, indent=2))
sources = {row[0]: row[1] for row in result["rows"]}
for prefix in ("kafka:", "rabbitmq:", "pulsar:persistent://public/default/"):
    assert sources.get(prefix + "sdata-e2e-exact", 0) > 0, prefix
    assert sources.get(prefix + "sdata-e2e-prefix-new", 0) >= 30, prefix
print("PASS: three brokers and prefix batches are queryable; duplicates are permitted.")
