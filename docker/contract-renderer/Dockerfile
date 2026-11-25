# Optional document-rendering runtime. This does not replace the standard API image.
# Run with --network none, --memory 768m, --cpus 1 and a private writable /work.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends libreoffice-writer poppler-utils fonts-liberation fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /work
ENV HOME=/work
USER $APP_UID
ENTRYPOINT ["/usr/bin/soffice"]
