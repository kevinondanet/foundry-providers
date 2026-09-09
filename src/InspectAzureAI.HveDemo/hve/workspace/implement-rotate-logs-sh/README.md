# ops-scripts

Operational helper scripts. `scripts/rotate-logs.sh <dir> <keep>` deletes all but the
newest `<keep>` `*.log` files in `<dir>` and prints one `deleted: <path>` line per removed file.
