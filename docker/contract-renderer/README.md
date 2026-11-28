# Optional contract renderer

For the separate authenticated Railway service (without Docker-in-Docker), see
[`docs/railway-contract-pdf.md`](../../docs/railway-contract-pdf.md) and
`Dockerfile.railway`. The image and instructions below remain the local Docker adapter.

The standard API image does not bundle LibreOffice. This optional image provides
LibreOffice Writer, Poppler's `pdftotext` and Vietnamese-capable fonts. Build it
with `docker build -t tripmate-contract-renderer -f docker/contract-renderer/Dockerfile .`.

Render untrusted documents only in a deployment that denies outbound networking
and caps CPU, RAM and runtime. For a standalone smoke test use `--network none`,
`--memory 768m`, `--cpus 1`, a read-only input mount and a separate private writable
output directory. Do not publish container ports or mount the Docker socket.
Run as the non-root application user. Configure executable paths to
`/usr/bin/soffice` and `/usr/bin/pdftotext` in the actual API render worker.

`ContractTemplateRendererOptions.IsolatedExecutionVerified` defaults to false.
Enable it only after the worker's network/resource isolation is verified. Merely
building this image does not configure or isolate the API's render process.
Each DOCX is inspected before rendering; each resulting PDF is checked for a
valid signature marker using `pdftotext -bbox`. Positions must be recomputed for
each final TO-specific PDF. The preview uses synthetic data and is not a signed
contract.

For a local API running on the host, set `ContractTemplates__Renderer__DockerExecutablePath`
to the absolute Docker CLI path and `ContractTemplates__Renderer__DockerImage` to
the trusted prebuilt image tag. The Docker adapter uses no network, read-only input,
768 MB RAM, one CPU, a read-only root filesystem and a separate private output mount.
It kills its uniquely named container after completion or timeout. Do not expose the
Docker socket to the renderer container. Production deployments must supply a worker
with its own suitable isolation; mounting the host socket into the API is not required
or recommended by this implementation.

Set `ContractTemplates__Artifacts__RootPath` to durable private storage and enable
`ContractTemplates__PreviewWorkerEnabled` only after applying
`database/migrations/20261123_admin_operator_contract_templates.sql`.
The default configuration fails closed and never returns a fabricated PDF.
