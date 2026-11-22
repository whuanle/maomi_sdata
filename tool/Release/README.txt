SData portable release
======================
Self-contained: no .NET or Node.js installation needed.

Windows x64: double-click SData.exe in the extracted folder (or run it from a terminal).
Linux/macOS: from the extracted folder, run ./SData

Then open http://localhost:5300 and sign in as admin. The initial password is
SData:Auth:InitialAdminPassword in appsettings.json (sdata@123 in this package). The
program has no built-in password, and a fresh database cannot start without one.
Change it in the UI after the first sign-in, and use unique credentials and TLS before
exposing the service outside localhost.

Configuration:
  appsettings.json          console logging, plus settings that have no page in the web UI
                            (HTTP port, data paths, DuckDB resources, initial admin password,
                            ingest buffer, alert and agent budgets, listeners, origin lists).
                            Every value has a built-in default.
  configs/appsettings.json  created on first start as a copy of appsettings.json, only when
                            missing. Edit it to override any value above. Not in this package.
  environment variables     highest priority, using "__" for nested keys, for example
                            SData__Port=5301 or SData__DuckDb__Threads=4.
  web UI                    business settings (S3 cold storage, retention, compression,
                            OTLP, log consumption, DuckDB memory limit, notification
                            channels, RUM sites, API keys) are configured in the UI and
                            stored in the database. Saved values take precedence.
Logs are written to the console only.

Runtime state is created beside the executable and must stay writable:
  data/                     SQLite (sdata.db) and DuckDB (sdata.duckdb)
  configs/rsa_private.key   JWT signing key, generated on first start
Keep both during upgrades. Back up first, stop the old service, extract the new
binaries into a NEW folder, copy data/, configs/rsa_private.key and any
configs/appsettings.json over, then start. Never overlay a running instance. Keep the
environment variables you set for the old service.

HTTP listens on SData:Port (5300 by default). The embedded MQTT broker listens on 1883;
set SData__Mqtt__Port to change it.

Linux archives require a glibc-based .NET 10-supported distribution and its
system dependencies (ICU, OpenSSL, zlib, libc/libgcc/libstdc++). Alpine/musl is not
supported. macOS archives are not Developer ID signed or notarized: review the
source/checksum and use the OS security UI to approve execution if required.
Do not disable Gatekeeper globally. Windows ARM64 is not a supported target.

SHA256SUMS.txt in the GitHub Release lists archive checksums. All native-library
files shipped with this package must remain beside their published paths.
