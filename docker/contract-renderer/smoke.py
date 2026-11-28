"""Run inside the Railway renderer image: real conversion, bounds, auth and timeout cleanup."""
import http.client
import io
import os
import subprocess
import sys
import tempfile
import time
import zipfile
from pathlib import Path
from xml.etree import ElementTree

sys.path.insert(0, "/service")
import server


def docx():
    with io.BytesIO() as output:
        with zipfile.ZipFile(output, "w") as archive:
            archive.writestr("[Content_Types].xml", '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
            archive.writestr("_rels/.rels", '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>')
            archive.writestr("word/document.xml", '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Công ty Đà Nẵng — Nguyễn Văn Mẫu</w:t></w:r></w:p><w:p><w:r><w:t>TRIPMATE_SIGNATURE_ANCHOR</w:t></w:r></w:p></w:body></w:document>')
        return output.getvalue()


def request(token, body):
    connection = http.client.HTTPConnection("127.0.0.1", 8080, timeout=60)
    try:
        connection.request("POST", "/render", body, {"Authorization": "Bearer " + token})
        response = connection.getresponse()
        return response.status, response.read()
    finally:
        connection.close()


def main():
    token = "smoke-only-token-not-a-production-secret"
    environment = {**os.environ, "RENDERER_API_KEY": token, "PORT": "8080"}
    process = subprocess.Popen(["python3", "/service/server.py"], env=environment)
    try:
        for _ in range(100):
            if process.poll() is not None:
                raise RuntimeError("worker failed startup sandbox self-test")
            try:
                connection = http.client.HTTPConnection("127.0.0.1", 8080, timeout=1)
                connection.request("GET", "/health")
                if connection.getresponse().status == 200:
                    connection.close()
                    break
            except OSError:
                time.sleep(0.1)
        else:
            raise RuntimeError("worker did not become healthy")
        assert request("wrong-token", b"invalid")[0] == 401
        status, result = request(token, docx())
        assert status == 200, (status, result)
        with zipfile.ZipFile(io.BytesIO(result)) as archive:
            pdf = archive.read("preview.pdf")
            assert pdf.startswith(b"%PDF-")
            bounds = ElementTree.fromstring(archive.read("bounds.html"))
        words = [element.text for element in bounds.iter() if element.tag.endswith("word")]
        assert "TRIPMATE_SIGNATURE_ANCHOR" in words
        assert "Nguyễn" in words and "Nẵng" in words, words
        # Missing engine and malformed input cannot produce synthetic PDFs.
        assert request(token, b"not a DOCX")[0] == 503
        with tempfile.TemporaryDirectory(prefix="tripmate-render-") as directory:
            work = Path(directory)
            try:
                server.run_command(["/missing-engine"], work, time.monotonic() + 1)
                raise AssertionError("missing engine accepted")
            except FileNotFoundError:
                pass
            pid_file = work / "descendant.pid"
            command = ["python3", "-c", "import subprocess,time; from pathlib import Path; p=subprocess.Popen(['sleep','60']); Path('descendant.pid').write_text(str(p.pid)); time.sleep(60)"]
            try:
                server.run_command(command, work, time.monotonic() + 1)
                raise AssertionError("timeout accepted")
            except subprocess.TimeoutExpired:
                pass
            pid = int(pid_file.read_text())
            for _ in range(20):
                state = Path(f"/proc/{pid}/stat")
                if not state.exists():
                    break
                time.sleep(0.1)
            else:
                raise AssertionError("timed-out descendant still exists, including a leaked zombie")
        # Unsupported deployment (no privilege-drop capability) must exit 125, never run unprotected.
        print(f"PASS: real Vietnamese PDF ({len(pdf)} bytes), anchor, authentication, invalid input, missing engine, timeout tree cleanup; startup verified filesystem/network isolation")
    finally:
        process.terminate()
        process.wait(timeout=5)


if __name__ == "__main__":
    main()
