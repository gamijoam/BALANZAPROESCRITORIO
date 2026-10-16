FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN --mount=type=secret,id=proxy_ca,required=false \
    if [ -s /run/secrets/proxy_ca ]; then \
        SSL_CERT_FILE=/run/secrets/proxy_ca dotnet publish src/Pos.Licensing.Vps/Pos.Licensing.Vps.csproj \
            --configuration Release --output /app/publish /p:UseAppHost=false; \
    else \
        dotnet publish src/Pos.Licensing.Vps/Pos.Licensing.Vps.csproj \
            --configuration Release --output /app/publish /p:UseAppHost=false; \
    fi

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build --chown=1654:1654 /app/publish .
USER 1654:1654
EXPOSE 8080
ENTRYPOINT ["dotnet", "Pos.Licensing.Vps.dll"]
