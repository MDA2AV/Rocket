# Build from the repo root:  docker build -f Playground/Dockerfile -t playground .
FROM mcr.microsoft.com/dotnet/sdk:11.0-preview AS build
WORKDIR /src

# global.json pins the SDK feature band, and every package csproj references the root README.md
# via <None Include="../../../README.md" />, so both have to be in place before restore.
COPY global.json README.md ./

# Project files first, so the restore layer stays cached until a csproj actually changes.
# These are the seven projects Playground.csproj pulls in, transitively included.
COPY src/ioxide/ioxide.csproj                           src/ioxide/
COPY src/clients/ioxide.pg/ioxide.pg.csproj             src/clients/ioxide.pg/
COPY src/clients/ioxide.file/ioxide.file.csproj         src/clients/ioxide.file/
COPY src/protocols/ioxide.http11/ioxide.http11.csproj   src/protocols/ioxide.http11/
COPY src/protocols/ioxide.http3/ioxide.http3.csproj     src/protocols/ioxide.http3/
COPY src/protocols/ioxide.ngtcp2/ioxide.ngtcp2.csproj   src/protocols/ioxide.ngtcp2/
COPY src/protocols/ioxide.nghttp3/ioxide.nghttp3.csproj src/protocols/ioxide.nghttp3/
COPY Playground/Playground.csproj                       Playground/
RUN dotnet restore Playground/Playground.csproj

# Then the sources. src/ carries the committed runtimes/linux-x64/native/*.so bundles, which the
# ngtcp2/nghttp3 projects copy next to the output so DllImport probing finds them.
COPY src/ src/
COPY Playground/ Playground/
RUN dotnet publish Playground/Playground.csproj -c Release -o /app --no-restore

# The ngtcp2 bundle's only external dependency is OpenSSL 3 (libcrypto.so.3), which the Debian-based
# runtime image already carries.
FROM mcr.microsoft.com/dotnet/runtime:11.0-preview
WORKDIR /app
COPY --from=build /app .
ENV PLAYGROUND_MODE=raw PLAYGROUND_REACTORS=1
EXPOSE 8080/tcp
EXPOSE 8443/udp
ENTRYPOINT ["dotnet", "Playground.dll"]
