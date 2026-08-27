# Build from the workspace root so the linked Lambda implementation remains
# available: docker build -f PropsSeekr-MobileAPI/Dockerfile -t propseekr-mobileapi .
# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["PropsSeekr-MobileAPI/PropSeekr.csproj", "PropsSeekr-MobileAPI/"]
RUN dotnet restore "PropsSeekr-MobileAPI/PropSeekr.csproj"
COPY PropsSeekr-MobileAPI/ PropsSeekr-MobileAPI/
COPY PropsSeekr-matchingapi/ PropsSeekr-matchingapi/
WORKDIR /src/PropsSeekr-MobileAPI
RUN dotnet build "PropSeekr.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "PropSeekr.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Production runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "PropSeekr.dll"]
