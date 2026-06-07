# Testing

## Unit tests vs integration tests

Tests are split using an xUnit trait. Integration tests carry `[Trait("Category", "Integration")]`; everything else is a unit test.

Unit tests require no credentials or network access and are what CI runs on every push/PR.

### Run only unit tests (no credentials needed)

```bash
dotnet test --filter "Category!=Integration"
```

### Run only integration tests

```bash
dotnet test --filter "Category=Integration"
```

### Run all tests

```bash
dotnet test
```

### Run a specific test class

```bash
dotnet test --filter "FullyQualifiedName~MasterServerFanOutTests"
dotnet test --filter "FullyQualifiedName~BatchProcessorTests"
```

---

## Integration test environment variables

| Variable | Required | Description |
|---|---|---|
| `BLASTER_TEST_STEAM_USERNAME` | Yes (integration) | Steam account username |
| `BLASTER_TEST_STEAM_PASSWORD` | Yes (integration) | Steam account password |
| `BLASTER_TEST_SERVER_ADDRESS` | No | `host:port` of a specific A2S server to query; if unset, the A2S tests pull a live server from the Steam master using the Steam credentials above |

### Setting variables — PowerShell

```powershell
$env:BLASTER_TEST_STEAM_USERNAME = "your_steam_username"
$env:BLASTER_TEST_STEAM_PASSWORD = "your_steam_password"
$env:BLASTER_TEST_SERVER_ADDRESS = "192.0.2.1:27015"  # optional
dotnet test --filter "Category=Integration"
```

### Setting variables — bash

```bash
export BLASTER_TEST_STEAM_USERNAME=your_steam_username
export BLASTER_TEST_STEAM_PASSWORD=your_steam_password
export BLASTER_TEST_SERVER_ADDRESS=192.0.2.1:27015  # optional
dotnet test --filter "Category=Integration"
```

---

## Skipped integration tests

Some integration tests are still marked `Skip` because they require a specific game server that is difficult to guarantee availability for, such as a The Ship server or a pre-OrangeBox Source server. These will appear as skipped in the test output and do not need any additional setup.
