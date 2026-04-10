# Build stage — SDK image compiles to a native self-contained binary via PublishAot=true in the csproj.
# --platform=$BUILDPLATFORM runs the SDK on the build host; -a $TARGETARCH cross-compiles for the target arch.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY ["DockerSwarmWebhook/DockerSwarmWebhook.csproj", "DockerSwarmWebhook/"]
RUN dotnet restore "./DockerSwarmWebhook/DockerSwarmWebhook.csproj" -a $TARGETARCH
COPY . .
WORKDIR "/src/DockerSwarmWebhook"
RUN dotnet publish "./DockerSwarmWebhook.csproj" -c Release -a $TARGETARCH -o /app/publish

# Final stage — Ubuntu Noble Chiseled (distroless-style) with only the native runtime dependencies.
# No .NET runtime needed: the binary is self-contained (Native AOT).
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS final
WORKDIR /app
EXPOSE 3000
COPY --from=build /app/publish .
ENTRYPOINT ["/app/DockerSwarmWebhook"]
