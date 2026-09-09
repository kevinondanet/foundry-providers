---
description: 'Repository instructions for the HVE Core demo workspace'
---

# Repository Instructions

This repository follows the Microsoft HVE Core engineering conventions. Path-specific
instruction files live in `.github/instructions/` and apply to the files they name:

* `python-script.instructions.md` - Python scripts and modules (`**/*.py`)
* `python-tests.instructions.md` - Python tests (`**/*.py`, pytest and unittest)
* `bash.instructions.md` - shell scripts (`**/*.sh`)
* `markdown.instructions.md` - Markdown documents (`**/*.md`)
* `commit-message.instructions.md` - commit message format
* `copilot-tracking.instructions.md` - the `.copilot-tracking/` conventions for RPI and review artifacts

Read the instruction file for a file type before creating or editing that kind of file.
This sandbox has `python3`, `bash`, `git` and coreutils only: `uv` is not installed, so run
Python with `python3` directly and do not create virtual environments. Do not install packages.
Never ask the user questions; make reasonable assumptions and finish the task.
