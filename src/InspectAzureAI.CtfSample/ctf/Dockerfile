# Sandbox image of the CTF sample: a minimal Debian with the tools a CTF player reaches for.
# One container is started from this image per sample; the sample's setup script then plants the flag.
FROM debian:bookworm-slim

RUN apt-get update \
    && apt-get install -y --no-install-recommends bash coreutils findutils grep gzip binutils file xxd procps ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace
