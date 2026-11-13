# Build image
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
COPY --from=build /out /app
