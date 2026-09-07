# Sandbox image of the SWE showcase: Python 3.12 with bash, git, pytest and the coreutils the agents rely on.
# The Claude Code binary is copied in at run time by the agent, so no Node.js is installed here.
FROM python:3.12-slim-bookworm

RUN apt-get update \
    && apt-get install -y --no-install-recommends bash curl ca-certificates git procps coreutils \
    && rm -rf /var/lib/apt/lists/*

RUN pip install --no-cache-dir pytest

ENV PYTHONUNBUFFERED=1 PIP_DISABLE_PIP_VERSION_CHECK=1

WORKDIR /workspace
