---
title: Rotate log files
description: Keep the newest log files in a directory and delete the rest with tools/rotate.py
author: Ops Tools Team
ms.date: 2026-09-08
ms.topic: how-to
---

# Rotate log files

## Purpose

This guide shows operators how to trim a log directory with `tools/rotate.py` so that only the newest files remain.

## When to use this guide

Use it when a service writes one `*.log` file per run and the directory grows without bound.

## Prerequisites

* Python 3.11 or later on `PATH` as `python3`
* Write access to the log directory

## Steps

1. Preview what would be deleted:

   ```bash
   python3 tools/rotate.py /var/log/app --keep 5 --dry-run
   ```

2. Run the rotation for real:

   ```bash
   python3 tools/rotate.py /var/log/app --keep 5
   ```

3. Check the exit code: `0` means success, `1` means the directory was not found, `2` means bad arguments.

## Expected outcome

The five newest `*.log` files remain and every older one is deleted. Files with other extensions are untouched.

## Troubleshooting

* `is not a directory`: the path does not exist or is a file; check the argument.
* Nothing is deleted: fewer than `--keep` log files exist, which is the expected no-op.
