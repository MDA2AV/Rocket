# Build from the repo root:  docker build -f Playground/Dockerfile -t playground .
FROM mcr.microsoft.com/dotnet/sdk:11.0-preview AS build
WORKDIR /src
COPY ioxide/ioxide.csproj ioxide/
COPY ioxide.pg/ioxide.pg.csproj ioxide.pg/
COPY ioxide.file/ioxide.file.csproj ioxide.file/
COPY Playground/Playground.csproj Playground/
RUN dotnet restore Playground/Playground.csproj
COPY ioxide/ ioxide/
COPY ioxide.pg/ ioxide.pg/
COPY ioxide.file/ ioxide.file/
COPY Playground/ Playground/
RUN dotnet publish Playground/Playground.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:11.0-preview
WORKDIR /app
COPY --from=build /app .
ENV PLAYGROUND_MODE=raw PLAYGROUND_REACTORS=1
EXPOSE 8080
ENTRYPOINT ["dotnet", "Playground.dll"]
