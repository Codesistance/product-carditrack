# CardiTrack development container.
#
# Ubuntu 24.04 (Noble) + the full server-side toolchain: .NET 10 SDK, dotnet-ef,
# Terraform, the PostgreSQL client, and optionally the gcloud CLI and the MAUI
# Android layer.
#
# The tool installation lives in install-toolchain.sh rather than inline RUN
# steps so the cloud-session bootstrap (.devcontainer/bootstrap.sh) provisions
# an identical toolchain on images we don't build.
ARG BASE_IMAGE=mcr.microsoft.com/devcontainers/base:ubuntu-24.04
FROM ${BASE_IMAGE}

# Keep in step with .github/workflows/_env.yml and infrastructure/versions.tf.
ARG DOTNET_MAJOR=10.0
ARG TERRAFORM_VERSION=1.14.9

# gcloud is on by default here (the image builds on an unrestricted network);
# the MAUI Android layer is opt-in because it adds several GB.
ARG INSTALL_GCLOUD=1
ARG INSTALL_MAUI=0

# The devcontainers base image ships a passwordless-sudo 'vscode' user. Install
# as that user so dotnet global tools land in the home directory the container
# actually runs as.
USER vscode

COPY --chown=vscode:vscode install-toolchain.sh /usr/local/share/carditrack/install-toolchain.sh
RUN chmod +x /usr/local/share/carditrack/install-toolchain.sh \
    && DOTNET_MAJOR="${DOTNET_MAJOR}" \
       TERRAFORM_VERSION="${TERRAFORM_VERSION}" \
       INSTALL_GCLOUD="${INSTALL_GCLOUD}" \
       INSTALL_MAUI="${INSTALL_MAUI}" \
       /usr/local/share/carditrack/install-toolchain.sh \
    && sudo rm -rf /var/lib/apt/lists/*

ENV PATH="/home/vscode/.dotnet/tools:${PATH}" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    ASPNETCORE_ENVIRONMENT=Development \
    ANDROID_HOME=/home/vscode/Android/Sdk

LABEL org.opencontainers.image.title="CardiTrack Dev Container"
LABEL org.opencontainers.image.description="Ubuntu 24.04 with .NET 10 SDK, EF Core tools, Terraform, and the PostgreSQL client"
LABEL org.opencontainers.image.vendor="CardiTrack"
