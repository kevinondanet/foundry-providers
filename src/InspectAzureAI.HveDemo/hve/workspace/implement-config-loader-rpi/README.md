# configkit

`configkit.load_config(path, env=None)` reads a TOML file into a `Config` dataclass and lets
environment variables override values (`APP_<SECTION>_<KEY>`, upper case).
