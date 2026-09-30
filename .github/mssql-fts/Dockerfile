# SQL Server 2025 + Full-Text Search for the Linux CI job (#194 hybrid chat retrieval).
# The stock mcr.microsoft.com/mssql/server:2025-latest image does NOT include Full-Text Search
# (FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 0) — it ships as the separate mssql-server-fts package,
# which must match the mssql-server version exactly, so both come from Microsoft's repo in one install.
FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive \
    ACCEPT_EULA=Y

RUN apt-get update \
 && apt-get install -y --no-install-recommends curl gnupg ca-certificates \
 && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg \
 && echo "deb [arch=amd64 signed-by=/usr/share/keyrings/microsoft-prod.gpg] https://packages.microsoft.com/ubuntu/24.04/mssql-server-2025 noble main" > /etc/apt/sources.list.d/mssql-server-2025.list \
 && echo "deb [arch=amd64 signed-by=/usr/share/keyrings/microsoft-prod.gpg] https://packages.microsoft.com/ubuntu/24.04/prod noble main" > /etc/apt/sources.list.d/microsoft-prod.list \
 && apt-get update \
 && apt-get install -y mssql-server mssql-server-fts mssql-tools18 \
 && rm -rf /var/lib/apt/lists/*

EXPOSE 1433
CMD ["/opt/mssql/bin/sqlservr"]
