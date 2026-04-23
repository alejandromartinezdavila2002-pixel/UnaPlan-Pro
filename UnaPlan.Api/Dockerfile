# 1. Imagen de SDK para compilar
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copiar archivos y restaurar dependencias
COPY *.sln .
COPY *.csproj .
RUN dotnet restore

# Copiar todo y publicar
COPY . .
RUN dotnet publish -c Release -o out

# 2. Imagen de Runtime para correr la app
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/out .

# Configurar el puerto para Render
ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000

ENTRYPOINT ["dotnet", "UnaPlan.dll"]