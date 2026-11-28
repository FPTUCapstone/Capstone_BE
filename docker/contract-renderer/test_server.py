"""Protocol tests; the separate container smoke test exercises the actual engine/sandbox."""
import http.client
import io
import os
import tempfile
import threading
import time
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

import server


class RendererProtocolTests(unittest.TestCase):
    def setUp(self):
        self.httpd = server.RenderServer(("127.0.0.1", 0), "test-token-at-least-32-characters-long")
        self.thread = threading.Thread(target=self.httpd.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.httpd.shutdown()
        self.httpd.server_close()
        self.thread.join()

    def request(self, body=b"docx", token="test-token-at-least-32-characters-long", **headers):
        client = http.client.HTTPConnection("127.0.0.1", self.httpd.server_port, timeout=3)
        try:
            client.request("POST", "/render", body, {"Authorization": "Bearer " + token, **headers})
            response = client.getresponse()
            return response.status, response.read()
        finally:
            client.close()

    def test_unauthenticated_request_never_renders(self):
        with patch.object(server, "render_document") as render:
            self.assertEqual(401, self.request(token="wrong")[0])
            render.assert_not_called()

    def test_oversized_request_rejected_before_read(self):
        self.assertEqual(413, self.request(body=b"", **{"Content-Length": str(server.MAX_DOCX + 1)})[0])

    def test_chunked_request_rejected(self):
        self.assertEqual(400, self.request(body=b"", **{"Transfer-Encoding": "chunked"})[0])

    def test_success_returns_only_fixed_pdf_and_bounds_entries(self):
        with patch.object(server, "render_document", return_value=(b"%PDF-test", b"<html/>")):
            status, body = self.request()
        self.assertEqual(200, status)
        with zipfile.ZipFile(io.BytesIO(body)) as archive:
            self.assertEqual(["preview.pdf", "bounds.html"], archive.namelist())
            self.assertEqual(b"%PDF-test", archive.read("preview.pdf"))

    def test_busy_worker_returns_429_instead_of_queuing_documents(self):
        self.httpd.render_slot.acquire()
        try:
            self.assertEqual(429, self.request()[0])
        finally:
            self.httpd.render_slot.release()

    def test_renderer_failure_is_generic_and_slot_is_released(self):
        with patch.object(server, "render_document", side_effect=RuntimeError("private details")):
            status, body = self.request()
        self.assertEqual(503, status)
        self.assertNotIn(b"private", body)
        self.assertTrue(self.httpd.render_slot.acquire(blocking=False))
        self.httpd.render_slot.release()

    def test_missing_engine_fails_without_pdf(self):
        with tempfile.TemporaryDirectory() as work:
            with self.assertRaises((OSError, RuntimeError)):
                server.run_command(["/definitely-missing-renderer"], Path(work), time.monotonic() + 1)

    def test_non_docx_is_rejected_before_running_tools(self):
        with patch.object(server, "run_command") as command:
            with self.assertRaises(RuntimeError):
                server.render_document(b"not a DOCX")
            command.assert_not_called()

    @unittest.skipIf(os.name == "nt", "Linux filesystem boundary")
    def test_worker_never_follows_engine_output_symlinks(self):
        with tempfile.TemporaryDirectory() as work:
            target = Path(work) / "secret"
            target.write_bytes(b"private")
            output = Path(work) / "preview.pdf"
            output.symlink_to(target)
            with self.assertRaises(OSError):
                server.read_artifact(output, server.MAX_PDF)


if __name__ == "__main__":
    unittest.main()
