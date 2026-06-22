# Build stage — SDK image compiles to a native self-contained binary via PublishAot=true in the csproj.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
# Native AOT prerequisites: clang (platform linker) and zlib1g-dev (compression headers)
RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY ["DockerSwarmWebhook/DockerSwarmWebhook.csproj", "DockerSwarmWebhook/"]
RUN dotnet restore "./DockerSwarmWebhook/DockerSwarmWebhook.csproj" -a $TARGETARCH
COPY . .
WORKDIR "/src/DockerSwarmWebhook"
RUN dotnet publish "./DockerSwarmWebhook.csproj" -c Release -a $TARGETARCH -o /app/publish

# Final stage — Ubuntu Noble runtime-deps with Docker CLI available for optional CLI update mode.
# No .NET runtime needed: the binary is self-contained (Native AOT).
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble AS final
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl gnupg \
    && install -m 0755 -d /etc/apt/keyrings \
    && curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc \
    && chmod a+r /etc/apt/keyrings/docker.asc \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu noble stable" > /etc/apt/sources.list.d/docker.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends docker-ce-cli \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
EXPOSE 3000
COPY --from=build /app/publish .
# Run as root — required to access the bind-mounted /var/run/docker.sock (owned by root:docker on the host).
USER root
ENTRYPOINT ["/app/DockerSwarmWebhook"]
