SData portable release
======================
Windows x64: run powershell -File .\start.ps1 from the extracted folder.
Linux/macOS: run ./start.sh. Self-contained: no .NET/Node installation needed.
On first launch, supply SDATA_INITIAL_ADMIN_PASSWORD through your environment
(at least 12 characters), or enter it at the hidden prompt. No default password
is included in this package. Then open http://localhost:5300 and log in as admin.

The launcher initializes configs/system.json only if missing. Configs and data
are stored beside the executable; both directories must be writable. Keep these
directories (including the locally generated JWT private key) during upgrades.
Back up first, stop the old service and extract new binaries into a NEW folder,
then copy your configs/data before starting. Never overlay a running instance.
To use your own config set SDATA_FILE to its absolute path; it must exist.
The external config path does not relocate data or the JWT key automatically.
Use unique credentials and TLS before exposing the service outside localhost.

Linux archives require a glibc-based .NET 10-supported distribution and its
system dependencies (ICU, OpenSSL, zlib, libc/libgcc/libstdc++). Alpine/musl is not
supported. macOS archives are not Developer ID signed or notarized: review the
source/checksum and use the OS security UI to approve execution if required.
Do not disable Gatekeeper globally. Windows ARM64 is not a supported target.
MQTT listens on port 1883 by default; HTTP uses 5300. Change configs/system.json
before startup if these ports are occupied.

SHA256SUMS.txt in the GitHub Release lists archive checksums. All native-library
files shipped with this package must remain beside their published paths.
