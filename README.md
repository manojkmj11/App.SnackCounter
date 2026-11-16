# Snack Counter

Snack Counter manages pantry refills in Oslo (NOK) and Bengaluru (INR), temporary office visits, purchases, and reimbursement claims. The API owns business state. The Angular client uses REST for state changes and SignalR for live updates.

See [DESIGN.md](DESIGN.md) for the core file map, visit assignment rules, regression tests, configuration reference, tradeoffs, and assumptions.

## Prerequisites

- .NET 10 SDK for the API, Mock HR service, and tests.
- Node.js **22.22.3 or newer in the 22.x line** (Angular 22 compatibility) and npm **11.4.2** (pinned by `UI.SnackCounter/package.json`).
- SQL Server Express running as `localhost\SQLEXPRESS`, or update both connection strings to reachable SQL Server databases. Windows integrated authentication is used by the checked-in development configuration.
- Two databases that the local Windows account can create/alter: `SnackCounter` and `SnackCounterMockHr`. Development startup applies EF migrations automatically.
- Ports 4200 (Angular), 5121 (API), and 5205 (Mock HR) available locally.
- Git checkout with the repository lockfile. No global Angular CLI is required; `npm start` uses the local CLI.

Angular’s supported Node.js ranges are listed in the [official compatibility table](https://angular.dev/reference/versions).

## Run locally

1. Review `API.SnackCounter/appsettings.Development.json` and `API.SnackCounter/MockHr.Service/appsettings*.json`. Set local secrets and connection strings outside source control for non-demo use. The checked-in Mock HR credentials are demo-only.
2. Restore and install packages:

   ```powershell
   dotnet restore API.SnackCounter/API.SnackCounter.csproj
   dotnet restore API.SnackCounter/MockHr.Service/MockHr.Service.csproj
   cd UI.SnackCounter
   npm ci
   cd ..
   ```

3. Start each service in a separate terminal from the repository root:

   ```powershell
   dotnet run --project API.SnackCounter/MockHr.Service/MockHr.Service.csproj --urls http://localhost:5205
   dotnet run --project API.SnackCounter/API.SnackCounter.csproj --urls http://localhost:5121
   cd UI.SnackCounter
   npm start
   ```

4. Open `http://localhost:4200`. Use the demo identities exposed by the API in Development.

## Verify

```powershell
dotnet test API.SnackCounter/tests/API.SnackCounter.Tests/API.SnackCounter.Tests.csproj
cd UI.SnackCounter
npm test
```
