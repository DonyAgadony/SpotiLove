# Use official .NET SDK for build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy everything
COPY . .

# Restore dependencies
RUN dotnet restore

# Build in release mode
RUN dotnet publish -c Release -o /app/publish

# Use smaller runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Expose Render's expected port
ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000

# Run the app
ENTRYPOINT ["dotnet", "SpotiLove.dll"]
