# LysKontroll — project notes for Claude

## Running hardware-in-the-loop integration tests

`DrammenMJKConfig/DrammenMJKConfig.IntegrationTests` holds tests that need a
real Arduino running `Firmware.ino` connected over USB serial (e.g.
`StatusSessionIntegrationTests`, `ArduinoConnectivityTests`). They're marked
`[Explicit("...")]` so a plain `dotnet test` on the solution skips them —
that's why the normal test run only shows 35 passing tests from
`DrammenMJKConfig.Tests` and reports 0/0 for the integration project.

To actually run them (board must be connected):

```
dotnet test DrammenMJKConfig.IntegrationTests --filter "FullyQualifiedName~StatusSessionIntegrationTests"
```

Selecting an `[Explicit]` test by name via `--filter` counts as explicitly
selecting it, which is what makes the NUnit adapter run it instead of
skipping it. This is ordinary `dotnet test`/VSTest filter syntax — nothing
Microsoft.Testing.Platform-specific about it. (`Microsoft.Testing.Platform.dll`
and friends show up in the build output only because `Microsoft.NET.Test.Sdk`
18.9.0 bundles them as a compat/bridge layer for classic VSTest via
`Microsoft.Testing.Extensions.VSTestBridge.dll` — their presence is not
evidence the MTP runner is what's actually executing tests here.)

`ArduinoTestHelper.Connect()` in that project auto-probes every available COM
port and returns whichever one answers `PING`/`SI` — don't hardcode a port
number, it isn't stable across sessions (it's been observed to drop and
reappear mid-session on this machine).
