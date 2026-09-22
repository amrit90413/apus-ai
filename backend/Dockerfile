# Multi-stage build for the gateway API.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/Gateway.Api/Gateway.Api.csproj Gateway.Api/
RUN dotnet restore Gateway.Api/Gateway.Api.csproj
COPY src/Gateway.Api/ Gateway.Api/
RUN dotnet publish Gateway.Api/Gateway.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
# Run as non-root (production safety).
RUN adduser --disabled-password --uid 10001 appuser
COPY --from=build /app .
USER appuser
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
# The aspnet image ships neither wget nor curl; bash's /dev/tcp is enough for a
# liveness probe and keeps the image free of extra packages.
HEALTHCHECK --interval=15s --timeout=3s --retries=3 CMD ["bash","-c","exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/live HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3 && head -n1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "Gateway.Api.dll"]
