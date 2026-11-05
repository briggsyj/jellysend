# jellysend image. Build from the repository root:  docker build -t jellysend .
#
# Stage 1 builds libdave (Discord's DAVE end-to-end encryption library) as a
# shared library so the bot can P/Invoke its C API (dave.h). This mirrors the
# libdave vcpkg recipe without vcpkg: mlspp at the commit libdave's overlay
# port pins, with GREASE disabled and the `mlspp` C++ namespace, against the
# system OpenSSL 3. Bump both pins together.
FROM registry.fedoraproject.org/fedora:44 AS libdave
ARG LIBDAVE_REF=6396c90aedbb9a6ee117ea9ce1ce9832af6a3375
ARG MLSPP_REF=1cc50a124a3bc4e143a787ec934280dc70c1034d
RUN dnf install -y gcc-c++ cmake ninja-build git openssl-devel json-devel \
    && dnf clean all
WORKDIR /src
RUN git clone https://github.com/cisco/mlspp.git \
    && git -C mlspp checkout "${MLSPP_REF}" \
    # libdave doesn't use these and GCC 15 trips -Werror in mls_ds (same as the overlay port)
    && sed -i '/^add_subdirectory(mls_ds)$/d;/^add_subdirectory(mls_vectors)$/d' mlspp/lib/CMakeLists.txt \
    && cmake -S mlspp -B mlspp/build -G Ninja -DCMAKE_BUILD_TYPE=Release \
        -DDISABLE_GREASE=ON -DMLS_CXX_NAMESPACE=mlspp -DTESTING=OFF \
        -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DCMAKE_INSTALL_PREFIX=/opt/dave \
    && cmake --build mlspp/build && cmake --install mlspp/build
RUN git clone https://github.com/discord/libdave.git \
    && git -C libdave checkout "${LIBDAVE_REF}" \
    && cmake -S libdave/cpp -B libdave/build -G Ninja -DCMAKE_BUILD_TYPE=Release \
        -DBUILD_SHARED_LIBS=ON -DTESTING=OFF -DCMAKE_PREFIX_PATH=/opt/dave \
        -DCMAKE_INSTALL_PREFIX=/opt/dave \
    && cmake --build libdave/build && cmake --install libdave/build \
    # The CMake project is named "libdave", so the shared object is liblibdave.so;
    # expose the name the P/Invoke resolver looks for as well.
    && cd /opt/dave/lib* && for f in liblibdave.so*; do [ -e "$f" ] && ln -sf "$f" libdave.so && break; done; true

# Stage 2: publish the host as a self-contained linux-x64 app.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . ./
RUN dotnet publish src/JellySend.Host/JellySend.Host.csproj -c Release -r linux-x64 \
        --self-contained true -o /out

# Stage 3: runtime. Fedora's ffmpeg-free plus the RPM Fusion VA-API drivers
# (mesa freeworld / Intel) for hardware H.264 encoding, libopus/libsodium for the
# bot's own Opus encoding and transport encryption, and a monospace font for the
# `!debug` overlay (FFmpeg drawtext resolves font=monospace via fontconfig).
FROM registry.fedoraproject.org/fedora-minimal:44
RUN microdnf install -y \
        ffmpeg-free \
        libva-utils \
        opus \
        libsodium \
        openssl-libs \
        libstdc++ \
        libicu \
        fontconfig \
        dejavu-sans-mono-fonts \
        https://mirrors.rpmfusion.org/free/fedora/rpmfusion-free-release-44.noarch.rpm \
        https://mirrors.rpmfusion.org/nonfree/fedora/rpmfusion-nonfree-release-44.noarch.rpm \
    && microdnf install -y --allowerasing \
        mesa-va-drivers-freeworld \
        intel-media-driver \
        libva-intel-driver \
    && microdnf clean all \
    && rm -rf /var/cache/dnf /var/cache/libdnf5
COPY --from=libdave /opt/dave/lib*/*dave*.so* /usr/local/lib/
RUN echo /usr/local/lib > /etc/ld.so.conf.d/jellysend.conf && ldconfig
WORKDIR /app
COPY --from=build /out ./
# Loading / pause cards (spec 015): assets/cards/loading.* and paused.* — credits in assets/cards/README.md.
COPY assets/cards/ ./cards/
ENTRYPOINT ["/app/JellySend.Host"]
