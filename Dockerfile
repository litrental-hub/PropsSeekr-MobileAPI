# Build from this project directory: docker build -t propseekr-mobileapi .
# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["PropSeekr.csproj", "./"]
RUN dotnet restore "PropSeekr.csproj"
COPY . .
WORKDIR /src
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
