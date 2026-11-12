# syntax=docker/dockerfile:1
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release
ARG SKIP_TESTS=false
WORKDIR /src

COPY ["Nugget.sln", "./"]
COPY ["Nugget.Api/Nugget.Api.csproj", "Nugget.Api/"]
COPY ["Nugget.Tests/Nugget.Tests.csproj", "Nugget.Tests/"]
RUN dotnet restore "Nugget.Tests/Nugget.Tests.csproj"

COPY . .
# The test suite runs as part of the image build (--build-arg SKIP_TESTS=true
# to skip it). It needs no S3: the tests use an in-memory store.
RUN if [ "$SKIP_TESTS" != "true" ]; then \
      dotnet test "Nugget.Tests/Nugget.Tests.csproj" \
        -c $BUILD_CONFIGURATION --no-restore; \
    fi
RUN dotnet publish "Nugget.Api/Nugget.Api.csproj" -a $TARGETARCH \
      -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
# S3 certificates are checked against the OS trust store. To bake an internal
# CA into the image, put it next to this Dockerfile and uncomment:
#   USER root
#   COPY internal-ca.crt /usr/local/share/ca-certificates/internal-ca.crt
#   RUN update-ca-certificates
# Or, without rebuilding, mount the CA into a directory and add it at runtime
# with SSL_CERT_DIR=/etc/ssl/certs:/certs/ca (see deploy/docker-compose.yml).
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
EXPOSE 8080 8443
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Nugget.Api.dll"]
