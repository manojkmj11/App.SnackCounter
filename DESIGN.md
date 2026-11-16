# Snack Counter design

## Core flow

The API is the source of truth for inventory, refill assignments, office visits, budgets, claims, and notifications. An empty-item report creates or reuses an active refill. A background worker offers each open refill to one eligible employee at a time. The offer expires after **2 minutes by default** (configurable); a pass or timeout returns it to the queue. Restocking updates inventory and office spend together. Purchase-linked claims go to Mock HR through a durable outbox; signed, deduplicated webhooks bring decisions back. SignalR provides live updates, while persisted messages and notifications support refresh and reconnect.

## Configuration

Values come from the listed JSON files and can be overridden with normal ASP.NET Core configuration providers (environment variables use `__`, for example `SnackCounter__OfferTimeoutMinutes`). **The two-minute offer window is an operational setting, not a fixed rule.**

| Key | Default | Effect and bounds |
|---|---:|---|
| `SnackCounter:OfferTimeoutMinutes` | **2 minutes** (`appsettings.json`) | Time an individual refill offer remains available. Clamped to 1–1,440 minutes. Set this to change the response window. |
| `SnackCounter:AssignmentTimeoutMinutes` | 1,440 minutes (24 hours) | Accepted refill assignment timeout; clamped to 1–43,200 minutes (30 days). |
| `ConnectionStrings:SnackCounter` | Development SQL Server Express connection in `appsettings.Development.json` | API database. Required for SQL Server provider. |
| `Database:Provider` | `SqlServer` | API provider; supports `SqlServer` or `InMemory` (the latter is for tests/demo only). |
| `Database:Name` | `SnackCounter-Tests` | In-memory API database name. |
| `Database:ApplyMigrationsOnStartup` | API: Development environment; Mock HR: `true` | Runs migrations at startup. Set deliberately in deployed environments. |
| `DemoExchangeRates:NOK:INR` / `DemoExchangeRates:INR:NOK` | `10` / `0.1` | Demo conversion rates for reimbursement/reporting; configure an approved rate source for real financial use. |
| `MockHr:Enabled` | `false` (Development: `true`) | Enables claim submission to Mock HR and its outbox delivery. |
| `MockHr:BaseUrl` | `http://localhost:5205/` | Mock HR API base URL used by Snack Counter. |
| `MockHr:ServiceKey` | Development demo value | Shared service authentication key; configure the same secret in both services. |
| `MockHr:WebhookSecret` | Development demo value | HMAC secret used to sign and verify decisions; configure the same secret in both services. |
| `ConnectionStrings:MockHr` | Development SQL Server Express connection | Mock HR database. Required by Mock HR. |
| `MockHr:SnackCounterWebhookUrl` | `http://localhost:5121/api/integrations/hr/claims/decision` | Callback endpoint for signed HR decisions. |
| `MockHr:PollSeconds` | `5` (`MockHr.Service/appsettings.json`) | Webhook outbox poll interval; clamped to 1–300 seconds and read by the worker. |
| `Cors:AngularOrigin` | `http://localhost:4200` | Allowed Angular origin for API requests and SignalR. |
| `AllowedHosts` | `*` | ASP.NET host filtering in the checked-in settings. |
| `Logging:LogLevel:Default` / `Microsoft.AspNetCore` | `Information` / `Warning` | Application and ASP.NET logging thresholds. |

The UI uses the Angular development proxy (`/api` and `/hubs/notifications`); its target is maintained in the UI proxy configuration rather than duplicated as an app setting. HTTP client timeouts, polling batch sizes, webhook retry delays, worker scan cadence, and the worker’s active-visit refresh cadence are code constants today, not configurable settings.

## Office visits and refill eligibility

Employees qualify through their active base-office roster or an active visit. On visit dates, the destination office takes precedence and the employee is excluded from their base-office refill roster. This prevents a visitor from taking a home-office turn before the destination queue is processed. Visit dates use the destination office’s local calendar and include the end date. Cancelling a visit removes its destination eligibility on the next worker check, normally within ten seconds.

The worker allows one active refill offer per employee, then ranks eligible people by completed refills, offers made, and employee ID. An offer is temporary ownership, not an accepted task. An employee whose offer expires is not offered that same refill again that local day; the worker continues to other eligible employees and retries later if the roster is exhausted. Leave, work-from-home, and wrong-location skips apply to the office where the offer was made, so one office’s skip does not block a visitor at another office.

## Core files and tests

- `API.SnackCounter/Infrastructure/SnackCounterAutomationWorker.cs`: timed offers, visitor precedence, fairness, expiry, and reassignment.
- `API.SnackCounter/Controllers/PeopleOperationsController.cs`: schedule/cancel visits and date-range validation.
- `API.SnackCounter/Controllers/FrontendApiSupport.cs` and `IdentityController.cs`: office access and automatic current-office selection at login.
- `API.SnackCounter/Controllers/RefillOperationsController.cs`: queue, accept, restock, and concurrency rules.
- `API.SnackCounter/Controllers/ChatController.cs`: empty-item reports and user confirmations.
- `API.SnackCounter/Controllers/ExternalHrWebhookController.cs` and `MockHr.Service/Program.cs`: signed decisions and idempotency.
- `API.SnackCounter/Infrastructure/SnackCounterEntityConfigurations.cs`: relationships, constraints, and row versions.
- `API.SnackCounter/tests/API.SnackCounter.Tests/EmployeeOfficeVisitsTests.cs`: visit access plus the two-office visitor-assignment regression.
- `API.SnackCounter/tests/API.SnackCounter.Tests/SnackCounterRulesTests.cs`: timeout, reassignment, and daily roster behavior.
- `UI.SnackCounter/src/app/app.ts` and `app.html`: automatic pantry display, visit refresh, queue and claim coordination.

## Tradeoffs and assumptions

Date-bounded visits reuse existing rosters instead of creating permanent memberships. This keeps the model small but requires eligibility checks against visit dates and time zones. One active offer makes task ownership clear and reduces collisions, but employees wait through timed turns. Daily attempt limits prevent repeatedly notifying the same person; an expired offer therefore cannot immediately return to that employee for the same refill.

Assumptions: a visit is active for its inclusive date range in the destination office’s calendar; overlapping active visits are rejected; an active visit moves refill eligibility to its destination; leave, prior attempts, and another current assignment can make an employee unavailable. Oslo purchases use NOK and Bengaluru purchases use INR; reimbursements use the employee’s payroll currency. Development email authentication, simulated HR/FX, and a single worker instance are demo constraints, not production identity or multi-instance guarantees.
