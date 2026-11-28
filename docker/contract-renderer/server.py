"""Private, single-render worker. No database, artifact volume or application secrets belong here."""
import hmac
import io
import os
import signal
import socket
import stat
import subprocess
import tempfile
import threading
import time
import zipfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

MAX_DOCX = 10 * 1024 * 1024
MAX_PDF = 50 * 1024 * 1024
MAX_BOUNDS = 10 * 1024 * 1024
SANDBOX = "/usr/local/bin/contract-render-sandbox"


def run_command(command, work, deadline, accepted=(0,)):
    # The document engine never inherits the service authentication token or host configuration.
    environment = {"PATH": "/usr/bin:/bin", "LANG": "C.UTF-8", "HOME": str(work), "TMPDIR": str(work), "SAL_USE_VCLPLUGIN": "svp"}
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise RuntimeError("render deadline exceeded")
    with subprocess.Popen(command, cwd=work, env=environment, stdin=subprocess.DEVNULL,
                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True,
                          close_fds=True) as process:
        try:
            code = process.wait(timeout=remaining)
            if code not in accepted:
                raise RuntimeError("renderer unavailable")
            return code
        finally:
            # Kill the whole group, including detached-from-parent LibreOffice descendants.
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass


def read_artifact(path, maximum):
    # An untrusted engine must not trick the privileged supervisor into following a symlink.
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(descriptor, "rb") as source:
        info = os.fstat(source.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_size > maximum:
            raise RuntimeError("invalid artifact")
        result = source.read(maximum + 1)
        if len(result) > maximum:
            raise RuntimeError("artifact too large")
        return result


def render_document(docx):
    try:
        with zipfile.ZipFile(io.BytesIO(docx)) as archive:
            names = archive.namelist()
            if len(names) > 1024 or len(set(names)) != len(names) or "word/document.xml" not in names or "[Content_Types].xml" not in names \
                    or sum(entry.file_size for entry in archive.infolist()) > MAX_PDF:
                raise RuntimeError("invalid DOCX package")
    except zipfile.BadZipFile as error:
        raise RuntimeError("invalid DOCX package") from error
    deadline = time.monotonic() + 50
    with tempfile.TemporaryDirectory(prefix="tripmate-render-") as directory:
        work = Path(directory)
        source = work / "preview.docx"
        source.write_bytes(docx)
        # The launcher drops privileges and applies filesystem/network/resource restrictions before exec.
        command = [SANDBOX, str(work), "/usr/lib/libreoffice/program/soffice.bin", "--headless", "--norestore", "--nodefault", "--nolockcheck",
                   "-env:OSL_SOCKET_PATH=" + str(work), "-env:UserInstallation=" + (work / "profile").as_uri(),
                   "--convert-to", "pdf:writer_pdf_Export", "--outdir", str(work), str(source)]
        # The direct engine requests one restart (81) after creating a fresh profile.
        # Never loop indefinitely, and retain the original total deadline.
        if run_command(command, work, deadline, accepted=(0, 81)) == 81:
            run_command(command, work, deadline)
        pdf = work / "preview.pdf"
        data = read_artifact(pdf, MAX_PDF)
        run_command([SANDBOX, str(work), "/usr/bin/pdftotext", "-bbox", "-enc", "UTF-8", str(pdf), str(work / "bounds.html")], work, deadline)
        bounds = work / "bounds.html"
        if not data.startswith(b"%PDF-"):
            raise RuntimeError("invalid PDF signature")
        return data, read_artifact(bounds, MAX_BOUNDS)


class RenderServer(ThreadingHTTPServer):
    daemon_threads = True
    request_queue_size = 8

    def __init__(self, address, token):
        self.token = token.encode("utf-8")
        self.render_slot = threading.BoundedSemaphore(1)
        self.connection_slots = threading.BoundedSemaphore(8)
        super().__init__(address, RenderHandler)

    def process_request(self, request, client_address):
        if not self.connection_slots.acquire(blocking=False):
            self.shutdown_request(request)
            return
        try:
            super().process_request(request, client_address)
        except BaseException:
            self.connection_slots.release()
            raise

    def process_request_thread(self, request, client_address):
        try:
            super().process_request_thread(request, client_address)
        finally:
            self.connection_slots.release()


class RenderHandler(BaseHTTPRequestHandler):
    def setup(self):
        super().setup()
        self.connection.settimeout(15)

    def log_message(self, *_):
        pass  # Do not log document content, tokens or tool diagnostics.

    def reply(self, status, body=b"", content_type="text/plain"):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        self.reply(200 if self.path == "/health" else 404, b"ready" if self.path == "/health" else b"")

    def do_POST(self):
        if self.path != "/render":
            self.reply(404)
            return
        supplied = self.headers.get("Authorization", "").encode("utf-8")
        if not hmac.compare_digest(supplied, b"Bearer " + self.server.token):
            self.reply(401)
            return
        if self.headers.get("Transfer-Encoding") or len(self.headers.get_all("Content-Length", [])) != 1:
            self.reply(400)
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            self.reply(400)
            return
        if length <= 0 or length > MAX_DOCX:
            self.reply(413)
            return
        if not self.server.render_slot.acquire(blocking=False):
            self.reply(429)
            return
        try:
            source = self.rfile.read(length)
            if len(source) != length:
                self.reply(400)
                return
            pdf, bounds = render_document(source)
            with io.BytesIO() as output:
                with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_STORED) as archive:
                    archive.writestr("preview.pdf", pdf)
                    archive.writestr("bounds.html", bounds)
                self.reply(200, output.getvalue(), "application/zip")
        except (OSError, RuntimeError, subprocess.SubprocessError):
            self.reply(503, b"renderer unavailable")
        finally:
            self.server.render_slot.release()


def main():
    token = os.environ.get("RENDERER_API_KEY", "")
    if len(token) < 32:
        raise SystemExit("RENDERER_API_KEY must contain at least 32 characters")
    # No healthy deployment if the host denies sandbox setup. Never substitute a fake verification flag.
    with tempfile.TemporaryDirectory(prefix="tripmate-render-") as directory:
        check = subprocess.run([SANDBOX, directory, "--self-test"], env={"PATH": "/usr/bin:/bin"},
                               stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
                               close_fds=True, timeout=10)
        if check.returncode != 0:
            # Startup self-test has no uploaded document; these are fixed launcher stage names only.
            raise SystemExit("Sandbox self-test failed: " + check.stderr.decode("utf-8", errors="replace")[:512])
    class DualStackServer(RenderServer):
        address_family = socket.AF_INET6
    server = DualStackServer(("::", int(os.environ.get("PORT", "8080"))), token)
    print("Contract renderer ready; sandbox self-test passed", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
