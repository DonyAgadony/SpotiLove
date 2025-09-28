# Use official .NET SDK for build
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy everything
COPY . .

# Restore dependencies
RUN dotnet restore

# Build in release mode
RUN dotnet publish -c Release -o /app/publish

# Use smaller runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Expose Render's expected port
ENV ASPNETCORE_URLS=http://+:${PORT}
EXPOSE 10000


# Run the app
ENTRYPOINT ["dotnet", "JsonDemo.dll"]
