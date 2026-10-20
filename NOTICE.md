# Third-party notices

## Pinball Memory Maps

This program makes use of content from the Pinball Memory Maps project
(https://github.com/tomlogic/pinball-memory-maps), pinned at commit `68c1b33`.

The maps are made available under the Open Database License (ODbL) v1.0; individual
contents are licensed under the Database Contents License (DbCL) v1.0. See
`vendor/pinball-memory-maps/LICENSE-ODbL.md` and `vendor/pinball-memory-maps/LICENSE-DbCL`. The maps are used unmodified.

## pinmame-nvram test data

`tests/Fixtures/nvram/*.nv` and `*.nv.json` are copied from francisdb/pinmame-nvram
(https://github.com/francisdb/pinmame-nvram, commit `d9f184c`), MIT License,
Copyright (c) 2024 Francis De Brabandere. See `tests/Fixtures/nvram/LICENSE`.

The decoding rules in `src/PinBot.Agent/Scoring/MemoryMaps.cs` follow the maps' README and
that project's reference implementation; no code was copied.

## NuGet packages

Agent:

- OpenMcdf 3.1.4 — MPL-2.0 — reads the .vpx compound file to find the table script.
- System.Management 10.0.13 — MIT — reads the VPX process command line.
- System.Security.Cryptography.ProtectedData 10.0.13 — MIT — keeps the server credential encrypted for the Windows user.

Server:

- Npgsql 8.0.5 — PostgreSQL License — PostgreSQL driver.
- Dapper 2.1.35 — Apache-2.0 — SQL queries.
- BouncyCastle.Cryptography 2.4.0 — MIT — checks the Ed25519 signature on Discord interactions.
