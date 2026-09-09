# Sandbox image of the HVE demo: Python 3.12 on Debian bookworm plus git (the commit-message sample); bash, tar and
# coreutils come with the base image. The Copilot CLI itself is NOT baked in and nothing here downloads it: the agent
# writes the pinned release tarball from the host cache into the container (sandbox.WriteFileAsync) at sample start
# and extracts it under /var/tmp, exactly as the Claude Code agent injects its binary. One container per sample; the
# sample's files land under /workspace.
FROM python:3.12-slim-bookworm

RUN apt-get update \
    && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace
